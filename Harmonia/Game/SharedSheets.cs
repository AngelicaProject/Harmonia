using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Harmonia.Runtime;
using Lumina;
using Lumina.Data.Files.Excel;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.Expressions;
using Lumina.Text.ReadOnly;

namespace Harmonia.Game;

// Gives other plugins the text the game shows. Plugins read the game's text
// from its files through the Lumina instance Dalamud shares, which the row
// hooks never reach: they get the original, look for it in menus that show
// the translation, and stop finding what they click. Here the pages of that
// instance get the same strings the row hook writes (the same layout, kept
// rows, and source checks, through TranslationRuntime.TryGetShown), so a
// plugin and the game see one text again.
//
// Lumina offers no way to do this: the pages are reached through two private
// A translation that reads the player's state (a global parameter: the
// player's gender, the time format) where the original does not is not
// given to plugins: Dalamud evaluates such a string only on the main thread
// and throws anywhere else, and plugins evaluate names in the background
// (Penumbra's list of NPC names did, inside a game hook, and the game
// closed). Those cells stay as the files have them; `Guarded` counts them.
//
// fields, RawExcelSheet._pages and ExcelPage.data. When either is missing or
// of another type after a Lumina update, nothing is changed and plugins read
// the original as before. Every page is put back on Dispose.
public sealed unsafe class SharedSheets : IDisposable
{
    private static readonly FieldInfo? PagesField =
        typeof(RawExcelSheet).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? DataField =
        typeof(ExcelPage).GetField("data", BindingFlags.Instance | BindingFlags.NonPublic);

    // Whether this Lumina keeps its pages where they are looked for.
    public static bool Available => PagesField?.FieldType == typeof(ExcelPage[]) && DataField?.FieldType == typeof(byte[]);

    private readonly GameData data;
    private readonly TranslationRuntime runtime;
    private readonly IHarmoniaLog log;
    private readonly ConcurrentBag<(ExcelPage Page, byte[] Original)> changed = [];
    private readonly CancellationTokenSource stopping = new();
    private int sheets;
    private long cells;
    private long bytes;
    private long failures;
    private long guarded;

    public SharedSheets(GameData data, TranslationRuntime runtime, IHarmoniaLog log)
    {
        this.data = data;
        this.runtime = runtime;
        this.log = log;
        if (!Available)
        {
            Error = "Lumina keeps its pages differently now.";
            log.Error("[SharedSheets] " + Error + " Other plugins read the original text.");
            Completed = Task.CompletedTask;
            return;
        }

        Completed = Task.Run(Apply);
    }

    // Ends when every sheet of the pack has been given its text.
    public Task Completed { get; }

    public string? Error { get; private set; }

    public int Sheets => Volatile.Read(ref sheets);

    public long Cells => Interlocked.Read(ref cells);

    // Cells left as the files have them because their translation reads the
    // player's state and the original does not.
    public long Guarded => Interlocked.Read(ref guarded);

    public TimeSpan Elapsed { get; private set; }

    public void Dispose()
    {
        stopping.Cancel();
        try
        {
            Completed.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        foreach (var (page, original) in changed)
            DataField!.SetValue(page, original);
        stopping.Dispose();
    }

    private void Apply()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            Parallel.ForEach(
                runtime.SheetNames,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8), CancellationToken = stopping.Token },
                name =>
                {
                    try
                    {
                        ApplySheet(name);
                    }
                    catch (Exception ex)
                    {
                        // The sheet stays as the files have it.
                        if (Interlocked.Increment(ref failures) <= 5)
                            log.Warning($"[SharedSheets] {name} was left untouched", ex);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Elapsed = clock.Elapsed;
        log.Info($"[SharedSheets] {Sheets} sheets, {Cells} cells, {Interlocked.Read(ref bytes) / (1024 * 1024)} MiB of pages, " +
                 $"{Guarded} cells left for their global parameters, {Interlocked.Read(ref failures)} failures, {Elapsed.TotalSeconds:F1} s.");
    }

    // Whether evaluating the string reads a global parameter.
    public static bool UsesGlobals(ReadOnlySeStringSpan text)
    {
        foreach (var payload in text)
        {
            if (payload.Type != ReadOnlySePayloadType.Macro)
                continue;

            foreach (var expression in payload)
            {
                if (UsesGlobals(expression))
                    return true;
            }
        }

        return false;
    }

    private static bool UsesGlobals(ReadOnlySeExpressionSpan expression)
    {
        if (expression.TryGetString(out var text))
            return UsesGlobals(text);
        if (expression.TryGetParameterExpression(out var type, out var operand))
            return type is (byte)ExpressionType.GlobalNumber or (byte)ExpressionType.GlobalString || UsesGlobals(operand);
        return expression.TryGetBinaryExpression(out _, out var first, out var second) && (UsesGlobals(first) || UsesGlobals(second));
    }

    private void ApplySheet(string name)
    {
        // A sheet the pack names may be gone from the game.
        if (data.GetFile<ExcelHeaderFile>($"exd/{name}.exh") is not { } header)
            return;

        var sheet = data.Excel.GetRawSheet(name);
        var subrows = sheet is RawSubrowExcelSheet;
        var columns = sheet.Columns
            .Select(static (c, i) => (Column: c, Index: i))
            .Where(static c => c.Column.Type == ExcelColumnDataType.String)
            .Select(static c => new StringColumn((ushort)c.Index, c.Column.Offset))
            .ToArray();
        var packSheet = runtime.FindSheet(name, subrows, columns);
        if (packSheet < 0 || PagesField!.GetValue(sheet) is not ExcelPage[] pages)
            return;

        var any = false;
        foreach (var page in pages)
        {
            if (page is null || DataField!.GetValue(page) is not byte[] original)
                continue;

            var translated = ExdPage.Translate(original, header.Header.DataOffset, subrows, columns, Shown, out var replaced);
            if (translated is null)
                continue;

            // One reference changes: a reader sees the whole old page or the
            // whole new one.
            DataField.SetValue(page, translated);
            changed.Add((page, original));
            Interlocked.Add(ref cells, replaced);
            Interlocked.Add(ref bytes, translated.Length);
            any = true;
        }

        if (any)
            Interlocked.Increment(ref sheets);

        bool Shown(uint rowId, ushort subrowId, ushort ordinal, ReadOnlySpan<byte> source, out ReadOnlySpan<byte> text)
        {
            if (runtime.TryGetShown(packSheet, rowId, subrowId, ordinal, source, out var cell))
            {
                text = new ReadOnlySpan<byte>(cell.String, cell.Length);
                if (!UsesGlobals(new ReadOnlySeStringSpan(text)) || UsesGlobals(new ReadOnlySeStringSpan(source)))
                    return true;

                Interlocked.Increment(ref guarded);
            }

            text = default;
            return false;
        }
    }
}
