using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Dictionary;
using Harmonia.Game;
using Harmonia.Localization;

namespace Harmonia.UI;

// The name dictionary as a page: search as you type, filter by category, and
// act on a name (copy it, open it on a site, link the item, try it on).
internal sealed partial class MainWindow
{
    private const int DictionaryMatches = 100;
    private const string RowMenu = "##row_menu";
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(2);
    // A search takes a few milliseconds; the pause only skips the letters
    // of a word typed in one go.
    private static readonly TimeSpan DictionarySearchDelay = TimeSpan.FromMilliseconds(60);
    private static readonly NameCategory[] DictionaryCategories = Enum.GetValues<NameCategory>();

    private string dictionaryQuery = string.Empty;
    private NameCategory? dictionaryCategory;
    private DateTime dictionaryEdited;
    private (string Query, NameCategory? Category)? dictionarySearched;
    private Task<DictionaryResults>? dictionarySearch;
    private CancellationTokenSource? dictionaryCancel;
    private DictionaryResults? dictionaryResults;
    private bool dictionaryFailed;
    private bool focusDictionary;
    private string? dictionaryCopiedText;
    private DateTime dictionaryCopiedAt;

    private sealed record DictionaryResults(string Query, NameSearchResult Result, NameDetails[] Details);

    public void OpenDictionary()
    {
        if (info.Dictionary is null)
            return;

        IsOpen = true;
        page = Page.Dictionary;
        focusDictionary = true;
        BringToFront();
    }

    private void DrawDictionary()
    {
        if (info.Dictionary is not { } dictionary || info.Lookup is not { } lookup)
            return;

        // The first search would wait for the index otherwise.
        dictionary.Prepare();

        Ui.Heading(headingFont, Lang.T("nav.dictionary"));
        Ui.Hint(Lang.T("dictionary.page_hint", NameLookup.FindCommand));
        Ui.Gap(4);

        if (focusDictionary)
        {
            ImGui.SetKeyboardFocusHere();
            focusDictionary = false;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputTextWithHint("##dictionary_query", Lang.T("dictionary.search_hint"), ref dictionaryQuery, 256))
            dictionaryEdited = DateTime.UtcNow;

        PollDictionary(dictionary);
        Ui.Gap(2);

        var query = dictionaryQuery.Trim();
        if (NameText.Normalize(query).Length < NameIndex.MinQueryLength)
        {
            Ui.Hint(Lang.T("dictionary.page_empty"));
            return;
        }

        if (dictionaryFailed)
        {
            Ui.IconText(FontAwesomeIcon.TimesCircle, Ui.Bad, Lang.T("dictionary.failed"));
            return;
        }

        if (dictionaryResults is not { } results)
        {
            Ui.Hint(Lang.T("dictionary.searching"));
            return;
        }

        DrawDictionaryCategories(results.Result);
        Ui.Gap(2);
        if (results.Result.Total == 0)
        {
            Ui.Hint(dictionarySearch is null ? Lang.T("dictionary.no_matches") : Lang.T("dictionary.searching"));
            return;
        }

        using var list = ImRaii.Child("##dictionary_results", Vector2.Zero, true);
        if (!list)
            return;

        var right = ImGui.GetWindowContentRegionMax().X;
        for (var i = 0; i < results.Result.Matches.Count; i++)
        {
            if (i > 0)
            {
                Ui.Gap(2);
                Ui.Divider(right);
                Ui.Gap(2);
            }

            DrawDictionaryRow(results.Result.Matches[i], results.Details[i], lookup, right);
        }

        if (results.Result.Total > results.Result.Matches.Count)
        {
            Ui.Gap(4);
            Ui.Hint(Lang.T("dictionary.showing_first", results.Result.Matches.Count, results.Result.Total));
        }
    }

