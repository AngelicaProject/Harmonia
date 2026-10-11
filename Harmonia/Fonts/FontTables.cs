using System.Buffers.Binary;

namespace Harmonia.Fonts;

// One record of the game's font tables: a font table file, the name pattern
// of its atlas textures, and how many of them the game loads with it.
public readonly record struct FontTableEntry(string Fdt, string Textures, int TextureCount);

// The fonts the game loads, from its own tables. The game draws a glyph only
// from the textures it loaded with the glyph's font and writes through a null
// pointer for a glyph on any other page. The title screen has two tables,
// chosen by the interface resolution, with a different number of textures
// (two at the standard resolution), so a font's pages are the fewest any
// table gives it.
public sealed class FontTables
{
    // The game's font loader refuses ten textures or more.
    public const int MaxTextures = 9;

    private readonly Dictionary<(string Set, string Target), int> pages = [];

    private FontTables()
    {
    }

    public static FontTables Parse(IEnumerable<FontTableEntry> entries)
    {
        var tables = new FontTables();
        foreach (var entry in entries)
        {
            var set = FontSet.All.FirstOrDefault(set => entry.Textures == set.TexturePattern)
                ?? throw new FormatException($"Unknown font textures \"{entry.Textures}\".");
            if (entry.TextureCount is < 1 or > MaxTextures)
                throw new FormatException($"{entry.Fdt} has {entry.TextureCount} textures.");

            var target = TargetName(set, entry.Fdt);
            var count = entry.TextureCount * FontTexture.Channels;
            var key = (set.Name, target);
            tables.pages[key] = tables.pages.TryGetValue(key, out var known) ? Math.Min(known, count) : count;
        }

        if (tables.pages.Count == 0)
            throw new FormatException("The font tables are empty.");
        return tables;
    }

    // The pages the game loads with a font; 0 when the set does not load it.
    public int Pages(FontSet set, string target) => pages.GetValueOrDefault((set.Name, target));

    // The most pages any font of the set has.
    public int Pages(FontSet set) => pages.Where(pair => pair.Key.Set == set.Name).Select(static pair => pair.Value).DefaultIfEmpty().Max();

    public IReadOnlyList<string> Fonts(FontSet set) =>
        [.. pages.Keys.Where(key => key.Set == set.Name).Select(static key => key.Target).Order(StringComparer.Ordinal)];

    private static string TargetName(FontSet set, string fdt)
    {
        const string Extension = ".fdt";
        var name = fdt.EndsWith(Extension, StringComparison.Ordinal) ? fdt[..^Extension.Length] : string.Empty;
        var lobby = name.EndsWith(FontSet.Lobby.FdtSuffix, StringComparison.Ordinal);
        if (lobby != (set == FontSet.Lobby))
            name = string.Empty;
        else if (lobby)
            name = name[..^FontSet.Lobby.FdtSuffix.Length];

        if (name.Length == 0 || !name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new FormatException($"Unexpected font table name \"{fdt}\".");
        return name;
    }
}

// Reads the game's font tables from its loaded image. The function that
// reloads the fonts picks one of three tables (the title screen's two and the
// in-game one) and loads every record of it; the tables are found through
// that code only, so a game build with other code yields nothing rather than
// a guess.
public static class FontTableScan
{
    // cmp sil, 1; jne; cmp [r13+..], 1; lea rsi, standard title table;
    // lea rax, high-resolution title table; cmovne rsi, rax; jmp;
    // lea rsi, in-game table
    private static readonly short[] Tables = Pattern(
        "40 80 FE 01 75 ?? 41 83 BD ?? ?? ?? ?? 01 48 8D 35 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 48 0F 45 F0 EB ?? 48 8D 35 ?? ?? ?? ??");

    // inc r12d; add rsi, 0x18; cmp r12d, <records>; jl
    private static readonly short[] Loop = Pattern("41 FF C4 48 83 C6 18 41 83 FC ?? 0F 8C");

    private static readonly int[] TableLeas = [14, 21, 34];
    private const int LoopCount = 10;
    private const int FunctionSize = 0x400;
    private const int RecordSize = 0x18;
    private const int MaxName = 64;

    // `module` is the loaded image, `baseAddress` the address it is loaded
    // at, `function` the offset of the font reload function in it.
    public static IReadOnlyList<FontTableEntry> Read(ReadOnlySpan<byte> module, ulong baseAddress, int function)
    {
        if (function < 0 || function >= module.Length)
            throw new FormatException("The font reload function is outside the game image.");

        var code = module.Slice(function, Math.Min(FunctionSize, module.Length - function));
        var tables = Find(code, Tables);
        if (tables < 0)
            throw new FormatException("The font tables were not found in the font reload function.");
        var loop = Find(code[tables..], Loop);
        if (loop < 0)
            throw new FormatException("The font table loop was not found in the font reload function.");

        int records = code[tables + loop + LoopCount];
        if (records is < 1 or > 0x7F)
            throw new FormatException($"Unexpected font table size {records}.");

        var entries = new List<FontTableEntry>();
        foreach (var lea in TableLeas)
        {
            // lea reg, [rip + disp32]: 3 bytes of opcode, the address is
            // relative to the next instruction.
            var at = tables + lea;
            long table = function + at + 7 + BinaryPrimitives.ReadInt32LittleEndian(code[(at + 3)..]);
            if (table < 0 || table + ((long)records * RecordSize) > module.Length)
                throw new FormatException("A font table is outside the game image.");

            for (var i = 0; i < records; i++)
            {
                var record = module.Slice((int)table + (i * RecordSize), RecordSize);
                entries.Add(new FontTableEntry(
                    Text(module, baseAddress, BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..])),
                    Text(module, baseAddress, BinaryPrimitives.ReadUInt64LittleEndian(record[8..])),
                    BinaryPrimitives.ReadInt32LittleEndian(record)));
            }
        }

        return entries;
    }

    private static string Text(ReadOnlySpan<byte> module, ulong baseAddress, ulong address)
    {
        if (address < baseAddress || address - baseAddress >= (ulong)module.Length)
            throw new FormatException("A font table name is outside the game image.");

        var text = module[(int)(address - baseAddress)..];
        var length = text[..Math.Min(MaxName, text.Length)].IndexOf((byte)0);
        if (length <= 0)
            throw new FormatException("A font table name is not a short string.");

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            if (text[i] is < 0x20 or > 0x7E)
                throw new FormatException("A font table name is not text.");
            chars[i] = (char)text[i];
        }

        return new string(chars);
    }

    private static short[] Pattern(string text) =>
        [.. text.Split(' ').Select(static part => part == "??" ? (short)-1 : Convert.ToInt16(part, 16))];

    private static int Find(ReadOnlySpan<byte> bytes, short[] pattern)
    {
        for (var start = 0; start + pattern.Length <= bytes.Length; start++)
        {
            var i = 0;
            while (i < pattern.Length && (pattern[i] < 0 || pattern[i] == bytes[start + i]))
                i++;
            if (i == pattern.Length)
                return start;
        }

        return -1;
    }
}
