using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Harmonia.Dictionary;
using Harmonia.Runtime;
using Lumina.Excel.Sheets;

namespace Harmonia.Game;

internal sealed unsafe class OutgoingChatHook : IDisposable
{
    private readonly Hook<EncodeDelegate> hook;
    private readonly OriginalSheets originals;
    private readonly IHarmoniaLog log;
    private volatile bool stopping;
    private int inFlight;
    private long errors;
    private long replaced;
    private bool disposed;

    private delegate Utf8String* EncodeDelegate(PronounModule* module, Utf8String* input, uint maxLength, byte flags);

    public OutgoingChatHook(IGameInteropProvider interop, ISigScanner scanner, OriginalSheets originals, IHarmoniaLog log)
    {
        this.originals = originals;
        this.log = log;
        var dispatch = scanner.ScanText(OutgoingChatText.EncoderDispatchSignature);
        Address = (nint)(OutgoingChatText.EncoderAddress(new ReadOnlySpan<byte>((void*)dispatch, 26), dispatch)
            ?? throw new InvalidOperationException("The PronounModule encoder dispatch was not recognized."));
        var text = scanner.Module.BaseAddress + (nint)scanner.TextSectionOffset;
        if (Address < text || Address >= text + scanner.TextSectionSize)
            throw new InvalidOperationException("The PronounModule encoder is outside the game's code section.");
        hook = interop.HookFromAddress<EncodeDelegate>(Address, Detour);
        try
        {
            hook.Enable();
        }
        catch
        {
            hook.Dispose();
            throw;
        }
        log.Info($"[OutgoingChatHook] Original chat names enabled at {Address:X}.");
    }

    public nint Address { get; }
    public long Errors => Interlocked.Read(ref errors);
    public long Replaced => Interlocked.Read(ref replaced);

    private Utf8String* Detour(PronounModule* module, Utf8String* input, uint maxLength, byte flags)
    {
        Interlocked.Increment(ref inFlight);
        try
        {
            var result = hook.Original(module, input, maxLength, flags);
            try
            {
                if (!stopping && result != null &&
                    OutgoingChatText.Restore(result->AsSpan(), OriginalName, (int)Math.Min(maxLength, int.MaxValue)) is { } changed)
                {
                    fixed (byte* bytes = changed)
                        result->SetString(bytes);
                    Interlocked.Increment(ref replaced);
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errors) <= 5)
                    log.Warning("[OutgoingChatHook] The encoded line was left unchanged.", ex);
            }
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private string? OriginalName(ChatNameKind kind, uint id)
    {
        var name = kind == ChatNameKind.Quest
            ? originals.GetExcelSheet<Quest>(originals.Language).GetRowOrDefault(id)?.Name
            : id >= 2_000_000
                ? originals.GetExcelSheet<EventItem>(originals.Language).GetRowOrDefault(id)?.Name
                : originals.GetExcelSheet<Item>(originals.Language).GetRowOrDefault(id % 500_000)?.Name;
        if (name is { } source && NameText.Plain(source.Data.Span) is { Length: > 0 } plain)
            return plain;
        throw new InvalidOperationException($"The original {kind} name for chat id {id} was not found.");
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
