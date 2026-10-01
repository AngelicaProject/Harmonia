using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Harmonia.Packs.Hpk;
using Newtonsoft.Json.Linq;

namespace Harmonia.Tests.Packs;

internal sealed record HpkTestCell(uint Row, ushort Subrow, ushort Ordinal, string Text, string Source);

internal sealed record HpkTestSheet(string Name, bool Subrows, (uint Index, uint Offset)[] Layout, List<HpkTestCell> Cells);

// Independent writer for Pack Format v1 used to produce test fixtures. It
// follows docs/formats/pack-v1.md rather than sharing code with the reader.
internal sealed class HpkBuilder
{
    public string Title { get; set; } = "Test pack";
    public string Team { get; set; } = "Test team";
    public List<string> Authors { get; } = ["Анна"];
    public string Version { get; set; } = "2026.10.01.0001";
    public string Channel { get; set; } = "stable";
    public string Language { get; set; } = "ru";
    public string GameLanguage { get; set; } = "en";
    public string GameVersion { get; set; } = "2026.08.12.0000.0000";
    public string MinHarmonia { get; set; } = "1.0.0";
    public List<HpkTestSheet> Sheets { get; } = [];
    public bool SortSheets { get; set; } = true;
    public byte[] TrailingStringBytes { get; set; } = [];
    public ECDsa? Signer { get; set; }
    public ECDsa? PreviousSigner { get; set; }

    // Edits applied to the manifest JSON before it is written.
    public Action<JObject>? EditManifest { get; set; }

    public static HpkBuilder WithDefaultSheet()
    {
        var builder = new HpkBuilder();
        builder.Sheets.Add(new HpkTestSheet("Addon", false, [(0, 4), (2, 8)],
        [
            new HpkTestCell(1, 0, 0, "Привет", "Hello"),
            new HpkTestCell(1, 0, 1, "Мир", "World"),
            new HpkTestCell(7, 0, 1, "Привет", "Hi"),
        ]));
        builder.Sheets.Add(new HpkTestSheet("quest/000/Test", true, [(1, 0)],
        [
            new HpkTestCell(3, 0, 0, "Первая", "First"),
            new HpkTestCell(3, 1, 0, "Вторая", "Second"),
        ]));
        return builder;
    }

    public byte[] Build(Action<byte[]>? mutateBody = null)
    {
        var sheets = SortSheets
            ? Sheets.OrderBy(static s => Encoding.UTF8.GetBytes(s.Name), ByteComparer.Instance).ToList()
            : Sheets.ToList();

        var names = new MemoryStream();
        var sheetRecords = new MemoryStream();
        var layout = new MemoryStream();
        var rows = new MemoryStream();
        var cells = new MemoryStream();
        var strings = new MemoryStream();
        var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        long rowCount = 0;
        long cellCount = 0;
        uint layoutCursor = 0;

        foreach (var sheet in sheets)
        {
            var nameBytes = Encoding.UTF8.GetBytes(sheet.Name);
            var grouped = sheet.Cells
                .GroupBy(static c => (c.Row, c.Subrow))
                .OrderBy(static g => g.Key.Row).ThenBy(static g => g.Key.Subrow)
                .ToList();

            Write32(sheetRecords, (uint)names.Length);
            Write32(sheetRecords, (uint)nameBytes.Length);
            sheetRecords.WriteByte(sheet.Subrows ? (byte)1 : (byte)0);
            sheetRecords.Write(new byte[3]);
            Write32(sheetRecords, layoutCursor);
            Write32(sheetRecords, (uint)sheet.Layout.Length);
            Write32(sheetRecords, (uint)rowCount);
            Write32(sheetRecords, (uint)grouped.Count);
            Write32(sheetRecords, 0);
            names.Write(nameBytes);

            foreach (var (index, offset) in sheet.Layout)
            {
                Write32(layout, index);
                Write32(layout, offset);
            }

            layoutCursor += (uint)sheet.Layout.Length;

            foreach (var row in grouped)
            {
                var rowCells = row.OrderBy(static c => c.Ordinal).ToList();
                Write32(rows, row.Key.Row);
                Write16(rows, row.Key.Subrow);
                Write16(rows, (ushort)rowCells.Count);
                Write32(rows, (uint)cellCount);
                Write32(rows, 0);
                rowCount++;

                foreach (var cell in rowCells)
                {
                    var text = Encoding.UTF8.GetBytes(cell.Text);
                    if (!stringOffsets.TryGetValue(cell.Text, out var offset))
                    {
                        offset = (uint)strings.Length;
                        stringOffsets.Add(cell.Text, offset);
                        strings.Write(text);
                        strings.WriteByte(0);
                    }

                    Write16(cells, cell.Ordinal);
                    cells.Write(new byte[2]);
                    Write32(cells, (uint)text.Length);
                    Write32(cells, offset);
                    Write32(cells, 0);
                    var guard = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(guard, SourceGuard.Compute(Encoding.UTF8.GetBytes(cell.Source)));
                    cells.Write(guard);
                    cellCount++;
                }
            }
        }

        strings.Write(TrailingStringBytes);

        var manifest = new JObject
        {
            ["title"] = Title,
            ["team"] = new JObject { ["name"] = Team, ["url"] = null },
            ["authors"] = new JArray(Authors),
            ["license"] = null,
            ["version"] = Version,
            ["channel"] = Channel,
            ["language"] = Language,
            ["game"] = new JObject { ["language"] = GameLanguage, ["version"] = GameVersion },
            ["built"] = new JObject { ["aeria"] = "0.0.0-test", ["commit"] = new string('a', 40) },
            ["minHarmonia"] = MinHarmonia,
        };
        EditManifest?.Invoke(manifest);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString(Newtonsoft.Json.Formatting.Indented) + "\n");

