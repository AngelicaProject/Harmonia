using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Harmonia.Runtime;

namespace Harmonia.Game;

// Adds Cyrillic to the game's letter case functions. Utf8String::ToUpper and
// Utf8String::ToLower change only Latin letters, and the text macros <head>,
// <headall>, <caps>, and <lower> are built on them, so a translated name the
// game capitalizes ("paladin" -> "Paladin") stays lowercase in Russian. The
// detours call the game's function, then apply the same rule to Cyrillic
// letters in place (CyrillicCase). Installed only while a pack in a Cyrillic
// language is loaded.
//
// Keybound Brawler compares the typed text with the word over an enemy after
// both pass one normalizing function; its comparison folds Latin case and
// diacritics but compares Cyrillic byte by byte, so "титан" misses "Титан".
// That function's detour folds Cyrillic the same way afterwards
// (CyrillicCase.FoldForTyping). It is optional: when the function is not
// found, the letter case hooks are installed without it.
public sealed unsafe class TextCaseHooks : IDisposable
{
    // Utf8String::ToUpper(this, firstCharOnly, everyWord, normalizeVowels,
    // excludeWords) and Utf8String::ToLower(this), from the installed game.
    public const string ToUpperSig = "40 53 57 41 54 41 55 41 57 48 83 EC 20 45 0F B6 E9 44 0F B6 E2 48 8B F9 41 B2 01";
    public const string ToLowerSig = "45 33 C0 4C 8B C9 4C 39 41 10 0F 86 ?? ?? ?? ?? 45 8D 50 06";

    // The Keybound Brawler normalizer (this, Utf8String* text): removes the
    // marks the rules make optional and evens out full-width characters. Its
    // prologue is common, so the language switch it holds is checked too:
    // cmp dword ptr [rdi+40h], 6 at TypingSwitchOffset.
    public const string TypingNormalizeSig = "48 89 5C 24 18 48 89 7C 24 20 55 48 8D 6C 24 F0 48 81 EC 10 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 00 4C 8B 05 ?? ?? ?? ?? 48 8B DA 48 8B F9 48 8D 15";
    private const int TypingSwitchOffset = 0x177;
    private static readonly byte[] TypingSwitch = [0x83, 0x7F, 0x40, 0x06];

    // Utf8String: the string pointer at 0x00, the used size with the
    // terminator at 0x10.
    private const int StringPtrOffset = 0x00;
    private const int BufUsedOffset = 0x10;
    private const int MaxLength = 1 << 20;

    // An exclusion list entry: a string pointer and its length; a zero length
    // ends the list.
    private const int ExcludeEntrySize = 16;

    private readonly Hook<ToUpperDelegate> toUpperHook;
    private readonly Hook<ToLowerDelegate> toLowerHook;
    private readonly Hook<TypingNormalizeDelegate>? typingHook;
    private readonly IHarmoniaLog log;
    private int inFlight;
    private long errors;
    private bool disposed;

    private delegate nint ToUpperDelegate(nint text, byte firstCharOnly, byte everyWord, byte normalizeVowels, byte* excludeWords);

    private delegate nint ToLowerDelegate(nint text);

    private delegate nint TypingNormalizeDelegate(nint self, nint text);

    public TextCaseHooks(IGameInteropProvider interop, ISigScanner scanner, IHarmoniaLog log)
    {
        this.log = log;
        var toUpper = scanner.ScanText(ToUpperSig);
        var toLower = scanner.ScanText(ToLowerSig);
        toUpperHook = interop.HookFromAddress<ToUpperDelegate>(toUpper, ToUpperDetour);
        toLowerHook = interop.HookFromAddress<ToLowerDelegate>(toLower, ToLowerDetour);
        toUpperHook.Enable();
        toLowerHook.Enable();
        log.Info($"[TextCaseHooks] Utf8String ToUpper and ToLower hooks installed at {toUpper:X} and {toLower:X}.");

        if (FindTypingNormalize(scanner) is { } typing)
        {
            typingHook = interop.HookFromAddress<TypingNormalizeDelegate>(typing, TypingNormalizeDetour);
            typingHook.Enable();
            log.Info($"[TextCaseHooks] Keybound Brawler typing hook installed at {typing:X}.");
        }
        else
        {
            log.Warning("[TextCaseHooks] The Keybound Brawler normalizer was not found; typed Cyrillic words keep their letter case.");
        }
    }

    public long Errors => Interlocked.Read(ref errors);

    public bool TypingHook => typingHook is not null;

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        toUpperHook.Disable();
        toLowerHook.Disable();
        typingHook?.Disable();
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref inFlight) != 0 && clock.ElapsedMilliseconds < 2000)
            Thread.Sleep(1);
        Thread.Sleep(20);
        toUpperHook.Dispose();
        toLowerHook.Dispose();
        typingHook?.Dispose();
    }

    private static nint? FindTypingNormalize(ISigScanner scanner)
    {
        if (!scanner.TryScanText(TypingNormalizeSig, out var address))
            return null;

        var at = new ReadOnlySpan<byte>((byte*)(address + TypingSwitchOffset), TypingSwitch.Length);
        return at.SequenceEqual(TypingSwitch) ? address : null;
    }

    private nint ToUpperDetour(nint text, byte firstCharOnly, byte everyWord, byte normalizeVowels, byte* excludeWords)
    {
        Interlocked.Increment(ref inFlight);
        try
        {
            var result = toUpperHook.Original(text, firstCharOnly, everyWord, normalizeVowels, excludeWords);
            var span = Text(text);
            if (!span.IsEmpty)
            {
                var start = *(nint*)(text + StringPtrOffset);
                var length = span.Length;
                var list = (nint)excludeWords;
                Func<int, bool>? excluded = list == 0
                    ? null
                    : i => Excluded((byte*)list, (byte*)(start + i), length - i);
                CyrillicCase.ToUpper(span, firstCharOnly != 0, everyWord != 0, excluded);
            }

            return result;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return text;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private nint ToLowerDetour(nint text)
    {
        Interlocked.Increment(ref inFlight);
        try
        {
            var result = toLowerHook.Original(text);
            CyrillicCase.ToLower(Text(text));
            return result;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return text;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private nint TypingNormalizeDetour(nint self, nint text)
    {
        Interlocked.Increment(ref inFlight);
        try
        {
            var result = typingHook!.Original(self, text);
            try
            {
                CyrillicCase.FoldForTyping(Text(text));
            }
            catch (Exception ex)
            {
                Fail(ex);
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    // The string's bytes without the terminator; empty when there are none.
    private static Span<byte> Text(nint text)
    {
        if (text == 0)
            return default;

        var pointer = *(byte**)(text + StringPtrOffset);
        var used = *(long*)(text + BufUsedOffset);
        if (pointer is null || used <= 1 || used > MaxLength)
            return default;

        return new Span<byte>(pointer, (int)used - 1);
    }

    // Whether a word of the game's exclusion list starts at this byte, as the
    // game's strncmp test decides it.
    private static bool Excluded(byte* list, byte* at, int remaining)
    {
        for (var entry = list; ; entry += ExcludeEntrySize)
        {
            var length = *(int*)(entry + 8);
            if (length <= 0)
                return false;

            var word = *(byte**)entry;
            if (word is not null && length <= remaining &&
                new ReadOnlySpan<byte>(word, length).SequenceEqual(new ReadOnlySpan<byte>(at, length)))
                return true;
        }
    }

    private void Fail(Exception ex)
    {
        if (Interlocked.Increment(ref errors) <= 5)
            log.Error("Letter case detour failed; the game's own result is kept.", ex);
    }
}
