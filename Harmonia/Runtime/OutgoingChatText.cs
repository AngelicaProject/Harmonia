using System.Buffers;
using System.Buffers.Binary;
using Harmonia.Packs.Hpk;
using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace Harmonia.Runtime;

public enum ChatNameKind
{
    Item,
    Quest,
}

// PronounModule's encoder puts display names inside Fixed(200,...) macros.
// These bytes go to other clients, unlike the Link macros drawn in ChatLog.
public static class OutgoingChatText
{
    public const string EncoderDispatchSignature = "41 8B C1 4D 85 C0 74 0D 4C 8B 4C 24 28 44 8B C0 E9 ?? ?? ?? ?? E9";

    // The dispatch stub forwards an additional flags argument from the
    // stack. Hook its encoder branch, whose four arguments are known,
    // rather than the incomplete ProcessString declaration in ClientStructs.
    public static long? EncoderAddress(ReadOnlySpan<byte> dispatch, long address)
    {
        ReadOnlySpan<byte> prefix = [0x41, 0x8B, 0xC1, 0x4D, 0x85, 0xC0, 0x74, 0x0D,
            0x4C, 0x8B, 0x4C, 0x24, 0x28, 0x44, 0x8B, 0xC0, 0xE9];
        return dispatch.Length >= 26 && dispatch[..17].SequenceEqual(prefix) && dispatch[21] == 0xE9
            ? address + 21 + BinaryPrimitives.ReadInt32LittleEndian(dispatch[17..])
            : null;
    }

    // Returns NUL-terminated bytes only when a supported payload changed.
    // Unknown macros and all text outside these payloads stay byte-for-byte.
    public static byte[]? Restore(ReadOnlySpan<byte> encoded, Func<ChatNameKind, uint, string?> original, int maxLength)
    {
        if (encoded.IsEmpty || encoded.Length > maxLength || !SeStringCheck.IsWellFormed(encoded))
            return null;

        var result = new ArrayBufferWriter<byte>();
        var changed = false;
        var offset = 0;
        foreach (var payload in new ReadOnlySeStringSpan(encoded))
        {
            if (payload.MacroCode == MacroCode.Fixed && RestorePayload(payload, original) is { } replacement)
            {
                result.Write(replacement);
                changed = true;
            }
            else
            {
                result.Write(encoded.Slice(offset, payload.EnvelopeByteLength));
            }
            offset += payload.EnvelopeByteLength;
        }

        if (!changed)
            return null;
        var bytes = result.WrittenSpan;
        // Never silently truncate a payload or the player's following text.
        if (bytes.Length > maxLength)
            throw new InvalidOperationException("The original chat names exceed the game's encoded line limit.");
        return [.. bytes, 0];
    }

    private static byte[]? RestorePayload(ReadOnlySePayloadSpan payload, Func<ChatNameKind, uint, string?> original)
    {
        var args = new List<byte[]>();
        foreach (var expression in payload)
            args.Add(expression.Body.ToArray());
        if (args.Count < 3 || !UInt(args[0], out var group) || group != 200 ||
            !UInt(args[1], out var kind) || !UInt(args[2], out var id))
            return null;

        if (kind == 10 && args.Count == 6 && UInt(args[3], out var custom) && custom == 1 &&
            String(args[4]) && String(args[5]))
        {
            // The non-custom form carries no title or description at all;
            // the receiving client resolves both from Status by id.
            return new SeStringBuilder().BeginMacro(MacroCode.Fixed)
                .AppendUIntExpression(group).AppendUIntExpression(kind).AppendUIntExpression(id)
                .AppendUIntExpression(0).AppendUIntExpression(0).EndMacro().ToArray();
        }

        if (kind is not (4 or 12) || args.Count != 7 ||
            !UInt(args[3], out _) || !UInt(args[4], out var zero1) || zero1 != 0 ||
            !UInt(args[5], out var zero2) || zero2 != 0 || !String(args[6]))
            return null;

        var name = original(kind == 4 ? ChatNameKind.Item : ChatNameKind.Quest, id);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\0', '\x02', '\x03']) >= 0)
            return null;
        if (kind == 4)
        {
            if (id is > 1_000_000 and < 2_000_000)
                name += " \uE03C";
            else if (id is > 500_000 and < 1_000_000)
                name += " \uE03D";
        }

        var result = new SeStringBuilder().BeginMacro(MacroCode.Fixed);
        for (var i = 0; i < 6; i++)
        {
            UInt(args[i], out var value);
            result.AppendUIntExpression(value);
        }
        result.AppendStringExpression(name).EndMacro();
        var bytes = result.ToArray();
        var before = new byte[payload.EnvelopeByteLength];
        payload.WriteEnvelopeTo(before);
        return bytes.AsSpan().SequenceEqual(before) ? null : bytes;
    }

    private static bool UInt(byte[] expression, out uint value) =>
        new ReadOnlySeExpressionSpan(expression).TryGetUInt(out value);

    private static bool String(byte[] expression) =>
        new ReadOnlySeExpressionSpan(expression).TryGetString(out _);
}
