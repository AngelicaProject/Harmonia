using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Harmonia.Feeds;
using Harmonia.Game;
using Harmonia.Localization;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;
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
    bool ApplyUnreviewed,
    string PluginVersion);

internal sealed class MainWindow : Window, IDisposable
{
    private const string TrustPopupId = "###harmonia_trust";

    private readonly Configuration configuration;
    private readonly Action save;
    private readonly TranslationPackStore packs;
    private readonly PackInstaller installer;
    private readonly FeedUpdateService feeds;
    private readonly FeedUpdateState feedState;
    private readonly SessionState session;
    private readonly SessionInfo info;
    private readonly FileDialogManager fileDialog = new();

    private Task<StagedPack>? staging;
    private StagedPack? pendingImport;
    private bool openTrustPopup;
    private string? importMessage;
    private bool importFailed;
    private string newFeedUrl = string.Empty;
    private string? feedError;

    public MainWindow(
        Configuration configuration,
        Action save,
        TranslationPackStore packs,
        PackInstaller installer,
        FeedUpdateService feeds,
        FeedUpdateState feedState,
        SessionState session,
        SessionInfo info)
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
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(760, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        pendingImport?.Dispose();
        pendingImport = null;
    }

    public override void Draw()
    {
        DrawRestartNotice();

        using (var tabs = ImRaii.TabBar("##harmonia_tabs"))
        {
            if (tabs)
            {
                Tab(Lang.T("tab.status"), DrawStatus);
                Tab(Lang.T("tab.packs"), DrawPacks);
                Tab(Lang.T("tab.updates"), DrawUpdates);
                Tab(Lang.T("tab.settings"), DrawSettings);
            }
        }

        PollStaging();
        DrawTrustPopup();
        fileDialog.Draw();
    }

    private static void Tab(string label, Action draw)
    {
        using var tab = ImRaii.TabItem(label);
        if (!tab)
            return;

        using var child = ImRaii.Child("##tab_body", Vector2.Zero);
        if (child)
            draw();
    }

    private bool SettingsDiffer =>
        !string.Equals(configuration.ActivePackId ?? string.Empty, info.LoadedPackId ?? string.Empty, StringComparison.Ordinal) ||
        (info.LoadedPackId is not null && configuration.ApplyUnreviewedTranslations != info.ApplyUnreviewed);

    private void DrawRestartNotice()
    {
        if (!session.IsRestartRequired && !SettingsDiffer)
            return;

        Ui.Colored(ImGuiColors.DalamudOrange, Lang.T("common.restart_to_apply"));
        Ui.Gap();
    }

    // ---- Status ----

    private void DrawStatus()
    {
        var runtime = info.Runtime;
        if (runtime is null)
        {
            ImGui.TextWrapped(info.PackError is null
                ? Lang.T("status.no_pack")
                : Lang.T("status.pack_failed", info.PackError));
        }
        else
        {
            ImGui.TextWrapped(Lang.T("status.active_pack", runtime.Info.Title, runtime.Info.PackId));
        }

        Ui.Gap();
        Ui.Row(Lang.T("status.game_version"), packs.CurrentGameVersion ?? Lang.T("common.unknown"));
        Ui.Row(Lang.T("status.client_language"), packs.ClientLanguage ?? Lang.T("common.unknown"));
        Ui.Row(Lang.T("status.engine"), EngineState());

        if (runtime is null)
            return;

        var totals = runtime.GetTotals();
        Ui.Gap();
        Ui.Row(Lang.T("status.applied"), totals.Applied.ToString("N0", CultureInfo.CurrentCulture));
        Ui.Row(Lang.T("status.source_changed"), totals.SourceChanged.ToString("N0", CultureInfo.CurrentCulture));
        if (totals.MatchRate is { } rate)
            Ui.Row(Lang.T("status.match_rate"), rate.ToString("P1", CultureInfo.CurrentCulture));
        if (totals.Unreviewed > 0)
            Ui.Row(Lang.T("status.unreviewed_skipped"), totals.Unreviewed.ToString("N0", CultureInfo.CurrentCulture));
        if (totals.LayoutMismatchSheets > 0)
            Ui.Colored(ImGuiColors.DalamudYellow, Lang.T("status.layout_mismatch", totals.LayoutMismatchSheets));
        Ui.Hint(Lang.T("status.counts_hint"));

        Ui.Gap();
        if (ImGui.CollapsingHeader(Lang.T("diagnostics.sheets")))
            DrawSheetTable(runtime);

        Ui.Gap();
        if (ImGui.Button(Lang.T("status.copy_diagnostics")))
            ImGui.SetClipboardText(DiagnosticsText());
    }

