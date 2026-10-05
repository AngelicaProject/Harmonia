using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Harmonia.Game;
using Harmonia.Localization;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;

namespace Harmonia.UI;

// What the active translation applies: the player keeps chosen sheets in the
// game's language. Groups on the left, the tree of the pack's folders and
// sheets on the right, limited to the selected group. Like every pack
// decision, a change applies at the next game start.
internal sealed partial class MainWindow
{
    // XIViewer shows a sheet's rows in every game language from its name.
    private const string ViewerUrl = "https://xiviewer.app/sheet/";

    private enum SheetShow
    {
        All,
        Kept,
        Translated,
    }

    private readonly HashSet<string> openFolders = new(StringComparer.Ordinal);
    private string sheetSearch = string.Empty;
    private SheetShow sheetShow = SheetShow.All;
    private SheetSort sheetSort = SheetSort.Name;
    private bool sheetSortDescending;
    private string? sheetListPath;
    private string? sheetListError;
    private SheetTree? sheetTree;
    private string? sheetTreeKept;
    private List<SheetNode>? sheetRows;
    private string? sheetRowsGroup;
    private bool questNamesMatched;
    private string? openLinkHovered;

    // What the header checkbox changes: the topmost nodes whose every sheet
    // is shown, and how many of the shown sheets are kept.
    private List<SheetNode> sheetTargets = [];
    private int sheetsShown;
    private int sheetsShownKept;

    // The selected group's sheets, kept ones, and their lines, whatever the
    // search; and the folders that open to show its entries.
    private (int Sheets, int Kept, long Cells, long KeptCells) scopeTotals;
    private HashSet<string> scopeOpen = new(StringComparer.Ordinal);

    private void DrawContent()
    {
        Ui.Heading(headingFont, Lang.T("nav.content"));
        Ui.Hint(Lang.T("content.intro"));
        Ui.Gap(4);
        DrawCompatibility();

        LoadSheetTree();
        if (sheetTree is not null)
            RefreshSheetTree(sheetTree);

        var groups = Groups;
        var group = groups.FirstOrDefault(g => string.Equals(g.Key, selectedGroup, StringComparison.Ordinal));
        if (group is null && selectedGroup is not null)
        {
            selectedGroup = null;
            sheetRows = null;
        }

        var height = Math.Max(240 * Ui.Scale, ImGui.GetContentRegionAvail().Y);
        DrawGroupList(groups, height);
        ImGui.SameLine();

        // The list may have selected another group or made one.
        groups = Groups;
        group = groups.FirstOrDefault(g => string.Equals(g.Key, selectedGroup, StringComparison.Ordinal));
        using var view = ImRaii.Child("##sheet_view", new Vector2(0, height));
        if (!view)
            return;

        if (group is null)
        {
            Ui.Heading(headingFont, Lang.T("groups.all"));
            Ui.Gap(2);
            DrawSheetTree(null);
        }
        else
        {
            DrawGroupHeader(group);
            Ui.Gap(2);
            if (group.Entries.Count == 0)
                Ui.Hint(Lang.T("groups.empty_view"));
            else
                DrawSheetTree(group);
        }
    }

    private void DrawSheetTree(SheetGroup? group)
    {
        if (sheetListPath is null)
        {
            Ui.Hint(Lang.T("content.no_pack"));
            if (group is not null)
                DrawGroupEntries(group);
            return;
        }

        if (sheetListError is not null || sheetTree is null)
        {
            Ui.IconText(FontAwesomeIcon.TimesCircle, Ui.Bad, sheetListError ?? "?");
            return;
        }

        // Quest names arrive after the first search; it may match them now.
        if (previews.QuestNamesReady && !questNamesMatched)
        {
            questNamesMatched = true;
            sheetRows = null;
        }

        if (!string.Equals(sheetRowsGroup, group?.Key, StringComparison.Ordinal))
        {
            sheetRowsGroup = group?.Key;
            sheetRows = null;
        }

        var rows = sheetRows ??= SheetRows(sheetTree, group);
        DrawSheetToolbar();
        Ui.Gap(2);
        DrawSheetSummary();
        if (group is not null)
            DrawMissingEntries(group);
        Ui.Gap(2);

        if (rows.Count == 0)
        {
            Ui.Hint(Lang.T("content.no_match"));
            return;
        }

        DrawSheetTable(rows);
    }

    private void DrawSheetToolbar()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var button = ImGui.GetFrameHeight();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var showWidth = 150 * Ui.Scale;
        var searchWidth = Math.Max(100 * Ui.Scale, right - ImGui.GetCursorPosX() - showWidth - button - (spacing * 2));

        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##sheet_search", Lang.T("content.search"), ref sheetSearch, 256))
            sheetRows = null;

