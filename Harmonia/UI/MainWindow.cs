using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Harmonia.Feeds;
using Harmonia.Game;
using Harmonia.Localization;
using Harmonia.Packs;
using Harmonia.Runtime;

namespace Harmonia.UI;

// What the plugin loaded at startup. Nothing here changes during a session:
// every pack decision applies at the next game start.
internal sealed record SessionInfo(
    string? LoadedPackId,
    TranslationRuntime? Runtime,
    ExcelRowHooks? Hooks,
    string? PackError,
    string? HookError,
    string PluginVersion,
    GameFonts? Fonts,
    bool Reloaded,
    string SelectedAtStart,
    IReadOnlyList<string> UntranslatedAtStart,
    TextCaseHooks? CaseHooks,
    IReadOnlyList<string> CompatibilityAtStart);

internal sealed partial class MainWindow : Window, IDisposable
{
    private enum Page
    {
        Translations,
        Content,
        Settings,
        Diagnostics,
    }

    private readonly Configuration configuration;
    private readonly Action save;
    private readonly TranslationPackStore packs;
    private readonly PackInstaller installer;
    private readonly FeedUpdateService feeds;
    private readonly FeedUpdateState feedState;
    private readonly SessionState session;
    private readonly SessionInfo info;
    private readonly FileDialogManager fileDialog = new();
    private readonly IFontHandle headingFont;
    private readonly IFontHandle largeIconFont;

    private Page page = Page.Translations;

    public MainWindow(
        Configuration configuration,
        Action save,
        TranslationPackStore packs,
        PackInstaller installer,
        FeedUpdateService feeds,
        FeedUpdateState feedState,
        SessionState session,
        SessionInfo info,
        IUiBuilder uiBuilder,
        Func<IEnumerable<string>> installedPlugins)
        : base("Harmonia###harmonia_main")
    {
        this.configuration = configuration;
        this.save = save;
        this.packs = packs;
        this.installer = installer;
        this.feeds = feeds;
        this.feedState = feedState;
        this.session = session;
        this.info = info;
        this.installedPlugins = installedPlugins;
        headingFont = uiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(MathF.Round(UiBuilder.DefaultFontSizePx * 1.25f))));
        largeIconFont = uiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = MathF.Round(UiBuilder.DefaultFontSizePx * 1.6f) })));

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(780, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        pendingImport?.Dispose();
        pendingImport = null;
        headingFont.Dispose();
        largeIconFont.Dispose();
    }

    public override void Draw()
    {
        DrawNavigation();
        ImGui.SameLine();

        using (var body = ImRaii.Child("##page", Vector2.Zero))
        {
            if (body)
            {
                using var wrap = ImRaii.TextWrapPos(0);
                switch (page)
                {
                    case Page.Translations:
                        DrawTranslations();
                        break;
                    case Page.Content:
                        DrawContent();
                        break;
                    case Page.Settings:
                        DrawSettings();
                        break;
                    case Page.Diagnostics:
                        DrawDiagnostics();
                        break;
                }
            }
        }

        PollStaging();
        PollInstalls();
        DrawAddPopup();
        DrawTrustPopup();
        fileDialog.Draw();
    }

    private void DrawNavigation()
    {
        var width = 170 * Ui.Scale;
        using var nav = ImRaii.Child("##nav", new Vector2(width, 0), true);
        if (!nav)
            return;

        NavItem(Page.Translations, FontAwesomeIcon.Language, Lang.T("nav.translations"), feedState.AvailableCount > 0);
        NavItem(Page.Content, FontAwesomeIcon.ListUl, Lang.T("nav.content"), false);
        NavItem(Page.Settings, FontAwesomeIcon.Cog, Lang.T("nav.settings"), false);
        NavItem(Page.Diagnostics, FontAwesomeIcon.Heartbeat, Lang.T("nav.diagnostics"), HasProblem);

        var version = "v" + info.PluginVersion;
        var bottom = ImGui.GetWindowHeight() - ImGui.GetTextLineHeightWithSpacing() - ImGui.GetStyle().WindowPadding.Y;
        if (bottom > ImGui.GetCursorPosY())
        {
            ImGui.SetCursorPosY(bottom);
            ImGui.TextDisabled(version);
        }
    }

    private void NavItem(Page target, FontAwesomeIcon icon, string label, bool dot)
    {
        var height = ImGui.GetTextLineHeight() + (12 * Ui.Scale);
        var pos = ImGui.GetCursorPos();
        if (ImGui.Selectable("##nav_" + target, page == target, ImGuiSelectableFlags.None, new Vector2(0, height)))
            page = target;
        var after = ImGui.GetCursorPos();

        var textY = pos.Y + ((height - ImGui.GetTextLineHeight()) / 2);
        ImGui.SetCursorPos(Ui.Snap(new Vector2(pos.X + (8 * Ui.Scale), textY)));
        Ui.Icon(icon, page == target ? null : Ui.Muted);
        Ui.SameLineAt(pos.X + (34 * Ui.Scale));
        ImGui.TextUnformatted(label);
        if (dot)
        {
            ImGui.SameLine();
            var radius = 3.5f * Ui.Scale;
            var center = ImGui.GetCursorScreenPos() + new Vector2(radius, ImGui.GetTextLineHeight() / 2);
            ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.GetColorU32(Ui.Pending));
            ImGui.Dummy(new Vector2(radius * 2, 0));
        }

        ImGui.SetCursorPos(after);
    }

    // The loaded pack failed, or the engine or fonts reported trouble.
    private bool HasProblem =>
        !info.Reloaded &&
        (info.PackError is not null ||
         (info.Runtime is not null && (info.Hooks is null || info.Hooks.Errors > 0)) ||
         info.Fonts?.State == GameFontsState.Failed);

    private bool RestartPending =>
        session.IsRestartRequired ||
        !string.Equals(configuration.ActivePackId ?? string.Empty, info.LoadedPackId ?? string.Empty, StringComparison.Ordinal) ||
        (info.LoadedPackId is not null && !configuration.UntranslatedSheets.Order(StringComparer.Ordinal)
            .SequenceEqual(info.UntranslatedAtStart.Order(StringComparer.Ordinal), StringComparer.Ordinal)) ||
        (info.LoadedPackId is not null && CompatibilityChanged);

    private void Select(string packId)
    {
        configuration.ActivePackId = packId;
        save();
    }

    // A first translation is turned on as soon as it is installed: nobody
    // installs a translation to leave it off.
    private void SelectIfNone(string packId)
    {
        if (!string.IsNullOrEmpty(configuration.ActivePackId))
            return;

        if (packs.TryGet(packId) is { IsSelectable: true })
            Select(packId);
    }
}
