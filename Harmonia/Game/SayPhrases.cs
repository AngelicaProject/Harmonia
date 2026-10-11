using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Harmonia.Game;

// A phrase of a quest the player has to say: the step and the objects (by
// their base id) the game asks for, the phrase in the game's language, and
// the pack's translation of it.
public sealed record SayQuestPhrase(byte Sequence, IReadOnlyList<uint> Targets, string Original, string Translation, bool Exact);

public sealed record SayQuest(ushort QuestId, string Name, IReadOnlyList<SayQuestPhrase> Phrases);

// Quests that wait for a phrase in the Say channel. The game compares what
// the player says with a row of the quest's text sheet, so with a translated
// row everyone nearby reads the translation. Here those rows stay in the
// game's language, and SayHook writes the game's phrase where the player
// typed the translated one.
//
// Nothing is listed by hand: when a quest's text sheet is first used, the
// quest's own script (game_script/quest/...) says which rows it compares, at
// which step, and with which objects near (Runtime/SayScript). A quest is
// taken whole or not at all: an unknown script, a row, step, or object that
// cannot be found, a phrase with a macro, or a translation made for another
// source leaves every row of the quest translated, as without this class.
public sealed class SayPhrases
{
    private const string SystemDefineSheet = "QuestSystemDefine";
    private const uint QuestRowBase = 0x10000;
    private const int QuestNumberDigits = 5;
    private const byte Stx = 0x02;

    private readonly GameData gameData;
    private readonly HpkFile pack;
    private readonly IHarmoniaLog log;
    private readonly ConcurrentDictionary<ushort, SayQuest> quests = new();
    private readonly ConcurrentDictionary<string, string> skipped = new(StringComparer.Ordinal);
    private readonly Lazy<Dictionary<string, int>> systemDefines;

    public SayPhrases(GameData gameData, HpkFile pack, IHarmoniaLog log)
    {
        this.gameData = gameData;
        this.pack = pack;
        this.log = log;
        systemDefines = new Lazy<Dictionary<string, int>>(ReadSystemDefines);
    }

    // Quests whose phrases stay in the game's language this session, by the
    // quest id the journal uses.
    public IReadOnlyDictionary<ushort, SayQuest> Quests => quests;

    // Quests with a Say phrase that were left translated, and why.
    public IReadOnlyDictionary<string, string> Skipped => skipped;

    // The rows of a quest text sheet that stay in the game's language, or
    // null. The pack must outlive every call; nothing is thrown.
    public IReadOnlySet<uint>? Rows(string sheetName)
    {
        try
        {
            return Read(sheetName);
        }
        catch (Exception ex)
        {
            Skip(sheetName, ex.Message);
            log.Warning($"[SayPhrases] {sheetName} could not be read; the quest stays translated.", ex);
            return null;
        }
    }

    // What the pack writes into a row, whatever the running source is.
    public unsafe IReadOnlyList<string> PackTexts(string sheetName, uint rowId)
    {
        var result = new List<string>();
        if (!pack.TryGetSheet(sheetName, out var sheet))
            return result;

        var columns = pack.GetLayout(sheet).Length;
        for (uint ordinal = 0; ordinal < columns; ordinal++)
        {
            if (pack.TryGetCell(sheet, rowId, 0, ordinal, out var cell))
                result.Add(Encoding.UTF8.GetString(cell.String, cell.Length));
        }

        return result;
    }

