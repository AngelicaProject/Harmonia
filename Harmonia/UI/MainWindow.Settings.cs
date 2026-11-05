using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Game;
using Harmonia.Localization;

namespace Harmonia.UI;

internal sealed partial class MainWindow
{
    private static readonly int[] IntervalChoices = [15, 30, 60, 180, 360, 1440];

    private enum SettingsSection
    {
        General,
        Translation,
        Dictionary,
        Updates,
    }

    private SettingsSection settingsSection;

    private void DrawSettings()
    {
        Ui.Heading(headingFont, Lang.T("nav.settings"));
        Ui.Gap(4);
        if (!ImGui.BeginTabBar("##settings_sections", ImGuiTabBarFlags.FittingPolicyScroll))
            return;
        try
        {
            DrawSettingsTab(SettingsSection.General, Lang.T("settings.section_general"));
            DrawSettingsTab(SettingsSection.Translation, Lang.T("settings.tab_translation"));
            DrawSettingsTab(SettingsSection.Dictionary, Lang.T("nav.dictionary"));
            DrawSettingsTab(SettingsSection.Updates, Lang.T("settings.section_updates"));
        }
        finally
        {
            ImGui.EndTabBar();
        }
        if (settingsSection != SettingsSection.Dictionary)
            dictionaryKeyboard.CancelRecording();
    }

