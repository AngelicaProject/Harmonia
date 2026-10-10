using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Compatibility;
using Harmonia.Localization;

namespace Harmonia.UI;

// Plugins that find menus and buttons by their text: while one is installed,
// the text it looks for stays in the game's language. The player sees which
// plugins leave the translation incomplete and can turn each one off.
internal sealed partial class MainWindow
{
    private static readonly TimeSpan InstalledRefresh = TimeSpan.FromSeconds(5);

    private readonly Func<IReadOnlyList<CompatibilityProfile>> relevantProfiles;
    private IReadOnlyList<CompatibilityProfile> installedProfiles = [];
    private DateTime installedCheckedAt = DateTime.MinValue;

    // Profiles of the plugins installed now that keep text with their current
    // settings; plugins and their settings change while the game runs.
    private IReadOnlyList<CompatibilityProfile> InstalledProfiles
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now - installedCheckedAt >= InstalledRefresh)
            {
                installedCheckedAt = now;
                installedProfiles = relevantProfiles();
            }

            return installedProfiles;
        }
    }

    // The profiles the next game start would apply differ from this session's.
    private bool CompatibilityChanged =>
        !InstalledProfiles
            .Where(p => CompatibilityProfile.IsOn(p, configuration.DisabledCompatibility))
            .Select(static p => p.Plugin)
            .Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(info.CompatibilityAtStart.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    // Experimental: instead of keeping text for each plugin, plugins are
    // given the translation where they read the game's text.
    private void DrawSharedSheets()
    {
        using (Ui.BeginCard("shared_sheets"))
        {
            ImGui.TextDisabled(Lang.T("compat.shared_title"));
            Ui.Gap(2);
            var on = configuration.TranslatePluginData;
            if (ImGui.Checkbox(Lang.T("compat.shared"), ref on))
            {
                configuration.TranslatePluginData = on;
                save();
            }

            Ui.Hint(Lang.T("compat.shared_hint"));
            if (info.SharedSheets is { } shared)
            {
                Ui.Gap(2);
                Ui.Hint(shared.Error is not null
                    ? Lang.T("compat.shared_failed")
                    : shared.Completed.IsCompleted
                        ? Lang.T("compat.shared_done", shared.Sheets, shared.Cells, shared.Elapsed.TotalSeconds.ToString("F1"))
                        : Lang.T("compat.shared_running", shared.Sheets));
            }
        }

        Ui.Gap(4);
    }

    private void DrawCompatibility()
    {
        DrawSharedSheets();
        var profiles = InstalledProfiles;
        if (profiles.Count == 0)
            return;

        using (Ui.BeginCard("compatibility"))
        {
            ImGui.TextDisabled(Lang.T("compat.title"));
            Ui.Gap(2);
            Ui.Hint(Lang.T("compat.intro"));
            Ui.Gap(2);
            foreach (var profile in profiles)
            {
                var on = CompatibilityProfile.IsOn(profile, configuration.DisabledCompatibility);
                if (ImGui.Checkbox(Lang.T("compat.keep", profile.Name) + "##compat_" + profile.Plugin, ref on))
                {
                    configuration.DisabledCompatibility.RemoveAll(p => string.Equals(p, profile.Plugin, StringComparison.OrdinalIgnoreCase));
                    if (!on)
                        configuration.DisabledCompatibility.Add(profile.Plugin);
                    save();
                }

                Ui.Gap(2);
            }
        }

        Ui.Gap(4);
    }

    // On the Translations page, while profiles of this session keep text in
    // the game's language.
    private void DrawCompatibilityNotice()
    {
        if (info.CompatibilityAtStart.Count == 0)
            return;

        var names = info.CompatibilityAtStart
            .Select(plugin => CompatibilityProfile.All.FirstOrDefault(p => string.Equals(p.Plugin, plugin, StringComparison.OrdinalIgnoreCase))?.Name ?? plugin);
        using var card = Ui.BeginCard("notice_compatibility", Ui.Info);
        Ui.Icon(FontAwesomeIcon.PuzzlePiece, Ui.Info);
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            using (ImRaii.TextWrapPos(card.Right))
                ImGui.TextWrapped(Lang.T("notice.compatibility", string.Join(", ", names)));
            if (Ui.LinkButton(FontAwesomeIcon.SlidersH, Lang.T("notice.compatibility_open")))
                page = Page.Content;
        }
    }
}
