using System.Text;
using Harmonia.Runtime;

namespace Harmonia.Tests.Runtime;

// Independent writer of Lua 5.1 bytecode for fixtures, in the layout the
// game's compiled scripts have.
internal sealed class LuaTestFunction(int parameters = 0)
{
    private const int Self = 0;
    private const int Player = 1;
    private const int Target = 2;
    private const int Text = 3;

    public List<uint> Code { get; } = [];

    public List<object?> Constants { get; } = [];

    public List<LuaTestFunction> Functions { get; } = [];

    public int K(object? value)
    {
        var index = Constants.IndexOf(value);
        if (index >= 0)
            return index;

        Constants.Add(value);
        return Constants.Count - 1;
    }

    public LuaTestFunction Abc(int op, int a, int b, int c)
    {
        Code.Add((uint)op | ((uint)a << 6) | ((uint)c << 14) | ((uint)b << 23));
        return this;
    }

    public LuaTestFunction Abx(int op, int a, int bx)
    {
        Code.Add((uint)op | ((uint)a << 6) | ((uint)bx << 14));
        return this;
    }

    public LuaTestFunction Field(int register, string name) => Abc(LuaChunk.OpGetTable, register, Self, LuaChunk.ConstantBit + K(name));

    public LuaTestFunction Method(int register, int receiver, string name) => Abc(LuaChunk.OpSelf, register, receiver, LuaChunk.ConstantBit + K(name));

    public LuaTestFunction Call(int register, int arguments) => Abc(LuaChunk.OpCall, register, arguments + 2, 2);

    public LuaTestFunction Jump() => Abx(LuaChunk.OpJmp, 0, 131071);

    // The opening of every IsAcceptSayEvent: the quest id in 4, its step in
    // 5, the target's base id in 6.
    public LuaTestFunction Opening() =>
        Method(4, Self, "GetQuestId").Call(4, 0)
            .Method(5, Player, "GetQuestSequence").Abc(LuaChunk.OpMove, 7, 4, 0).Call(5, 1)
            .Method(6, Target, "GetBaseId").Call(6, 0);

    public LuaTestFunction IfStep(string step) => Field(7, step).Abc(LuaChunk.OpEq, 0, 5, 7).Jump();

    public LuaTestFunction IfTarget(string target) => Field(7, target).Abc(LuaChunk.OpEq, 0, 6, 7).Jump();

    // "if self:CompareString(text, self.<key>, self.<mode>) == true then
    // return true, self.<scene> end return false, 0".
    public LuaTestFunction Compare(string key, string? mode, int textRegister = Text)
    {
        Method(7, Self, "CompareString").Abc(LuaChunk.OpMove, 9, textRegister, 0).Field(10, key);
        if (mode is not null)
            Field(11, mode);
        Call(7, mode is null ? 2 : 3);
        Abc(LuaChunk.OpEq, 0, 7, LuaChunk.ConstantBit + K(true)).Jump();
        Abc(LuaChunk.OpLoadBool, 7, 1, 0).Field(8, "SAY_SCENE").Abc(LuaChunk.OpReturn, 7, 3, 0);
        return Abc(LuaChunk.OpLoadBool, 7, 0, 0).Abx(LuaChunk.OpLoadK, 8, K(0.0)).Abc(LuaChunk.OpReturn, 7, 3, 0);
    }

    // A script whose class function stores `say` as IsAcceptSayEvent.
    public static byte[] Script(LuaTestFunction say, string name = SayScript.Function, LuaTestFunction? other = null)
    {
        var define = new LuaTestFunction();
        define.Functions.Add(say);
        if (other is not null)
            define.Functions.Add(other);
        define.Abx(LuaChunk.OpClosure, 1, 0).Abc(LuaChunk.OpSetTable, 0, LuaChunk.ConstantBit + define.K(name), 1);
        define.Abc(LuaChunk.OpReturn, 0, 1, 0);

        var main = new LuaTestFunction();
        main.Functions.Add(define);
        main.Abx(LuaChunk.OpClosure, 0, 0).Abc(LuaChunk.OpReturn, 0, 1, 0);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write([0x1B, (byte)'L', (byte)'u', (byte)'a', 0x51, 0, 1, 4, 4, 4, 8, 0]);
        main.Write(writer);
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string? text)
    {
        if (text is null)
        {
            writer.Write(0u);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((uint)bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private void Write(BinaryWriter writer)
    {
        WriteString(writer, "@test.lua");
        writer.Write(0);
        writer.Write(0);
        writer.Write((byte)0);
        writer.Write((byte)parameters);
        writer.Write((byte)0);
        writer.Write((byte)16);

        writer.Write(Code.Count);
        foreach (var instruction in Code)
            writer.Write(instruction);

        writer.Write(Constants.Count);
        foreach (var constant in Constants)
        {
            switch (constant)
            {
                case null:
                    writer.Write((byte)0);
                    break;
                case bool value:
                    writer.Write((byte)1);
                    writer.Write(value);
                    break;
                case double value:
                    writer.Write((byte)3);
                    writer.Write(value);
                    break;
                default:
                    writer.Write((byte)4);
                    WriteString(writer, (string)constant);
                    break;
            }
        }

        writer.Write(Functions.Count);
        foreach (var function in Functions)
            function.Write(writer);

        // Line numbers, one local, no upvalue names.
        writer.Write(Code.Count);
        foreach (var _ in Code)
            writer.Write(1);
        writer.Write(1);
        WriteString(writer, "self");
        writer.Write(0);
        writer.Write(Code.Count);
        writer.Write(0);
    }
}