    private void DrawSettingsTab(SettingsSection section, string label)
    {
        var requested = selectDictionarySettings && section == SettingsSection.Dictionary ||
            openCoverageOptions && section == SettingsSection.Translation;
        if (!ImGui.BeginTabItem(label, requested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            return;
        try
        {
            settingsSection = section;
            if (section == SettingsSection.Dictionary)
                selectDictionarySettings = false;
            using var body = ImRaii.Child("##settings_" + section, System.Numerics.Vector2.Zero);
            if (!body)
                return;
            Ui.Gap(4);
            switch (section)
            {
                case SettingsSection.General:
                    DrawGeneralSettings();
                    break;
                case SettingsSection.Translation:
                    DrawTranslationSettings();
                    DrawCompatibility();
                    break;
                case SettingsSection.Dictionary:
                    DrawDictionarySettings();
                    break;
                case SettingsSection.Updates:
                    DrawUpdateSettings();
                    break;
            }
        }
        finally
        {
            ImGui.EndTabItem();
        }
    }

    private void DrawGeneralSettings()
    {
        using (var card = Ui.BeginCard("general"))
        {
            ImGui.TextDisabled(Lang.T("settings.section_general"));
            Ui.Gap(2);
            ImGui.SetNextItemWidth(Math.Min(260 * Ui.Scale, card.Width));
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
        }
    }

    private void DrawUpdateSettings()
    {
        using (var card = Ui.BeginCard("updates"))
        {
            ImGui.TextDisabled(Lang.T("settings.section_updates"));
            Ui.Gap(2);
            var auto = configuration.AutoDownloadUpdates;
            if (ImGui.Checkbox(Lang.T("settings.auto_install"), ref auto))
            {
                configuration.AutoDownloadUpdates = auto;
                save();
            }

            Hinted(Lang.T("settings.auto_install_hint"));
            Ui.Gap(4);

            var testing = configuration.FollowTestingChannel;
            if (ImGui.Checkbox(Lang.T("settings.follow_testing"), ref testing))
            {
                configuration.FollowTestingChannel = testing;
                save();
            }

            Hinted(Lang.T("settings.follow_testing_hint"));
            Ui.Gap(4);

            ImGui.SetNextItemWidth(Math.Min(260 * Ui.Scale, card.Width));
            using var combo = ImRaii.Combo(Lang.T("settings.interval"), IntervalText(configuration.UpdateCheckIntervalMinutes));
            if (combo)
            {
                foreach (var minutes in IntervalChoices)
                {
                    if (ImGui.Selectable(IntervalText(minutes), minutes == configuration.UpdateCheckIntervalMinutes))
                    {
                        configuration.UpdateCheckIntervalMinutes = minutes;
                        save();
                    }
                }
            }
        }
    }

    private void DrawDictionarySettings()
    {
        Ui.Hint(Lang.T("dictionary.settings_hint", NameLookup.FindCommand));
        Ui.Gap(4);
        using (Ui.BeginCard("dictionary_shortcut"))
        {
            ImGui.TextDisabled(Lang.T("dictionary.search_behavior"));
            Ui.Gap(2);
            var enabled = configuration.DictionaryShortcutEnabled;
            if (ImGui.Checkbox(Lang.T("dictionary.shortcut_enabled"), ref enabled))
            {
                configuration.DictionaryShortcutEnabled = enabled;
                if (!enabled)
                    dictionaryKeyboard.CancelRecording();
                save();
            }
            using (ImRaii.Disabled(!enabled))
            {
                ImGui.TextUnformatted(dictionaryKeyboard.ShortcutLabel);
                ImGui.SameLine();
                if (dictionaryKeyboard.IsRecording)
                {
                    Ui.Hint(Lang.T("dictionary.shortcut_recording"));
                    if (ImGui.Button(Lang.T("dictionary.shortcut_cancel")))
                        dictionaryKeyboard.CancelRecording();
                }
                else if (ImGui.Button(Lang.T("dictionary.shortcut_record")))
                    dictionaryKeyboard.StartRecording();
            }
            Ui.Gap(4);
            var tabFocus = configuration.DictionaryTabFocusSearch;
            if (ImGui.Checkbox(Lang.T("dictionary.tab_focus"), ref tabFocus))
            {
                configuration.DictionaryTabFocusSearch = tabFocus;
                save();
            }
            Ui.Tooltip(Lang.T("dictionary.tab_focus_hint"));
            var clear = configuration.DictionaryClearSearchOnOpen;
            if (ImGui.Checkbox(Lang.T("dictionary.clear_search"), ref clear))
            {
                configuration.DictionaryClearSearchOnOpen = clear;
                if (clear)
                    configuration.DictionaryQuery = string.Empty;
                save();
            }
            Ui.Tooltip(Lang.T("dictionary.clear_search_hint"));
        }
        Ui.Gap(4);
        using (Ui.BeginCard("dictionary_position"))
        {
            ImGui.TextDisabled(Lang.T("dictionary.position"));
            var custom = configuration.DictionaryCustomPosition;
            if (ImGui.RadioButton(Lang.T("dictionary.position_default"), !custom))
            {
                configuration.DictionaryCustomPosition = false;
                save();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton(Lang.T("dictionary.position_custom"), custom))
            {
                configuration.DictionaryCustomPosition = true;
                save();
            }
            using (ImRaii.Disabled(info.Dictionary is null))
            {
                if (configuration.DictionaryCustomPosition && ImGui.Button(Lang.T("dictionary.position_configure")))
                    arrangeDictionary();
            }
        }
        Ui.Gap(4);
        using (Ui.BeginCard("dictionary_languages"))
        {
            ImGui.TextDisabled(Lang.T("dictionary.languages"));
            Ui.Hint(Lang.T("dictionary.languages_hint"));
            foreach (var language in GameLanguages.All)
            {
                var tag = GameLanguages.Tag(language);
                var client = info.Dictionary?.ClientLanguage == language;
                var shown = client || configuration.DictionaryLanguages.Contains(tag);
                using var disabled = ImRaii.Disabled(client);
                if (!ImGui.Checkbox(Ui.LanguageName(tag) + "##dictionary_" + tag, ref shown))
                    continue;
                configuration.DictionaryLanguages.Remove(tag);
                if (shown)
                    configuration.DictionaryLanguages.Add(tag);
                save();
                info.Dictionary?.Invalidate();
            }
        }
    }

    // A hint under a checkbox, aligned with the checkbox label.
    private static void Hinted(string text)
    {
        using var indent = ImRaii.PushIndent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X, false);
        Ui.Hint(text);
    }

    private static string IntervalText(int minutes) => minutes switch
    {
        1440 => Lang.T("settings.every_day"),
        60 => Lang.T("settings.every_hour"),
        _ when minutes % 60 == 0 => Lang.T("settings.every_hours", minutes / 60),
        _ => Lang.T("settings.every_minutes", minutes),
    };
}
