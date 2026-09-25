using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Harmonia.UI;

internal static class Ui
{
    public static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void Colored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void Row(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(170 * ImGuiHelpers.GlobalScale);
        ImGui.TextWrapped(value);
    }

    public static void Gap(float height = 6) => ImGui.Dummy(new Vector2(0, height * ImGuiHelpers.GlobalScale));

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
        }
    }
}
