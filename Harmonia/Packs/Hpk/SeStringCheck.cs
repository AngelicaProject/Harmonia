using System.Text.Unicode;

namespace Harmonia.Packs.Hpk;

// Structural SeString check before a pack is accepted: the bytes are written
// into game rows unchanged, so a malformed macro must never get that far.
//
// This is the well-formed string rule of Pack Format v1, the same rule as
// Aeria's `aeria_se::bytes::is_well_formed`; both run the shared vectors in
// TestData/well_formed.vectors.txt. It needs no macro catalog: any macro code
// and any nullary value of the reserved ranges is accepted, so macros the game
// adds later never make a pack unreadable.
public static class SeStringCheck
{
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte StringExpression = 0xFF;
    private static readonly (int Flag, int Shift)[] Flags = [(8, 24), (4, 16), (2, 8), (1, 0)];

    // Bounds recursion. Game strings reach 46 levels (chains of <if> whose
    // else branch holds the next <if>, one per job), and Aeria's macro text
    // cannot nest deeper than 128, so the limit stays far above both.
    public const int MaxExpressionDepth = 256;

    public static bool IsWellFormed(ReadOnlySpan<byte> bytes)
    {
        return !bytes.IsEmpty && IsWellFormedText(bytes, 0);
    }

    private static bool IsWellFormedText(ReadOnlySpan<byte> bytes, int depth)
    {
        while (!bytes.IsEmpty)
        {
            int length;
            if (bytes[0] == 0)
                return false;

            if (bytes[0] == Stx)
            {
                length = PayloadLength(bytes, depth);
                if (length < 0)
                    return false;
            }
            else
            {
                length = bytes.IndexOfAny(Stx, (byte)0);
                if (length < 0)
                    length = bytes.Length;
                if (!Utf8.IsValid(bytes[..length]))
                    return false;
            }

            bytes = bytes[length..];
        }

        return true;
    }

    // STX, code, length, body, ETX, where the body is a sequence of expressions.
    private static int PayloadLength(ReadOnlySpan<byte> bytes, int depth)
    {
        if (bytes.Length < 3 || !TryReadCanonicalUInt(bytes[2..], out var length, out var lengthBytes))
            return -1;

        var start = 2 + lengthBytes;
        var end = start + (long)length;
        if (end >= bytes.Length || bytes[(int)end] != Etx)
            return -1;

        var body = bytes[start..(int)end];
        while (!body.IsEmpty)
        {
            var used = ExpressionLength(body, depth + 1);
            if (used < 0)
                return -1;
            body = body[used..];
        }

        return (int)end + 1;
    }

    private static int ExpressionLength(ReadOnlySpan<byte> bytes, int depth)
    {
        if (depth > MaxExpressionDepth || bytes.IsEmpty)
            return -1;

        var kind = bytes[0];
        if (TryReadUInt(bytes, out _, out var intLength))
            return TryReadCanonicalUInt(bytes, out _, out _) ? intLength : -1;

        if (kind == StringExpression)
        {
            if (!TryReadCanonicalUInt(bytes[1..], out var length, out var lengthBytes))
                return -1;
            var total = 1 + lengthBytes + (long)length;
            if (total > bytes.Length)
                return -1;
            return IsWellFormedText(bytes[(1 + lengthBytes)..(int)total], depth) ? (int)total : -1;
        }

        // Nullary values such as the hour of the set time.
        if (kind is >= 0xD0 and <= 0xDF or 0xEC)
            return 1;

        // Parameters: a type byte and an operand.
        if (kind is >= 0xE8 and <= 0xEB)
        {
            var operand = ExpressionLength(bytes[1..], depth + 1);
            return operand < 0 ? -1 : 1 + operand;
        }

        // Comparisons: a type byte and two operands.
        if (kind is >= 0xE0 and <= 0xE5)
        {
            var left = ExpressionLength(bytes[1..], depth + 1);
            if (left < 0)
                return -1;
            var right = ExpressionLength(bytes[(1 + left)..], depth + 1);
            return right < 0 ? -1 : 1 + left + right;
        }

        return -1;
    }

    // Integers are one byte (value + 1) below 0xCF; otherwise a marker 0xF0..0xFE
    // whose low bits, plus one, flag which of the four big-endian bytes follow.
    // Present bytes are never zero.
    internal static bool TryReadUInt(ReadOnlySpan<byte> bytes, out uint value, out int length)
    {
        value = 0;
        length = 0;
        if (bytes.IsEmpty)
            return false;

        var first = bytes[0];
        if (first is >= 0x01 and <= 0xCF)
        {
            value = (uint)first - 1;
            length = 1;
            return true;
        }

        if (first is < 0xF0 or > 0xFE || bytes.Length < 2)
            return false;

        var flags = (first + 1) & 0x0F;
        var at = 1;
        foreach (var (flag, shift) in Flags)
        {
            if ((flags & flag) == 0)
                continue;
            if (at >= bytes.Length || bytes[at] == 0)
                return false;
            value |= (uint)bytes[at] << shift;
            at++;
        }

        length = at;
        return true;
    }

    // The canonical form: the one-byte form whenever the value fits it.
    private static bool TryReadCanonicalUInt(ReadOnlySpan<byte> bytes, out uint value, out int length)
    {
        return TryReadUInt(bytes, out value, out length) && (length == 1 || value >= 0xCF);
    }
}
