using Lumina.Text.ReadOnly;

namespace Harmonia.Packs.Hpk;

// Structural SeString check before a pack is accepted: the bytes are written
// into game rows unchanged, so a malformed macro must never get that far.
public static class SeStringCheck
{
    // Only bounds recursion. Game strings reach 46 levels (chains of <if> whose
    // else branch holds the next <if>, one per job), and a translation keeps
    // that structure, so the limit stays far above it.
    private const int MaxDepth = 256;

    public static bool IsWellFormed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Contains((byte)0))
            return false;

        return IsValid(new ReadOnlySeStringSpan(bytes), 0);
    }

    // Lumina's ReadOnlySeStringSpan.Validate() returns false for every string
    // (Lumina 7.0), so payload and expression validation that reaches a string
    // parameter, such as the separator of <kilo(lnum1,\,)>, is reimplemented here.
    private static bool IsValid(ReadOnlySeStringSpan text, int depth)
    {
        if (depth > MaxDepth)
            return false;

        foreach (var payload in text)
        {
            if (!IsValid(payload, depth))
                return false;
        }

        return true;
    }

    private static bool IsValid(ReadOnlySePayloadSpan payload, int depth)
    {
        switch (payload.Type)
        {
            case ReadOnlySePayloadType.Text:
                return payload.Validate();
            case ReadOnlySePayloadType.Macro:
                foreach (var expression in payload)
                {
                    if (!IsValid(expression, depth + 1))
                        return false;
                }

                return true;
            default:
                return false;
        }
    }

    private static bool IsValid(ReadOnlySeExpressionSpan expression, int depth)
    {
        if (depth > MaxDepth)
            return false;
        if (expression.TryGetInt(out _) || expression.TryGetPlaceholderExpression(out _))
            return true;
        if (expression.TryGetString(out var text))
            return IsValid(text, depth + 1);
        if (expression.TryGetParameterExpression(out _, out var operand))
            return IsValid(operand, depth + 1);
        if (expression.TryGetBinaryExpression(out _, out var left, out var right))
            return IsValid(left, depth + 1) && IsValid(right, depth + 1);
        return false;
    }
}