    // Searches once typing pauses; a newer search cancels the one running.
    private void PollDictionary(NameDictionary dictionary)
    {
        if (dictionarySearch is { IsCompleted: true } done)
        {
            dictionaryFailed = done.IsFaulted;
            if (done.IsCompletedSuccessfully)
                dictionaryResults = done.Result;
            dictionarySearch = null;
        }

        var wanted = (Query: dictionaryQuery.Trim(), Category: dictionaryCategory);
        if (wanted == dictionarySearched || DateTime.UtcNow - dictionaryEdited < DictionarySearchDelay)
            return;

        dictionarySearched = wanted;
        dictionaryCancel?.Cancel();
        dictionaryCancel?.Dispose();
        dictionaryCancel = new CancellationTokenSource();
        dictionarySearch = SearchDictionaryAsync(dictionary, wanted.Query, wanted.Category, dictionaryCancel.Token);
    }

    private static async Task<DictionaryResults> SearchDictionaryAsync(
        NameDictionary dictionary, string query, NameCategory? category, CancellationToken cancellationToken)
    {
        var result = await dictionary.SearchAsync(query, DictionaryMatches, category, cancellationToken).ConfigureAwait(false);
        var details = result.Matches.Select(dictionary.Describe).ToArray();
        return new DictionaryResults(query, result, details);
    }

    private void DrawDictionaryCategories(NameSearchResult result)
    {
        var all = result.ByCategory.Values.Sum();
        if (DictionaryChip(Lang.T("dictionary.filter_all") + " " + all, dictionaryCategory is null))
            dictionaryCategory = null;

        foreach (var category in DictionaryCategories)
        {
            var count = result.ByCategory.GetValueOrDefault(category);
            if (count == 0 && dictionaryCategory != category)
                continue;

            ImGui.SameLine();
            if (DictionaryChip(CategoryFilterName(category) + " " + count + "##" + category, dictionaryCategory == category))
                dictionaryCategory = category;
        }
    }

