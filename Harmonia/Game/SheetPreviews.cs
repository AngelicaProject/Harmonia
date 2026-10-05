using Dalamud.Plugin.Services;
using Harmonia.Dictionary;
using Harmonia.Packs.Hpk;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Harmonia.Game;

// One string of a sheet as the game files have it, and the pack's text for it.
internal sealed record SheetSample(string Original, string? Translated);

// What helps the player recognize a sheet on the Coverage page: a few of its
// strings with their translations, and for quest sheets the quest's name.
// Originals come from the game files through Lumina; translations from the
// selected pack, opened and verified on its own (it may not be the one this
// session loaded). Reference only: nothing here decides what the game shows.
internal sealed class SheetPreviews(IDataManager data, IHarmoniaLog log) : IDisposable
{
    private const int MaxSamples = 3;
    private const int MaxRows = 400;
    private const int MaxLength = 140;
    private const string QuestFolder = "quest/";

    private readonly Dictionary<string, IReadOnlyList<SheetSample>> samples = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private string? packPath;
    private Task<HpkFile?>? packTask;
    private Task<QuestNames?>? questTask;

    // A quest sheet is named after the quest's Id ("ClsArc000_00021"); the
    // pack's Quest sheet has the translated name in column NameOrdinal.
    private sealed record QuestNames(Dictionary<string, (uint RowId, string Name)> ById, int NameOrdinal);

    // Turns true once the quest names are read; the caller refreshes what it
    // matched without them.
    public bool QuestNamesReady => questTask is { IsCompleted: true };

    private HpkFile? Pack => packTask is { IsCompletedSuccessfully: true } task ? task.Result : null;

    // The translation the page shows. Samples read before its pack opened
    // lack translations, so they are read again.
    public void SetPack(string? path)
    {
        if (string.Equals(path, packPath, StringComparison.Ordinal))
            return;

        ReleasePack();
        samples.Clear();
        packPath = path;
        if (path is null)
            return;

        var token = stopping.Token;
        packTask = Task.Run(() =>
        {
            try
            {
                return HpkFile.Open(path, HpkOpenMode.Full);
            }
            catch (Exception ex) when (ex is HpkFormatException or IOException or UnauthorizedAccessException)
            {
                log.Warning($"Sheet previews: the translation could not be opened ({ex.Message}).");
                return null;
            }
        }, token);
    }

    // The quest's name for a sheet in the quest folder, translated when the
    // pack has it; null otherwise or while the names are read.
    public string? QuestName(string sheet)
    {
        if (!sheet.StartsWith(QuestFolder, StringComparison.Ordinal))
            return null;

        questTask ??= Task.Run(ReadQuestNames, stopping.Token);
        if (questTask is not { IsCompletedSuccessfully: true, Result: { } quests })
            return null;

        var id = sheet[(sheet.LastIndexOf('/') + 1)..];
        if (!quests.ById.TryGetValue(id, out var quest))
            return null;

        return Pack is { } pack && quests.NameOrdinal >= 0 && pack.TryGetSheet("Quest", out var packSheet) &&
            pack.TryGetCell(packSheet, quest.RowId, 0, (uint)quests.NameOrdinal, out var cell) &&
            CellText(cell) is { Length: > 0 } translated
                ? translated
                : quest.Name;
    }

    public IReadOnlyList<SheetSample> Samples(string sheet)
    {
        if (samples.TryGetValue(sheet, out var cached))
            return cached;

        var read = Read(sheet);
        if (packTask is null or { IsCompleted: true })
            samples[sheet] = read;
        return read;
    }

    public void Dispose()
    {
        stopping.Cancel();
        ReleasePack();
        stopping.Dispose();
    }

    private void ReleasePack()
    {
        var task = packTask;
        packTask = null;
        _ = task?.ContinueWith(static t =>
        {
            if (t.IsCompletedSuccessfully)
                t.Result?.Dispose();
        }, TaskScheduler.Default);
    }

