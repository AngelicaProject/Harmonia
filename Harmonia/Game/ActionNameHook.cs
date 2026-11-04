using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.Shell;
using Harmonia.Dictionary;
using Harmonia.Runtime;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Harmonia.Game;

// The shell searches a sheet/column for a command argument, including the
// /micon line read while drawing a saved macro. Retry only action name
// entries, preserving the native row bounds, comparison and outer type tag.
internal sealed unsafe class ActionNameHook : IDisposable
{
    private readonly Hook<MatchDelegate> hook;
    private readonly Dictionary<string, (ActionNameAliases Aliases, int Column)> sheets;
    private readonly IHarmoniaLog log;
    private volatile bool stopping;
    private int inFlight;
    private long errors;
    private long replaced;
    private bool disposed;

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool MatchDelegate(ShellCommandModule* module, Utf8String* name, int* rowId, Utf8String* entry, int start, int end);

    public ActionNameHook(IGameInteropProvider interop, ISigScanner scanner, OriginalSheets originals, TranslationRuntime runtime, IHarmoniaLog log)
    {
        this.log = log;
        sheets = new(StringComparer.Ordinal)
        {
            ["Action"] = Build<LuminaAction>(originals, runtime, "Action", static r => r.Name),
            ["CraftAction"] = Build<CraftAction>(originals, runtime, "CraftAction", static r => r.Name),
            ["GeneralAction"] = Build<GeneralAction>(originals, runtime, "GeneralAction", static r => r.Name),
        };
        var matches = scanner.ScanAllText(ActionNameAliases.MatchSignature);
        if (matches.Length != 1)
            throw new InvalidOperationException($"The command name matcher signature has {matches.Length} matches; expected one.");
        Address = matches[0];
        var textStart = scanner.Module.BaseAddress + (nint)scanner.TextSectionOffset;
        if (Address < textStart || Address >= textStart + scanner.TextSectionSize)
            throw new InvalidOperationException("The command name matcher is outside the game's code section.");
        hook = interop.HookFromAddress<MatchDelegate>(Address, Detour);
        try
        {
            hook.Enable();
        }
        catch
        {
            hook.Dispose();
            throw;
        }
        log.Info($"[ActionNameHook] Original command action names enabled at {Address:X}.");
    }

    public nint Address { get; }
    public long Errors => Interlocked.Read(ref errors);
    public long Replaced => Interlocked.Read(ref replaced);

    private bool Detour(ShellCommandModule* module, Utf8String* name, int* rowId, Utf8String* entry, int start, int end)
    {
        Interlocked.Increment(ref inFlight);
        try
        {
            if (hook.Original(module, name, rowId, entry, start, end))
                return true;

            try
            {
                // The entry starts with its sheet name (Utf8String), followed
                // by the sheet pointer at +0x68 and the column at +0x70.
                if (stopping || name == null || rowId == null || entry == null ||
                    !sheets.TryGetValue(Encoding.UTF8.GetString(entry->AsSpan()), out var sheet) ||
                    *(short*)((byte*)entry + 0x70) != sheet.Column)
                    return false;

                foreach (var alias in sheet.Aliases.Find(Encoding.UTF8.GetString(name->AsSpan())))
                {
                    var bytes = Encoding.UTF8.GetBytes(alias.Shown + '\0');
                    fixed (byte* text = bytes)
                    {
                        var changed = Utf8String.FromSequence(text);
                        if (changed == null)
                            continue;
                        try
                        {
                            var resolved = -1;
                            if (!hook.Original(module, changed, &resolved, entry, start, end) ||
                                resolved < 0 || !alias.Rows.Contains((uint)resolved))
                                continue;
                            *rowId = resolved;
                            Interlocked.Increment(ref replaced);
                            return true;
                        }
                        finally
                        {
                            changed->Dtor(true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errors) <= 5)
                    log.Warning("[ActionNameHook] The command name was left unresolved.", ex);
            }
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private static (ActionNameAliases Aliases, int Column) Build<T>(OriginalSheets originals, TranslationRuntime runtime,
        string sheetName, Func<T, ReadOnlySeString> name) where T : struct, IExcelRow<T>
    {
        var sheet = originals.GetExcelSheet<T>(originals.Language);
        var raw = originals.GetExcelSheet<RawRow>(originals.Language, sheetName);
        var columns = raw.Columns.Select(static (c, i) => (Column: c, Index: i))
            .Where(static c => c.Column.Type == ExcelColumnDataType.String)
            .Select(static c => new StringColumn((ushort)c.Index, c.Column.Offset)).ToArray();
        var ordinal = NameColumn.Find(columns.Length, sheet.Select(row => new NameColumnSample(
            name(row).Data, i => raw.GetRow(row.RowId).ReadStringColumn(columns[i].Index).Data)));
        if (ordinal < 0)
            throw new InvalidOperationException($"The {sheetName} name column was not recognized.");
        var bound = runtime.FindSheet(sheetName, false, columns);
        var names = new List<(uint, string, string)>();
        if (bound >= 0)
        {
            foreach (var row in sheet)
            {
                var source = name(row).Data.Span;
                if (source.IsEmpty || source.Contains((byte)0x02) ||
                    !runtime.TryGetShown(bound, row.RowId, 0, (ushort)ordinal, source, out var cell))
                    continue;
                var shown = new ReadOnlySpan<byte>(cell.String, cell.Length);
                // Parameterized names need the game's evaluator; guessing
                // their plain text here could resolve an unrelated action.
                if (shown.IsEmpty || shown.Contains((byte)0x02))
                    continue;
                names.Add((row.RowId, Encoding.UTF8.GetString(source), Encoding.UTF8.GetString(shown)));
            }
        }
        return (new ActionNameAliases(names), columns[ordinal].Index);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        stopping = true;
        hook.Disable();
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref inFlight) != 0 && clock.ElapsedMilliseconds < 2000)
            Thread.Sleep(1);
        Thread.Sleep(20);
        hook.Dispose();
    }
}