    private static bool DictionaryChip(string label, bool active)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), active);
        return ImGui.SmallButton(label);
    }

    // A row: the icon, the shown name, and the originals. Clicking a name
    // copies it; everything else is in the row's menu, opened by the button
    // at the row's end or a right click anywhere on the row.
    private void DrawDictionaryRow(NameEntry entry, NameDetails details, NameLookup lookup, float right)
    {
        using var id = ImRaii.PushId($"{entry.Category}_{entry.RowId}");
        var menuWidth = ImGui.GetFrameHeight();
        var textRight = right - menuWidth - ImGui.GetStyle().ItemSpacing.X;
        var iconSize = new Vector2(36 * Ui.Scale);

        using (ImRaii.Group())
        {
            DrawDictionaryIcon(entry.Category, details.Icon, iconSize);
            ImGui.SameLine();
            using (ImRaii.Group())
            using (ImRaii.TextWrapPos(textRight))
            {
                CopyableText(entry.Shown, null, true);
                ImGui.TextDisabled(NameLookup.CategoryName(entry.Category));
                foreach (var (original, i) in entry.Originals.Select(static (o, i) => (o, i)))
                {
                    // The client language's name only when it differs from the shown one.
                    if (i == 0 && !entry.IsTranslated)
                        continue;

                    ImGui.SameLine(0, 0);
                    ImGui.TextDisabled(" · " + (i == 0 ? string.Empty : original.Language.ToUpperInvariant() + ": "));
                    ImGui.SameLine(0, 0);
                    CopyableText(original.Text, Ui.Muted, false);
                }
            }

            Ui.SameLineAt(right - menuWidth);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Round((iconSize.Y - menuWidth) / 2));
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.EllipsisH))
                    ImGui.OpenPopup(RowMenu);
            }

            Ui.Tooltip(Lang.T("dictionary.more_actions"));
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup(RowMenu);

        using var menu = ImRaii.Popup(RowMenu);
        if (menu)
            DrawDictionaryMenu(entry, details, lookup);
    }

    private void DrawDictionaryIcon(NameCategory category, uint gameIcon, Vector2 size)
    {
        if (gameIcon != 0 && textures.TryGetFromGameIcon(new GameIconLookup(gameIcon), out var icon))
        {
            ImGui.Image(icon.GetWrapOrEmpty().Handle, size);
            return;
        }

        // Drawn over a placeholder, so the row continues after the icon box.
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var glyph = CategoryIcon(category).ToIconString();
        ImGui.GetWindowDrawList().AddText(Ui.Snap(at + ((size - ImGui.CalcTextSize(glyph)) / 2)), ImGui.GetColorU32(Ui.Muted), glyph);
    }

    // Text that copies itself when clicked, underlined while hovered; after
    // a copy it says so in its tooltip for a moment.
    private void CopyableText(string text, Vector4? color, bool wrap)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? Vector4.Zero, color is not null))
        {
            if (wrap)
                ImGui.TextWrapped(text);
            else
                ImGui.TextUnformatted(text);
        }

        if (!ImGui.IsItemHovered())
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), max, ImGui.GetColorU32(ImGuiCol.Text));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ImGui.SetClipboardText(text);
            dictionaryCopiedText = text;
            dictionaryCopiedAt = DateTime.UtcNow;
        }

        var copied = dictionaryCopiedText == text && DateTime.UtcNow - dictionaryCopiedAt < CopiedFeedback;
        Ui.Tooltip(copied ? Lang.T("dictionary.copied_short") : Lang.T("dictionary.click_to_copy"));
    }

    private static void DrawDictionaryMenu(NameEntry entry, NameDetails details, NameLookup lookup)
    {
        if (ImGui.Selectable(Lang.T("dictionary.copy_shown", entry.Shown)))
            lookup.Copy(entry.Shown);
        foreach (var (original, i) in entry.Originals.Select(static (o, i) => (o, i)))
        {
            if (i == 0 && !entry.IsTranslated)
                continue;
            if (ImGui.Selectable(Lang.T("dictionary.copy_named", Ui.LanguageName(original.Language), original.Text)))
                lookup.Copy(original.Text);
        }

        if (entry.Category == NameCategory.Item)
        {
            ImGui.Separator();
            if (ImGui.Selectable(Lang.T("dictionary.show_link")))
                lookup.PrintLink(entry);
            if (details.CanTryOn && ImGui.Selectable(Lang.T("dictionary.try_on")))
                lookup.TryOn(entry.RowId);
        }

        var sites = NameSites.For(entry, details.Marketable);
        if (sites.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextDisabled(Lang.T("dictionary.open_site"));
        foreach (var site in sites)
        {
            if (ImGui.Selectable(site.Name))
                lookup.OpenSite(site.Url);
        }
    }

    private static string CategoryFilterName(NameCategory category) => category switch
    {
        NameCategory.Item => Lang.T("dictionary.filter_item"),
        NameCategory.Action => Lang.T("dictionary.filter_action"),
        NameCategory.Status => Lang.T("dictionary.filter_status"),
        NameCategory.Place => Lang.T("dictionary.filter_place"),
        NameCategory.Duty => Lang.T("dictionary.filter_duty"),
        NameCategory.Quest => Lang.T("dictionary.filter_quest"),
        _ => category.ToString(),
    };

    private static FontAwesomeIcon CategoryIcon(NameCategory category) => category switch
    {
        NameCategory.Item => FontAwesomeIcon.Box,
        NameCategory.Action => FontAwesomeIcon.Magic,
        NameCategory.Status => FontAwesomeIcon.Heartbeat,
        NameCategory.Place => FontAwesomeIcon.MapMarkerAlt,
        NameCategory.Duty => FontAwesomeIcon.Dungeon,
        NameCategory.Quest => FontAwesomeIcon.Exclamation,
        _ => FontAwesomeIcon.Question,
    };
}
