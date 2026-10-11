using System.Text;
using Harmonia.Packs.Hpk;

namespace Harmonia.Runtime;

// A line the player sends to the chat: finding a quest phrase typed in the
// translation and putting the game's own phrase in its place. The rule is
// never stricter than the game's comparison (the phrase anywhere in the text,
// or the whole text), so a line the game would have accepted in the
// translation is always changed; letter case, typographic punctuation, a
// missing final mark, and "е" for "ё" are forgiven on top of that.
public static class SayText
{
    // The game's limit for a chat line.
    public const int MaxLineBytes = 500;

    private const byte Stx = 0x02;
    private const byte Etx = 0x03;

    private static readonly UTF8Encoding Strict = new(false, true);

    // The text command a line starts with ("/say" of "/say hello"), empty for
    // a line without one; bodyStart is where the text after it begins.
    public static ReadOnlySpan<byte> Command(ReadOnlySpan<byte> line, out int bodyStart)
    {
        bodyStart = 0;
        if (line.IsEmpty || line[0] != (byte)'/')
            return default;

        var space = line.IndexOf((byte)' ');
        bodyStart = space < 0 ? line.Length : space + 1;
        return space < 0 ? line : line[..space];
    }

    // body with `phrase` replaced by `original`, or null when the phrase is
    // not there. Macros in the line (item links, auto-translate) stay as they
    // are; the phrase is looked for in the text between them.
    public static byte[]? Replace(ReadOnlySpan<byte> body, string phrase, string original, bool exact)
    {
        var wanted = Normalize(phrase);
        var shorter = wanted.TrimEnd('.', '!', '?', '…');
        if (wanted.Length == 0)
            return null;

        for (var position = 0; position < body.Length;)
        {
            if (body[position] == 0)
                return null;

            if (body[position] == Stx)
            {
                var length = PayloadLength(body[position..]);
                if (length < 0 || exact)
                    return null;
                position += length;
                continue;
            }

            var end = body[position..].IndexOfAny(Stx, (byte)0);
            end = end < 0 ? body.Length : position + end;
            if (Decode(body[position..end]) is not { } text)
                return null;

            if (exact)
            {
                var said = Normalize(text.Trim());
                var same = said.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                    (shorter.Length > 0 && said.Equals(shorter, StringComparison.OrdinalIgnoreCase));
                return same && end == body.Length && position == 0 ? Encoding.UTF8.GetBytes(original) : null;
            }

            // Normalizing keeps every character in its place.
            var normalized = Normalize(text);
            var matched = wanted.Length;
            var at = normalized.IndexOf(wanted, StringComparison.OrdinalIgnoreCase);
            if (at < 0 && shorter.Length > 0 && shorter.Length < wanted.Length)
            {
                matched = shorter.Length;
                at = normalized.IndexOf(shorter, StringComparison.OrdinalIgnoreCase);
            }

            if (at >= 0)
            {
                var replaced = Encoding.UTF8.GetBytes(string.Concat(text.AsSpan(0, at), original, text.AsSpan(at + matched)));
                var result = new byte[position + replaced.Length + body.Length - end];
                body[..position].CopyTo(result);
                replaced.CopyTo(result, position);
                body[end..].CopyTo(result.AsSpan(position + replaced.Length));
                return result;
            }

            position = end;
        }

        return null;
    }

    // The punctuation the game itself evens out before it compares, and "ё".
    public static string Normalize(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = Normalize(source[i]);
        });

    private static char Normalize(char c) => c switch
    {
        ' ' or '　' or ' ' or (>= ' ' and <= ' ') => ' ',
        '＝' => '=',
        '！' => '!',
        '？' => '?',
        (>= '‐' and <= '―') or '−' or '－' or 'ー' or 'ｰ' => '-',
        '’' or '‘' or '′' or '`' or '＇' => '\'',
        '“' or '”' or '＂' or '«' or '»' => '"',
        'ё' => 'е',
        'Ё' => 'Е',
        _ => c,
    };

    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // STX, code, length, that many bytes, ETX.
    private static int PayloadLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || !SeStringCheck.TryReadUInt(bytes[2..], out var size, out var sizeLength))
            return -1;

        var end = 2L + sizeLength + size;
        return end < bytes.Length && bytes[(int)end] == Etx ? (int)end + 1 : -1;
    }
}
