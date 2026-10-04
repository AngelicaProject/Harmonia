namespace Harmonia.Dictionary;

// In result order when matches are equally good.
public enum NameCategory
{
    Item,
    Action,
    Status,
    Place,
    Duty,
    Quest,
}

// A name in the game's language (Language is a pack language tag).
public readonly record struct NameOriginal(string Language, string Text);

// Shown is what the game shows this session: the translation, or the
// original when the pack has none that applies. Originals start with the
// client language.
public sealed record NameEntry(NameCategory Category, uint RowId, string Shown, IReadOnlyList<NameOriginal> Originals)
{
    public bool IsTranslated => Originals.Count == 0 || !string.Equals(Shown, Originals[0].Text, StringComparison.Ordinal);
}

// Total counts the matches of the searched category; ByCategory counts the
// matches of every category, whatever was searched.
public sealed record NameSearchResult(IReadOnlyList<NameEntry> Matches, int Total, IReadOnlyDictionary<NameCategory, int> ByCategory)
{
    public static readonly NameSearchResult Empty = new([], 0, new Dictionary<NameCategory, int>());
}

// Every name of the session in every indexed language, searchable by any of
// them. Immutable once built; searches may run on any thread.
public sealed class NameIndex
{
    public const int MinQueryLength = 2;

    private const int Exact = 1000;
    private const int Prefix = 800;
    private const int Words = 600;
    private const int Substring = 500;
    private const int Fuzzy = 300;
    private const int SwappedLayoutPenalty = 50;
    private const int MaxFuzzyLength = 48;

    private readonly Item[] items;

    private NameIndex(Item[] items) => this.items = items;

    public int Count => items.Length;

    // Names that read the same in every language collapse into the first
    // one, so a name shared by many rows (an action every class has) is
    // listed once.
    public static NameIndex Build(IEnumerable<NameEntry> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<Item>();
        foreach (var name in names)
        {
            var forms = new List<string> { NameText.Normalize(name.Shown) };
            foreach (var original in name.Originals)
            {
                var form = NameText.Normalize(original.Text);
                if (!forms.Contains(form))
                    forms.Add(form);
            }

            forms.RemoveAll(static f => f.Length == 0);
            if (forms.Count == 0 || !seen.Add((int)name.Category + "\n" + string.Join("\n", forms)))
                continue;

            items.Add(new Item(name, [.. forms]));
        }

        return new NameIndex([.. items]);
    }

    // category null searches every category.
    public NameSearchResult Search(string query, int limit, NameCategory? category = null, CancellationToken cancellationToken = default)
    {
        var queries = new List<(Query Query, int Penalty)>();
        var normalized = NameText.Normalize(query);
        if (normalized.Length >= MinQueryLength)
            queries.Add((new Query(normalized, normalized.Split(' ')), 0));
        if (NameText.SwapLayout(query) is { } swapped && NameText.Normalize(swapped) is { Length: >= MinQueryLength } other &&
            other != normalized)
            queries.Add((new Query(other, other.Split(' ')), SwappedLayoutPenalty));
        if (queries.Count == 0)
            return NameSearchResult.Empty;

        // Typos only count when nothing matches as typed, so the costly
        // typo pass runs only then.
        var scored = Collect(queries, false, cancellationToken);
        if (scored.Count == 0)
            scored = Collect(queries, true, cancellationToken);

        var byCategory = scored.CountBy(static s => s.Item.Entry.Category).ToDictionary();
        if (category is { } only)
            scored.RemoveAll(s => s.Item.Entry.Category != only);

        var matches = scored
            .OrderByDescending(static s => s.Score)
            .ThenBy(static s => s.Closeness)
            .ThenBy(static s => s.Item.Entry.Category)
            .ThenBy(static s => s.Item.Entry.RowId)
            .Take(limit)
            .Select(static s => s.Item.Entry)
            .ToList();
        return new NameSearchResult(matches, scored.Count, byCategory);
    }

