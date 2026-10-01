namespace Harmonia.Runtime;

// Cyrillic letter case for the game's Utf8String ToUpper and ToLower, which
// change only Latin letters. The game builds <head>, <headall>, <caps>, and
// <lower> from them: in English a sheet holds "paladin" and the interface
// shows "Paladin", while a Russian "паладин" stays lowercase. These passes run
// after the game's own and follow the same rules on Cyrillic letters, so
// Latin text is never touched twice. Cyrillic letters are two bytes in both
// cases (U+0400..U+045F), so a string never changes length. SeString
// payloads (0x02 … 0x03) are skipped whole.
public static class CyrillicCase
{
    private const byte Space = 0x20;
    private const byte PayloadStart = 0x02;

    private static readonly HashSet<string> CyrillicLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ru", "uk", "be", "bg", "sr", "mk", "kk", "ky", "mn", "tg", "tt", "ba", "cv",
    };

    // Whether a pack's language is written in Cyrillic, so the casing passes
    // are needed.
    public static bool IsCyrillicLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;

        var primary = language.Split('-')[0];
        return CyrillicLanguages.Contains(primary) || language.Contains("Cyrl", StringComparison.OrdinalIgnoreCase);
    }

    // Utf8String::ToUpper(firstCharOnly, everyWord, …, excludeWord): with
    // firstCharOnly and not everyWord only the first character; with both,
    // the first character of every word (a word starts after a space); without
    // firstCharOnly every letter. excluded(i) tells whether a word of the
    // exclusion list starts at byte i; the game never excludes position 0.
    public static void ToUpper(Span<byte> text, bool firstCharOnly, bool everyWord, Func<int, bool>? excluded = null)
    {
        if (firstCharOnly && !everyWord)
        {
            Upper(text, 0);
            return;
        }

        var wordStart = true;
        var i = 0;
        while (i < text.Length)
        {
            var b = text[i];
            if (b == 0)
                return;
            if (b == PayloadStart)
            {
                if (!TrySkipPayload(text, ref i))
                    return;
                wordStart = false;
                continue;
            }

            if (b == Space)
            {
                wordStart = true;
                i++;
                continue;
            }

            if ((!firstCharOnly || wordStart) && (i == 0 || excluded is null || !excluded(i)))
                Upper(text, i);
            wordStart = false;
            i += Utf8Length(b);
        }
    }

    // Utf8String::ToLower: every letter.
    public static void ToLower(Span<byte> text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var b = text[i];
            if (b == 0)
                return;
            if (b == PayloadStart)
            {
                if (!TrySkipPayload(text, ref i))
                    return;
                continue;
            }

            Lower(text, i);
            i += Utf8Length(b);
        }
    }

    private static void Upper(Span<byte> text, int i)
    {
        if (i + 1 >= text.Length)
            return;

        var (lead, tail) = (text[i], text[i + 1]);
        if (lead == 0xD0 && tail is >= 0xB0 and <= 0xBF)
        {
            text[i + 1] = (byte)(tail - 0x20);
        }
        else if (lead == 0xD1 && tail is >= 0x80 and <= 0x8F)
        {
            text[i] = 0xD0;
            text[i + 1] = (byte)(tail + 0x20);
        }
        else if (lead == 0xD1 && tail is >= 0x90 and <= 0x9F)
        {
            text[i] = 0xD0;
            text[i + 1] = (byte)(tail - 0x10);
        }
    }

    private static void Lower(Span<byte> text, int i)
    {
        if (i + 1 >= text.Length || text[i] != 0xD0)
            return;

        var tail = text[i + 1];
        if (tail is >= 0x80 and <= 0x8F)
        {
            text[i] = 0xD1;
            text[i + 1] = (byte)(tail + 0x10);
        }
        else if (tail is >= 0x90 and <= 0x9F)
        {
            text[i + 1] = (byte)(tail + 0x20);
        }
        else if (tail is >= 0xA0 and <= 0xAF)
        {
            text[i] = 0xD1;
            text[i + 1] = (byte)(tail - 0x20);
        }
    }

    private static int Utf8Length(byte lead) => lead switch
    {
        < 0xC0 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        _ => 4,
    };

    // 0x02, a code byte, the body length as a SeString integer, the body, and
    // 0x03. False when the payload is cut off or malformed.
    private static bool TrySkipPayload(ReadOnlySpan<byte> text, ref int i)
    {
        var at = i + 2;
        if (at >= text.Length)
            return false;

        var marker = text[at++];
        long length;
        if (marker is > 0 and < 0xD0)
        {
            length = marker - 1;
        }
        else if (marker is >= 0xF0 and <= 0xFE)
        {
            var flags = (marker & 0x0F) + 1;
            length = 0;
            for (var bit = 3; bit >= 0; bit--)
            {
                length <<= 8;
                if ((flags & (1 << bit)) == 0)
                    continue;
                if (at >= text.Length)
                    return false;
                length |= text[at++];
            }
        }
        else
        {
            return false;
        }

        var end = at + length;
        if (length < 0 || end >= text.Length || text[(int)end] != 0x03)
            return false;

        i = (int)end + 1;
        return true;
    }
}