        ImGui.SameLine(0, spacing);
        using (ImRaii.Disabled(sheetSearch.Length == 0))
        {
            if (ImGuiComponents.IconButton("##clear_search", FontAwesomeIcon.Times))
            {
                sheetSearch = string.Empty;
                sheetRows = null;
            }
        }

        Ui.Tooltip(Lang.T("content.clear_search"));
        ImGui.SameLine(0, spacing);
        ImGui.SetNextItemWidth(Math.Max(80 * Ui.Scale, right - ImGui.GetCursorPosX()));
        using var combo = ImRaii.Combo("##sheet_show", ShowText(sheetShow));
        if (!combo)
            return;

        foreach (var show in Enum.GetValues<SheetShow>())
        {
            if (ImGui.Selectable(ShowText(show), show == sheetShow))
            {
                sheetShow = show;
                sheetRows = null;
            }
        }
    }

    private void DrawSheetSummary()
    {
        var (sheets, kept, cells, keptCells) = scopeTotals;
        Ui.Hint(kept == 0
            ? Lang.T("content.summary_none", Number(sheets), Number(cells))
            : Lang.T("content.summary", Number(kept), Number(sheets), Number(keptCells), Number(cells)));
        Ui.Hint(Lang.T("content.legend"));

        // Only while the player opened folders that a search does not open.
        if (openFolders.Count > 0 && sheetSearch.Trim().Length == 0)
        {
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding with { Y = 0 });
            if (Ui.LinkButton(FontAwesomeIcon.FolderMinus, Lang.T("content.collapse")))
            {
                openFolders.Clear();
                sheetRows = null;
            }
        }
    }

    private void DrawSheetTable(List<SheetNode> rows)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;
        var height = Math.Max(120 * Ui.Scale, ImGui.GetContentRegionAvail().Y);
        using var table = ImRaii.Table("##sheet_tree", 3, flags, new Vector2(0, height));
        if (!table)
            return;

        var lines = Lang.T("content.col_lines");
        var padding = ImGui.GetStyle().CellPadding.X * 2;
        // The checkbox column has no title: its header checks everything
        // shown, the line above the table says what a check means, and each
        // checkbox says it again on hover.
        ImGui.TableSetupColumn("##keep", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort |
            ImGuiTableColumnFlags.NoResize | ImGuiTableColumnFlags.NoHeaderWidth, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn(Lang.T("content.col_sheet"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort);
        ImGui.TableSetupColumn(lines, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
            Math.Max(ImGui.CalcTextSize(lines).X + ImGui.GetFontSize() + padding, ImGui.CalcTextSize("0 000 000").X));
        ImGui.TableSetupScrollFreeze(0, 1);
        DrawSheetHeaders();
        ApplySheetSort();

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawSheetRow(rows[i]);
        }

        clipper.End();
        clipper.Destroy();
    }

    private void DrawSheetHeaders()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        var all = sheetsShown > 0 && sheetsShownKept == sheetsShown;
        if (Ui.Checkbox("##keep_all", all, sheetsShownKept > 0 && !all))
        {
            foreach (var node in sheetTargets)
            {
                if (all)
                    SheetTree.Translate(configuration.UntranslatedSheets, node);
                else
                    SheetTree.Keep(configuration.UntranslatedSheets, node);
            }

            save();
        }

        var filtered = sheetSearch.Trim().Length > 0 || sheetShow != SheetShow.All;
        var key = (all, filtered, selectedGroup is not null) switch
        {
            (true, true, _) => "content.uncheck_shown",
            (false, true, _) => "content.check_shown",
            (true, false, true) => "groups.uncheck_group",
            (false, false, true) => "groups.check_group",
            (true, false, false) => "content.uncheck_all",
            _ => "content.check_all",
        };
        Ui.Tooltip(Lang.T(key, Number(sheetsShown)));

        for (var column = 1; column < 3; column++)
        {
            ImGui.TableSetColumnIndex(column);
            ImGui.TableHeader(ImGui.TableGetColumnName(column));
        }
    }

    private void ApplySheetSort()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || !specs.SpecsDirty || specs.SpecsCount == 0)
            return;

        sheetSort = specs.Specs.ColumnIndex == 2 ? SheetSort.Lines : SheetSort.Name;
        sheetSortDescending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
        specs.SpecsDirty = false;
        sheetTree?.Sort(sheetSort, sheetSortDescending);
        sheetRows = null;
    }

    private void DrawSheetRow(SheetNode node)
    {
        using var id = ImRaii.PushId(node.Path);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        if (Ui.Checkbox("##keep", node.State == KeepState.All, node.State == KeepState.Some))
            ToggleKept(node);
        Ui.Tooltip(node.Covered
            ? Lang.T("content.covered", KeptFolder(node))
            : node.State == KeepState.None ? Lang.T("content.check_keep") : Lang.T("content.check_translate"));

        ImGui.TableNextColumn();
        DrawSheetName(node);

        ImGui.TableNextColumn();
        var count = Number(node.Cells);
        ImGui.AlignTextToFramePadding();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(count).X);
        ImGui.TextUnformatted(count);
    }

    private void DrawSheetName(SheetNode node)
    {
        var indent = node.Depth * ImGui.GetStyle().IndentSpacing;
        if (indent > 0)
            ImGui.Indent(indent);

        var flags = ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding;
        if (node.IsFolder)
        {
            var forced = FolderForcedOpen(node);
            ImGui.SetNextItemOpen(forced || openFolders.Contains(node.Path), ImGuiCond.Always);
            ImGui.TreeNodeEx("##node", flags);
            DrawSheetMenu(node);
            if (ImGui.IsItemToggledOpen() && !forced)
            {
                if (!openFolders.Remove(node.Path))
                    openFolders.Add(node.Path);
                sheetRows = null;
            }

            ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);
            Ui.Icon(FontAwesomeIcon.Folder, Ui.Muted);
            ImGui.SameLine();
            ImGui.TextUnformatted(node.Name);
            ImGui.SameLine();
            ImGui.TextDisabled(node.State == KeepState.Some
                ? Lang.T("content.folder_kept", Number(node.KeptSheets), Number(node.Sheets))
                : Lang.T("content.folder_count", Number(node.Sheets)));
        }
        else
        {
            ImGui.TreeNodeEx(node.Name, flags | ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.AllowItemOverlap);
            DrawSheetMenu(node);
            if (ImGui.IsItemClicked())
                ToggleKept(node);
            DrawSheetTooltip(node);
            var hovered = ImGui.IsItemHovered();
            if (previews.QuestName(node.Path) is { } quest)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(quest);
            }

            // The link to the sheet's rows follows its name on the hovered
            // row only, so the list stays quiet.
            if (hovered || openLinkHovered == node.Path)
            {
                ImGui.SameLine();
                DrawViewerLink(node);
            }
        }

        if (indent > 0)
            ImGui.Unindent(indent);
    }

    private void ToggleKept(SheetNode node)
    {
        if (node.State == KeepState.All)
            SheetTree.Translate(configuration.UntranslatedSheets, node);
        else
            SheetTree.Keep(configuration.UntranslatedSheets, node);
        save();
    }

    // Folders open by themselves while searching, to show what matched inside
    // them (except a folder that matches itself: everything under it would),
    // and down to the entries of the selected group.
    private bool FolderForcedOpen(SheetNode folder)
    {
        var search = sheetSearch.Trim();
        return (search.Length > 0 && !folder.Path.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
            scopeOpen.Contains(folder.Path);
    }

    // The rows to show: the visible part of the tree, in order.
    private List<SheetNode> SheetRows(SheetTree tree, SheetGroup? group)
    {
        var search = sheetSearch.Trim();
        var scope = group is null ? null : new HashSet<string>(group.Entries, StringComparer.Ordinal);
        scopeOpen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in scope ?? [])
        {
            for (var parent = tree.Find(entry)?.Parent; parent is { Depth: >= 0 }; parent = parent.Parent)
                scopeOpen.Add(parent.Path);
        }

        scopeTotals = default;
        var matches = new Dictionary<SheetNode, int>();
        Count(tree.Root, scope is null);

        var rows = new List<SheetNode>();
        Add(tree.Root);

        sheetTargets = [];
        sheetsShown = matches[tree.Root];
        sheetsShownKept = 0;
        Target(tree.Root);
        return rows;

        int Count(SheetNode node, bool inScope)
        {
            inScope = inScope || scope!.Contains(node.Entry);
            int count;
            if (node.IsFolder)
            {
                count = node.Children.Sum(c => Count(c, inScope));
            }
            else
            {
                count = inScope && Shows(node) ? 1 : 0;
                if (inScope)
                {
                    scopeTotals = (scopeTotals.Sheets + 1, scopeTotals.Kept + node.KeptSheets,
                        scopeTotals.Cells + node.Cells, scopeTotals.KeptCells + node.KeptCells);
                }
            }

            matches[node] = count;
            return count;
        }

        void Add(SheetNode folder)
        {
            foreach (var child in folder.Children)
            {
                if (matches[child] == 0)
                    continue;

                rows.Add(child);
                if (child.IsFolder && (FolderForcedOpen(child) || openFolders.Contains(child.Path)))
                    Add(child);
            }
        }

        void Target(SheetNode folder)
        {
            foreach (var child in folder.Children)
            {
                var count = matches[child];
                if (count == child.Sheets)
                {
                    sheetTargets.Add(child);
                    sheetsShownKept += child.KeptSheets;
                }
                else if (count > 0)
                {
                    Target(child);
                }
            }
        }

        bool Shows(SheetNode sheet) =>
            (search.Length == 0 || sheet.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                previews.QuestName(sheet.Path)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) &&
            sheetShow switch
            {
                SheetShow.Kept => sheet.KeptSheets > 0,
                SheetShow.Translated => sheet.KeptSheets == 0,
                _ => true,
            };
    }

    // The sheets of the translation chosen for the next start, read once per
    // file from its metadata.
    private void LoadSheetTree()
    {
        var path = packs.TryGet(configuration.ActivePackId)?.FilePath;
        previews.SetPack(path);
        if (string.Equals(path, sheetListPath, StringComparison.Ordinal))
            return;

        sheetListPath = path;
        sheetListError = null;
        sheetTree = null;
        sheetTreeKept = null;
        sheetRows = null;
        openFolders.Clear();
        if (path is null)
            return;

        try
        {
            using var file = HpkFile.Open(path, HpkOpenMode.Metadata);
            sheetTree = SheetTree.Build(Enumerable.Range(0, file.SheetCount).Select(i => (file.SheetNames[i], file.GetSheetCellCount(i))));
            sheetTree.Sort(sheetSort, sheetSortDescending);
        }
        catch (Exception ex) when (ex is HpkFormatException or IOException or UnauthorizedAccessException)
        {
            sheetListError = ex.Message;
        }
    }

    // Checks change from the group list and the tree; the tree follows them.
    private void RefreshSheetTree(SheetTree tree)
    {
        var kept = string.Join('\n', configuration.UntranslatedSheets);
        if (string.Equals(kept, sheetTreeKept, StringComparison.Ordinal))
            return;

        sheetTreeKept = kept;
        tree.Refresh(configuration.UntranslatedSheets);
        sheetRows = null;
    }

    private void DrawViewerLink(SheetNode node)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.ButtonHovered, Ui.Info with { W = 0.25f })
            .Push(ImGuiCol.ButtonActive, Ui.Info with { W = 0.4f })
            .Push(ImGuiCol.Text, Ui.Info);
        if (ImGuiComponents.IconButton("##open", FontAwesomeIcon.ExternalLinkAlt))
            Util.OpenLink(ViewerUrl + string.Join('/', node.Path.Split('/').Select(Uri.EscapeDataString)));

        // Moving onto the link leaves the name; the link stays while hovered.
        if (ImGui.IsItemHovered())
            openLinkHovered = node.Path;
        else if (openLinkHovered == node.Path)
            openLinkHovered = null;
        Ui.Tooltip(Lang.T("content.open_viewer"));
    }

    // What the sheet holds: the quest it belongs to and a few of its strings
    // with their translations.
    private void DrawSheetTooltip(SheetNode node)
    {
        if (!ImGui.IsItemHovered())
            return;

        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + (420 * Ui.Scale));
        ImGui.TextUnformatted(node.Path);
        if (previews.QuestName(node.Path) is { } quest)
            ImGui.TextDisabled(Lang.T("content.quest", quest));

        Ui.Gap(4);
        var samples = previews.Samples(node.Path);
        if (samples.Count == 0)
        {
            ImGui.TextDisabled(Lang.T("content.no_samples"));
            return;
        }

        ImGui.TextDisabled(Lang.T("content.samples"));
        foreach (var sample in samples)
        {
            Ui.Gap(2);
            ImGui.TextWrapped(sample.Original);
            if (sample.Translated is { } translated)
                Ui.Colored(Ui.Good, translated);
        }
    }

    private static string KeptFolder(SheetNode node)
    {
        var folder = node.Parent;
        while (folder is { Depth: >= 0, Listed: false })
            folder = folder.Parent;
        return folder?.Path ?? string.Empty;
    }

    private static string ShowText(SheetShow show) => show switch
    {
        SheetShow.Kept => Lang.T("content.show_kept"),
        SheetShow.Translated => Lang.T("content.show_translated"),
        _ => Lang.T("content.show_all"),
    };

    private static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
