// Checks the compatibility profiles in Harmonia/Assets/Compatibility against
// an installed game: every sheet, row, and column they name must exist, and
// kept columns must be String columns. Run it after every game patch:
//   dotnet run --project tools/CompatibilityCheck -- "<game>/game/sqpack"
// Exit code 1 lists what no longer exists; nothing breaks at runtime then
// (Harmonia ignores it), but the plugin may stop finding that text.
using Lumina;
using Lumina.Data;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
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

Console.WriteLine(problems == 0 ? "All profiles match the game data." : $"{problems} problem(s).");
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
