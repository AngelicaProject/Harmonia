using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Localization;
using Harmonia.Packs;

namespace Harmonia.UI;

// Sheet groups: one checkbox keeps several sheets in the game's language, and
// selecting a group shows only its sheets in the tree. The player changes the
// built-in groups and makes their own; a group only chooses sheets, so
// changing or deleting it never changes the checks.
internal sealed partial class MainWindow
{
    private const int MaxGroupCandidates = 100;
    private const int ShownGroupEntries = 6;

    private string? selectedGroup;
    private string? renamingGroup;
    private string renameText = string.Empty;
    private bool renameFocus;
    private string addSearch = string.Empty;

    private IReadOnlyList<SheetGroup> Groups => SheetGroups.Effective(configuration.SheetGroupChanges);

    private void DrawGroupList(IReadOnlyList<SheetGroup> groups, float height)
    {
        using var list = ImRaii.Child("##group_list", new Vector2(210 * Ui.Scale, height), true);
        if (!list)
            return;

        ImGui.TextDisabled(Lang.T("groups.title"));
        Ui.Gap(2);
        DrawGroupRow(null, Lang.T("groups.all"), sheetTree?.Root.Sheets);
        ImGui.Separator();

        if (groups.Count == 0)
            Ui.Hint(Lang.T("groups.none"));
        foreach (var group in groups)
            DrawGroupRow(group, GroupName(group), sheetTree is null ? null : group.Entries.Sum(e => sheetTree.Find(e)?.Sheets ?? 0));

        Ui.Gap(4);
        if (Ui.LinkButton(FontAwesomeIcon.Plus, Lang.T("groups.new")))
            CreateGroup([]);
    }

    // A group, or all sheets when group is null: its checkbox, its name to
    // select it, and how many sheets of the pack it has. The whole row is one
    // selectable; the checkbox and the texts are drawn over it.
    private void DrawGroupRow(SheetGroup? group, string name, int? sheets)
    {
        using var id = ImRaii.PushId(group?.Key ?? "##all");
        var frame = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var start = ImGui.GetCursorPos();
        var width = ImGui.GetContentRegionAvail().X;
        var selected = string.Equals(group?.Key, selectedGroup, StringComparison.Ordinal);
        if (ImGui.Selectable("##select", selected, ImGuiSelectableFlags.AllowItemOverlap, new Vector2(width, frame)) && !selected)
        {
            selectedGroup = group?.Key;
            renamingGroup = null;
            sheetRows = null;
        }

        if (group is not null)
            Ui.Tooltip(name + "\n" + GroupHint(group));
        var end = ImGui.GetCursorPos();

        ImGui.SetCursorPos(start);
        if (group is not null)
        {
            var state = GroupState(group);
            using (ImRaii.Disabled(group.Entries.Count == 0))
            {
                if (Ui.Checkbox("##keep", state == KeepState.All, state == KeepState.Some))
                    ToggleGroup(group, state);
            }

            Ui.Tooltip(Lang.T(state == KeepState.All ? "groups.uncheck_group" : "groups.check_group", Number(sheets ?? group.Entries.Count)));
        }

        var count = sheets is { } n ? Number(n) : string.Empty;
        var countWidth = ImGui.CalcTextSize(count).X;
        var textY = start.Y + ((frame - ImGui.GetTextLineHeight()) / 2);
        var nameX = start.X + frame + spacing;
        var countX = start.X + width - countWidth;

        // A long name is cut off before the count.
        var origin = ImGui.GetWindowPos() - new Vector2(ImGui.GetScrollX(), ImGui.GetScrollY());
        ImGui.PushClipRect(origin + new Vector2(nameX, start.Y), origin + new Vector2(countX - spacing, start.Y + frame), true);
        ImGui.SetCursorPos(Ui.Snap(new Vector2(nameX, textY)));
        ImGui.TextUnformatted(name);
        ImGui.PopClipRect();

        if (count.Length > 0)
        {
            ImGui.SetCursorPos(Ui.Snap(new Vector2(countX, textY)));
            ImGui.TextDisabled(count);
        }

        ImGui.SetCursorPos(end);
    }

