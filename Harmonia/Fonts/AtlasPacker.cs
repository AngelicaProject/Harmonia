namespace Harmonia.Fonts;

public readonly record struct AtlasPlacement(int Page, int X, int Y);

// Shelf packing of glyph rectangles into free atlas pages. A group of
// rectangles is placed all together or not at all, so a target that does not
// fit leaves no partial glyphs behind. Placement is deterministic.
public sealed class AtlasPacker
{
    private readonly int[] pages;
    private readonly int width;
    private readonly int height;
    private readonly int padding;
    private List<Shelf>[] shelves;

    public AtlasPacker(IReadOnlyList<int> pages, int width, int height, int padding = 1)
    {
        this.pages = [.. pages];
        this.width = width;
        this.height = height;
        this.padding = padding;
        shelves = this.pages.Select(static _ => new List<Shelf>()).ToArray();
    }

    public IReadOnlyList<int> Pages => pages;

    // Places every rectangle or none. Taller rectangles are placed first,
    // which keeps shelves dense; the result is in input order.
    public bool TryPlace(IReadOnlyList<(int Width, int Height)> sizes, out AtlasPlacement[] placements)
    {
        placements = new AtlasPlacement[sizes.Count];
        var snapshot = shelves.Select(static page => page.Select(static shelf => shelf with { }).ToList()).ToArray();
        var order = Enumerable.Range(0, sizes.Count)
            .OrderByDescending(i => sizes[i].Height)
            .ThenByDescending(i => sizes[i].Width)
            .ThenBy(static i => i);
        foreach (var index in order)
        {
            var (w, h) = sizes[index];
            if (w == 0 || h == 0)
            {
                if (pages.Length == 0)
                {
                    shelves = snapshot;
                    return false;
                }

                placements[index] = new AtlasPlacement(pages[0], 0, 0);
                continue;
            }

            if (!TryPlaceOne(w, h, out placements[index]))
            {
                shelves = snapshot;
                placements = [];
                return false;
            }
        }

        return true;
    }

    private bool TryPlaceOne(int w, int h, out AtlasPlacement placement)
    {
        var paddedW = w + padding;
        var paddedH = h + padding;
        for (var p = 0; p < pages.Length; p++)
        {
            var pageShelves = shelves[p];
            foreach (var shelf in pageShelves)
            {
                if (shelf.Height >= paddedH && shelf.Cursor + paddedW <= width)
                {
                    placement = new AtlasPlacement(pages[p], shelf.Cursor, shelf.Y);
                    shelf.Cursor += paddedW;
                    return true;
                }
            }

            var top = pageShelves.Count == 0 ? 0 : pageShelves[^1].Y + pageShelves[^1].Height;
            if (top + paddedH <= height && paddedW <= width)
            {
                pageShelves.Add(new Shelf(top, paddedH) { Cursor = paddedW });
                placement = new AtlasPlacement(pages[p], 0, top);
                return true;
            }
        }

        placement = default;
        return false;
    }

    private sealed record Shelf(int Y, int Height)
    {
        public int Cursor { get; set; }
    }
}