    private string EngineState()
    {
        if (info.Runtime is null)
            return Lang.T("status.engine_idle");
        if (info.Hooks is null)
            return Lang.T("status.engine_failed", info.HookError ?? "?");
        return info.Hooks.Errors == 0
            ? Lang.T("status.engine_running")
            : Lang.T("status.engine_errors", info.Hooks.Errors);
    }

    private static void DrawSheetTable(TranslationRuntime runtime)
    {
        var sheets = runtime.GetSheetStats();
        if (sheets.Count == 0)
        {
            Ui.Hint(Lang.T("diagnostics.no_sheets"));
            return;
        }

        using var table = ImRaii.Table("##sheets", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;

        ImGui.TableSetupColumn(Lang.T("diagnostics.sheet"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.applied"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.source_changed"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.unreviewed"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.rows"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.problems"));
        ImGui.TableHeadersRow();
        foreach (var sheet in sheets)
        {
            ImGui.TableNextRow();
            Cell(sheet.SheetName);
            Cell(sheet.Applied.ToString(CultureInfo.InvariantCulture));
            Cell(sheet.SourceChanged.ToString(CultureInfo.InvariantCulture));
            Cell(sheet.Unreviewed.ToString(CultureInfo.InvariantCulture));
            Cell(sheet.RowsRebuilt.ToString(CultureInfo.InvariantCulture));
            Cell(sheet.LayoutMismatch
                ? Lang.T("diagnostics.layout_mismatch")
                : sheet.RowsUnexpected > 0 ? Lang.T("diagnostics.unexpected_rows", sheet.RowsUnexpected) : string.Empty);
        }
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private string DiagnosticsText()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Harmonia {info.PluginVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Game version: {packs.CurrentGameVersion ?? "?"}, client language: {packs.ClientLanguage ?? "?"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Selected pack: {configuration.ActivePackId}, loaded: {info.LoadedPackId ?? "-"}");
        if (info.PackError is not null)
            text.AppendLine(CultureInfo.InvariantCulture, $"Pack error: {info.PackError}");
        if (info.HookError is not null)
            text.AppendLine(CultureInfo.InvariantCulture, $"Hook error: {info.HookError}");
        if (info.Hooks is { } hooks)
            text.AppendLine(CultureInfo.InvariantCulture, $"StoreRow hooks: {hooks.HashTableAddress:X}, {hooks.RingBufferAddress:X}; errors {hooks.Errors}");

        if (info.Runtime is { } runtime)
        {
            var t = runtime.GetTotals();
            text.AppendLine(CultureInfo.InvariantCulture, $"Pack: {runtime.Info.PackId} ({runtime.Info.Sheets} sheets, {runtime.Info.Cells} cells)");
            text.AppendLine(CultureInfo.InvariantCulture, $"Applied {t.Applied}, source changed {t.SourceChanged}, unreviewed {t.Unreviewed}, rows {t.RowsRebuilt}, unexpected rows {t.RowsUnexpected}, layout mismatch sheets {t.LayoutMismatchSheets}");
            foreach (var s in runtime.GetSheetStats())
                text.AppendLine(CultureInfo.InvariantCulture, $"  {s.SheetName}: applied {s.Applied}, changed {s.SourceChanged}, unreviewed {s.Unreviewed}, rows {s.RowsRebuilt}, unexpected {s.RowsUnexpected}, layout mismatch {s.LayoutMismatch}");
        }

        return text.ToString();
    }

    // ---- Packs ----

    private void DrawPacks()
    {
        Ui.Hint(Lang.T("packs.hint"));
        Ui.Gap();

        if (packs.Packs.Count == 0)
            Ui.Hint(Lang.T("packs.empty"));

        foreach (var pack in packs.Packs)
            DrawPack(pack);

        Ui.Gap();
        using (ImRaii.Disabled(staging is not null))
        {
            if (ImGui.Button(Lang.T("packs.import")))
                OpenImportDialog();
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.T("packs.open_folder")))
            Ui.OpenFolder(packs.PacksDir);

        if (staging is not null)
            Ui.Hint(Lang.T("packs.importing"));
        if (importMessage is not null)
            Ui.Colored(importFailed ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen, importMessage);
    }

    private void DrawPack(TranslationPack pack)
    {
        using var id = ImRaii.PushId(pack.Id);
        var selected = string.Equals(pack.Id, configuration.ActivePackId, StringComparison.Ordinal);
        var header = pack.DisplayName + (selected ? "  " + Lang.T("packs.selected_mark") : string.Empty) + "###pack";
        if (!ImGui.CollapsingHeader(header, selected ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            return;

        using var indent = ImRaii.PushIndent();
        Ui.Row(Lang.T("packs.id"), pack.Id);
        if (pack.Manifest is not { } manifest)
        {
            Ui.Colored(ImGuiColors.DalamudRed, Lang.T("packs.invalid", pack.InvalidReason ?? "?"));
            return;
        }

        Ui.Row(Lang.T("packs.publisher"), manifest.PublisherName);
        Ui.Row(Lang.T("packs.release"), manifest.Channel == "testing"
            ? Lang.T("packs.release_testing", manifest.Version, manifest.Sequence)
            : Lang.T("packs.release_value", manifest.Version, manifest.Sequence));
        Ui.Row(Lang.T("packs.languages"), manifest.SourceLanguage + " → " + manifest.TargetLanguage);
        Ui.Row(Lang.T("packs.game_version"), manifest.SourceGameVersion);
        Ui.Row(Lang.T("packs.content"), manifest.ContentPolicy == HpkContentPolicy.Reviewed
            ? Lang.T("packs.content_reviewed")
            : Lang.T("packs.content_all"));
        Ui.Row(Lang.T("packs.signature"), pack.PublisherFingerprint is { } fingerprint
            ? HpkSignature.ShortFingerprint(fingerprint)
            : Lang.T("packs.unsigned"));

        if (!pack.LanguageCompatible)
            Ui.Colored(ImGuiColors.DalamudYellow, Lang.T("packs.requires_language", manifest.SourceLanguage, packs.ClientLanguage ?? "?"));
        if (!pack.PluginCompatible)
            Ui.Colored(ImGuiColors.DalamudYellow, Lang.T("packs.requires_plugin", manifest.MinHarmonia));
        if (pack.GameVersionMatches == false)
            Ui.Hint(Lang.T("packs.game_drift", packs.CurrentGameVersion ?? "?"));

        if (feedState.UpdateForPack(pack.Id) is { } update)
            Ui.Colored(ImGuiColors.HealerGreen, Lang.T("packs.update_available", update.RemoteVersion ?? "?"));

        if (selected)
        {
            if (ImGui.Button(Lang.T("packs.deselect")))
                Select(string.Empty);
        }
        else if (pack.IsSelectable && ImGui.Button(Lang.T("packs.select")))
        {
            Select(pack.Id);
        }

        Ui.Gap();
    }

    private void Select(string packId)
    {
        configuration.ActivePackId = packId;
        save();
    }

    private void OpenImportDialog()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        fileDialog.OpenFileDialog(
            Lang.T("packs.import_title"),
            "Harmonia pack{.hpk,.br}",
            (ok, paths) =>
            {
                if (ok && paths.Count > 0)
                    StartStaging(paths[0]);
            },
            1,
            Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            false);
    }

    // Verification hashes and parses the whole pack, so it runs off the UI thread.
    private void StartStaging(string path)
    {
        if (staging is not null)
            return;

        importMessage = null;
        staging = Task.Run(() => installer.Stage(path));
    }

    private void PollStaging()
    {
        if (staging is not { IsCompleted: true } task)
            return;

        staging = null;
        if (!task.IsCompletedSuccessfully)
        {
            Report(Lang.T("packs.import_failed", task.Exception?.GetBaseException().Message ?? "?"), failed: true);
            return;
        }

        var staged = task.Result;
        if (PublisherTrust.InstallsWithoutConfirmation(staged.Trust) && !staged.IsDowngrade)
        {
            Commit(staged, trustConfirmed: false);
            return;
        }

        pendingImport?.Dispose();
        pendingImport = staged;
        openTrustPopup = true;
    }

    private void Commit(StagedPack staged, bool trustConfirmed)
    {
        try
        {
            installer.Commit(staged, trustConfirmed);
            var packId = staged.Manifest.PackId;
            if (string.Equals(packId, info.LoadedPackId, StringComparison.Ordinal))
                session.IsRestartRequired = true;
            Report(Lang.T("packs.import_done", packs.TryGet(packId)?.DisplayName ?? packId), failed: false);
        }
        catch (Exception ex)
        {
            Report(Lang.T("packs.import_failed", ex.Message), failed: true);
        }
        finally
        {
            staged.Dispose();
        }
    }

    private void Report(string message, bool failed)
    {
        importMessage = message;
        importFailed = failed;
    }

    private void DrawTrustPopup()
    {
        if (openTrustPopup)
        {
            ImGui.OpenPopup(Lang.T("trust.title") + TrustPopupId);
            openTrustPopup = false;
        }

        ImGui.SetNextWindowSize(new Vector2(460 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Appearing);
        using var popup = ImRaii.PopupModal(Lang.T("trust.title") + TrustPopupId, ImGuiWindowFlags.NoResize);
        if (!popup)
            return;

        if (pendingImport is not { } staged)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        var name = staged.Manifest.Title;
        var fingerprint = staged.Fingerprint is null ? string.Empty : HpkSignature.ShortFingerprint(staged.Fingerprint);
        configuration.PinnedPublisherKeys.TryGetValue(staged.Manifest.PackId, out var pinned);
        var message = staged.Trust switch
        {
            PublisherTrustState.FirstUse => Lang.T("trust.first_use", name, fingerprint),
            PublisherTrustState.KeyChanged => Lang.T("trust.key_changed", name, fingerprint,
                pinned is null ? "?" : HpkSignature.ShortFingerprint(pinned)),
            PublisherTrustState.Unsigned => Lang.T("trust.unsigned", name),
            _ => null,
        };
        if (message is not null)
            ImGui.TextWrapped(message);
        if (staged.IsDowngrade)
            ImGui.TextWrapped(Lang.T("trust.downgrade", staged.Manifest.Sequence, staged.InstalledSequence ?? 0));

        Ui.Gap();
        if (ImGui.Button(Lang.T("trust.install")))
        {
            pendingImport = null;
            ImGui.CloseCurrentPopup();
            Commit(staged, trustConfirmed: true);
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.T("common.cancel")))
        {
            pendingImport = null;
            staged.Dispose();
            ImGui.CloseCurrentPopup();
        }
    }

    // ---- Updates ----

    private void DrawUpdates()
    {
        Ui.Hint(Lang.T("updates.hint"));
        Ui.Gap();

        ImGui.SetNextItemWidth(Math.Max(200 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - (110 * ImGuiHelpers.GlobalScale)));
        ImGui.InputTextWithHint("##feed_url", Lang.T("updates.url_hint"), ref newFeedUrl, 1024);
        ImGui.SameLine();
        if (ImGui.Button(Lang.T("updates.add")))
        {
            if (feeds.AddFeed(newFeedUrl, out var errorKey))
            {
                newFeedUrl = string.Empty;
                feedError = null;
            }
            else
            {
                feedError = Lang.T(errorKey);
            }
        }

        if (feedError is not null)
            Ui.Colored(ImGuiColors.DalamudRed, feedError);

        Ui.Gap();
        foreach (var url in configuration.UpdateFeedUrls.ToArray())
            DrawFeed(url);

        Ui.Gap();
        using (ImRaii.Disabled(feedState.Checking || configuration.UpdateFeedUrls.Count == 0))
        {
            if (ImGui.Button(Lang.T("updates.check_now")))
                _ = feeds.CheckNowAsync();
        }

        ImGui.SameLine();
        Ui.Hint(feedState.Checking
            ? Lang.T("updates.checking")
            : feedState.LastCheck is { } last ? Lang.T("updates.last_check", last.ToString("g", CultureInfo.CurrentCulture)) : string.Empty);

        Ui.Gap();
        var auto = configuration.AutoDownloadUpdates;
        if (ImGui.Checkbox(Lang.T("updates.auto_install"), ref auto))
        {
            configuration.AutoDownloadUpdates = auto;
            save();
        }

        var testing = configuration.FollowTestingChannel;
        if (ImGui.Checkbox(Lang.T("updates.follow_testing"), ref testing))
        {
            configuration.FollowTestingChannel = testing;
            save();
        }

        var interval = configuration.UpdateCheckIntervalMinutes;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt(Lang.T("updates.interval"), ref interval, 5, 1440))
        {
            configuration.UpdateCheckIntervalMinutes = interval;
            save();
        }
    }

    private void DrawFeed(string url)
    {
        using var id = ImRaii.PushId(url);
        var status = feedState.Feeds.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
        ImGui.TextUnformatted(status?.Title ?? status?.PackId ?? url);
        Ui.Hint(url);
        if (status is not null)
        {
            Ui.Row(Lang.T("updates.status"), StatusText(status));
            if (status.InstalledVersion is not null)
                Ui.Row(Lang.T("updates.installed"), status.InstalledVersion);
            if (status.RemoteVersion is not null)
                Ui.Row(Lang.T("updates.remote"), status.RemoteVersion);
            if (status.Changelog is { Length: > 0 } changelog)
                Ui.Hint(changelog);
            if (status.Status == FeedPackStatus.Error && status.Error is not null)
                Ui.Colored(ImGuiColors.DalamudRed, status.Error);

            if (status.Status == FeedPackStatus.UpdateAvailable && status.TrustFingerprint is null && ImGui.Button(Lang.T("updates.install")))
                _ = feeds.InstallUpdateAsync(url);

            if (status.Status is FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust && status.TrustFingerprint is { } fingerprint)
            {
                Ui.Colored(ImGuiColors.DalamudYellow, Lang.T("updates.trust_prompt", HpkSignature.ShortFingerprint(fingerprint)));
                if (ImGui.Button(Lang.T("updates.trust_install")))
                    _ = feeds.InstallUpdateAsync(url, fingerprint);
            }

            if (status.Status == FeedPackStatus.Downloading)
                ImGui.ProgressBar(status.Progress, new Vector2(240 * ImGuiHelpers.GlobalScale, 0));
        }

        if (ImGui.SmallButton(Lang.T("updates.remove")))
            feeds.RemoveFeed(url);

        ImGui.Separator();
    }

    private static string StatusText(FeedStatus status) => status.Status switch
    {
        FeedPackStatus.Checking => Lang.T("updates.status_checking"),
        FeedPackStatus.UpToDate => Lang.T("updates.status_uptodate"),
        FeedPackStatus.UpdateAvailable => Lang.T("updates.status_available"),
        FeedPackStatus.NeedsTrust => Lang.T("updates.status_needs_trust"),
        FeedPackStatus.Downloading => Lang.T("updates.status_downloading"),
        FeedPackStatus.Incompatible => Lang.T("updates.status_incompatible"),
        FeedPackStatus.Error => Lang.T("updates.status_error"),
        _ => Lang.T("updates.status_unknown"),
    };

    // ---- Settings ----

    private void DrawSettings()
    {
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo(Lang.T("settings.language"), Lang.GetDisplayName(Lang.CurrentLanguage)))
        {
            if (combo)
            {
                foreach (var code in Lang.AvailableLanguages)
                {
                    var current = string.Equals(code, Lang.CurrentLanguage, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(Lang.GetDisplayName(code), current) && !current && Lang.SetLanguage(code))
                    {
                        configuration.Language = code;
                        save();
                    }
                }
            }
        }

        Ui.Gap();
        var unreviewed = configuration.ApplyUnreviewedTranslations;
        if (ImGui.Checkbox(Lang.T("settings.apply_unreviewed"), ref unreviewed))
        {
            configuration.ApplyUnreviewedTranslations = unreviewed;
            save();
        }

        Ui.Hint(Lang.T("settings.apply_unreviewed_hint"));
    }
}
