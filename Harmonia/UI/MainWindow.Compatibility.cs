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
    private bool openCoverageOptions;

    private static void DrawCoverageHelp(string text)
    {
        ImGui.SameLine();
        Ui.Icon(FontAwesomeIcon.QuestionCircle, Ui.Muted);
        Ui.Tooltip(text);
    }

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

    private void DrawTranslationSettings()
    {
        using (Ui.BeginCard("shared_sheets"))
        {
            ImGui.TextDisabled(Lang.T("settings.section_translation"));
            if (openCoverageOptions)
                ImGui.SetScrollHereY(0);
            Ui.Hint(Lang.T("settings.translation_restart"));
            Ui.Gap(2);
            var on = configuration.TranslatePluginData;
            if (ImGui.Checkbox(Lang.T("compat.shared"), ref on))
            {
                configuration.TranslatePluginData = on;
                save();
            }

            var sharedHelp = Lang.T("compat.shared_hint");
            if (info.SharedSheets is { } shared)
            {
                sharedHelp += "\n\n" + (shared.Error is not null
                    ? Lang.T("compat.shared_failed")
                    : shared.Completed.IsCompleted
                        ? Lang.T("compat.shared_done", shared.Sheets, shared.Cells, shared.Elapsed.TotalSeconds.ToString("F1"))
                        : Lang.T("compat.shared_running", shared.Sheets));
            }
            DrawCoverageHelp(sharedHelp);
            Hinted(Lang.T("settings.plugin_text_hint"));
            if (info.SharedSheets?.Error is not null)
                Ui.Colored(Ui.Warn, Lang.T("compat.shared_failed"));

            Ui.Gap(4);
            var say = configuration.OriginalSayPhrases;
            if (ImGui.Checkbox(Lang.T("say.option"), ref say))
            {
                configuration.OriginalSayPhrases = say;
                save();
            }

            var sayHelp = Lang.T("say.hint");
            if (info.SayError is not null)
            {
                sayHelp += "\n\n" + Lang.T("say.failed");
            }
            else if (info.SayPhrases is { } phrases && info.SayHook is { } sayHook)
            {
                sayHelp += "\n\n" + Lang.T("say.status", phrases.Quests.Count, sayHook.Replaced);
                if (phrases.Skipped.Count != 0)
                    sayHelp += "\n" + Lang.T("say.skipped", phrases.Skipped.Count);
            }
            DrawCoverageHelp(sayHelp);
            Hinted(Lang.T("settings.say_hint"));
            if (info.SayError is not null)
                Ui.Colored(Ui.Warn, Lang.T("say.failed"));
        }

        Ui.Gap(4);
    }

    private void DrawCompatibility()
    {
        var profiles = InstalledProfiles;
        if (profiles.Count == 0)
        {
            openCoverageOptions = false;
            return;
        }

        if (openCoverageOptions)
            ImGui.SetNextItemOpen(true);
        openCoverageOptions = false;
        if (!ImGui.CollapsingHeader(Lang.T("compat.title") + "##compatibility"))
            return;

        using (Ui.BeginCard("compatibility"))
        {
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
            {
                page = Page.Settings;
                openCoverageOptions = true;
            }
        }
    }
}
