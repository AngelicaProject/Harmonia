namespace Harmonia.Fonts;

public readonly record struct AtlasPlacement(int Page, int X, int Y);

// A page with room: everything from row Top down is free.
public readonly record struct AtlasPage(int Page, int Top);

// Shelf packing of glyph rectangles into the free rows of atlas pages. A
// group of rectangles is placed all together or not at all, so a target that
// does not fit leaves no partial glyphs behind. Placement is deterministic.
public sealed class AtlasPacker
{
    private readonly AtlasPage[] pages;
    private readonly int width;
    private readonly int height;
    private readonly int padding;
    private List<Shelf>[] shelves;

    public AtlasPacker(IReadOnlyList<AtlasPage> pages, int width, int height, int padding = 1)
    {
        this.pages = [.. pages];
        this.width = width;
        this.height = height;
        this.padding = padding;
        shelves = this.pages.Select(static _ => new List<Shelf>()).ToArray();
    }

    // Places every rectangle or none, on pages below `pageLimit` only. Taller
    // rectangles are placed first, which keeps shelves dense; the result is
    // in input order.
    public bool TryPlace(IReadOnlyList<(int Width, int Height)> sizes, int pageLimit, out AtlasPlacement[] placements)
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
            if (!TryPlaceOne(w, h, pageLimit, out placements[index]))
            {
                shelves = snapshot;
                placements = [];
                return false;
            }
        }

        return true;
    }

    private bool TryPlaceOne(int w, int h, int pageLimit, out AtlasPlacement placement)
    {
        var paddedW = w + padding;
        var paddedH = h + padding;
        for (var p = 0; p < pages.Length; p++)
        {
            if (pages[p].Page >= pageLimit)
                continue;

            // A glyph without pixels takes no room.
            if (w == 0 || h == 0)
            {
                placement = new AtlasPlacement(pages[p].Page, 0, 0);
                return true;
            }

            var pageShelves = shelves[p];
            foreach (var shelf in pageShelves)
            {
                if (shelf.Height >= paddedH && shelf.Cursor + paddedW <= width)
                {
                    placement = new AtlasPlacement(pages[p].Page, shelf.Cursor, shelf.Y);
                    shelf.Cursor += paddedW;
                    return true;
                }
            }

            // The first shelf keeps the padding away from what the page holds.
            var top = pageShelves.Count > 0 ? pageShelves[^1].Y + pageShelves[^1].Height
                : pages[p].Top == 0 ? 0 : pages[p].Top + padding;
            if (top + paddedH <= height && paddedW <= width)
            {
                pageShelves.Add(new Shelf(top, paddedH) { Cursor = paddedW });
                placement = new AtlasPlacement(pages[p].Page, 0, top);
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
