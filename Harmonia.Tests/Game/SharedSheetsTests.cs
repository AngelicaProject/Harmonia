using Harmonia.Game;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Harmonia.Tests.Packs;
using Lumina;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Xunit;
using Xunit.Abstractions;

namespace Harmonia.Tests.Game;

// Needs an installed game and a pack made for it:
//   HARMONIA_GAME=<game>/game/sqpack HARMONIA_PACK=<file.hpk> dotnet test --filter SharedSheets
// Without both it does nothing.
public sealed class SharedSheetsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Every_string_Lumina_reads_is_the_one_the_game_shows()
    {
        var game = Environment.GetEnvironmentVariable("HARMONIA_GAME");
        var packPath = Environment.GetEnvironmentVariable("HARMONIA_PACK");
        if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(packPath))
            return;

        var options = new LuminaOptions { PanicOnSheetChecksumMismatch = false };
        using var shared = new GameData(game, options);
        using var files = new GameData(game, options);
        using var runtime = new TranslationRuntime(HpkFile.Open(packPath, HpkOpenMode.Full), "test", packPath);

        var sheets = new SharedSheets(shared, runtime, new NullLog());
        await sheets.Completed;
        Assert.Null(sheets.Error);
        output.WriteLine($"{sheets.Sheets} sheets, {sheets.Cells} cells, {sheets.Guarded} left for their global parameters, {sheets.Elapsed.TotalSeconds:F1} s");
        Assert.True(sheets.Cells > 0);

        long translated = 0, original = 0;
        var guardedBySheet = new Dictionary<string, int>();
        foreach (var name in runtime.SheetNames)
        {
            if (files.GetFile<Lumina.Data.Files.Excel.ExcelHeaderFile>($"exd/{name}.exh") is null)
                continue;

            var before = files.Excel.GetRawSheet(name);
            var after = shared.Excel.GetRawSheet(name);
            var subrows = before is RawSubrowExcelSheet;
            var columns = before.Columns
                .Select(static (c, i) => (Column: c, Index: i))
                .Where(static c => c.Column.Type == ExcelColumnDataType.String)
                .Select(static c => new StringColumn((ushort)c.Index, c.Column.Offset))
                .ToArray();
            var packSheet = runtime.FindSheet(name, subrows, columns);

            void Check(uint rowId, ushort subrowId, Func<int, byte[]> was, Func<int, byte[]> now)
            {
                for (ushort ordinal = 0; ordinal < columns.Length; ordinal++)
                {
                    var source = was(columns[ordinal].Index);
                    var expected = source;
                    byte[]? shown = null;
                    if (packSheet >= 0 && runtime.TryGetShown(packSheet, rowId, subrowId, ordinal, source, out var cell))
                    {
                        unsafe
                        {
                            shown = new ReadOnlySpan<byte>(cell.String, cell.Length).ToArray();
                        }
                    }

                    // A translation that reads the player's state where the
                    // original does not is not given to plugins.
                    if (shown is not null && SharedSheets.UsesGlobals(shown) && !SharedSheets.UsesGlobals(source))
                    {
                        guardedBySheet[name] = guardedBySheet.GetValueOrDefault(name) + 1;
                        shown = null;
                    }

                    if (shown is not null)
                    {
                        expected = shown;
                        translated++;
                    }
                    else
                    {
                        original++;
                    }

                    if (!expected.AsSpan().SequenceEqual(now(columns[ordinal].Index)))
                        Assert.Fail($"{name} row {rowId}.{subrowId} column {columns[ordinal].Index} reads differently from what the game shows.");
                }
            }

            if (subrows)
            {
                var a = files.Excel.GetSubrowSheet<RawSubrow>(name: name);
                var b = shared.Excel.GetSubrowSheet<RawSubrow>(name: name);
                foreach (var collection in a)
                {
                    foreach (var row in collection)
                    {
                        var other = b.GetSubrow(row.RowId, row.SubrowId);
                        Check(row.RowId, row.SubrowId, c => row.ReadStringColumn(c).Data.ToArray(), c => other.ReadStringColumn(c).Data.ToArray());
                    }
                }
            }
            else
            {
                var a = files.Excel.GetSheet<RawRow>(name: name);
                var b = shared.Excel.GetSheet<RawRow>(name: name);
                foreach (var row in a)
                {
                    var other = b.GetRow(row.RowId);
                    Check(row.RowId, 0, c => row.ReadStringColumn(c).Data.ToArray(), c => other.ReadStringColumn(c).Data.ToArray());
                }
            }

            _ = after;
        }

        output.WriteLine($"{translated} strings read as the translation, {original} as the original");
        Assert.Equal(sheets.Cells, translated);
        Assert.Equal(sheets.Guarded, guardedBySheet.Values.Sum());
        foreach (var (name, count) in guardedBySheet.OrderByDescending(static g => g.Value).Take(25))
            output.WriteLine($"  left as in the files: {name} {count}");

        // Unloading puts every page back.
        sheets.Dispose();
        var addon = shared.Excel.GetSheet<RawRow>(name: "Addon");
        var addonFiles = files.Excel.GetSheet<RawRow>(name: "Addon");
        foreach (var row in addonFiles.Take(500))
            Assert.True(row.ReadStringColumn(0).Data.Span.SequenceEqual(addon.GetRow(row.RowId).ReadStringColumn(0).Data.Span));
    }
}
