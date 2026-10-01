using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Localization;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;

namespace Harmonia.UI;

// What the active translation applies: the player keeps chosen sheets in the
// game's language, by group in the simple view or sheet by sheet in the full
// one. Like every pack decision, a change applies at the next game start.
internal sealed partial class MainWindow
{
    private const int MaxSearchResults = 300;

    private bool contentAdvanced;
    private string sheetSearch = string.Empty;
    private string? sheetListPath;
    private string? sheetListError;
    private IReadOnlyList<(string Name, long Cells)> sheetList = [];

    private sealed record SheetRow(string Entry, string Label, int Sheets, long Cells, bool Folder);

    private void DrawContent()
    {
        Ui.Heading(headingFont, Lang.T("nav.content"));
        Ui.Hint(Lang.T("content.intro"));
        Ui.Gap(4);

        if (ImGui.RadioButton(Lang.T("content.simple"), !contentAdvanced))
            contentAdvanced = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(Lang.T("content.advanced"), contentAdvanced))
            contentAdvanced = true;

        if (configuration.UntranslatedSheets.Count > 0)
        {
            ImGui.SameLine();
            var reset = Lang.T("content.translate_all");
            Ui.AlignRight(Ui.ButtonWidth(FontAwesomeIcon.Undo, reset), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X, true);
            if (Ui.LinkButton(FontAwesomeIcon.Undo, reset))
            {
                configuration.UntranslatedSheets.Clear();
                save();
            }
        }

        Ui.Gap(4);
        if (contentAdvanced)
            DrawSheetList();
        else
            DrawSheetGroups();
    }

    private void DrawSheetGroups()
    {
        using var card = Ui.BeginCard("groups");
        ImGui.TextDisabled(Lang.T("content.keep_original"));
        Ui.Gap(2);
        foreach (var group in SheetGroups.All)
        {
            var kept = group.Entries.Count(IsUntranslated);
            var all = kept == group.Entries.Count;
            var on = all;
            if (ImGui.Checkbox(Lang.T(GroupKey(group)) + "##" + group.Key, ref on))
                SetUntranslated(group.Entries, on);
            if (kept > 0 && !all)
            {
                ImGui.SameLine();
                Ui.Badge(Lang.T("content.partial"), Ui.Muted);
            }

            Hinted(Lang.T(GroupKey(group) + "_hint"));
            Ui.Gap(2);
        }
    }

    private void DrawSheetList()
    {
        LoadSheetList();
        if (sheetListPath is null)
        {
            Ui.Hint(Lang.T("content.no_pack"));
            return;
        }

        if (sheetListError is not null)
        {
            Ui.IconText(FontAwesomeIcon.TimesCircle, Ui.Bad, sheetListError);
            return;
        }

        ImGui.SetNextItemWidth(Math.Min(320 * Ui.Scale, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##sheet_search", Lang.T("content.search"), ref sheetSearch, 256);
        Ui.Gap(2);

        var rows = SheetRows();
        if (rows.Count == 0)
        {
            Ui.Hint(Lang.T("content.no_match"));
            return;
        }

        var filter = new SheetFilter(configuration.UntranslatedSheets);
        using var list = ImRaii.Child("##sheets", Vector2.Zero, true);
        if (!list)
            return;

        var countWidth = 110 * Ui.Scale;
        foreach (var row in rows)
        {
            using var id = ImRaii.PushId(row.Entry);
            var covered = !row.Folder && filter.ExcludedByFolder(row.Entry);
            var on = covered || IsUntranslated(row.Entry);
            using (ImRaii.Disabled(covered))
            {
                if (ImGui.Checkbox(row.Label, ref on))
                    SetUntranslated([row.Entry], on);
            }

            if (covered)
                Ui.Tooltip(Lang.T("content.covered", SheetFilter.FolderOf(row.Entry) ?? string.Empty));

            var count = row.Folder
                ? Lang.T("content.folder_count", row.Sheets.ToString("N0", CultureInfo.CurrentCulture))
                : Lang.T("content.line_count", row.Cells.ToString("N0", CultureInfo.CurrentCulture));
            Ui.SameLineAt(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - countWidth));
            ImGui.TextDisabled(count);
        }

        if (rows.Count >= MaxSearchResults)
            Ui.Hint(Lang.T("content.more_results", MaxSearchResults));
    }

    // Without a search: sheets outside folders and one row per folder. With
    // one: every matching sheet, folders included.
    private List<SheetRow> SheetRows()
    {
        var search = sheetSearch.Trim();
        if (search.Length > 0)
        {
            return sheetList
                .Where(s => s.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSearchResults)
                .Select(static s => new SheetRow(s.Name, s.Name, 1, s.Cells, false))
                .ToList();
        }

        var rows = new List<SheetRow>();
        foreach (var group in sheetList.GroupBy(static s => SheetFilter.FolderOf(s.Name) ?? s.Name, StringComparer.Ordinal))
        {
            var folder = SheetFilter.IsFolder(group.Key);
            rows.Add(new SheetRow(group.Key, folder ? group.Key[..^1] : group.Key, group.Count(), group.Sum(static s => s.Cells), folder));
        }

        rows.Sort(static (a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    // The sheets of the translation chosen for the next start, read once per
    // file from its metadata.
    private void LoadSheetList()
    {
        var path = packs.TryGet(configuration.ActivePackId)?.FilePath;
        if (string.Equals(path, sheetListPath, StringComparison.Ordinal))
            return;

        sheetListPath = path;
        sheetListError = null;
        sheetList = [];
        if (path is null)
            return;

        try
        {
            using var file = HpkFile.Open(path, HpkOpenMode.Metadata);
            sheetList = Enumerable.Range(0, file.SheetCount)
                .Select(i => (file.SheetNames[i], file.GetSheetCellCount(i)))
                .ToArray();
        }
        catch (Exception ex) when (ex is HpkFormatException or IOException or UnauthorizedAccessException)
        {
            sheetListError = ex.Message;
        }
    }

    // The text key of a group's name; its hint adds "_hint".
    internal static string GroupKey(SheetGroup group) => "sheets." + group.Key;

    private bool IsUntranslated(string entry) =>
        configuration.UntranslatedSheets.Contains(entry, StringComparer.Ordinal);

    private void SetUntranslated(IEnumerable<string> entries, bool untranslated)
    {
        foreach (var entry in entries)
        {
            if (!untranslated)
                configuration.UntranslatedSheets.RemoveAll(e => string.Equals(e, entry, StringComparison.Ordinal));
            else if (!IsUntranslated(entry))
                configuration.UntranslatedSheets.Add(entry);
        }

        save();
    }
}
