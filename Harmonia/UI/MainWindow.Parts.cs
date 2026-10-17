using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Localization;
using Harmonia.Packs;

namespace Harmonia.UI;

// Names and descriptions in the tree (Packs/SheetParts): a sheet that holds
// both opens to a row for each, and the column filter shows only the rows of
// one part, so "check all shown" keeps that part of the shown sheets.
internal sealed partial class MainWindow
{
    private static readonly SheetPart[] Parts = [SheetPart.Names, SheetPart.Descriptions];

    private enum SheetColumns
    {
        All,
        Names,
        Descriptions,
    }

    private SheetColumns sheetColumns = SheetColumns.All;

    // The parts of the shown sheets the header checkbox changes while the
    // column filter is on.
    private List<SheetPartMap> partTargets = [];

    private SheetPart? FilteredPart => sheetColumns switch
    {
        SheetColumns.Names => SheetPart.Names,
        SheetColumns.Descriptions => SheetPart.Descriptions,
        _ => null,
    };

    // A row of the tree: a folder, a sheet, or a part of a sheet.
    private readonly record struct SheetRow(SheetNode Node, SheetPartMap? Map = null, SheetPart Part = default);

    private void DrawColumnsFilter(float width)
    {
        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo("##sheet_columns", ColumnsText(sheetColumns));
        if (!combo)
            return;

        foreach (var columns in Enum.GetValues<SheetColumns>())
        {
            if (ImGui.Selectable(ColumnsText(columns), columns == sheetColumns))
            {
                sheetColumns = columns;
                sheetRows = null;
            }
        }
    }

    // The sheet holds the filtered part, or no part is filtered.
    private bool ShowsColumns(SheetNode sheet) =>
        FilteredPart is not { } part || SheetParts.Of(sheet.Path)?.Has(part) == true;

    // A sheet that holds both parts opens to them; the filter opens it to the
    // filtered one.
    private void AddPartRows(List<SheetRow> rows, SheetNode sheet)
    {
        if (SheetParts.Of(sheet.Path) is not { Split: true } map)
            return;
        if (FilteredPart is null && !openFolders.Contains(sheet.Path))
            return;

        foreach (var part in Parts)
        {
            if (map.Has(part) && (FilteredPart is null || FilteredPart == part))
                rows.Add(new SheetRow(sheet, map, part));
        }
    }

    private void DrawPartRow(SheetRow row)
    {
        var map = row.Map!;
        using var id = ImRaii.PushId(row.Node.Path + "#" + PartKey(row.Part));
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var kept = SheetParts.IsKept(configuration.UntranslatedSheets, configuration.UntranslatedParts, map, row.Part);
        using (ImRaii.Disabled(row.Node.Covered || row.Node.Listed))
        {
            if (Ui.Checkbox("##keep", kept, false))
                TogglePart([map], row.Part, kept ? KeepState.All : KeepState.None);
        }

        Ui.Tooltip(row.Node.Listed || row.Node.Covered
            ? Lang.T("content.part_covered")
            : kept ? Lang.T("content.check_translate") : Lang.T("content.check_keep"));

        ImGui.TableNextColumn();
        var indent = (row.Node.Depth + 1) * ImGui.GetStyle().IndentSpacing;
        using (ImRaii.PushIndent(indent, false))
        {
            var flags = ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding |
                ImGuiTreeNodeFlags.Leaf;
            ImGui.TreeNodeEx(Lang.T(PartKey(row.Part)), flags);
            if (ImGui.IsItemClicked() && !row.Node.Listed && !row.Node.Covered)
                TogglePart([map], row.Part, kept ? KeepState.All : KeepState.None);
            Ui.Tooltip(Lang.T("content.part_columns",
                string.Join(", ", map.Columns(row.Part).Select(static c => c.ToString(CultureInfo.InvariantCulture))), row.Node.Path));
        }

        ImGui.TableNextColumn();
    }

    // The header checkbox while the column filter is on: the filtered part of
    // every shown sheet.
    private void DrawPartHeader(SheetPart part)
    {
        var state = SheetParts.StateOf(configuration.UntranslatedSheets, configuration.UntranslatedParts, partTargets, part);
        if (Ui.Checkbox("##keep_all", state == KeepState.All, state == KeepState.Some))
            TogglePart(partTargets, part, state);
        Ui.Tooltip(Lang.T(state == KeepState.All ? "content.uncheck_part_shown" : "content.check_part_shown",
            Lang.T(PartName(part)), Number(partTargets.Count)));
    }

    private void TogglePart(IEnumerable<SheetPartMap> maps, SheetPart part, KeepState state)
    {
        foreach (var map in maps)
        {
            if (state == KeepState.All)
                SheetParts.Translate(configuration.UntranslatedSheets, configuration.UntranslatedParts, map, part);
            else
                SheetParts.Keep(configuration.UntranslatedSheets, configuration.UntranslatedParts, map, part);
        }

        save();
    }

    // The parts of a sheet the player keeps, for its tooltip.
    private string KeptParts(string sheet) =>
        SheetParts.Of(sheet) is { } map
            ? string.Join(", ", Parts
                .Where(p => SheetParts.IsKept(configuration.UntranslatedSheets, configuration.UntranslatedParts, map, p))
                .Select(p => Lang.T(PartName(p))))
            : string.Empty;

    private static string ColumnsText(SheetColumns columns) => columns switch
    {
        SheetColumns.Names => Lang.T("content.columns_names"),
        SheetColumns.Descriptions => Lang.T("content.columns_descriptions"),
        _ => Lang.T("content.columns_all"),
    };

    private static string PartKey(SheetPart part) => part == SheetPart.Names ? "parts.names" : "parts.descriptions";

    private static string PartName(SheetPart part) => part == SheetPart.Names ? "parts.names_short" : "parts.descriptions_short";
}
