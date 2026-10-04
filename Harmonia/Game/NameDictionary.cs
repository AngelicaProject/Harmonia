using System.Diagnostics;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Harmonia.Dictionary;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Harmonia.Game;

// Icon is a game icon id, 0 for none.
internal readonly record struct NameDetails(uint Icon, bool CanTryOn, bool Marketable);

internal static class GameLanguages
{
    public static readonly ClientLanguage[] All = [ClientLanguage.English, ClientLanguage.Japanese, ClientLanguage.German, ClientLanguage.French];

    // Pack language tags of the client languages.
    public static string Tag(ClientLanguage language) => language switch
    {
        ClientLanguage.Japanese => "ja",
        ClientLanguage.English => "en",
        ClientLanguage.German => "de",
        ClientLanguage.French => "fr",
        _ => language.ToString(),
    };
}

// The session's names in the game's languages next to what the game shows,
// for players who know a name from outside the translation. Lumina reads the
// game files, which the row hooks never change, so it has the originals; what
// the game shows comes from the pack under the row hook's own rules. Built on
// first use; exists only while a pack is loaded.
internal sealed class NameDictionary(IDataManager data, TranslationRuntime runtime, Configuration configuration, IHarmoniaLog log)
    : IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly List<Task> builds = [];
    private Task<NameIndex>? index;
    private bool disposed;

    public ClientLanguage ClientLanguage => data.Language;

    public async Task<NameSearchResult> SearchAsync(string query, int limit, NameCategory? category = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
        var token = linked.Token;
        var built = await GetIndexAsync().WaitAsync(token).ConfigureAwait(false);
        return await Task.Run(() => built.Search(query, limit, category, token), token).ConfigureAwait(false);
    }

    // Starts building the index ahead of a search. Called every frame the
    // dictionary page is shown, so a failed build waits for a search to retry.
    public void Prepare()
    {
        lock (gate)
        {
            if (index is not null || disposed)
                return;
        }

        _ = GetIndexAsync();
    }

    // The name in the client language as the game files have it.
    public string? OriginalItemName(uint itemId) =>
        data.GetExcelSheet<Item>(data.Language).GetRowOrDefault(itemId) is { } item && NameText.Plain(item.Name.Data.Span) is { Length: > 0 } name
            ? name
            : null;

    // What the dictionary window draws next to a name; read from the game
    // files when shown rather than kept for every name.
    public NameDetails Describe(NameEntry entry)
    {
        try
        {
            return entry.Category switch
            {
                NameCategory.Item when data.GetExcelSheet<Item>().GetRowOrDefault(entry.RowId) is { } item =>
                    new NameDetails(item.Icon, item.EquipSlotCategory.RowId != 0, item.ItemSearchCategory.RowId != 0),
                NameCategory.Action when data.GetExcelSheet<LuminaAction>().GetRowOrDefault(entry.RowId) is { } action =>
                    new NameDetails(action.Icon, false, false),
                NameCategory.Status when data.GetExcelSheet<Status>().GetRowOrDefault(entry.RowId) is { } status =>
                    new NameDetails(status.Icon, false, false),
                NameCategory.Duty when data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(entry.RowId) is { } duty =>
                    new NameDetails(duty.ContentType.ValueNullable?.Icon ?? 0, false, false),
                NameCategory.Quest when data.GetExcelSheet<Quest>().GetRowOrDefault(entry.RowId) is { } quest =>
                    new NameDetails((uint)Math.Max(0, quest.JournalGenre.ValueNullable?.Icon ?? 0), false, false),
                _ => default,
            };
        }
        catch (Exception ex)
        {
            log.Warning($"Name dictionary: {entry.Category} {entry.RowId} could not be described ({ex.Message}).");
            return default;
        }
    }

    // The shown languages changed: the next search builds again.
    public void Invalidate()
    {
        lock (gate)
            index = null;
    }

    public void Dispose()
    {
        Task[] running;
        lock (gate)
        {
            disposed = true;
            running = [.. builds];
        }

        // Builds read translations from the pack mapping, which is unmapped
        // after this; they stop at the next row once cancelled.
        stopping.Cancel();
        try
        {
            Task.WaitAll(running);
        }
        catch (AggregateException)
        {
        }

        stopping.Dispose();
    }

    private Task<NameIndex> GetIndexAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (index is { IsFaulted: false, IsCanceled: false })
                return index;

            var languages = configuration.DictionaryLanguages
                .Select(static tag => GameLanguages.All.FirstOrDefault(l => GameLanguages.Tag(l) == tag, (ClientLanguage)(-1)))
                .Where(l => (int)l >= 0 && l != data.Language)
                .Distinct()
                .ToArray();
            var token = stopping.Token;
            index = Task.Run(() => Build(languages, token), token);
            builds.RemoveAll(static b => b.IsCompleted);
            builds.Add(index);
            return index;
        }
    }

    private NameIndex Build(ClientLanguage[] extraLanguages, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var names = new List<NameEntry>();
        try
        {
            Add<Item>(names, NameCategory.Item, nameof(Item), static r => r.Name, extraLanguages, token);
            Add<LuminaAction>(names, NameCategory.Action, "Action", static r => r.Name, extraLanguages, token);
            Add<Status>(names, NameCategory.Status, nameof(Status), static r => r.Name, extraLanguages, token);
            Add<PlaceName>(names, NameCategory.Place, nameof(PlaceName), static r => r.Name, extraLanguages, token);
            Add<ContentFinderCondition>(names, NameCategory.Duty, nameof(ContentFinderCondition), static r => r.Name, extraLanguages, token);
            Add<Quest>(names, NameCategory.Quest, nameof(Quest), static r => r.Name, extraLanguages, token);
            var built = NameIndex.Build(names);
            log.Info($"Name dictionary: {built.Count} names in {clock.ElapsedMilliseconds} ms.");
            return built;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("The name dictionary could not be built.", ex);
            throw;
        }
    }

    private void Add<T>(
        List<NameEntry> names,
        NameCategory category,
        string sheetName,
        Func<T, ReadOnlySeString> name,
        ClientLanguage[] extraLanguages,
        CancellationToken token)
        where T : struct, IExcelRow<T>
    {
        var sheet = data.GetExcelSheet<T>(data.Language);
        var raw = data.GetExcelSheet<RawRow>(data.Language, sheetName);
        var columns = raw.Columns
            .Select(static (c, i) => (Column: c, Index: i))
            .Where(static c => c.Column.Type == ExcelColumnDataType.String)
            .Select(static c => new StringColumn((ushort)c.Index, c.Column.Offset))
            .ToArray();
        var packSheet = runtime.FindSheet(sheetName, false, columns);
        var ordinal = packSheet < 0 ? -1 : NameOrdinal(sheet, raw, columns, name);
        if (packSheet >= 0 && ordinal < 0)
            log.Warning($"Name dictionary: the name column of {sheetName} was not found; its names are listed untranslated.");

        var others = new List<(string Tag, ExcelSheet<T> Sheet)>();
        foreach (var language in extraLanguages)
        {
            try
            {
                others.Add((GameLanguages.Tag(language), data.GetExcelSheet<T>(language)));
            }
            catch (Exception ex)
            {
                log.Warning($"Name dictionary: {sheetName} has no {language} text ({ex.Message}).");
            }
        }

        var clientTag = GameLanguages.Tag(data.Language);
        foreach (var row in sheet)
        {
            token.ThrowIfCancellationRequested();
            var source = name(row).Data.Span;
            var original = NameText.Plain(source);
            if (original.Length == 0)
                continue;

            var shown = original;
            if (ordinal >= 0 && runtime.TryGetShown(packSheet, row.RowId, 0, (ushort)ordinal, source, out var cell))
                shown = CellText(cell);

            var originals = new List<NameOriginal>(1 + others.Count) { new(clientTag, original) };
            foreach (var (tag, other) in others)
            {
                if (other.GetRowOrDefault(row.RowId) is { } translatedRow && NameText.Plain(name(translatedRow).Data.Span) is { Length: > 0 } text)
                    originals.Add(new NameOriginal(tag, text));
            }

            names.Add(new NameEntry(category, row.RowId, shown.Length > 0 ? shown : original, originals.ToArray()));
        }
    }

    private static unsafe string CellText(HpkCell cell) => NameText.Plain(new ReadOnlySpan<byte>(cell.String, cell.Length));

    private static int NameOrdinal<T>(ExcelSheet<T> sheet, ExcelSheet<RawRow> raw, StringColumn[] columns, Func<T, ReadOnlySeString> name)
        where T : struct, IExcelRow<T>
    {
        var samples = sheet
            .Select(row => (Row: row, Raw: raw.GetRowOrDefault(row.RowId)))
            .Where(static r => r.Raw is not null)
            .Select(r => new NameColumnSample(
                name(r.Row).Data,
                ordinal => r.Raw!.Value.ReadStringColumn(columns[ordinal].Index).Data));
        return NameColumn.Find(columns.Length, samples);
    }
}
