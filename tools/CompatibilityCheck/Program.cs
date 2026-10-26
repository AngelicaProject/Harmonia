// Checks the compatibility profiles in Harmonia/Assets/Compatibility against
// an installed game: every sheet, row, and column they name must exist, and
// kept columns must be String columns. The same for the built-in sheet groups
// and the names and descriptions of Packs/SheetParts, and the Addon rows of
// Runtime/ServerNames must be the ones the scripts send. Run it after every game
// patch:
//   dotnet run --project tools/CompatibilityCheck -- "<game>/game/sqpack"
// Exit code 1 lists what no longer exists; nothing breaks at runtime then
// (Harmonia ignores it), but the plugin may stop finding that text.
using Lumina;
using Lumina.Data;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Harmonia.Packs;
using Harmonia.Runtime;
using Newtonsoft.Json.Linq;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: CompatibilityCheck <sqpack directory> [profiles directory]");
    return 2;
}

var profilesDir = args.Length > 1 ? args[1] : FindProfiles();
var data = new GameData(args[0], new LuminaOptions { PanicOnSheetChecksumMismatch = false });
var problems = 0;

foreach (var file in Directory.GetFiles(profilesDir, "*.json").Order(StringComparer.Ordinal))
{
    var name = Path.GetFileName(file);
    var profile = JObject.Parse(File.ReadAllText(file));
    var parts = new List<JObject> { profile };
    parts.AddRange((profile["optional"] as JArray ?? []).OfType<JObject>());
    foreach (var part in parts)
    {
        foreach (var sheet in (part["sheets"] as JArray ?? []).Values<string>())
            Sheet(name, sheet!);
        foreach (var (sheet, rows) in part["rows"] as JObject ?? [])
        {
            if (Sheet(name, sheet) is { } raw)
                Rows(name, raw, sheet, rows!.Values<uint>());
        }

        foreach (var cells in (part["cells"] as JArray ?? []).OfType<JObject>())
        {
            var sheet = (string)cells["sheet"]!;
            if (Sheet(name, sheet) is not { } raw)
                continue;
            foreach (var column in cells["columns"]!.Values<int>())
            {
                if (column >= raw.Columns.Count || raw.Columns[column].Type != ExcelColumnDataType.String)
                    Problem(name, $"{sheet}: column {column} is not a String column");
            }

            if (cells["rows"] is JArray rows)
                Rows(name, raw, sheet, rows.Values<uint>());
        }
    }
}

foreach (var group in SheetGroups.All)
{
    foreach (var entry in group.Entries.Where(static e => !SheetFilter.IsFolder(e)))
        Sheet("groups/" + group.Key, entry);
}

foreach (var map in SheetParts.All)
{
    if (Sheet("parts", map.Sheet) is not { } raw)
        continue;
    foreach (var column in map.Names.Concat(map.Descriptions))
    {
        if (column >= raw.Columns.Count || raw.Columns[(int)column].Type != ExcelColumnDataType.String)
            Problem("parts", $"{map.Sheet}: column {column} is not a String column");
    }
}

// CustomTalk gives a script its constants: a row's 30 instruction names
// (String columns 3-32) and their values in the same order (columns 33-62).
if (Sheet("server", "CustomTalk") is not null)
{
    var customTalk = data.Excel.GetSheet<RawRow>(Language.English, "CustomTalk");
    foreach (var name in ServerNames.All)
    {
        var value = customTalk.Select(row => Constant(row, name.Script, name.Constant)).FirstOrDefault(static v => v is not null);
        if (value != name.AddonRow)
            Problem("server", $"{name.Script} {name.Constant} is {value?.ToString() ?? "missing"}, not Addon row {name.AddonRow}");
    }
}

Console.WriteLine(problems == 0 ? "All profiles, groups, parts, and server names match the game data." : $"{problems} problem(s).");
return problems == 0 ? 0 : 1;

RawExcelSheet? Sheet(string profile, string sheet)
{
    try
    {
        return data.Excel.GetRawSheet(sheet, Language.English);
    }
    catch (Exception)
    {
        try
        {
            return data.Excel.GetRawSheet(sheet, Language.None);
        }
        catch (Exception)
        {
            Problem(profile, $"sheet {sheet} does not exist");
            return null;
        }
    }
}

void Rows(string profile, RawExcelSheet raw, string sheet, IEnumerable<uint> rows)
{
    var missing = rows.Where(r => !raw.HasRow(r)).ToList();
    if (missing.Count > 0)
        Problem(profile, $"{sheet}: rows {string.Join(", ", missing)} do not exist");
}

static uint? Constant(RawRow row, string script, string constant)
{
    if (row.ReadStringColumn(2).ExtractText() != script)
        return null;
    for (var i = 3; i < 33; i++)
    {
        if (row.ReadStringColumn(i).ExtractText() == constant)
            return row.ReadUInt32Column(i + 30);
    }

    return null;
}

void Problem(string profile, string text)
{
    problems++;
    Console.WriteLine($"{profile}: {text}");
}

static string FindProfiles()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "Harmonia", "Assets", "Compatibility");
        if (Directory.Exists(candidate))
            return candidate;
    }

    throw new DirectoryNotFoundException("Harmonia/Assets/Compatibility not found; pass it as the second argument.");
}
