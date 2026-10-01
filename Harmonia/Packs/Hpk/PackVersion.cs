using System.Globalization;
using System.Text.RegularExpressions;

namespace Harmonia.Packs.Hpk;

// A release version, YYYY.MM.DD.NNNN: the UTC date of the release and its
// number on that day (Aeria docs/formats/pack-v1.md, "Release version").
// Every part has a fixed width, so text order is version order.
public static partial class PackVersion
{
    public static bool IsValid(string? text) =>
        text is not null &&
        Pattern().IsMatch(text) &&
        text[11..] != "0000" &&
        DateTime.TryParseExact(text[..10], "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
        date.Year >= 2000;

    // Both versions are valid.
    public static int Compare(string left, string right) => string.CompareOrdinal(left, right);

    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
