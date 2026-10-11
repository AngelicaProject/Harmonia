using System.Buffers.Binary;
using System.Text;

namespace Harmonia.Runtime;

// One function of a compiled Lua 5.1 chunk. A constant is null, a bool, a
// double, or a string.
public sealed class LuaFunction
{
    public required int Parameters { get; init; }

    public required uint[] Code { get; init; }

    public required object?[] Constants { get; init; }

    public required LuaFunction[] Functions { get; init; }
}

// Reads the game's compiled scripts (.luab): Lua 5.1 bytecode, little endian,
// 4-byte int, size_t, and instruction, 8-byte number. Anything else is not
// read at all.
public static class LuaChunk
{
    public const int OpMove = 0;
    public const int OpLoadK = 1;
    public const int OpLoadBool = 2;
    public const int OpGetTable = 6;
    public const int OpSetTable = 9;
    public const int OpSelf = 11;
    public const int OpJmp = 22;
    public const int OpEq = 23;
    public const int OpCall = 28;
    public const int OpReturn = 30;
    public const int OpClosure = 36;

    // B and C at or above this value name a constant, below it a register.
    public const int ConstantBit = 256;

    private const int MaxDepth = 32;
    private const int MaxCount = 1 << 20;

    private static ReadOnlySpan<byte> Header => [0x1B, (byte)'L', (byte)'u', (byte)'a', 0x51, 0, 1, 4, 4, 4, 8, 0];

    public static int Op(uint instruction) => (int)(instruction & 0x3F);

    public static int A(uint instruction) => (int)((instruction >> 6) & 0xFF);

    public static int C(uint instruction) => (int)((instruction >> 14) & 0x1FF);

    public static int B(uint instruction) => (int)((instruction >> 23) & 0x1FF);

    public static int Bx(uint instruction) => (int)(instruction >> 14);

    // The main function, or null when the data is not such a chunk.
    public static LuaFunction? Read(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(Header))
            return null;

        var position = Header.Length;
        return ReadFunction(data, ref position, 0);
    }

    private static LuaFunction? ReadFunction(ReadOnlySpan<byte> data, ref int position, int depth)
    {
        if (depth > MaxDepth || !SkipString(data, ref position) || data.Length - position < 12)
            return null;

        // Line defined, last line defined, upvalue count.
        position += 9;
        var parameters = data[position];

        // Parameter count, vararg flag, stack size.
        position += 3;

        if (!ReadCount(data, ref position, 4, out var codeCount))
            return null;
        var code = new uint[codeCount];
        for (var i = 0; i < codeCount; i++, position += 4)
            code[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);

        if (!ReadCount(data, ref position, 1, out var constantCount))
            return null;
        var constants = new object?[constantCount];
        for (var i = 0; i < constantCount; i++)
        {
            if (position >= data.Length)
                return null;

            switch (data[position++])
            {
                case 0:
                    break;
                case 1:
                    if (position >= data.Length)
                        return null;
                    constants[i] = data[position++] != 0;
                    break;
                case 3:
                    if (data.Length - position < 8)
                        return null;
                    constants[i] = BinaryPrimitives.ReadDoubleLittleEndian(data[position..]);
                    position += 8;
                    break;
                case 4:
                    if (!ReadString(data, ref position, out var text))
                        return null;
                    constants[i] = text;
                    break;
                default:
                    return null;
            }
        }

        if (!ReadCount(data, ref position, 1, out var functionCount))
            return null;
        var functions = new LuaFunction[functionCount];
        for (var i = 0; i < functionCount; i++)
        {
            if (ReadFunction(data, ref position, depth + 1) is not { } function)
                return null;
            functions[i] = function;
        }

        // Debug information: line numbers, local variables, upvalue names.
        if (!ReadCount(data, ref position, 4, out var lineCount))
            return null;
        position += lineCount * 4;

        if (!ReadCount(data, ref position, 1, out var localCount))
            return null;
        for (var i = 0; i < localCount; i++)
        {
            if (!SkipString(data, ref position) || data.Length - position < 8)
                return null;
            position += 8;
        }

        if (!ReadCount(data, ref position, 1, out var upvalueCount))
            return null;
        for (var i = 0; i < upvalueCount; i++)
        {
            if (!SkipString(data, ref position))
                return null;
        }

        return new LuaFunction { Parameters = parameters, Code = code, Constants = constants, Functions = functions };
    }

    // A count of items of at least itemSize bytes each that the data can hold.
    private static bool ReadCount(ReadOnlySpan<byte> data, ref int position, int itemSize, out int count)
    {
        count = 0;
        if (data.Length - position < 4)
            return false;

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        position += 4;
        if (value > MaxCount || (long)value * itemSize > data.Length - position)
            return false;

        count = (int)value;
        return true;
    }

    private static bool SkipString(ReadOnlySpan<byte> data, ref int position) => ReadString(data, ref position, out _);

    // The stored size counts the terminating NUL; zero is no string.
    private static bool ReadString(ReadOnlySpan<byte> data, ref int position, out string? text)
    {
        text = null;
        if (!ReadCount(data, ref position, 1, out var size))
            return false;
        if (size == 0)
            return true;

        text = Encoding.UTF8.GetString(data.Slice(position, size - 1));
        position += size;
        return true;
    }
}