    // Strings of the first rows, distinct and not too short; ones the pack
    // translates first.
    private List<SheetSample> Read(string sheet)
    {
        var pack = Pack;
        var packSheet = pack is not null && pack.TryGetSheet(sheet, out var index) ? index : -1;
        var translated = new List<SheetSample>();
        var untranslated = new List<SheetSample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var (rowId, subrowId, strings) in Rows(sheet))
            {
                for (var ordinal = 0; ordinal < strings.Length && translated.Count < MaxSamples; ordinal++)
                {
                    var original = strings[ordinal];
                    if (original.Length < 2 || !seen.Add(original))
                        continue;

                    string? text = null;
                    if (packSheet >= 0 && pack!.TryGetCell(packSheet, rowId, subrowId, (uint)ordinal, out var cell))
                        text = CellText(cell);

                    (string.IsNullOrEmpty(text) ? untranslated : translated).Add(new SheetSample(Shorten(original), string.IsNullOrEmpty(text) ? null : Shorten(text)));
                }

                if (translated.Count >= MaxSamples)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warning($"Sheet previews: {sheet} could not be read ({ex.Message}).");
        }

        return [.. translated.Concat(untranslated).Take(MaxSamples)];
    }

    // The first rows of a sheet with the text of each String column, in the
    // order the pack numbers them.
    private IEnumerable<(uint RowId, ushort SubrowId, string[] Strings)> Rows(string sheet)
    {
        var count = 0;
        ExcelSheet<RawRow>? rows = null;
        try
        {
            rows = data.GetExcelSheet<RawRow>(data.Language, sheet);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        if (rows is not null)
        {
            var columns = StringColumns(rows.Columns);
            foreach (var row in rows)
            {
                if (count++ >= MaxRows)
                    yield break;
                yield return (row.RowId, 0, columns.Select(c => NameText.Plain(row.ReadStringColumn(c).Data.Span)).ToArray());
            }

            yield break;
        }

        var subrows = data.GetSubrowExcelSheet<RawSubrow>(data.Language, sheet);
        var subColumns = StringColumns(subrows.Columns);
        foreach (var collection in subrows)
        {
            foreach (var row in collection)
            {
                if (count++ >= MaxRows)
                    yield break;
                yield return (row.RowId, row.SubrowId, subColumns.Select(c => NameText.Plain(row.ReadStringColumn(c).Data.Span)).ToArray());
            }
        }
    }

    private static int[] StringColumns(IReadOnlyList<ExcelColumnDefinition> columns) =>
        columns
            .Select(static (c, i) => (Column: c, Index: i))
            .Where(static c => c.Column.Type == ExcelColumnDataType.String)
            .Select(static c => c.Index)
            .ToArray();

    private QuestNames? ReadQuestNames()
    {
        try
        {
            var quests = data.GetExcelSheet<Quest>(data.Language);
            var raw = data.GetExcelSheet<RawRow>(data.Language, "Quest");
            var columns = StringColumns(raw.Columns);
            var byId = new Dictionary<string, (uint, string)>(StringComparer.Ordinal);
            var nameOrdinal = -1;
            foreach (var quest in quests)
            {
                stopping.Token.ThrowIfCancellationRequested();
                var id = NameText.Plain(quest.Id.Data.Span);
                var name = NameText.Plain(quest.Name.Data.Span);
                if (id.Length == 0 || name.Length == 0)
                    continue;

                byId.TryAdd(id, (quest.RowId, name));
                if (nameOrdinal < 0 && raw.GetRowOrDefault(quest.RowId) is { } row)
                    nameOrdinal = Array.FindIndex(columns, c => row.ReadStringColumn(c).Data.Span.SequenceEqual(quest.Name.Data.Span));
            }

            return new QuestNames(byId, nameOrdinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warning($"Sheet previews: quest names could not be read ({ex.Message}).");
            return null;
        }
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    private static unsafe string CellText(HpkCell cell) => NameText.Plain(new ReadOnlySpan<byte>(cell.String, cell.Length));
}
