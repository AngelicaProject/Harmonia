using System.Collections.Concurrent;
using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.Excel;
using FFXIVClientStructs.FFXIV.Component.Excel;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Harmonia.Runtime;

namespace Harmonia.Game;

// Translates Excel rows as the game creates them. Every row is built by
// ExcelRow_Parse_v3 and then handed to its sheet's row resolver with
// IExcelPageRowResolver::StoreRow (vtable slot 3); nothing else can see the
// row before that call. The detour rebuilds the row buffer with the
// translated strings at exactly the size it needs and passes the row on.
public sealed unsafe class ExcelRowHooks : IDisposable
{
    // HashTableExcelPageRowResolver::StoreRow and
    // RingBufferExcelPageRowResolver::StoreRow.
    public const string HashTableStoreRowSig =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 44 8B 49 18 33 C0 49 8B F0 48 8B FA 45 85 C9";

    public const string RingBufferStoreRowSig =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 54 41 56 41 57 48 83 EC 20 48 8B 59 20 48 8D 41 28";

    // The allocation in ExcelRow_Parse_v3: mov rcx, [ExdEnvironment instance];
    // xor r8d, r8d; mov rdx, rdi; mov rax, [rcx]; call [rax+8].
    public const string ExdEnvironmentInstanceSig = "48 8B 0D ?? ?? ?? ?? 45 33 C0 48 8B D7 48 8B 01 FF 50 08";

    private const int StoreRowSlot = 3;
    private const int VtableSlotsChecked = 8;
    private const int MaxStringColumns = 512;
    private const int MaxLoggedErrors = 5;

    private readonly TranslationRuntime runtime;
    private readonly IHarmoniaLog log;
    private readonly Hook<StoreRowDelegate> hashTableHook;
    private readonly Hook<StoreRowDelegate> ringBufferHook;
    private readonly ConcurrentDictionary<nint, SheetBinding> sheets = new();
    private readonly ExdEnvironment** exdEnvironment;
    private volatile bool stopping;
    private int inFlight;
    private long errors;
    private bool disposed;

    private delegate byte StoreRowDelegate(nint resolver, ExcelRowDescriptor* descriptor, ExcelRow* row);

    private readonly record struct SheetBinding(nint Name, ushort ColumnCount, ushort Version, int PackSheet);

    public ExcelRowHooks(IGameInteropProvider interop, ISigScanner scanner, TranslationRuntime runtime, IHarmoniaLog log)
    {
        this.runtime = runtime;
        this.log = log;

        // Row buffers are allocated and freed by this instance; rebuilt
        // buffers must come from the same one.
        exdEnvironment = (ExdEnvironment**)scanner.GetStaticAddressFromSig(ExdEnvironmentInstanceSig);
        var hashTable = Resolve(scanner, HashTableStoreRowSig, "HashTableExcelPageRowResolver::StoreRow");
        var ringBuffer = Resolve(scanner, RingBufferStoreRowSig, "RingBufferExcelPageRowResolver::StoreRow");
        hashTableHook = interop.HookFromAddress<StoreRowDelegate>(hashTable, HashTableDetour);
        ringBufferHook = interop.HookFromAddress<StoreRowDelegate>(ringBuffer, RingBufferDetour);
        hashTableHook.Enable();
        ringBufferHook.Enable();
        HashTableAddress = hashTable;
        RingBufferAddress = ringBuffer;
        log.Info($"[ExcelRowHooks] StoreRow hooks installed at {hashTable:X} and {ringBuffer:X}.");
    }

    public nint HashTableAddress { get; }

    public nint RingBufferAddress { get; }

    public long Errors => Interlocked.Read(ref errors);

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        stopping = true;
        hashTableHook.Disable();
        ringBufferHook.Disable();

