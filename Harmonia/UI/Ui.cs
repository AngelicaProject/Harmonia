using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Harmonia.UI;

internal static class Ui
{
    public static readonly Vector4 Good = ImGuiColors.HealerGreen;
    public static readonly Vector4 Pending = ImGuiColors.DalamudOrange;
    public static readonly Vector4 Bad = ImGuiColors.DalamudRed;
    public static readonly Vector4 Warn = ImGuiColors.DalamudYellow;
    public static readonly Vector4 Info = ImGuiColors.TankBlue;
    public static readonly Vector4 Muted = ImGuiColors.DalamudGrey;

    public static float Scale => ImGuiHelpers.GlobalScale;

    public static void Hint(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.TextWrapped(text);
    }

    public static void Colored(Vector4 color, string text)
    {
        using var push = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
    }

    public static void Gap(float height = 6) => ImGui.Dummy(new Vector2(0, height * Scale));

    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var push = ImRaii.PushColor(ImGuiCol.Text, color ?? Vector4.Zero, color is not null);
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(icon.ToIconString());
    }

    // An icon followed by wrapped text, both in one color.
    public static void IconText(FontAwesomeIcon icon, Vector4 color, string text)
    {
        Icon(icon, color);
        ImGui.SameLine();
        Colored(color, text);
    }

    public static void Row(string label, string value, float labelWidth = 170)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(labelWidth * Scale);
        ImGui.TextWrapped(value);
    }

    public static void Badge(string text, Vector4 color)
    {
        var pad = Snap(new Vector2(5, 0) * Scale);
        var size = ImGui.CalcTextSize(text) + (pad * 2);
        var pos = Snap(ImGui.GetCursorScreenPos());
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(color with { W = 0.2f }), size.Y / 2);
        drawList.AddText(pos + pad, ImGui.GetColorU32(color), text);
        ImGui.Dummy(size);
    }

    // The one action a screen suggests. Blue reads as "go ahead" in every
    // theme; the themes' own accents are often red, which reads as danger.
    private static readonly Vector4 PrimaryColor = new(0.20f, 0.42f, 0.72f, 1f);
    private static readonly Vector4 PrimaryHovered = new(0.26f, 0.50f, 0.84f, 1f);
    private static readonly Vector4 SwitchOn = new(0.24f, 0.62f, 0.36f, 1f);
    private static readonly Vector4 SwitchOff = new(0.38f, 0.38f, 0.40f, 1f);

    public static bool PrimaryButton(FontAwesomeIcon icon, string text) =>
        ImGuiComponents.IconButtonWithText(icon, text, PrimaryColor, PrimaryHovered, PrimaryHovered);

    // An on/off switch: green and knob on the right when on. Returns true
    // when clicked; the caller flips the value.
    public static bool Switch(string id, bool on)
    {
        var frame = ImGui.GetFrameHeight();
        var height = frame * 0.78f;
        var width = height * 1.85f;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, frame));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var top = pos + new Vector2(0, (frame - height) / 2);
        var track = on ? SwitchOn : SwitchOff;
        if (hovered)
            track = Vector4.Min(track * 1.15f, Vector4.One) with { W = 1f };
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(top, top + new Vector2(width, height), ImGui.GetColorU32(track), height / 2);
        var radius = (height / 2) - (2.5f * Scale);
        var x = on ? top.X + width - (height / 2) : top.X + (height / 2);
        drawList.AddCircleFilled(new Vector2(x, top.Y + (height / 2)), radius, ImGui.GetColorU32(Vector4.One));
        return clicked;
    }

    // A checkbox that can also show "some of it" with a filled square.
    // Returns true when clicked; the caller decides the new value.
    public static bool Checkbox(string label, bool on, bool mixed)
    {
        var value = on && !mixed;
        var clicked = ImGui.Checkbox(label, ref value);
        if (mixed)
        {
            var min = ImGui.GetItemRectMin();
            var size = ImGui.GetFrameHeight();
            var pad = MathF.Max(1, MathF.Floor(size / 3.6f));
            ImGui.GetWindowDrawList().AddRectFilled(
                Snap(min + new Vector2(pad)), Snap(min + new Vector2(size - pad)), ImGui.GetColorU32(ImGuiCol.CheckMark), ImGui.GetStyle().FrameRounding);
        }

        return clicked;
    }

    public static bool Button(FontAwesomeIcon icon, string text) => ImGuiComponents.IconButtonWithText(icon, text);

    public static float ButtonWidth(FontAwesomeIcon icon, string text) => ImGuiComponents.GetIconButtonWithTextWidth(icon, text);

    // A flat button that looks like a link, for secondary actions.
    public static bool LinkButton(FontAwesomeIcon icon, string text)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        return ImGuiComponents.IconButtonWithText(icon, text);
    }

    // A link-colored action placed on a text row, as tall as the text.
    public static bool InlineLink(FontAwesomeIcon icon, string text)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding with { Y = 0 });
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero).Push(ImGuiCol.Text, Info);
        return ImGuiComponents.IconButtonWithText(icon, text);
    }

    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered())
            return;

        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(360 * Scale);
        ImGui.TextUnformatted(text);
    }

    // Continues the current row at window-local x (never to the left of the
    // previous item). SameLine's own offset is relative to the enclosing
    // group, which inside a card is not the window.
    public static void SameLineAt(float x)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Round(Math.Max(ImGui.GetCursorPosX(), x)));
    }

    // Text drawn at a fractional pixel position is resampled and looks
    // blurry, so every position this class computes is rounded.
    public static Vector2 Snap(Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));

    // Places the next item so that `width` pixels end at `right` (window-local
    // x): on the current row when it already has items, else on a new row.
    public static void AlignRight(float width, float right, bool sameRow)
    {
        if (sameRow)
            SameLineAt(right - width);
        else
            ImGui.SetCursorPosX(MathF.Round(Math.Max(ImGui.GetCursorPosX(), right - width)));
    }

    // A horizontal line from the cursor to `right` (window-local x).
    public static void Divider(float right)
    {
        var pos = ImGui.GetCursorScreenPos();
        var end = ImGui.GetWindowPos().X - ImGui.GetScrollX() + right;
        ImGui.GetWindowDrawList().AddLine(pos, new Vector2(end, pos.Y), ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(0, 1));
    }

    // A one-line message in a card with a colored edge. Returns true when the
    // player dismissed it.
    public static bool Notice(string id, FontAwesomeIcon icon, Vector4 color, string text, bool dismissible = false)
    {
        using var card = BeginCard("notice_" + id, color);
        var closeWidth = dismissible ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X : 0;
        Icon(icon, color);
        ImGui.SameLine();
        using (ImRaii.TextWrapPos(card.Right - closeWidth))
            ImGui.TextWrapped(text);

        if (!dismissible)
            return false;

        SameLineAt(card.Right - ImGui.GetFrameHeight());
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero);
        return ImGuiComponents.IconButton("##close", FontAwesomeIcon.Times);
    }

    public static void Heading(Dalamud.Interface.ManagedFontAtlas.IFontHandle font, string text)
    {
        using (font.Push())
            ImGui.TextUnformatted(text);
    }

    // "English", "Русский": the language's own name, as players know it.
    public static string LanguageName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "?";

        try
        {
            var culture = CultureInfo.GetCultureInfo(tag.Trim());
            var name = culture.NativeName;
            if (string.IsNullOrEmpty(name) || culture.ThreeLetterISOLanguageName == "ivl")
                return tag;
            return char.ToUpper(name[0], culture) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return tag.ToUpperInvariant();
        }
    }

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

    public static Card BeginCard(string id, Vector4? accent = null) => new(id, accent);

    // A rounded panel sized to its content. The content is drawn first on a
    // foreground channel and the background behind it once its height is
    // known, so cards must not contain tables, columns, or other cards.
    public readonly ref struct Card
    {
        private readonly Vector2 start;
        private readonly Vector2 padding;
        private readonly float width;
        private readonly Vector4? accent;
        private readonly ImDrawListPtr drawList;

        public Card(string id, Vector4? accent)
        {
            this.accent = accent;
            padding = Snap(new Vector2(12, 8) * Scale);
            start = Snap(ImGui.GetCursorScreenPos());
            width = ImGui.GetContentRegionAvail().X;
            drawList = ImGui.GetWindowDrawList();
            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1);

            ImGui.PushID(id);
            ImGui.SetCursorScreenPos(start + padding);
            ImGui.BeginGroup();
            Left = ImGui.GetCursorPosX();
            Width = width - (padding.X * 2);
            ImGui.PushTextWrapPos(Left + Width);
        }

        // Window-local x of the content's left edge, and the content width.
        public float Left { get; }

        public float Width { get; }

        public float Right => Left + Width;

        public void Dispose()
        {
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            ImGui.PopID();

            var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + padding.Y);
            var rounding = 5 * Scale;
            drawList.ChannelsSetCurrent(0);
            drawList.AddRectFilled(start, end, ImGui.GetColorU32(ImGuiCol.FrameBg), rounding);
            if (accent is { } color)
                drawList.AddRectFilled(start, new Vector2(start.X + (3 * Scale), end.Y), ImGui.GetColorU32(color), rounding, ImDrawFlags.RoundCornersLeft);
            drawList.ChannelsMerge();

            ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
            ImGui.Dummy(new Vector2(width, 6 * Scale));
        }
    }
}
