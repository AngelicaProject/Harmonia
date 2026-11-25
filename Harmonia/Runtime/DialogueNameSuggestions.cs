using Harmonia.Packs.Hpk;

namespace Harmonia.Runtime;

internal static class DialogueNameSuggestions
{
    public static void ApplyName(Dictionary<PlayerNameCase, DialogueNameForms> forms, string fullName)
    {
        var parts = fullName.Split((char[]?)null, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var nominative = new DialogueNameForms
        {
            First = parts[0],
            Family = parts.Length == 2 ? parts[1] : string.Empty,
            Full = string.Join(" ", parts),
        };
        var previous = new Dictionary<PlayerNameCase, DialogueNameForms>();
        if (forms.GetValueOrDefault(PlayerNameCase.Nominative) is { } original)
        {
            previous[PlayerNameCase.Nominative] = new()
            {
                First = original.First,
                Family = original.Family,
                Full = original.Full,
            };
            FillEmpty(previous);
        }

        var suggestions = new Dictionary<PlayerNameCase, DialogueNameForms> { [PlayerNameCase.Nominative] = nominative };
        FillEmpty(suggestions);
        forms[PlayerNameCase.Nominative] = nominative;
        foreach (var (nameCase, suggested) in suggestions)
        {
            if (nameCase == PlayerNameCase.Nominative)
                continue;
            if (forms.GetValueOrDefault(nameCase) is not { } existing)
            {
                forms[nameCase] = suggested;
                continue;
            }

            var old = previous.GetValueOrDefault(nameCase);
            var first = Update(existing.First, old?.First, suggested.First);
            var family = Update(existing.Family, old?.Family, suggested.Family);
            existing.Full = Update(existing.Full, old?.Full, Combine(first, family));
            existing.First = first;
            existing.Family = family;
        }
    }

    private static string Update(string value, string? old, string replacement) =>
        string.IsNullOrWhiteSpace(value) || value == old ? replacement : value;

    public static void FillEmpty(Dictionary<PlayerNameCase, DialogueNameForms> forms)
    {
        if (forms.GetValueOrDefault(PlayerNameCase.Nominative) is not { } nominative)
            return;

        var first = Decline(nominative.First, surname: false);
        var family = Decline(nominative.Family, surname: true);
        foreach (var nameCase in Enum.GetValues<PlayerNameCase>())
        {
            if (!forms.TryGetValue(nameCase, out var form) || form is null)
                forms[nameCase] = form = new DialogueNameForms();

            Fill(form, first[(int)nameCase], family[(int)nameCase]);
        }
    }

    private static void Fill(DialogueNameForms form, string first, string family)
    {
        if (string.IsNullOrWhiteSpace(form.First))
            form.First = first;
        if (string.IsNullOrWhiteSpace(form.Family))
            form.Family = family;
        if (string.IsNullOrWhiteSpace(form.Full))
            form.Full = Combine(form.First, form.Family);
    }

    private static string Combine(string first, string family) => $"{first} {family}".Trim();

    private static string[] Decline(string? value, bool surname)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0 || !value.All(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё'))
            return Unchanged(value);

        var lower = value.ToLowerInvariant();
        if (surname)
        {
            if (HasEnding(lower, "ова", "ева", "ёва", "ина", "ына"))
                return Forms(value, 1, "ой", "ой", "у", "ой", "ой");
            if (HasEnding(lower, "ов", "ев", "ёв", "ин", "ын"))
                return Forms(value, 0, "а", "у", "а", "ым", "е");
        }
        else if (lower.EndsWith("ия", StringComparison.Ordinal))
            return Forms(value, 1, "и", "и", "ю", "ей", "и");
        else if (lower.EndsWith("ий", StringComparison.Ordinal))
            return Forms(value, 1, "я", "ю", "я", "ем", "и");
        else if (lower.EndsWith('й'))
            return Forms(value, 1, "я", "ю", "я", "ем", "е");

        if (lower.Length > 1 && lower.EndsWith('а'))
        {
            var genitive = "гкхжчшщ".Contains(lower[^2]) ? "и" : "ы";
            var instrumental = "жчшщц".Contains(lower[^2]) ? "ей" : "ой";
            return Forms(value, 1, genitive, "е", "у", instrumental, "е");
        }
        if (lower.EndsWith('я'))
            return Forms(value, 1, "и", "е", "ю", "ей", "е");
        return Unchanged(value);
    }

    private static string[] Unchanged(string value) => [value, value, value, value, value, value];

    private static bool HasEnding(string value, params string[] endings) =>
        endings.Any(ending => value.EndsWith(ending, StringComparison.Ordinal));

    private static string[] Forms(string value, int trim, params string[] endings)
    {
        var upper = value.All(char.IsUpper);
        var stem = value[..(value.Length - trim)];
        return [value, .. endings.Select(ending => stem + (upper ? ending.ToUpperInvariant() : ending))];
    }
}