        // A call that entered a detour before the hooks were disabled may
        // still read the pack; the pack is disposed only after it returns.
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref inFlight) != 0 && clock.ElapsedMilliseconds < 2000)
            Thread.Sleep(1);
        Thread.Sleep(20);

        hashTableHook.Dispose();
        ringBufferHook.Dispose();
    }

    // The signature must match exactly one function, and that function must
    // be slot 3 of a vtable whose first slots all point into code: a moved
    // signature must never hook the wrong function.
    private static nint Resolve(ISigScanner scanner, string signature, string name)
    {
        var address = scanner.ScanText(signature);

        // Dalamud scans a copy of the module: TextSectionBase points into the
        // copy, while ScanText results and vtable entries are addresses in the
        // loaded module. Compare against the loaded module's code.
        var text = scanner.Module.BaseAddress + (nint)scanner.TextSectionOffset;
        var textEnd = text + scanner.TextSectionSize;
        var rdata = (byte*)scanner.RDataSectionBase;
        var rdataSize = scanner.RDataSectionSize;

        var references = 0;
        for (long offset = StoreRowSlot * 8; offset + ((VtableSlotsChecked - StoreRowSlot) * 8) <= rdataSize; offset += 8)
        {
            if (*(nint*)(rdata + offset) != address)
                continue;

            references++;

            var vtable = (nint*)(rdata + offset - (StoreRowSlot * 8));
            var valid = true;
            for (var slot = 0; valid && slot < VtableSlotsChecked; slot++)
                valid = vtable[slot] >= text && vtable[slot] < textEnd;

            if (valid)
                return address;
        }

        throw new InvalidOperationException(
            $"{name} at {address:X} is not slot {StoreRowSlot} of a row resolver vtable " +
            $"({references} references in .rdata, code {text:X}..{textEnd:X}).");
    }

    private byte HashTableDetour(nint resolver, ExcelRowDescriptor* descriptor, ExcelRow* row)
    {
        Translate(descriptor, row);
        return hashTableHook.Original(resolver, descriptor, row);
    }

    private byte RingBufferDetour(nint resolver, ExcelRowDescriptor* descriptor, ExcelRow* row)
    {
        Translate(descriptor, row);
        return ringBufferHook.Original(resolver, descriptor, row);
    }

    private void Translate(ExcelRowDescriptor* descriptor, ExcelRow* row)
    {
        Interlocked.Increment(ref inFlight);
        try
        {
            if (!stopping && descriptor != null && row != null)
                TranslateRow(descriptor, row);
        }
        catch (Exception ex)
        {
            // The row stays as the game parsed it.
            if (Interlocked.Increment(ref errors) <= MaxLoggedErrors)
                log.Warning("[ExcelRowHooks] row translation failed", ex);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private void TranslateRow(ExcelRowDescriptor* descriptor, ExcelRow* row)
    {
        var sheet = row->Sheet;
        var data = (byte*)row->Data;
        if (sheet == null || data == null || sheet->Version <= 2)
            return;

        var columnCount = sheet->ColumnCount;
        var definitions = sheet->ColumnDefinitions;
        if (definitions == null || columnCount == 0)
            return;

        var stringCount = 0;
        for (var i = 0; i < columnCount; i++)
        {
            if (definitions[i].Type == 0)
                stringCount++;
        }

        if (stringCount == 0 || stringCount > MaxStringColumns)
            return;

        Span<StringColumn> columns = stackalloc StringColumn[stringCount];
        for (int i = 0, n = 0; i < columnCount; i++)
        {
            if (definitions[i].Type == 0)
                columns[n++] = new StringColumn((ushort)i, definitions[i].Offset);
        }

        var packSheet = Bind(sheet, columns);
        if (packSheet < 0)
            return;

        // A MultiRow descriptor carries the subrow id as its single key;
        // LowerRowIdPart is a hash of the keys, not an id.
        ushort subrowId = 0;
        if (sheet->Variant == ExcelVariant.MultiRow)
        {
            if (descriptor->SubRowCount != 1)
                return;
            subrowId = descriptor->SubRowIds[0];
        }

        if (!runtime.TryGetRow(packSheet, descriptor->RowId, subrowId, out var packRow))
            return;

        Span<RowString> strings = stackalloc RowString[stringCount];
        if (!RowLayout.TryRead(data, (int)sheet->DataOffset, columns, strings))
        {
            runtime.CountUnexpected(packSheet);
            return;
        }

        // Cleared explicitly: stackalloc is not zeroed under SkipLocalsInit, and
        // an unset replacement must mean "keep the original".
        Span<Replacement> replacements = stackalloc Replacement[stringCount];
        replacements.Clear();
        var any = false;
        for (var i = 0; i < packRow.CellCount; i++)
        {
            var cell = runtime.GetCell(packRow, i, out var ordinal);
            if (ordinal >= stringCount)
                continue;

            var original = strings[ordinal];
            var source = new ReadOnlySpan<byte>(data + original.Start, original.Length);
            if (runtime.Decide(packSheet, cell, source) != CellDecision.Applied)
                continue;

            replacements[ordinal] = new Replacement(cell.String, cell.Length);
            any = true;
        }

        if (!any)
            return;

        var size = RowLayout.Measure((int)sheet->DataOffset, strings, replacements);
        var environment = *exdEnvironment;
        if (size < 0 || environment == null)
        {
            runtime.CountUnexpected(packSheet);
            return;
        }

        // Called exactly as ExcelRow_Parse_v3 allocates and ExcelRow_Clear frees.
        var vtable = *(nint**)environment;
        var alloc = (delegate* unmanaged<ExdEnvironment*, ulong, ulong, byte*>)vtable[1];
        var free = (delegate* unmanaged<ExdEnvironment*, void*, void*>)vtable[2];
        var target = alloc(environment, (ulong)size, 0);
        if (target == null)
            return;

        RowLayout.Write(data, target, (int)sheet->DataOffset, strings, replacements);
        row->Data = target;
        free(environment, data);
        runtime.CountRebuilt(packSheet);
    }

    private int Bind(ExcelSheet* sheet, ReadOnlySpan<StringColumn> columns)
    {
        var name = (nint)sheet->SheetName.Value;
        if (sheets.TryGetValue((nint)sheet, out var binding) &&
            binding.Name == name && binding.ColumnCount == sheet->ColumnCount && binding.Version == sheet->Version)
            return binding.PackSheet;

        var packSheet = name == 0
            ? -1
            : runtime.BindSheet(sheet->SheetName.ToString(), sheet->Variant == ExcelVariant.MultiRow, columns);
        sheets[(nint)sheet] = new SheetBinding(name, sheet->ColumnCount, sheet->Version, packSheet);
        return packSheet;
    }
}
