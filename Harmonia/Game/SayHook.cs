using System.Diagnostics;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;
using Harmonia.Runtime;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;

namespace Harmonia.Game;

// Writes the game's phrase where the player says a quest phrase in the
// translation (SayPhrases keeps the compared rows in the game's language, so
// the game waits for its own phrase and that is what others read).
//
// ShellCommandModule::ExecuteCommandInner runs every line of the chat box, of
// macros, and of plugins before the game splits it into a command and its
// text. The detour changes a line only when the game itself would react to
// the phrase: the line goes to the Say channel, the quest is at the step the
// script asks for, and one of the script's objects is loaded. Each check is
// the game's own or wider, never narrower: a missed line would leave the
// quest waiting, a changed one only says the phrase in the game's language.
// The player's string is never written to; the game gets a new one.
public sealed unsafe class SayHook : IDisposable
{
    private const string SayCommand = "/say";
    private const int MaxLoggedErrors = 5;

    private readonly SayPhrases phrases;
    private readonly IHarmoniaLog log;
    private readonly Hook<ExecuteCommandDelegate> hook;
    private readonly HashSet<string> sayCommands = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool stopping;
    private int inFlight;
    private long errors;
    private long replaced;
    private bool disposed;

    private delegate void ExecuteCommandDelegate(ShellCommandModule* module, Utf8String* command, UIModule* uiModule);

    public SayHook(IGameInteropProvider interop, GameData gameData, SayPhrases phrases, IHarmoniaLog log)
    {
        this.phrases = phrases;
        this.log = log;

        // The names the game itself knows the command by, in the client
        // language and in English.
        var english = gameData.GetExcelSheet<TextCommand>(Language.English)!;
        var say = english.FirstOrDefault(static c => c.Command.ExtractText() == SayCommand);
        if (say.RowId == 0 || !gameData.GetExcelSheet<TextCommand>()!.TryGetRow(say.RowId, out var local))
            throw new InvalidOperationException("The Say command is not in the TextCommand sheet.");
        foreach (var row in new[] { say, local })
        {
            foreach (var name in new[] { row.Command, row.ShortCommand, row.Alias, row.ShortAlias })
            {
                if (name.ExtractText() is { Length: > 1 } text)
                    sayCommands.Add(text);
            }
        }

        // A pack that translates the command itself: the game then knows it
        // by that name too.
        foreach (var text in phrases.PackTexts(nameof(TextCommand), say.RowId))
        {
            if (text.Length > 1 && text[0] == '/' && !text.Any(char.IsWhiteSpace))
                sayCommands.Add(text);
        }

        var address = (nint)ShellCommandModule.Addresses.ExecuteCommandInner.Value;
        if (address == 0)
            throw new InvalidOperationException("ShellCommandModule::ExecuteCommandInner was not found.");
        hook = interop.HookFromAddress<ExecuteCommandDelegate>(address, Detour);
        hook.Enable();
        Address = address;
        log.Info($"[SayHook] ExecuteCommandInner hook installed at {address:X}; Say is {string.Join(", ", sayCommands)}.");
    }

    public nint Address { get; }

    public long Errors => Interlocked.Read(ref errors);

    // Lines changed this session.
    public long Replaced => Interlocked.Read(ref replaced);

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

    private void Detour(ShellCommandModule* module, Utf8String* command, UIModule* uiModule)
    {
        byte[]? line = null;
        Interlocked.Increment(ref inFlight);
        try
        {
            if (!stopping && module != null && command != null && phrases.Quests.Count != 0)
                line = Replace(module, command);
        }
        catch (Exception ex)
        {
            // The line goes out as the player wrote it.
            line = null;
            if (Interlocked.Increment(ref errors) <= MaxLoggedErrors)
                log.Warning("[SayHook] the line was left as typed", ex);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }

        var changed = line is null ? null : Create(line);
        if (changed == null)
        {
            hook.Original(module, command, uiModule);
            return;
        }

        try
        {
            hook.Original(module, changed, uiModule);
            Interlocked.Increment(ref replaced);
        }
        finally
        {
            changed->Dtor(true);
        }
    }

    // A game string of its own for the changed line (NUL-terminated bytes).
    private Utf8String* Create(byte[] line)
    {
        try
        {
            fixed (byte* bytes = line)
                return Utf8String.FromSequence(bytes);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref errors) <= MaxLoggedErrors)
                log.Warning("[SayHook] the line was left as typed", ex);
            return null;
        }
    }

    // The line with the game's phrase and a terminating NUL, or null.
    private byte[]? Replace(ShellCommandModule* module, Utf8String* command)
    {
        var line = command->AsSpan();
        if (line.IsEmpty || line.Length > SayText.MaxLineBytes)
            return null;

        // Text without a command goes to the channel the chat box is set to,
        // which the game keeps as that channel's command.
        var name = SayText.Command(line, out var bodyStart);
        if (name.IsEmpty)
            name = SayText.Command(module->CurrentChannel.AsSpan(), out _);
        if (name.IsEmpty || !sayCommands.Contains(Encoding.UTF8.GetString(name)))
            return null;

        var body = line[bodyStart..];
        var manager = QuestManager.Instance();
        if (body.IsEmpty || manager == null)
            return null;

        foreach (ref var work in manager->NormalQuests)
        {
            if (work.QuestId == 0 || !phrases.Quests.TryGetValue(work.QuestId, out var quest))
                continue;

            foreach (var phrase in quest.Phrases)
            {
                if (phrase.Sequence != work.Sequence || !IsLoaded(phrase.Targets))
                    continue;
                if (SayText.Replace(body, phrase.Translation, phrase.Original, phrase.Exact) is not { } text)
                    continue;

                if (bodyStart + text.Length > SayText.MaxLineBytes)
                {
                    log.Warning($"[SayHook] {quest.Name}: the line would be too long with the game's phrase and was left as typed.");
                    continue;
                }

                var result = new byte[bodyStart + text.Length + 1];
                line[..bodyStart].CopyTo(result);
                text.CopyTo(result, bodyStart);
                return result;
            }
        }

        return null;
    }

    // The game takes the nearest object with one of these base ids among
    // those bound to the quest; every loaded object is at least that many.
    private static bool IsLoaded(IReadOnlyList<uint> targets)
    {
        var manager = GameObjectManager.Instance();
        if (manager == null)
            return false;

        foreach (var pointer in manager->Objects.IndexSorted)
        {
            var gameObject = pointer.Value;
            if (gameObject == null)
                continue;

            var baseId = gameObject->BaseId;
            for (var i = 0; i < targets.Count; i++)
            {
                if (targets[i] == baseId)
                    return true;
            }
        }

        return false;
    }
}
