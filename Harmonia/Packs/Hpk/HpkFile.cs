using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Harmonia.Packs.Hpk;

public enum HpkOpenMode
{
    // Header, sections and manifest only: enough to list an installed pack.
    Metadata,

    // Everything the format requires: digest, signature, every record and string.
    Full,
}

public readonly record struct HpkLayoutColumn(uint ColumnIndex, uint Offset);

public readonly record struct HpkRow(long FirstCell, int CellCount);

public readonly unsafe struct HpkCell(byte* @string, int length, ulong sourceGuard)
{
    public byte* String { get; } = @string;
    public int Length { get; } = length;
    public ulong SourceGuard { get; } = sourceGuard;
}

// A mapped, validated .hpk file. Lookups read the mapping directly, so a
// translation pointer stays valid for the lifetime of this object.
public sealed unsafe class HpkFile : IDisposable
{
    private readonly IDisposable? owner;
    private readonly byte* data;
    private readonly Dictionary<string, int> sheetsByName = new(StringComparer.Ordinal);
    private readonly string[] sheetNames;
    private readonly HpkLayoutColumn[]?[] layouts;

    private long sheetsOffset;
    private long layoutOffset;
    private long rowsOffset;
    private long cellsOffset;
    private long stringsOffset;
    private long stringsLength;
    private (long Offset, long Length)? fontsSection;
    private long namesOffset;
    private long namesLength;
    private int sheetCount;
    private int layoutCount;
    private int rowCount;
    private int cellCount;
    private bool disposed;

    private HpkFile(byte* data, long length, IDisposable? owner, HpkOpenMode mode)
    {
        this.data = data;
        this.owner = owner;
        Length = length;
        Mode = mode;

        var bodyLength = ReadHeader();
        PackHash = Span(bodyLength - HpkFormat.DigestSize, HpkFormat.DigestSize).ToArray();
        var sections = ReadSections(bodyLength);

        var manifest = sections[HpkFormat.KindManifest];
        Manifest = HpkManifest.Parse(Span(manifest.Offset, checked((int)manifest.Length)));

        if (bodyLength < length)
            Signature = HpkSignature.Parse(Span(bodyLength, checked((int)(length - bodyLength))));

        BindSections(sections);
        if (sections.TryGetValue(HpkFormat.KindFonts, out var fonts))
            fontsSection = fonts;
        sheetNames = new string[sheetCount];
        layouts = new HpkLayoutColumn[]?[sheetCount];
        ReadSheets();

        if (mode == HpkOpenMode.Full)
        {
            VerifyDigest(bodyLength);
            Signature?.Verify(PackHash);
            ValidateRecords();
            if (HasFonts)
                ReadFonts();
        }
    }

    public HpkOpenMode Mode { get; }
    public long Length { get; }
    public byte[] PackHash { get; }
    public string PackHashText => "sha256:" + Convert.ToHexStringLower(PackHash);
    public HpkManifest Manifest { get; }
    public HpkSignature? Signature { get; }
    public int SheetCount => sheetCount;
    public int CellCount => cellCount;
    public IReadOnlyList<string> SheetNames => sheetNames;

    // The pack carries a FONTS section (format minor 1).
    public bool HasFonts => fontsSection is not null;

    // Parses and validates the FONTS section; null when the pack has none.
    public HpkFonts? ReadFonts()
    {
        if (fontsSection is not { } section)
            return null;
        if (section.Length > int.MaxValue)
            throw new HpkFormatException("FONTS section is too large.");

        return HpkFonts.Parse(Span(section.Offset, (int)section.Length));
    }

    public static HpkFile Open(string path, HpkOpenMode mode)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new HpkFormatException("Pack file not found.");
        if (info.Length < HpkFormat.HeaderSize + HpkFormat.DigestSize || info.Length > HpkFormat.MaxPackBytes)
            throw new HpkFormatException("Pack file size is out of range.");

