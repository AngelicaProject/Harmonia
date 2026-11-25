using System.Buffers;
using System.Text;
using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace Harmonia.Packs.Hpk;

public enum PlayerNamePart : byte { Full, First, Family }

public enum PlayerNameCase : byte { Nominative, Genitive, Dative, Accusative, Instrumental, Prepositional }

public readonly record struct HpkNameCase(ushort Offset, PlayerNamePart Part, PlayerNameCase Case)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool Matches(ReadOnlySpan<byte> text, ReadOnlySpan<HpkNameCase> cases)
    {
        var index = 0;
        return Visit(new ReadOnlySeStringSpan(text), 0, cases, ref index, null, null) && index == cases.Length;
    }

    // Call with the translated cell after its source guard has matched.
    public static byte[]? Format(ReadOnlySpan<byte> text, ReadOnlySpan<HpkNameCase> cases,
        Func<PlayerNamePart, PlayerNameCase, string?> form)
    {
        if (cases.IsEmpty || text.Length > HpkFormat.MaxStringLength ||
            cases.Length > text.Length / 6 || !SeStringCheck.IsWellFormed(text))
            return null;

        var names = new byte[cases.Length][];
        var totalNameBytes = 0;
        for (var i = 0; i < cases.Length; i++)
        {
            var name = form(cases[i].Part, cases[i].Case);
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                return null;
            try
            {
                var length = Utf8.GetByteCount(name);
                if (length > HpkFormat.MaxStringLength - totalNameBytes)
                    return null;
                totalNameBytes += length;
                names[i] = Utf8.GetBytes(name);
            }
            catch (EncoderFallbackException)
            {
                return null;
            }
        }

        var builder = new SeStringBuilder();
        var index = 0;
        if (!Visit(new ReadOnlySeStringSpan(text), 0, cases, ref index, names, builder) || index != cases.Length)
            return null;

        var result = builder.ToArray();
        return result.Length <= HpkFormat.MaxStringLength && SeStringCheck.IsWellFormed(result) ? result : null;
    }

    private static bool Visit(ReadOnlySeStringSpan text, int start, ReadOnlySpan<HpkNameCase> cases,
        ref int index, byte[][]? names, SeStringBuilder? builder)
    {
        var enumerator = text.GetOffsetEnumerator();
        while (enumerator.MoveNext())
        {
            var current = enumerator.Current;
            var payload = current.Payload;
            var offset = start + current.Offset;
            if (payload.Type != ReadOnlySePayloadType.Macro)
            {
                builder?.Append(payload);
                continue;
            }
            if (Identify(payload) is { } part)
            {
                if (index == cases.Length || cases[index].Offset != offset ||
                    cases[index].Part != part || cases[index].Case > PlayerNameCase.Prepositional)
                    return false;
                if (names is not null)
                    builder!.Append(names[index]);
                index++;
                continue;
            }

            if (!VisitArguments(payload, offset, cases, ref index, names, builder))
                return false;
        }
        return true;
    }

    private static bool VisitArguments(ReadOnlySePayloadSpan payload, int offset, ReadOnlySpan<HpkNameCase> cases,
        ref int index, byte[][]? names, SeStringBuilder? builder)
    {
        var code = (byte)payload.MacroCode;
        if (code is not (0x08 or 0x09 or 0x29 or 0x2A or 0x2B or 0x2F))
        {
            builder?.Append(payload);
            return true;
        }

        var body = builder is null ? null : new ArrayBufferWriter<byte>();
        var argOffset = offset + payload.EnvelopeByteLength - payload.Body.Length - 1;
        var argIndex = 0;
        foreach (var expression in payload)
        {
            var displayed = code is 0x08 or 0x09 ? argIndex != 0 : argIndex == 0;
            argIndex++;
            argOffset += expression.Body.Length;
            if (displayed && expression.TryGetString(out var nested))
            {
                var inner = builder is null ? null : new SeStringBuilder();
                if (!Visit(nested, argOffset - nested.ByteLength, cases, ref index, names, inner))
                    return false;
                if (inner is not null)
                {
                    var bytes = inner.ToArray();
                    var header = body!.GetSpan(6);
                    header[0] = 0xFF;
                    body.Advance(1 + SeExpressionUtilities.EncodeUInt(header[1..], (uint)bytes.Length));
                    body.Write(bytes);
                }
            }
            else
                body?.Write(expression.Body);
        }
        if (body is not null)
            builder!.Append(new ReadOnlySePayloadSpan(ReadOnlySePayloadType.Macro, payload.MacroCode, body.WrittenSpan));
        return true;
    }

    private static PlayerNamePart? Identify(ReadOnlySePayloadSpan payload)
    {
        if (IsFullName(payload))
            return PlayerNamePart.Full;
        if ((byte)payload.MacroCode != 0x2C ||
            !payload.TryGetExpression(out var text, out var separator, out var part) ||
            !text.TryGetString(out var nested) || !separator.TryGetString(out var space) ||
            !space.Data.SequenceEqual(" "u8) || !part.TryGetUInt(out var number) || number is not (1 or 2))
            return null;

        var elements = nested.GetEnumerator();
        return elements.MoveNext() && IsFullName(elements.Current) && !elements.MoveNext()
            ? (PlayerNamePart)number : null;
    }

    private static bool IsFullName(ReadOnlySePayloadSpan payload) =>
        payload.Type == ReadOnlySePayloadType.Macro && (byte)payload.MacroCode == 0x29 &&
        payload.TryGetExpression(out var argument) &&
        argument.TryGetParameterExpression(out var type, out var index) && type == 0xEB &&
        index.TryGetUInt(out var number) && number == 1;
}
