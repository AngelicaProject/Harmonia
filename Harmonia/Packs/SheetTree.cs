namespace Harmonia.Packs;

public enum KeepState
{
    None,
    Some,
    All,
}

// A folder ("quest/000/") or a sheet ("quest/000/ClsArc000_00021", "Item")
// of a pack, with how much of it the player keeps in the game's language.
public sealed class SheetNode
{
    private readonly List<SheetNode> children = [];

    internal SheetNode(string name, string path, bool isFolder, SheetNode? parent)
    {
        Name = name;
        Path = path;
        IsFolder = isFolder;
        Parent = parent;
        Depth = parent is null ? -1 : parent.Depth + 1;
    }

    public string Name { get; }

    // Folders end with '/', which makes the path the prefix SheetFilter
    // matches for the folder's entry.
    public string Path { get; }

    public bool IsFolder { get; }

    public SheetNode? Parent { get; }

    // 0 for the top level; the root is -1.
    public int Depth { get; }

    public IReadOnlyList<SheetNode> Children => children;

    public int Sheets { get; private set; }

    public long Cells { get; private set; }

    public int KeptSheets { get; private set; }

    public long KeptCells { get; private set; }

    // The node's own entry is in the list.
    public bool Listed { get; private set; }

    // A folder above the node is in the list.
    public bool Covered { get; private set; }

    // "quest/*" for a folder, the sheet name for a sheet.
    public string Entry => IsFolder ? Path + "*" : Path;

    public KeepState State => KeptSheets == 0 ? KeepState.None : KeptSheets == Sheets ? KeepState.All : KeepState.Some;

    internal List<SheetNode> MutableChildren => children;

    internal void SetSize(int sheets, long cells)
    {
        Sheets = sheets;
        Cells = cells;
    }

    internal void SetKept(bool listed, bool covered, int sheets, long cells)
    {
        Listed = listed;
        Covered = covered;
        KeptSheets = sheets;
        KeptCells = cells;
    }
}

// The sheets of a pack as a tree of folders, and the edits of
// Configuration.UntranslatedSheets that keep or translate one node. A folder
// is kept with one entry ("quest/*"), which also covers sheets later versions
// of the pack add to it.
public sealed class SheetTree
{
    private readonly Dictionary<string, SheetNode> entries;

    private SheetTree(SheetNode root, Dictionary<string, SheetNode> entries)
    {
        Root = root;
        this.entries = entries;
    }

    public SheetNode Root { get; }

    // The node of an entry ("Item", "quest/*"); null when the pack has none.
    public SheetNode? Find(string entry) => entries.GetValueOrDefault(entry);

    public static SheetTree Build(IEnumerable<(string Name, long Cells)> sheets)
    {
        var root = new SheetNode(string.Empty, string.Empty, true, null);
        var folders = new Dictionary<string, SheetNode>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, SheetNode>(StringComparer.Ordinal);
        foreach (var (name, cells) in sheets)
        {
            var parent = root;
            var start = 0;
            int slash;
            while ((slash = name.IndexOf('/', start)) > start)
            {
                var path = name[..(slash + 1)];
                if (!folders.TryGetValue(path, out var folder))
                {
                    folder = new SheetNode(name[start..slash], path, true, parent);
                    folders.Add(path, folder);
                    nodes[folder.Entry] = folder;
                    parent.MutableChildren.Add(folder);
                }

                parent = folder;
                start = slash + 1;
            }

            var sheet = new SheetNode(name[start..], name, false, parent);
            sheet.SetSize(1, cells);
            parent.MutableChildren.Add(sheet);
            nodes[sheet.Entry] = sheet;
        }

        Total(root);
        var tree = new SheetTree(root, nodes);
        tree.Sort(SheetSort.Name, false);
        return tree;
    }

    public void Sort(SheetSort by, bool descending) => Sort(Root, by, descending);

    // Recomputes what is kept from the configured entries.
    public void Refresh(IEnumerable<string> entries) =>
        Refresh(Root, new HashSet<string>(entries, StringComparer.Ordinal), false);

    // Keeps the node in the game's language: its own entry replaces the
    // entries under it.
    public static void Keep(List<string> entries, SheetNode node)
    {
        if (node.Depth < 0)
            return;

        RemoveUnder(entries, node);
        if (!node.Covered)
            entries.Add(node.Entry);
    }

    // Translates the node again. A kept folder above it is replaced by entries
    // for its other children, level by level down to the node.
    public static void Translate(List<string> entries, SheetNode node)
    {
        if (node.Depth < 0)
            return;

        var path = new Stack<SheetNode>();
        for (var parent = node.Parent; parent is { Depth: >= 0 }; parent = parent.Parent)
            path.Push(parent);

        foreach (var folder in path)
        {
            if (!entries.Contains(folder.Entry, StringComparer.Ordinal))
                continue;

            RemoveUnder(entries, folder);
            entries.AddRange(folder.Children.Select(static c => c.Entry));
        }

        RemoveUnder(entries, node);
    }

    // The same edits for an entry of a sheet group, which is a top-level sheet
    // or folder, without the pack's tree.
    public static void Keep(List<string> entries, string entry)
    {
        RemoveUnder(entries, entry);
        entries.Add(entry);
    }

    public static void Translate(List<string> entries, string entry) => RemoveUnder(entries, entry);

    public static KeepState StateOf(IReadOnlyCollection<string> entries, string entry)
    {
        if (entries.Contains(entry, StringComparer.Ordinal))
            return KeepState.All;
        return SheetFilter.IsFolder(entry) && entries.Any(e => e.StartsWith(entry[..^1], StringComparison.Ordinal))
            ? KeepState.Some
            : KeepState.None;
    }

    private static void RemoveUnder(List<string> entries, SheetNode node) => RemoveUnder(entries, node.Entry);

    private static void RemoveUnder(List<string> entries, string entry) =>
        entries.RemoveAll(e => SheetFilter.IsFolder(entry)
            ? e.StartsWith(entry[..^1], StringComparison.Ordinal)
            : string.Equals(e, entry, StringComparison.Ordinal));

    private static void Total(SheetNode node)
    {
        if (!node.IsFolder)
            return;

        foreach (var child in node.Children)
            Total(child);
        node.SetSize(node.Children.Sum(static c => c.Sheets), node.Children.Sum(static c => c.Cells));
    }

    private static void Refresh(SheetNode node, HashSet<string> entries, bool covered)
    {
        var listed = node.Depth >= 0 && entries.Contains(node.Entry);
        if (!node.IsFolder)
        {
            var kept = covered || listed;
            node.SetKept(listed, covered, kept ? 1 : 0, kept ? node.Cells : 0);
            return;
        }

        foreach (var child in node.Children)
            Refresh(child, entries, covered || listed);
        node.SetKept(listed, covered, node.Children.Sum(static c => c.KeptSheets), node.Children.Sum(static c => c.KeptCells));
    }

    // Folders first, each level on its own.
    private static void Sort(SheetNode node, SheetSort by, bool descending)
    {
        if (!node.IsFolder)
            return;

        node.MutableChildren.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder)
                return a.IsFolder ? -1 : 1;

            var order = by == SheetSort.Lines ? a.Cells.CompareTo(b.Cells) : 0;
            if (order == 0)
                order = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (order == 0)
                order = string.CompareOrdinal(a.Name, b.Name);
            return descending ? -order : order;
        });
        foreach (var child in node.Children)
            Sort(child, by, descending);
    }
}

public enum SheetSort
{
    Name,
    Lines,
}