    private IReadOnlySet<uint>? Read(string sheetName)
    {
        // quest/045/BanArk127_04577: the quest's Id, which ends in its number.
        var id = sheetName[(sheetName.LastIndexOf('/') + 1)..];
        if (id.Length <= QuestNumberDigits ||
            !ushort.TryParse(id.AsSpan(id.Length - QuestNumberDigits), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return null;

        if (gameData.GetFile($"game_script/{sheetName}.luab") is not { } script)
            return null;

        var read = SayScript.Read(script.Data);
        if (read.Kind == SayScriptKind.None)
            return null;
        if (read.Kind == SayScriptKind.Unknown)
            return Skip(sheetName, "the script is not understood");

        if (!gameData.GetExcelSheet<Quest>()!.TryGetRow(QuestRowBase + number, out var quest) || quest.Id.ExtractText() != id)
            return Skip(sheetName, "the quest is not in the Quest sheet");
        var parameters = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var parameter in quest.QuestParams)
        {
            var name = parameter.ScriptInstruction.ExtractText();
            if (name.Length != 0)
                parameters[name] = parameter.ScriptArg;
        }

        if (!pack.TryGetSheet(sheetName, out var packSheet))
            return null;
        var ordinal = Array.FindIndex(pack.GetLayout(packSheet), static c => c.ColumnIndex == TranslationRuntime.QuestTextColumn);
        if (ordinal < 0)
            return null;

        var texts = new Dictionary<string, (uint RowId, byte[] Text)>(StringComparer.Ordinal);
        foreach (var row in gameData.Excel.GetSheet<RawRow>(name: sheetName))
            texts[row.ReadStringColumn(0).ExtractText()] = (row.RowId, row.ReadStringColumn((int)TranslationRuntime.QuestTextColumn).Data.ToArray());

        var rows = new HashSet<uint>();
        var phrases = new List<SayQuestPhrase>();
        foreach (var phrase in read.Phrases)
        {
            if (!systemDefines.Value.TryGetValue(phrase.Sequence, out var sequence) || sequence is < 0 or > byte.MaxValue)
                return Skip(sheetName, $"the step {phrase.Sequence} is unknown");

            var targets = new List<uint>();
            foreach (var target in phrase.Targets)
            {
                if (!parameters.TryGetValue(target, out var baseId) || baseId == 0)
                    return Skip(sheetName, $"the object {target} is unknown");
                targets.Add(baseId);
            }

            if (!texts.TryGetValue(phrase.TextKey, out var text))
                return Skip(sheetName, $"the row {phrase.TextKey} is missing");
            if (text.Text.Length == 0 || text.Text.AsSpan().Contains(Stx))
                return Skip(sheetName, $"the phrase {phrase.TextKey} is not plain text");

            // An untranslated phrase is what the game compares already.
            if (!TryGetTranslation(packSheet, text.RowId, (uint)ordinal, text.Text, out var translation, out var problem))
            {
                if (problem is null)
                    continue;
                return Skip(sheetName, $"the phrase {phrase.TextKey}: {problem}");
            }

            var original = Encoding.UTF8.GetString(text.Text);
            if (string.Equals(original, translation, StringComparison.Ordinal))
                continue;

            rows.Add(text.RowId);
            phrases.Add(new SayQuestPhrase((byte)sequence, targets, original, translation, phrase.Exact));
        }

        if (phrases.Count == 0)
            return null;

        // The longest first: one phrase of a quest can begin another.
        quests[number] = new SayQuest(number, id, [.. phrases.OrderByDescending(static p => p.Translation.Length)]);
        return rows;
    }

    // False with no problem: the pack leaves the phrase as the game has it.
    private unsafe bool TryGetTranslation(int packSheet, uint rowId, uint ordinal, ReadOnlySpan<byte> source, out string translation, out string? problem)
    {
        translation = string.Empty;
        problem = null;
        if (!pack.TryGetCell(packSheet, rowId, 0, ordinal, out var cell))
            return false;

        if (SourceGuard.Compute(source) != cell.SourceGuard)
        {
            problem = "translated from another text";
            return false;
        }

        var bytes = new ReadOnlySpan<byte>(cell.String, cell.Length);
        if (bytes.IsEmpty || bytes.Contains(Stx))
        {
            problem = "the translation is not plain text";
            return false;
        }

        translation = Encoding.UTF8.GetString(bytes);
        return true;
    }

    // SEQ_1 .. SEQ_FINISH: the step numbers quest scripts name.
    private Dictionary<string, int> ReadSystemDefines()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in gameData.Excel.GetSheet<RawRow>(name: SystemDefineSheet))
            result[row.ReadStringColumn(0).ExtractText()] = row.ReadInt32Column(1);
        return result;
    }

    private IReadOnlySet<uint>? Skip(string sheetName, string reason)
    {
        skipped[sheetName] = reason;
        return null;
    }
}
