using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Harmonia.Localization;

namespace Harmonia.UI;

// Shown when the plugin is loaded again in the game session it already ran
// in: rows created meanwhile were not translated, so only a game restart gives
// a consistent state. The engine stays off until then.
internal sealed class RestartWindow : Window
{
    private readonly Action disablePlugin;

    public RestartWindow(Action disablePlugin)
        : base(Lang.T("restart.title") + "###harmonia_restart", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.disablePlugin = disablePlugin;
    }

    public override void Draw()
    {
        using var wrap = ImRaii.TextWrapPos(420 * Ui.Scale);
        Ui.Icon(FontAwesomeIcon.RedoAlt, Ui.Pending);
        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.T("restart.heading"));
        Ui.Gap(2);
        ImGui.TextWrapped(Lang.T("restart.message"));
        Ui.Gap(8);

        if (Ui.PrimaryButton(FontAwesomeIcon.Check, Lang.T("restart.ok")))
            IsOpen = false;

        ImGui.SameLine();
        if (Ui.Button(FontAwesomeIcon.PowerOff, Lang.T("restart.disable")))
        {
            IsOpen = false;
            disablePlugin();
        }
    }
}