    private List<(Item Item, int Score, int Closeness)> Collect(List<(Query Query, int Penalty)> queries, bool typos, CancellationToken cancellationToken)
    {
        var scored = new List<(Item Item, int Score, int Closeness)>();
        for (var i = 0; i < items.Length; i++)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            var best = 0;
            var closeness = int.MaxValue;
            foreach (var (q, penalty) in queries)
            {
                foreach (var form in item.Forms)
                {
                    var score = typos ? FuzzyScore(form, q.Words) : Score(form, q);
                    if (score == 0)
                        continue;
                    score -= penalty;
                    var distance = Math.Abs(form.Length - q.Text.Length);
                    if (score > best || (score == best && distance < closeness))
                    {
                        best = score;
                        closeness = distance;
                    }
                }
            }

            if (best > 0)
                scored.Add((item, best, closeness));
        }

        return scored;
    }

    // As typed: 0 when only a typo pass could match.
    private static int Score(string text, Query query)
    {
        var q = query.Text;
        if (text == q)
            return Exact;

        // Every match as typed contains every query word; most names
        // contain none, and this vectorized check rejects them.
        foreach (var word in query.Words)
        {
            if (!text.Contains(word, StringComparison.Ordinal))
                return 0;
        }

        if (text.StartsWith(q, StringComparison.Ordinal))
            return Prefix;
        if (AllWordsStart(text, query.Words))
            return Words;
        return text.Contains(q, StringComparison.Ordinal) ? Substring : 0;
    }

    // Every query word starts a different word of the name, in any order.
    // Words are split on demand: the index keeps only the keys.
    private static bool AllWordsStart(string text, string[] queryWords)
    {
        var count = text.AsSpan().Count(' ') + 1;
        if (queryWords.Length > count)
            return false;

        Span<bool> used = stackalloc bool[count];
        foreach (var q in queryWords)
        {
            var found = false;
            var i = 0;
            foreach (var range in text.AsSpan().Split(' '))
            {
                if (!used[i] && text.AsSpan(range).StartsWith(q, StringComparison.Ordinal))
                {
                    used[i] = found = true;
                    break;
                }

                i++;
            }

            if (!found)
                return false;
        }

        return true;
    }

    // Every query word is a word of the name with a typo or two; short words
    // must start one. Fewer typos score higher.
    private static int FuzzyScore(string text, string[] queryWords)
    {
        var total = 0;
        foreach (var q in queryWords)
        {
            var allowed = q.Length >= 8 ? 2 : q.Length >= 4 ? 1 : 0;
            var best = int.MaxValue;
            foreach (var range in text.AsSpan().Split(' '))
            {
                var word = text.AsSpan(range);
                if (allowed == 0)
                {
                    if (word.StartsWith(q, StringComparison.Ordinal))
                        best = 0;
                }
                else if (Math.Abs(word.Length - q.Length) <= allowed)
                {
                    best = Math.Min(best, Distance(word, q, allowed));
                }
                else if (word.Length > q.Length && Distance(word[..q.Length], q, allowed) <= allowed)
                {
                    // A typo in a word that is still being typed.
                    best = Math.Min(best, allowed);
                }

                if (best == 0)
                    break;
            }

            if (best > allowed)
                return 0;
            total += best;
        }

        return Fuzzy - (10 * total);
    }

    // Edit distance with adjacent transpositions; anything above `limit` is
    // reported as limit + 1.
    private static int Distance(ReadOnlySpan<char> a, string b, int limit)
    {
        if (a.Length > MaxFuzzyLength || b.Length > MaxFuzzyLength)
            return limit + 1;

        var width = b.Length + 1;
        Span<int> previous2 = stackalloc int[width];
        Span<int> previous = stackalloc int[width];
        Span<int> current = stackalloc int[width];
        for (var j = 0; j < width; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = i;
            for (var j = 1; j < width; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    value = Math.Min(value, previous2[j - 2] + 1);
                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }

            if (rowMin > limit)
                return limit + 1;

            var spare = previous2;
            previous2 = previous;
            previous = current;
            current = spare;
        }

        return Math.Min(previous[b.Length], limit + 1);
    }

    private readonly record struct Query(string Text, string[] Words);

    // Forms: the search keys of the shown name and the originals.
    private sealed record Item(NameEntry Entry, string[] Forms);
}