    private KeepState GroupState(SheetGroup group)
    {
        var states = GroupStates(group);
        if (states.Length > 0 && states.All(static s => s == KeepState.All))
            return KeepState.All;
        return states.Any(static s => s != KeepState.None) ? KeepState.Some : KeepState.None;
    }

    // With a pack, only the group's sheets the pack has count: a sheet it does
    // not translate is the same in either state, and "Check all" in the tree
    // never adds it.
    private KeepState[] GroupStates(SheetGroup group)
    {
        var nodes = sheetTree is null ? [] : group.Entries.Select(sheetTree.Find).OfType<SheetNode>().ToArray();
        return nodes.Length > 0
            ? nodes.Select(static n => n.State).ToArray()
            : group.Entries.Select(e => SheetTree.StateOf(configuration.UntranslatedSheets, e)).ToArray();
    }

    private void ToggleGroup(SheetGroup group, KeepState state)
    {
        foreach (var entry in group.Entries)
        {
            if (state == KeepState.All)
                SheetTree.Translate(configuration.UntranslatedSheets, entry);
            else
                SheetTree.Keep(configuration.UntranslatedSheets, entry);
        }

        save();
    }

    // The selected group's name with its actions: add sheets, rename, delete,
    // and for a changed built-in group, restore it.
    private void DrawGroupHeader(SheetGroup group)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var button = ImGui.GetFrameHeight();
        var addText = Lang.T("groups.add_sheets");
        var actions = Ui.ButtonWidth(FontAwesomeIcon.Plus, addText) + ((button + spacing) * 2);
        var right = ImGui.GetWindowContentRegionMax().X;

        if (string.Equals(renamingGroup, group.Key, StringComparison.Ordinal))
            DrawRename(group, right - actions - spacing);
        else
            Ui.Heading(headingFont, GroupName(group));

        Ui.AlignRight(actions, right, true);
        if (Ui.Button(FontAwesomeIcon.Plus, addText))
        {
            addSearch = string.Empty;
            ImGui.OpenPopup("##add_sheets");
        }

        ImGui.SameLine(0, spacing);
        if (ImGuiComponents.IconButton("##rename", FontAwesomeIcon.Pen))
            StartRename(group);
        Ui.Tooltip(Lang.T("groups.rename"));

        ImGui.SameLine(0, spacing);
        if (ImGuiComponents.IconButton("##delete", FontAwesomeIcon.Trash))
            ImGui.OpenPopup("##delete_group");
        Ui.Tooltip(Lang.T("groups.delete"));

        DrawAddSheetsPopup(group);
        DrawDeleteGroupPopup(group);