        var mapping = new Mapping(path);
        try
        {
            return new HpkFile(mapping.Pointer, info.Length, mapping, mode);
        }
        catch
        {
            mapping.Dispose();
            throw;
        }
    }

    public static HpkFile FromBytes(ReadOnlySpan<byte> bytes, HpkOpenMode mode)
    {
        if (bytes.Length < HpkFormat.HeaderSize + HpkFormat.DigestSize)
            throw new HpkFormatException("Pack file size is out of range.");

        var memory = new NativeBuffer(bytes);
        try
        {
            return new HpkFile(memory.Pointer, bytes.Length, memory, mode);
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    public bool TryGetSheet(string sheetName, out int sheet) => sheetsByName.TryGetValue(sheetName, out sheet);

    public bool IsSubrowSheet(int sheet) => Span(SheetRecord(sheet) + 8, 1)[0] == 1;

    public HpkLayoutColumn[] GetLayout(int sheet)
    {
        var cached = Volatile.Read(ref layouts[sheet]);
        if (cached is not null)
            return cached;

        var record = SheetRecord(sheet);
        var start = U32(record + 12);
        var count = (int)U32(record + 16);
        var result = new HpkLayoutColumn[count];
        for (var i = 0; i < count; i++)
        {
            var at = layoutOffset + ((start + i) * (long)HpkFormat.LayoutRecordSize);
            result[i] = new HpkLayoutColumn(U32(at), U32(at + 4));
        }

        Volatile.Write(ref layouts[sheet], result);
        return result;
    }

    // The translated cells of one row, in ordinal order. False when the pack
    // has no cells for the row.
    public bool TryGetRow(int sheet, uint rowId, ushort subrowId, out HpkRow row)
    {
        row = default;
        if (disposed || (uint)sheet >= (uint)sheetCount)
            return false;

        var record = SheetRecord(sheet);
        long low = U32(record + 20);
        long high = low + U32(record + 24) - 1;
        var key = ((ulong)rowId << 16) | subrowId;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var at = rowsOffset + (mid * HpkFormat.RowRecordSize);
            var candidate = ((ulong)U32(at) << 16) | U16(at + 4);
            if (candidate < key)
                low = mid + 1;
            else if (candidate > key)
                high = mid - 1;
            else
            {
                row = new HpkRow(U32(at + 8), U16(at + 6));
                return true;
            }
        }

        return false;
    }

    // Cell `index` (0..row.CellCount) of a row returned by TryGetRow.
    public HpkCell GetCell(HpkRow row, int index, out ushort ordinal)
    {
        if ((uint)index >= (uint)row.CellCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        var at = cellsOffset + ((row.FirstCell + index) * HpkFormat.CellRecordSize);
        ordinal = U16(at);
        return new HpkCell(
            data + stringsOffset + U32(at + 8),
            (int)U32(at + 4),
            BinaryPrimitives.ReadUInt64LittleEndian(Span(at + 16, HpkFormat.GuardSize)));
    }

    public bool TryGetCell(int sheet, uint rowId, ushort subrowId, uint ordinal, out HpkCell cell)
    {
        cell = default;
        if (!TryGetRow(sheet, rowId, subrowId, out var row))
            return false;

        for (var i = 0; i < row.CellCount; i++)
        {
            var candidate = GetCell(row, i, out var cellOrdinal);
            if (cellOrdinal == ordinal)
            {
                cell = candidate;
                return true;
            }

            if (cellOrdinal > ordinal)
                break;
        }

        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        owner?.Dispose();
    }

    private ReadOnlySpan<byte> Span(long offset, int length) => new(data + offset, length);

    private ushort U16(long offset) => BinaryPrimitives.ReadUInt16LittleEndian(Span(offset, 2));

    private uint U32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(Span(offset, 4));

    private ulong U64(long offset) => BinaryPrimitives.ReadUInt64LittleEndian(Span(offset, 8));

    private long SheetRecord(int sheet) => sheetsOffset + ((long)sheet * HpkFormat.SheetRecordSize);

    private long ReadHeader()
    {
        if (!Span(0, 8).SequenceEqual(HpkFormat.Magic))
            throw new HpkFormatException("Not a Harmonia pack.");
        if (U16(8) != HpkFormat.FormatMajor)
            throw new HpkFormatException("Unsupported pack format version " + U16(8) + ".");
        if (U32(12) != HpkFormat.HeaderSize || U32(28) != 0 || !IsZero(32, 32))
            throw new HpkFormatException("Invalid pack header.");

        var bodyLength = U64(16);
        var sectionCount = U32(24);
        if (sectionCount > HpkFormat.MaxSections)
            throw new HpkFormatException("Too many pack sections.");

        var minimum = HpkFormat.HeaderSize + (sectionCount * HpkFormat.SectionEntrySize) + HpkFormat.DigestSize;
        if (bodyLength < (ulong)minimum || bodyLength > (ulong)Length)
            throw new HpkFormatException("Invalid pack body length.");

        return (long)bodyLength;
    }

    private Dictionary<uint, (long Offset, long Length)> ReadSections(long bodyLength)
    {
        var sectionCount = (int)U32(24);
        var tableEnd = HpkFormat.HeaderSize + ((long)sectionCount * HpkFormat.SectionEntrySize);
        var digestStart = bodyLength - HpkFormat.DigestSize;
        var result = new Dictionary<uint, (long, long)>();
        var cursor = tableEnd;

        for (var i = 0; i < sectionCount; i++)
        {
            var entry = HpkFormat.HeaderSize + ((long)i * HpkFormat.SectionEntrySize);
            var kind = U32(entry);
            var offset = U64(entry + 8);
            var length = U64(entry + 16);
            if (U32(entry + 4) != 0 || offset % 8 != 0 || offset < (ulong)cursor ||
                length > (ulong)digestStart || offset > (ulong)digestStart - length)
                throw new HpkFormatException("Invalid pack section table.");

            if (!IsZero(cursor, (int)((long)offset - cursor)))
                throw new HpkFormatException("Non-zero padding between pack sections.");

            cursor = (long)(offset + length);
            if (kind == HpkFormat.KindFonts)
            {
                if (!result.TryAdd(kind, ((long)offset, (long)length)))
                    throw new HpkFormatException("Duplicate pack section " + kind + ".");
                continue;
            }

            if (kind > HpkFormat.FirstOptionalKind)
                continue;
            if (kind is < HpkFormat.KindManifest or > HpkFormat.KindStrings)
                throw new HpkFormatException("Unknown required pack section " + kind + ".");
            if (!result.TryAdd(kind, ((long)offset, (long)length)))
                throw new HpkFormatException("Duplicate pack section " + kind + ".");
        }

        if (!IsZero(cursor, (int)(digestStart - cursor)))
            throw new HpkFormatException("Non-zero padding before the pack digest.");

        for (var kind = HpkFormat.KindManifest; kind <= HpkFormat.KindStrings; kind++)
        {
            if (!result.ContainsKey(kind))
                throw new HpkFormatException("Missing pack section " + kind + ".");
        }

        return result;
    }

    private void BindSections(Dictionary<uint, (long Offset, long Length)> sections)
    {
        (namesOffset, namesLength) = sections[HpkFormat.KindNames];
        (stringsOffset, stringsLength) = sections[HpkFormat.KindStrings];
        sheetsOffset = sections[HpkFormat.KindSheets].Offset;
        layoutOffset = sections[HpkFormat.KindLayout].Offset;
        rowsOffset = sections[HpkFormat.KindRows].Offset;
        cellsOffset = sections[HpkFormat.KindCells].Offset;
        sheetCount = Records(sections[HpkFormat.KindSheets].Length, HpkFormat.SheetRecordSize, "SHEETS");
        layoutCount = Records(sections[HpkFormat.KindLayout].Length, HpkFormat.LayoutRecordSize, "LAYOUT");
        rowCount = Records(sections[HpkFormat.KindRows].Length, HpkFormat.RowRecordSize, "ROWS");
        cellCount = Records(sections[HpkFormat.KindCells].Length, HpkFormat.CellRecordSize, "CELLS");
    }

    private static int Records(long length, int size, string name)
    {
        if (length % size != 0 || length / size > int.MaxValue)
            throw new HpkFormatException($"Section {name} has an invalid length.");

        return (int)(length / size);
    }

    // Sheet names and ranges are needed for any lookup, so they are checked
    // in every mode.
    private void ReadSheets()
    {
        var decoder = new UTF8Encoding(false, true);
        long nameCursor = 0;
        long layoutCursor = 0;
        long rowCursor = 0;
        ReadOnlySpan<byte> previous = default;

        for (var i = 0; i < sheetCount; i++)
        {
            var record = SheetRecord(i);
            var nameOffset = U32(record);
            var nameLength = U32(record + 4);
            var variant = Span(record + 8, 1)[0];
            var layoutStart = U32(record + 12);
            var layoutCountForSheet = U32(record + 16);
            var rowStart = U32(record + 20);
            var rowCountForSheet = U32(record + 24);

            if (!IsZero(record + 9, 3) || U32(record + 28) != 0 || variant > 1)
                throw new HpkFormatException("Invalid sheet record.");
            if (nameOffset != nameCursor || nameLength == 0 || nameOffset + (long)nameLength > namesLength)
                throw new HpkFormatException("Invalid sheet name range.");
            if (layoutStart != layoutCursor || layoutCountForSheet == 0 || layoutStart + (long)layoutCountForSheet > layoutCount)
                throw new HpkFormatException("Invalid sheet layout range.");
            if (rowStart != rowCursor || rowCountForSheet == 0 || rowStart + (long)rowCountForSheet > rowCount)
                throw new HpkFormatException("Invalid sheet row range.");

            var nameBytes = Span(namesOffset + nameOffset, (int)nameLength);
            if (i > 0 && previous.SequenceCompareTo(nameBytes) >= 0)
                throw new HpkFormatException("Sheets are not sorted by name.");

            string name;
            try
            {
                name = decoder.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                throw new HpkFormatException("Sheet name is not valid UTF-8.");
            }

            sheetNames[i] = name;
            sheetsByName.Add(name, i);
            previous = nameBytes;
            nameCursor += nameLength;
            layoutCursor += layoutCountForSheet;
            rowCursor += rowCountForSheet;
        }

        if (nameCursor != namesLength || layoutCursor != layoutCount || rowCursor != rowCount)
            throw new HpkFormatException("Sheet records do not cover their sections.");
    }

    private void VerifyDigest(long bodyLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunk = 1 << 20;
        var end = bodyLength - HpkFormat.DigestSize;
        for (long offset = 0; offset < end; offset += chunk)
            hash.AppendData(Span(offset, (int)Math.Min(chunk, end - offset)));

        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(PackHash))
            throw new HpkFormatException("Pack digest does not match its content.");
    }

    private void ValidateRecords()
    {
        var strings = new Dictionary<uint, int>();

        for (var sheet = 0; sheet < sheetCount; sheet++)
        {
            var record = SheetRecord(sheet);
            var subrows = Span(record + 8, 1)[0] == 1;
            var layoutStart = U32(record + 12);
            var layoutCountForSheet = U32(record + 16);
            var rowStart = U32(record + 20);
            var rowCountForSheet = U32(record + 24);

            long previousColumn = -1;
            for (long l = layoutStart; l < layoutStart + layoutCountForSheet; l++)
            {
                var at = layoutOffset + (l * HpkFormat.LayoutRecordSize);
                var column = U32(at);
                if (column <= previousColumn || column > ushort.MaxValue || U32(at + 4) > ushort.MaxValue)
                    throw new HpkFormatException($"Invalid layout in sheet '{sheetNames[sheet]}'.");
                previousColumn = column;
            }

            long previousKey = -1;
            for (long r = rowStart; r < rowStart + rowCountForSheet; r++)
            {
                var row = rowsOffset + (r * HpkFormat.RowRecordSize);
                var rowId = U32(row);
                var subrow = U16(row + 4);
                var cells = U16(row + 6);
                var first = U32(row + 8);
                var key = ((long)rowId << 16) | subrow;
                if (key <= previousKey || (!subrows && subrow != 0) || cells == 0 || U32(row + 12) != 0 ||
                    first + (long)cells > cellCount)
                    throw new HpkFormatException($"Invalid row {rowId}/{subrow} in sheet '{sheetNames[sheet]}'.");
                previousKey = key;

                long previousOrdinal = -1;
                for (long c = first; c < first + cells; c++)
                {
                    var at = cellsOffset + (c * HpkFormat.CellRecordSize);
                    var ordinal = U16(at);
                    var length = U32(at + 4);
                    var offset = U32(at + 8);
                    if (ordinal <= previousOrdinal || ordinal >= layoutCountForSheet ||
                        !IsZero(at + 2, 2) || U32(at + 12) != 0 ||
                        length is 0 or > HpkFormat.MaxStringLength || offset + (long)length + 1 > stringsLength)
                        throw new HpkFormatException($"Invalid cell in row {rowId}/{subrow} of sheet '{sheetNames[sheet]}'.");
                    previousOrdinal = ordinal;

                    if (strings.TryGetValue(offset, out var knownLength))
                    {
                        if (knownLength != length)
                            throw new HpkFormatException("Cells share a string with different lengths.");
                        continue;
                    }

                    var bytes = Span(stringsOffset + offset, (int)length);
                    if (Span(stringsOffset + offset + length, 1)[0] != 0 || !SeStringCheck.IsWellFormed(bytes))
                        throw new HpkFormatException($"Malformed string in row {rowId}/{subrow} of sheet '{sheetNames[sheet]}'.");
                    strings.Add(offset, (int)length);
                }
            }
        }

        // Stored strings must tile STRINGS exactly: no hidden or orphaned bytes.
        long cursor = 0;
        foreach (var (offset, length) in strings.OrderBy(static s => s.Key))
        {
            if (offset != cursor)
                throw new HpkFormatException("STRINGS contains unreferenced bytes.");
            cursor += length + 1;
        }

        if (cursor != stringsLength)
            throw new HpkFormatException("STRINGS contains unreferenced bytes.");
    }

    private bool IsZero(long offset, int length)
    {
        return !Span(offset, length).ContainsAnyExcept((byte)0);
    }

    private sealed class Mapping : IDisposable
    {
        private readonly MemoryMappedFile file;
        private readonly MemoryMappedViewAccessor view;

        public Mapping(string path)
        {
            file = MemoryMappedFile.CreateFromFile(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            try
            {
                view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                Pointer = pointer + view.PointerOffset;
            }
            catch
            {
                view?.Dispose();
                file.Dispose();
                throw;
            }
        }

        public byte* Pointer { get; }

        public void Dispose()
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
            file.Dispose();
        }
    }

    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(ReadOnlySpan<byte> bytes)
        {
            Pointer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(Pointer, bytes.Length));
        }

        public byte* Pointer { get; private set; }

        public void Dispose()
        {
            if (Pointer == null)
                return;

            NativeMemory.Free(Pointer);
            Pointer = null;
        }
    }
}
