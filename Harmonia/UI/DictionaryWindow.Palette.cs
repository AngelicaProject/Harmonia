using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Dictionary;
using Harmonia.Game;
using Harmonia.Localization;

namespace Harmonia.UI;

internal sealed partial class DictionaryWindow
{
    private static readonly Vector4 PaletteBackground = new(0.141f, 0.141f, 0.141f, 1);
    private static readonly Vector4 PaletteText = new(0.929f, 0.925f, 0.914f, 1);
    private static readonly Vector4 PaletteMuted = new(0.675f, 0.671f, 0.659f, 1);
    private static readonly Vector4 PaletteAccent = new(0.780f, 0.765f, 0.733f, 1);
    private static readonly Vector4 PaletteBorder = new(0.325f, 0.318f, 0.306f, 1);
    private static readonly Vector4 PaletteSelected = new(0.239f, 0.239f, 0.239f, 1);
    private static readonly Vector4 PaletteHover = new(0.180f, 0.180f, 0.180f, 1);

    private bool arranging;
    private bool arrangeStarted;
    private Vector2 arrangedPosition;

    public void BeginArrange()
    {
        arranging = true;
        arrangeStarted = false;
        focusDictionary = false;
        IsOpen = true;
        BringToFront();
    }

    private void FinishArrange(bool commit)
    {
        arranging = false;
        if (commit)
        {
            configuration.DictionaryCustomPosition = true;
            configuration.DictionaryPositionX = arrangedPosition.X;
            configuration.DictionaryPositionY = arrangedPosition.Y;
            save();
        }
        IsOpen = false;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        var width = Math.Min(640 * Ui.Scale, viewport.WorkSize.X * 0.9f);
        const int height = 172 + PaletteMatches * 56;
        var size = new Vector2(width, Math.Min(height * Ui.Scale, viewport.WorkSize.Y * 0.75f));
        var room = Vector2.Max(Vector2.Zero, viewport.WorkSize - size);
        var position = configuration.DictionaryCustomPosition
            ? viewport.WorkPos + room * new Vector2(Math.Clamp(configuration.DictionaryPositionX, 0, 1), Math.Clamp(configuration.DictionaryPositionY, 0, 1))
            : viewport.WorkPos + new Vector2((viewport.WorkSize.X - width) / 2, viewport.WorkSize.Y * 0.14f);
        Position = arranging && arrangeStarted ? null : Ui.Snap(position);
        PositionCondition = ImGuiCond.Always;
        Size = size;
        SizeCondition = ImGuiCond.Always;
        arrangeStarted = arranging;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 14) * Ui.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10 * Ui.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 10) * Ui.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4) * Ui.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 5 * Ui.Scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, PaletteBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, PaletteBorder);
        ImGui.PushStyleColor(ImGuiCol.Text, PaletteText);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, PaletteMuted);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(7);
        ImGui.PopStyleVar(6);
    }

    private void DrawPalette()
    {
        var popupOpen = ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var opening = focusDictionary;
        ignoreTabNavigation = opening;
        var io = ImGui.GetIO();
        if (!arranging && !popupOpen && configuration.DictionaryTabFocusSearch && !dictionaryInputActive &&
            keyboard.GameHasFocus && (focused || !ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow)) &&
            !io.KeyShift && !io.KeyCtrl && !io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.Tab, false))
        {
            focusDictionary = true;
            ignoreTabNavigation = true;
        }
        if (arranging)
        {
            var viewport = ImGui.GetMainViewport();
            var room = Vector2.Max(Vector2.One, viewport.WorkSize - ImGui.GetWindowSize());
            arrangedPosition = Vector2.Clamp((ImGui.GetWindowPos() - viewport.WorkPos) / room, Vector2.Zero, Vector2.One);
        }
        if (!arranging && !opening && !popupOpen && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)) &&
            !ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
        {
            IsOpen = false;
            return;
        }
        if (focused || popupOpen)
            keyboard.Capture();
        var ownsEscape = keyboard.GameHasFocus && !popupOpen &&
            (focused || !ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow));
        if (keyboard.PaletteEscapePressed(ownsEscape) ||
            (ownsEscape && ImGui.IsKeyPressed(ImGuiKey.Escape, false)))
        {
            if (arranging)
                FinishArrange(false);
            else
                IsOpen = false;
            return;
        }

        dictionary.Prepare();
        navigationDelta = 0;
        if (focused && !popupOpen && ImGui.GetIO().KeyCtrl)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.N))
                navigationDelta++;
            if (ImGui.IsKeyPressed(ImGuiKey.P))
                navigationDelta--;
        }
        var enter = !opening && focused && !popupOpen &&
            (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
        var submitted = DrawPaletteSearch();
        if (!IsOpen)
            return;

        PollDictionary(dictionary, popupOpen);
        var results = dictionaryResults;
        if (results is { Result.Matches.Count: > 0 })
        {
            var previous = selectedResult;
            selectedResult = Math.Clamp(selectedResult + navigationDelta, 0, results.Result.Matches.Count - 1);
            scrollToSelected |= previous != selectedResult;
            if (!arranging && DictionaryResultsCurrent && (enter || (submitted && !opening && !popupOpen)))
            {
                CopyOriginal(results.Result.Matches[selectedResult]);
                return;
            }
        }

        if (results is { Result.Total: > 0 })
        {
            var at = ImGui.GetCursorPos();
            ImGui.SetCursorPos(at + new Vector2(6, 5) * Ui.Scale);
            ImGui.TextDisabled(Lang.T("dictionary.palette_count", results.Result.Matches.Count, results.Result.Total));
            ImGui.SetCursorPos(new Vector2(at.X, at.Y + 28 * Ui.Scale));
            DrawPaletteColumnHeaders(results.Result.Matches[0]);
        }

        var footerHeight = 36 * Ui.Scale;
        var listHeight = Math.Max(ImGui.GetTextLineHeight(), ImGui.GetContentRegionAvail().Y - footerHeight);
        using (var list = ImRaii.Child("##palette_results", new Vector2(0, listHeight), false, ImGuiWindowFlags.NoBackground))
        {
            if (list)
            {
                if (NameText.Normalize(dictionaryQuery).Length < NameIndex.MinQueryLength)
                    DrawPaletteMessage(Lang.T("dictionary.palette_empty"), Lang.T("dictionary.page_empty"));
                else if (dictionaryFailed)
                    DrawPaletteMessage(Lang.T("dictionary.failed"), string.Empty);
                else if (results is null)
                    DrawPaletteMessage(Lang.T("dictionary.searching"), string.Empty);
                else if (results.Result.Total == 0)
                    DrawPaletteMessage(Lang.T("dictionary.no_matches"), Lang.T("dictionary.palette_retry"));
                else
                {
                    if (resetDictionaryScroll)
                    {
                        ImGui.SetScrollY(0);
                        resetDictionaryScroll = false;
                    }
                    using var disabledAlpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, 1);
                    using var staleResults = ImRaii.Disabled(arranging || !DictionaryResultsCurrent);
                    for (var i = 0; i < results.Result.Matches.Count; i++)
                    {
                        DrawPaletteResult(results.Result.Matches[i], results.Details[i], i);
                        if (scrollToSelected && selectedResult == i)
                        {
                            ImGui.SetScrollHereY();
                            scrollToSelected = false;
                        }
                        if (!IsOpen)
                            break;
                    }
                }
            }
        }
        DrawPaletteFooter();
    }

    private bool DrawPaletteSearch()
    {
        if (arranging)
        {
            var dragAt = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.GetContentRegionAvail().X, 44 * Ui.Scale);
            ImGui.InvisibleButton("##dictionary_drag", size);
            var dragDraw = ImGui.GetWindowDrawList();
            dragDraw.AddRect(dragAt, dragAt + size, ImGui.GetColorU32(PaletteBorder), 6 * Ui.Scale);
            var text = Lang.T("dictionary.position_drag");
            dragDraw.AddText(Ui.Snap(dragAt + (size - ImGui.CalcTextSize(text)) / 2), ImGui.GetColorU32(PaletteText), text);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var viewport = ImGui.GetMainViewport();
                var limit = viewport.WorkPos + Vector2.Max(Vector2.Zero, viewport.WorkSize - ImGui.GetWindowSize());
                ImGui.SetWindowPos(Vector2.Clamp(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, viewport.WorkPos, limit));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            return false;
        }
        var pos = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Math.Max(44 * Ui.Scale, ImGui.GetFrameHeight());
        var draw = ImGui.GetWindowDrawList();
        var end = screen + new Vector2(width, height);
        draw.AddRectFilled(screen, end, ImGui.GetColorU32(PaletteHover), 6 * Ui.Scale);
        draw.AddRect(screen, end, ImGui.GetColorU32(PaletteBorder), 6 * Ui.Scale);
        var center = screen + new Vector2(21 * Ui.Scale, height / 2 - 2 * Ui.Scale);
        draw.AddCircle(center, 6 * Ui.Scale, ImGui.GetColorU32(PaletteMuted), 20, 1.5f * Ui.Scale);
        draw.AddLine(center + new Vector2(4, 4) * Ui.Scale, center + new Vector2(9, 9) * Ui.Scale,
            ImGui.GetColorU32(PaletteMuted), 1.5f * Ui.Scale);

        ImGui.SetCursorPos(pos + new Vector2(40 * Ui.Scale, (height - ImGui.GetFrameHeight()) / 2));
        ImGui.SetNextItemWidth(width - 102 * Ui.Scale);
        if (focusDictionary)
        {
            ImGui.SetKeyboardFocusHere();
            focusDictionary = false;
        }
        var previous = dictionaryQuery;
        var submitted = ImGui.InputTextWithHint("##palette_query", Lang.T("dictionary.search_hint"),
            ref dictionaryQuery, 256, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue |
            ImGuiInputTextFlags.CallbackHistory | ImGuiInputTextFlags.CallbackCompletion, NavigateHistory);
        dictionaryInputActive = ImGui.IsItemActive();
        if (dictionaryInputActive || submitted)
            keyboard.Capture();
        if (previous != dictionaryQuery)
        {
            dictionaryEdited = DateTime.UtcNow;
            queryChanged = true;
        }

        var closeSize = new Vector2(42 * Ui.Scale, 26 * Ui.Scale);
        ImGui.SetCursorPos(new Vector2(pos.X + width - closeSize.X - 10 * Ui.Scale, pos.Y + (height - closeSize.Y) / 2));
        var closeAt = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##palette_close", closeSize))
            IsOpen = false;
        DrawPaletteKey(closeAt, closeSize, "Esc", ImGui.IsItemHovered() ? PaletteText : PaletteMuted);
        Ui.Tooltip(Lang.T("dictionary.close"));
        ImGui.SetCursorPos(new Vector2(pos.X, pos.Y + height + 4 * Ui.Scale));
        return submitted;
    }

    private static (float First, float Second, float NameWidth, float Menu) PaletteColumns(float width)
    {
        var first = 56 * Ui.Scale;
        var menu = width - 30 * Ui.Scale;
        var column = (menu - first - 20 * Ui.Scale) / 2;
        return (first, first + column + 16 * Ui.Scale, column, menu);
    }

    private static void DrawPaletteColumnHeaders(NameEntry entry)
    {
        var at = ImGui.GetCursorScreenPos();
        var columns = PaletteColumns(ImGui.GetContentRegionAvail().X);
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(at + new Vector2(columns.First, 0), ImGui.GetColorU32(PaletteMuted), Lang.T("dictionary.shown_column"));
        var language = entry.Originals.Count > 0 ? Ui.LanguageName(entry.Originals[0].Language) : Lang.T("dictionary.original_column");
        draw.AddText(at + new Vector2(columns.Second, 0), ImGui.GetColorU32(PaletteMuted), language);
        ImGui.Dummy(new Vector2(0, 24 * Ui.Scale));
    }

    private void DrawPaletteResult(NameEntry entry, NameDetails details, int index)
    {
        using var id = ImRaii.PushId(index);
        var pos = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 52 * Ui.Scale);
        var columns = PaletteColumns(size.X);
        var draw = ImGui.GetWindowDrawList();
        if (selectedResult == index)
            draw.AddRectFilled(screen, screen + size, ImGui.GetColorU32(PaletteSelected with { W = 0.55f }), 3 * Ui.Scale);

        var iconAt = screen + new Vector2(10, 8) * Ui.Scale;
        var iconSize = new Vector2(32 * Ui.Scale);
        if (details.Icon != 0 && textures.TryGetFromGameIcon(new GameIconLookup(details.Icon), out var icon))
            draw.AddImage(icon.GetWrapOrEmpty().Handle, iconAt, iconAt + iconSize);
        else
        {
            var glyph = NameLookup.CategoryName(entry.Category)[..1];
            draw.AddText(iconAt + (iconSize - ImGui.CalcTextSize(glyph)) / 2, ImGui.GetColorU32(PaletteMuted), glyph);
        }

        var original = entry.Originals.Count > 0 ? entry.Originals[0].Text : entry.Shown;
        DrawPaletteName(entry.Shown, pos + new Vector2(columns.First, 7 * Ui.Scale), columns.NameWidth, index, "shown");
        DrawPaletteName(original, pos + new Vector2(columns.Second, 7 * Ui.Scale), columns.NameWidth, index, "original");
        if (dictionaryCopiedText != entry.Shown || DateTime.UtcNow - dictionaryCopiedAt >= CopiedFeedback)
            draw.AddText(screen + new Vector2(columns.First, 28 * Ui.Scale), ImGui.GetColorU32(PaletteMuted), NameLookup.CategoryName(entry.Category));

        ImGui.SetCursorPos(pos + new Vector2(columns.Menu, 9 * Ui.Scale));
        var menuAt = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##more", new Vector2(28, 28) * Ui.Scale))
        {
            selectedResult = index;
            ImGui.OpenPopup(RowMenu);
        }
        var hovered = ImGui.IsItemHovered();
        var dotColor = ImGui.GetColorU32(hovered ? PaletteText : PaletteMuted);
        for (var i = 0; i < 3; i++)
            draw.AddCircleFilled(menuAt + new Vector2(8 + i * 5, 14) * Ui.Scale, 1.4f * Ui.Scale, dotColor);
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        Ui.Tooltip(Lang.T("dictionary.more_actions"));
        if (DictionaryResultsCurrent && ImGui.IsMouseHoveringRect(screen, screen + size) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            selectedResult = index;
            ImGui.OpenPopup(RowMenu);
        }

        // Popup items must remain interactive outside the search panel.
        using (ImRaii.PushColor(ImGuiCol.PopupBg, PaletteBackground))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(6, 4) * Ui.Scale))
        {
            using var menu = ImRaii.Popup(RowMenu);
            if (menu)
                DrawDictionaryMenu(entry, details, lookup);
        }
        ImGui.SetCursorPos(pos + new Vector2(0, size.Y + 4 * Ui.Scale));
    }

    private void DrawPaletteName(string text, Vector2 pos, float width, int index, string id)
    {
        ImGui.SetCursorPos(pos);
        var screen = ImGui.GetCursorScreenPos();
        var label = FitName(text, width - 6 * Ui.Scale);
        var textSize = ImGui.CalcTextSize(label);
        if (ImGui.InvisibleButton("##" + id, new Vector2(width, ImGui.GetTextLineHeightWithSpacing())))
        {
            selectedResult = index;
            ImGui.SetClipboardText(text);
            dictionaryCopiedText = text;
            dictionaryCopiedAt = DateTime.UtcNow;
        }
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        draw.AddText(screen, ImGui.GetColorU32(PaletteText), label);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddLine(screen + new Vector2(0, textSize.Y), screen + textSize, ImGui.GetColorU32(PaletteMuted));
            Ui.Tooltip(text + "\n" + Lang.T("dictionary.click_to_copy"));
        }
        if (dictionaryCopiedText == text && DateTime.UtcNow - dictionaryCopiedAt < CopiedFeedback)
            draw.AddText(screen + new Vector2(0, 21 * Ui.Scale), ImGui.GetColorU32(PaletteMuted), Lang.T("dictionary.copied_short"));
    }

    private static void DrawPaletteKey(Vector2 at, Vector2 size, string text, Vector4 color)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRect(at, at + size, ImGui.GetColorU32(PaletteBorder), 4 * Ui.Scale);
        draw.AddText(Ui.Snap(at + (size - ImGui.CalcTextSize(text)) / 2), ImGui.GetColorU32(color), text);
    }

    private static void DrawPaletteMessage(string title, string hint)
    {
        var start = ImGui.GetCursorPos();
        var available = ImGui.GetContentRegionAvail();
        var width = Math.Max(1, Math.Min(420 * Ui.Scale, available.X - 24 * Ui.Scale));
        var titleSize = ImGui.CalcTextSize(title, false, width);
        var hintSize = hint.Length > 0 ? ImGui.CalcTextSize(hint, false, width) : Vector2.Zero;
        var gap = hint.Length > 0 ? 6 * Ui.Scale : 0;
        var y = start.Y + Math.Max(0, (available.Y - titleSize.Y - gap - hintSize.Y) / 2);
        var x = start.X + (available.X - titleSize.X) / 2;
        ImGui.SetCursorPos(Ui.Snap(new Vector2(x, y)));
        using (ImRaii.TextWrapPos(x + width))
            ImGui.TextUnformatted(title);
        if (hint.Length > 0)
        {
            x = start.X + (available.X - hintSize.X) / 2;
            ImGui.SetCursorPos(Ui.Snap(new Vector2(x, y + titleSize.Y + gap)));
            using var wrap = ImRaii.TextWrapPos(x + width);
            using var color = ImRaii.PushColor(ImGuiCol.Text, PaletteMuted);
            ImGui.TextUnformatted(hint);
        }
    }

    private void DrawPaletteFooter()
    {
        if (arranging)
        {
            if (ImGui.Button(Lang.T("dictionary.position_done")))
                FinishArrange(true);
            ImGui.SameLine();
            if (ImGui.Button(Lang.T("dictionary.shortcut_cancel")))
                FinishArrange(false);
            return;
        }
        var pos = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(screen, screen + new Vector2(width, 0), ImGui.GetColorU32(PaletteBorder with { W = 0.5f }));
        var y = screen.Y + 10 * Ui.Scale;
        DrawPaletteKey(new Vector2(screen.X + 4 * Ui.Scale, y), new Vector2(40, 22) * Ui.Scale, "Tab", PaletteMuted);
        draw.AddText(new Vector2(screen.X + 52 * Ui.Scale, y + 3 * Ui.Scale), ImGui.GetColorU32(PaletteMuted), Lang.T("dictionary.palette_next"));
        if (width >= 500 * Ui.Scale)
        {
            DrawPaletteKey(new Vector2(screen.X + 198 * Ui.Scale, y), new Vector2(46, 22) * Ui.Scale, "Enter", PaletteMuted);
            draw.AddText(new Vector2(screen.X + 252 * Ui.Scale, y + 3 * Ui.Scale),
                ImGui.GetColorU32(PaletteMuted), Lang.T("dictionary.palette_copy_close"));
        }

        var open = Lang.T("nav.settings");
        var buttonSize = ImGui.CalcTextSize(open) + new Vector2(12, 8) * Ui.Scale;
        ImGui.SetCursorPos(new Vector2(pos.X + width - buttonSize.X, pos.Y + 7 * Ui.Scale));
        var buttonAt = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##palette_full", buttonSize))
        {
            SaveQuery();
            IsOpen = false;
            openSettings();
        }
        var hovered = ImGui.IsItemHovered();
        draw.AddText(buttonAt + new Vector2(6, 4) * Ui.Scale, ImGui.GetColorU32(hovered ? PaletteText : PaletteAccent), open);
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        Ui.Tooltip(Lang.T("dictionary.settings_open"));
    }
}