        Ui.Hint(GroupHint(group));
        if (group.BuiltIn && group.Edited)
        {
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding with { Y = 0 });
            if (Ui.LinkButton(FontAwesomeIcon.Undo, Lang.T("groups.restore")))
            {
                SheetGroups.Restore(configuration.SheetGroupChanges, group.Key);
                SaveGroups();
            }
        }
    }

    private void StartRename(SheetGroup group)
    {
        renamingGroup = group.Key;
        renameText = GroupName(group);
        renameFocus = true;
    }

    // Enter or leaving the field saves the name, Escape cancels. An empty name
    // gives a built-in group its own name back.
    private void DrawRename(SheetGroup group, float width)
    {
        if (renameFocus)
        {
            ImGui.SetKeyboardFocusHere();
            renameFocus = false;
        }

        var hint = group.BuiltIn && SheetGroups.BuiltIn(group.Key) is { } builtIn ? Lang.T(GroupKey(builtIn)) : Lang.T("groups.name_hint");
        ImGui.SetNextItemWidth(Math.Max(120 * Ui.Scale, width));
        var entered = ImGui.InputTextWithHint("##name", hint, ref renameText, 100, ImGuiInputTextFlags.EnterReturnsTrue);
        if (!entered && !ImGui.IsItemDeactivated())
            return;

        renamingGroup = null;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            return;

        var name = renameText.Trim();
        if (group.BuiltIn && SheetGroups.BuiltIn(group.Key) is { } original && string.Equals(name, Lang.T(GroupKey(original)), StringComparison.Ordinal))
            name = string.Empty;
        if (name.Length == 0 && !group.BuiltIn)
            return;

        SaveGroup(group, name, group.Entries);
    }

    private void DrawAddSheetsPopup(SheetGroup group)
    {
        ImGui.SetNextWindowSize(new Vector2(420 * Ui.Scale, 0));
        using var popup = ImRaii.Popup("##add_sheets");
        if (!popup)
            return;

        ImGui.TextDisabled(Lang.T("groups.add"));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", Lang.T("groups.search"), ref addSearch, 256);
        DrawCandidates(group);
    }

    // Folders and sheets of the pack that match the search; without a pack,
    // or for a sheet the pack lacks, the typed name itself.
    private void DrawCandidates(SheetGroup group)
    {
        var search = addSearch.Trim();
        if (search.Length == 0)
        {
            Ui.Hint(Lang.T(sheetTree is null ? "groups.type_name" : "groups.type_search"));
            return;
        }

        var found = new List<SheetNode>();
        var total = 0;
        if (sheetTree is not null)
            Collect(sheetTree.Root);

        if (found.Count == 0 && !group.Entries.Contains(search, StringComparer.Ordinal))
        {
            if (Ui.LinkButton(FontAwesomeIcon.Plus, Lang.T("groups.add_name", search)))
            {
                SaveGroup(group, group.Name, [.. group.Entries, search]);
                addSearch = string.Empty;
            }

            Ui.Tooltip(Lang.T("groups.add_name_hint"));
            return;
        }

        var row = ImGui.GetFrameHeightWithSpacing();
        var height = (Math.Min(found.Count, 10) * row) + (ImGui.GetStyle().WindowPadding.Y * 2);
        using (var list = ImRaii.Child("##candidates", new Vector2(0, height), true))
        {
            if (list)
            {
                foreach (var node in found)
                {
                    using var id = ImRaii.PushId(node.Entry);
                    using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
                    {
                        if (ImGuiComponents.IconButton("##add", FontAwesomeIcon.Plus))
                            SaveGroup(group, group.Name, [.. group.Entries, node.Entry]);
                    }

                    Ui.Tooltip(Lang.T("groups.add_one"));
                    ImGui.SameLine();
                    DrawEntryLabel(node.Entry);
                }
            }
        }

        if (total > found.Count)
            Ui.Hint(Lang.T("groups.more", found.Count, Number(total)));

        void Collect(SheetNode folder)
        {
            foreach (var child in folder.Children)
            {
                if (child.Path.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !group.Entries.Contains(child.Entry, StringComparer.Ordinal))
                {
                    total++;
                    if (found.Count < MaxGroupCandidates)
                        found.Add(child);
                }

                if (child.IsFolder)
                    Collect(child);
            }
        }
    }

    private void DrawDeleteGroupPopup(SheetGroup group)
    {
        using var popup = ImRaii.Popup("##delete_group");
        if (!popup)
            return;

        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + (320 * Ui.Scale)))
            ImGui.TextWrapped(Lang.T("groups.delete_confirm", GroupName(group)));
        Ui.Gap(4);
        using (ImRaii.PushColor(ImGuiCol.Button, Ui.Bad with { W = 0.6f }))
        {
            if (Ui.Button(FontAwesomeIcon.Trash, Lang.T("groups.delete")))
            {
                SheetGroups.Remove(configuration.SheetGroupChanges, group.Key);
                selectedGroup = null;
                SaveGroups();
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (Ui.Button(FontAwesomeIcon.Times, Lang.T("groups.cancel")))
            ImGui.CloseCurrentPopup();
    }

    // The group's entries the pack does not have; they count again once a
    // translation has them.
    private void DrawMissingEntries(SheetGroup group)
    {
        var missing = group.Entries.Where(e => sheetTree?.Find(e) is null).ToArray();
        if (missing.Length == 0)
            return;

        Ui.Hint(Lang.T("groups.missing", string.Join(", ", missing)));
    }

    // Without a pack there is no tree: the group's entries as a list.
    private void DrawGroupEntries(SheetGroup group)
    {
        Ui.Gap(4);
        string? removed = null;
        foreach (var entry in group.Entries)
        {
            using var id = ImRaii.PushId(entry);
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
            {
                if (ImGuiComponents.IconButton("##remove", FontAwesomeIcon.Times))
                    removed = entry;
            }

            Ui.Tooltip(Lang.T("groups.remove"));
            ImGui.SameLine();
            DrawEntryLabel(entry);
        }

        if (removed is not null)
            SaveGroup(group, group.Name, group.Entries.Where(e => !string.Equals(e, removed, StringComparison.Ordinal)).ToList());
    }

    // An entry with its icon and, with a pack, its line count.
    private void DrawEntryLabel(string entry)
    {
        ImGui.AlignTextToFramePadding();
        Ui.Icon(SheetFilter.IsFolder(entry) ? FontAwesomeIcon.Folder : FontAwesomeIcon.File, Ui.Muted);
        ImGui.SameLine();
        ImGui.TextUnformatted(entry);
        if (sheetTree is null)
            return;

        var node = sheetTree.Find(entry);
        ImGui.SameLine();
        ImGui.TextDisabled(node is null
            ? Lang.T("groups.not_in_pack")
            : Lang.T("content.line_count", Number(node.Cells)));
    }

    // Right click on a folder or sheet in the tree: put it into a group or
    // take it out, or start a new group with it.
    private void DrawSheetMenu(SheetNode node)
    {
        using var menu = ImRaii.ContextPopupItem("##groups_menu");
        if (!menu)
            return;

        ImGui.TextDisabled(Lang.T("groups.menu_title", node.Entry));
        ImGui.Separator();

        // Checkboxes, unlike menu items, leave the menu open to show the result
        // and to change several groups at once.
        foreach (var group in Groups)
        {
            var member = group.Entries.Contains(node.Entry, StringComparer.Ordinal);
            if (!ImGui.Checkbox(GroupName(group) + "##" + group.Key, ref member))
                continue;

            SaveGroup(group, group.Name, member
                ? [.. group.Entries, node.Entry]
                : group.Entries.Where(e => !string.Equals(e, node.Entry, StringComparison.Ordinal)).ToList());
        }

        ImGui.Separator();
        if (ImGui.MenuItem(Lang.T("groups.menu_new")))
            CreateGroup([node.Entry]);
    }

    // A new group is selected with its name ready to edit.
    private void CreateGroup(IReadOnlyList<string> entries)
    {
        var key = SheetGroups.NewKey(configuration.SheetGroupChanges);
        var names = Groups.Select(GroupName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name = Lang.T("groups.default_name");
        for (var n = 2; names.Contains(name); n++)
            name = Lang.T("groups.default_name") + " " + n.ToString(System.Globalization.CultureInfo.CurrentCulture);

        SheetGroups.Save(configuration.SheetGroupChanges, key, name, entries);
        SaveGroups();
        selectedGroup = key;
        renamingGroup = key;
        renameText = name;
        renameFocus = true;
    }

    private void SaveGroup(SheetGroup group, string? name, IReadOnlyList<string> entries)
    {
        SheetGroups.Save(configuration.SheetGroupChanges, group.Key, name, entries);
        SaveGroups();
    }

    private void SaveGroups()
    {
        save();
        sheetRows = null;
    }

    private static string GroupName(SheetGroup group) => group.Name ?? Lang.T(GroupKey(group));

    // A built-in group as shipped is described by its hint, any other by its
    // sheets.
    private static string GroupHint(SheetGroup group)
    {
        if (group.BuiltIn && !group.EntriesEdited)
            return Lang.T(GroupKey(group) + "_hint");
        if (group.Entries.Count == 0)
            return Lang.T("groups.no_sheets");

        var shown = string.Join(", ", group.Entries.Take(ShownGroupEntries));
        return group.Entries.Count <= ShownGroupEntries
            ? shown
            : Lang.T("groups.more_entries", shown, group.Entries.Count - ShownGroupEntries);
    }

    // The text key of a built-in group's name; its hint adds "_hint".
    internal static string GroupKey(SheetGroup group) => "sheets." + group.Key;
}
