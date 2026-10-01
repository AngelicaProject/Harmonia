using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Localization;

namespace Harmonia.UI;

internal sealed partial class MainWindow
{
    private static readonly int[] IntervalChoices = [15, 30, 60, 180, 360, 1440];

    private void DrawSettings()
    {
        Ui.Heading(headingFont, Lang.T("nav.settings"));
        Ui.Gap(4);

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