        var sections = new (uint Kind, byte[] Data)[]
        {
            (HpkFormat.KindManifest, manifestBytes),
            (HpkFormat.KindNames, names.ToArray()),
            (HpkFormat.KindSheets, sheetRecords.ToArray()),
            (HpkFormat.KindLayout, layout.ToArray()),
            (HpkFormat.KindRows, rows.ToArray()),
            (HpkFormat.KindCells, cells.ToArray()),
            (HpkFormat.KindStrings, strings.ToArray()),
        };

        var file = new MemoryStream();
        file.Write(new byte[HpkFormat.HeaderSize + (sections.Length * HpkFormat.SectionEntrySize)]);
        var table = new List<(uint Kind, long Offset, long Length)>();
        foreach (var (kind, data) in sections)
        {
            Pad(file);
            table.Add((kind, file.Length, data.Length));
            file.Write(data);
        }

        Pad(file);
        var bodyLength = file.Length + HpkFormat.DigestSize;
        var body = file.ToArray();

        "AERIAHPK"u8.CopyTo(body);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), HpkFormat.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), (ulong)bodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), (uint)sections.Length);
        for (var i = 0; i < table.Count; i++)
        {
            var at = HpkFormat.HeaderSize + (i * HpkFormat.SectionEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), table[i].Kind);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(at + 8), (ulong)table[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(at + 16), (ulong)table[i].Length);
        }

        mutateBody?.Invoke(body);

        var digest = SHA256.HashData(body);
        var output = new MemoryStream();
        output.Write(body);
        output.Write(digest);

        if (Signer is not null)
        {
            var publicKey = PublicKey(Signer);
            output.Write("HPKSIG01"u8);
            Write16(output, HpkFormat.SignatureAlgorithmEcdsaP256);
            Write16(output, 0);
            output.Write(publicKey);
            output.Write(Sign(Signer, "AERIA-HPK-V1-SIGNATURE"u8, digest));
            if (PreviousSigner is null)
            {
                output.WriteByte(0);
            }
            else
            {
                output.WriteByte(1);
                output.Write(PublicKey(PreviousSigner));
                output.Write(Sign(PreviousSigner, "AERIA-HPK-V1-KEY-ROTATION"u8, publicKey));
            }
        }

        return output.ToArray();
    }

    public static byte[] PublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
    }

    public static string Fingerprint(ECDsa key) => HpkSignature.FingerprintOf(PublicKey(key));

    private static byte[] Sign(ECDsa key, ReadOnlySpan<byte> domain, ReadOnlySpan<byte> payload)
    {
        byte[] message = [.. domain, .. payload];
        return key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static void Pad(MemoryStream stream)
    {
        while (stream.Length % 8 != 0)
            stream.WriteByte(0);
    }

    private static void Write16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void Write32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
