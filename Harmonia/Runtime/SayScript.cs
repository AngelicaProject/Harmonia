using System.Text;

namespace Harmonia.Runtime;

// A phrase a quest waits for in the Say channel: the key of the quest text
// row the game compares the said text with, the quest step, and the objects
// one of which must be near (names of the quest's script parameters). Exact:
// the whole message must be the phrase; otherwise it only has to contain it.
public sealed record SayPhrase(string TextKey, string Sequence, IReadOnlyList<string> Targets, bool Exact);

public enum SayScriptKind
{
    // The quest takes nothing from the chat.
    None,

    Recognized,

    // The script handles said text in a way this reader does not know; the
    // quest is left exactly as it is.
    Unknown,
}

public readonly record struct SayScriptResult(SayScriptKind Kind, IReadOnlyList<SayPhrase> Phrases);

// What a quest script accepts in the Say channel. The game gives the said
// text to the script's IsAcceptSayEvent(self, player, target, text), which
// checks the quest step and the target and calls
// self:CompareString(text, self.TEXT_..., self.COMPARE_STRING_...). Only that
// shape is read, with the instructions and calls the game's scripts use;
// every other script is Unknown.
public static class SayScript
{
    public const string Function = "IsAcceptSayEvent";

    private const string Compare = "CompareString";
    private const string QuestSequence = "GetQuestSequence";
    private const string BaseId = "GetBaseId";
    private const string ModeEqual = "COMPARE_STRING_EQUAL";
    private const string ModeInclude = "COMPARE_STRING_INCLUDE";
    // A call reads up to four registers above its own.
    private const int Registers = 256 + 8;

