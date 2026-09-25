using System.Numerics;
using Dalamud.Bindings.ImGui;
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
        ImGui.PushTextWrapPos(420);
        ImGui.TextUnformatted(Lang.T("restart.message"));
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, 6));
        if (ImGui.Button(Lang.T("restart.disable")))
        {
            IsOpen = false;
            disablePlugin();
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.T("common.close")))
            IsOpen = false;
    }
}
