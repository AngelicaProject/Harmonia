using System.Globalization;
using System.Text;
using Harmonia.Packs.Hpk;

namespace Harmonia.Dictionary;

// The text forms the name dictionary compares: the plain text of a game
// string and a search key that survives what happens to a name copied from a
// web page or typed from memory.
public static class NameText
{
    private const byte Stx = 0x02;
    private const byte NewLine = 0x10;
    private const byte SoftHyphen = 0x16;
    private const byte NonBreakingSpace = 0x1D;
    private const byte Hyphen = 0x1F;

    // Standard Russian and US layouts, key by key; the shifted punctuation
    // keys carry the letters Х, Ъ, Ж, Э, Б, Ю, and Ё.
    private const string Latin = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.~{}:\"<>";
    private const string Cyrillic = "ёйцукенгшщзхъфывапролджэячсмитьбюёхъжэбю";

    private static readonly char[] Apostrophes = ['\'', '’', '‘', 'ʼ', '`'];

    // Macros are dropped, except the ones that stand for a character.
    public static string Plain(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (bytes[0] != Stx)
            {
                var length = bytes.IndexOf(Stx);
                if (length < 0)
                    length = bytes.Length;
                text.Append(Encoding.UTF8.GetString(bytes[..length]));
                bytes = bytes[length..];
                continue;
            }

            // STX, code, length, body, ETX.
            if (bytes.Length < 3 || !SeStringCheck.TryReadUInt(bytes[2..], out var bodyLength, out var lengthBytes))
                break;
            var end = 2L + lengthBytes + bodyLength + 1;
            if (end > bytes.Length)
                break;

            switch (bytes[1])
            {
                case NewLine or NonBreakingSpace:
                    text.Append(' ');
                    break;
                case Hyphen:
                    text.Append('-');
                    break;
                case SoftHyphen:
                    break;
            }

            bytes = bytes[(int)end..];
        }

        return text.ToString().Trim();
    }

    // Lowercase letters and digits separated by single spaces. Accents of
    // Latin letters, apostrophes, and the game's icon glyphs (the HQ and
    // collectable marks in the Private Use Area) are dropped; ё is е.
    // Compatibility forms fold too (full-width Latin, half-width kana).
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var key = new StringBuilder(decomposed.Length);
        var latinBase = false;
        var separated = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                // Й and Ё and the kana voicing marks stay; é becomes e.
                if (!latinBase && key.Length > 0)
                    key.Append(ch);
                continue;
            }

            latinBase = ch < 'ɐ';
            if (ch is >= '' and <= '' || Array.IndexOf(Apostrophes, ch) >= 0)
                continue;

            if (!char.IsLetterOrDigit(ch))
            {
                separated = true;
                continue;
            }

            if (separated && key.Length > 0)
                key.Append(' ');
            separated = false;
            key.Append(char.ToLowerInvariant(ch));
        }

        return key.ToString().Normalize(NormalizationForm.FormC).Replace('ё', 'е');
    }

    // The text as if typed with the other keyboard layout ("Скувутвгь" for
    // "Credendum" and back), or null when no key maps. The direction follows
    // the script most of the letters are in.
    public static string? SwapLayout(string text)
    {
        var latin = 0;
        var cyrillic = 0;
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                latin++;
            else if (ch is >= 'Ѐ' and <= 'ӿ')
                cyrillic++;
        }

        if (latin == cyrillic)
            return null;

        var (from, to) = latin > cyrillic ? (Latin, Cyrillic) : (Cyrillic, Latin);
        var swapped = new StringBuilder(text.Length);
        var changed = false;
        foreach (var ch in text)
        {
            var at = from.IndexOf(char.ToLowerInvariant(ch));
            if (at < 0)
            {
                swapped.Append(ch);
                continue;
            }

            swapped.Append(to[at]);
            changed = true;
        }

        return changed ? swapped.ToString() : null;
    }
}