    private static readonly byte[] FunctionName = Encoding.ASCII.GetBytes(Function);

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        Compare, QuestSequence, BaseId, "GetQuestId", "GetQuestBitFlag8", "CompareFixedPhrase",
    };

    private enum Kind : byte
    {
        Other,
        Parameter,
        Field,
        Method,
        Result,
    }

    private readonly record struct Value(Kind Kind, string? Name = null, int Index = 0);

    public static SayScriptResult Read(ReadOnlySpan<byte> script)
    {
        if (script.IndexOf(FunctionName) < 0)
            return new SayScriptResult(SayScriptKind.None, []);

        try
        {
            if (LuaChunk.Read(script) is { } main && Find(main) is { } function && Analyze(function) is { } phrases)
                return new SayScriptResult(SayScriptKind.Recognized, phrases);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
        }

        return new SayScriptResult(SayScriptKind.Unknown, []);
    }

    // The one function stored as IsAcceptSayEvent; the script must compare
    // said text nowhere else.
    private static LuaFunction? Find(LuaFunction main)
    {
        LuaFunction? found = null;
        var count = 0;
        Visit(main);
        if (count != 1 || found is null || found.Functions.Length != 0)
            return null;

        return ComparesOutside(main, found) ? null : found;

        void Visit(LuaFunction function)
        {
            var code = function.Code;
            for (var i = 0; i + 1 < code.Length; i++)
            {
                if (LuaChunk.Op(code[i]) != LuaChunk.OpClosure || LuaChunk.Op(code[i + 1]) != LuaChunk.OpSetTable)
                    continue;
                if (Constant(function, LuaChunk.B(code[i + 1])) != Function)
                    continue;

                count++;
                var index = LuaChunk.Bx(code[i]);
                if (index < function.Functions.Length)
                    found = function.Functions[index];
            }

            foreach (var inner in function.Functions)
                Visit(inner);
        }
    }

    private static bool ComparesOutside(LuaFunction function, LuaFunction except)
    {
        if (ReferenceEquals(function, except))
            return false;
        if (function.Constants.Any(static c => c is Compare))
            return true;

        return function.Functions.Any(inner => ComparesOutside(inner, except));
    }

    private static string? Constant(LuaFunction function, int operand) =>
        operand >= LuaChunk.ConstantBit && operand - LuaChunk.ConstantBit < function.Constants.Length
            ? function.Constants[operand - LuaChunk.ConstantBit] as string
            : null;

    // Follows what each register holds through the function. The scripts are
    // chains of "if step, if target, compare", so the step and target checked
    // last are the ones a comparison stands under.
    private static List<SayPhrase>? Analyze(LuaFunction function)
    {
        if (function.Parameters != 4)
            return null;

        var registers = new Value[Registers];
        for (var i = 0; i < function.Parameters; i++)
            registers[i] = new Value(Kind.Parameter, Index: i);

        var targets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var found = new List<(string Key, string Sequence, bool Exact)>();
        string? sequence = null;
        string? target = null;

        foreach (var instruction in function.Code)
        {
            var a = LuaChunk.A(instruction);
            var b = LuaChunk.B(instruction);
            var c = LuaChunk.C(instruction);
            switch (LuaChunk.Op(instruction))
            {
                case LuaChunk.OpMove:
                    registers[a] = registers[b];
                    break;

                case LuaChunk.OpLoadK:
                case LuaChunk.OpLoadBool:
                    registers[a] = default;
                    break;

                case LuaChunk.OpGetTable:
                    registers[a] = registers[b] is { Kind: Kind.Parameter, Index: 0 } && Constant(function, c) is { } field
                        ? new Value(Kind.Field, field)
                        : default;
                    break;

                case LuaChunk.OpSelf:
                    if (Constant(function, c) is not { } method || !Methods.Contains(method))
                        return null;
                    registers[a + 1] = registers[b];
                    registers[a] = new Value(Kind.Method, method);
                    break;

                case LuaChunk.OpCall:
                    if (registers[a] is not { Kind: Kind.Method, Name: { } called })
                        return null;

                    var receiver = registers[a + 1];
                    if (called == QuestSequence && receiver is not { Kind: Kind.Parameter, Index: 1 })
                        return null;
                    if (called == BaseId && receiver is not { Kind: Kind.Parameter, Index: 2 })
                        return null;
                    if (called == Compare)
                    {
                        // b counts the receiver and the arguments, plus one.
                        if (b is not (4 or 5) || receiver is not { Kind: Kind.Parameter, Index: 0 } ||
                            registers[a + 2] is not { Kind: Kind.Parameter, Index: 3 } ||
                            registers[a + 3] is not { Kind: Kind.Field, Name: { } key } || !key.StartsWith("TEXT_", StringComparison.Ordinal) ||
                            sequence is null || target is null)
                            return null;

                        // Without a mode the game compares for equality.
                        var exact = true;
                        if (b == 5)
                        {
                            if (registers[a + 4] is not { Kind: Kind.Field, Name: ModeEqual or ModeInclude } mode)
                                return null;
                            exact = mode.Name == ModeEqual;
                        }

                        found.Add((key, sequence, exact));
                    }

                    Array.Clear(registers, a, Registers - a);
                    registers[a] = new Value(Kind.Result, called);
                    break;

                case LuaChunk.OpEq:
                    if (b >= LuaChunk.ConstantBit || c >= LuaChunk.ConstantBit)
                    {
                        // Only the result of a check is compared with a constant.
                        var register = b >= LuaChunk.ConstantBit ? c : b;
                        if (register >= LuaChunk.ConstantBit || registers[register] is { Kind: Kind.Result, Name: QuestSequence or BaseId })
                            return null;
                        break;
                    }

                    // Only "equal, then the body" is read.
                    if (a != 0 || Pair(registers[b], registers[c]) is not { } check)
                        return null;
                    if (check.Result == QuestSequence)
                    {
                        if (!check.Field.StartsWith("SEQ_", StringComparison.Ordinal))
                            return null;
                        sequence = check.Field;
                        target = null;
                    }
                    else
                    {
                        if (sequence is null)
                            return null;
                        target = check.Field;
                        if (!targets.TryGetValue(sequence, out var list))
                            targets[sequence] = list = [];
                        if (!list.Contains(target))
                            list.Add(target);
                    }

                    break;

                case LuaChunk.OpJmp:
                case LuaChunk.OpReturn:
                    break;

                default:
                    return null;
            }
        }

        // Every target checked under the step: more than the one a comparison
        // stands under is harmless, fewer could miss the object the game takes.
        return found
            .Distinct()
            .Select(p => new SayPhrase(p.Key, p.Sequence, targets[p.Sequence], p.Exact))
            .ToList();
    }

    private static (string Result, string Field)? Pair(Value left, Value right)
    {
        if (left.Kind == Kind.Field)
            (left, right) = (right, left);

        return left is { Kind: Kind.Result, Name: QuestSequence or BaseId } && right is { Kind: Kind.Field, Name: { } field }
            ? (left.Name, field)
            : null;
    }
}
