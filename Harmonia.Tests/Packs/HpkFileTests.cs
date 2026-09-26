using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Harmonia.Packs.Hpk;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed unsafe class HpkFileTests
{
    [Fact]
    public void Source_guard_is_the_domain_framed_raw_hash_prefix()
    {
        var raw = "Hello"u8.ToArray();
        byte[] framed = [.. "HARMONIA-HXS-V1-RAW-STRING"u8, 5, 0, 0, 0, .. raw];
        var expected = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(framed));

        Assert.Equal(expected, SourceGuard.Compute(raw));
        Assert.NotEqual(expected, SourceGuard.Compute("Hello!"u8));
    }

    [Fact]
    public void Valid_pack_opens_and_serves_cells()
    {
        using var file = HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full);

        Assert.Equal("test-pack", file.Manifest.PackId);
        Assert.Equal(2, file.SheetCount);
        Assert.Equal(5, file.CellCount);
        Assert.Null(file.Signature);
        Assert.True(file.TryGetSheet("Addon", out var addon));
        Assert.False(file.IsSubrowSheet(addon));
        Assert.Equal([new HpkLayoutColumn(0, 4), new HpkLayoutColumn(2, 8)], file.GetLayout(addon));

        Assert.True(file.TryGetCell(addon, 1, 0, 1, out var cell));
        Assert.Equal("Мир", Encoding.UTF8.GetString(cell.String, cell.Length));
        Assert.Equal(0, cell.String[cell.Length]);
        Assert.Equal(SourceGuard.Compute("World"u8), cell.SourceGuard);
        Assert.False(file.TryGetCell(addon, 7, 0, 0, out _));
        Assert.False(file.TryGetCell(addon, 2, 0, 0, out _));

        Assert.True(file.TryGetRow(addon, 1, 0, out var row));
        Assert.Equal(2, row.CellCount);
        Assert.Equal("Привет", Encoding.UTF8.GetString(file.GetCell(row, 0, out var first).String, 12));
        Assert.Equal(0, first);
        file.GetCell(row, 1, out var second);
        Assert.Equal(1, second);
        Assert.False(file.TryGetRow(addon, 2, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetCell(row, 2, out _));
    }

    [Fact]
    public void Subrows_are_separate_cells()
    {
        using var file = HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full);
        Assert.True(file.TryGetSheet("quest/000/Test", out var quest));
        Assert.True(file.IsSubrowSheet(quest));

        Assert.True(file.TryGetCell(quest, 3, 0, 0, out var first));
        Assert.True(file.TryGetCell(quest, 3, 1, 0, out var second));
        Assert.Equal("Первая", Encoding.UTF8.GetString(first.String, first.Length));
        Assert.Equal("Вторая", Encoding.UTF8.GetString(second.String, second.Length));
        Assert.False(file.TryGetCell(quest, 3, 2, 0, out _));
    }

    [Fact]
    public void Identical_translations_are_stored_once()
    {
        using var file = HpkFile.FromBytes(HpkBuilder.WithDefaultSheet().Build(), HpkOpenMode.Full);
        Assert.Equal(4, file.Manifest.Counts.Strings);
        file.TryGetSheet("Addon", out var addon);
        file.TryGetCell(addon, 1, 0, 0, out var a);
        file.TryGetCell(addon, 7, 0, 1, out var b);
        Assert.True(a.String == b.String);
    }

    [Fact]
    public void Tampered_content_fails_the_digest()
    {
        var bytes = HpkBuilder.WithDefaultSheet().Build();
        var index = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Мир"));
        bytes[index] ^= 1;

        var error = Assert.Throws<HpkFormatException>(() => HpkFile.FromBytes(bytes, HpkOpenMode.Full));
        Assert.Contains("digest", error.Message);
        using var metadata = HpkFile.FromBytes(bytes, HpkOpenMode.Metadata);
    }

    [Theory]
    [InlineData("unsorted")]
    [InlineData("duplicate-ordinal")]
    [InlineData("ordinal-out-of-layout")]
    [InlineData("subrow-in-default-sheet")]
    [InlineData("orphan-string-bytes")]
    [InlineData("malformed-sestring")]
    [InlineData("unreviewed-under-reviewed-policy")]
    [InlineData("unknown-manifest-field")]
    [InlineData("wrong-counts")]
    [InlineData("bad-pack-id")]
    public void Structurally_invalid_packs_are_rejected(string defect)
    {
        var builder = HpkBuilder.WithDefaultSheet();
        var addon = builder.Sheets.Single(s => s.Name == "Addon").Cells;
        switch (defect)
        {
            case "unsorted":
                builder.Sheets.Reverse();
                builder.SortSheets = false;
                break;
            case "duplicate-ordinal":
                addon.Add(new HpkTestCell(1, 0, 1, "Дубль", "World"));
                break;
            case "ordinal-out-of-layout":
                addon.Add(new HpkTestCell(9, 0, 2, "Лишний", "Extra"));
                break;
            case "subrow-in-default-sheet":
                addon.Add(new HpkTestCell(9, 1, 0, "Подстрока", "Subrow"));
                break;
            case "orphan-string-bytes":
                builder.TrailingStringBytes = "hidden\0"u8.ToArray();
                break;
            case "malformed-sestring":
                addon.Add(new HpkTestCell(9, 0, 0, "\u0002\u0013\u0002ÿ\u0003", "Broken"));
                break;
            case "unreviewed-under-reviewed-policy":
                addon.Add(new HpkTestCell(9, 0, 0, "Черновик", "Draft", HpkFormat.StateUnreviewed));
                break;
            case "unknown-manifest-field":
                builder.EditManifest = m => m["extra"] = true;
                break;
            case "wrong-counts":
                builder.EditManifest = m => m["counts"]!["cells"] = 99;
                break;
            case "bad-pack-id":
                builder.PackId = "Bad Id";
                break;
        }

        Assert.Throws<HpkFormatException>(() => HpkFile.FromBytes(builder.Build(), HpkOpenMode.Full));
    }

    [Fact]
    public void Unreviewed_cells_are_allowed_under_the_all_policy()
    {
        var builder = HpkBuilder.WithDefaultSheet();
        builder.ContentPolicy = "all";
        builder.Sheets[0].Cells.Add(new HpkTestCell(9, 0, 0, "Черновик", "Draft", HpkFormat.StateUnreviewed));

        using var file = HpkFile.FromBytes(builder.Build(), HpkOpenMode.Full);
        Assert.Equal(HpkContentPolicy.All, file.Manifest.ContentPolicy);
        file.TryGetSheet("Addon", out var addon);
        Assert.True(file.TryGetCell(addon, 9, 0, 0, out var cell));
        Assert.Equal(HpkFormat.StateUnreviewed, cell.State);
    }

    [Fact]
    public void Unknown_major_version_is_rejected()
    {
        var bytes = HpkBuilder.WithDefaultSheet().Build(body => BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), 2));
        Assert.Throws<HpkFormatException>(() => HpkFile.FromBytes(bytes, HpkOpenMode.Metadata));
    }

    [Fact]
    public void Signed_pack_verifies_and_exposes_its_fingerprint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var builder = HpkBuilder.WithDefaultSheet();
        builder.Signer = key;

        using var file = HpkFile.FromBytes(builder.Build(), HpkOpenMode.Full);
        Assert.NotNull(file.Signature);
        Assert.Equal(HpkBuilder.Fingerprint(key), file.Signature!.Fingerprint);
        Assert.Null(file.Signature.PreviousFingerprint);
    }

    [Fact]
    public void Signature_over_other_content_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var builder = HpkBuilder.WithDefaultSheet();
        builder.Signer = key;
        var signed = builder.Build();
        builder.Sequence = 2;
        var other = builder.Build();

        // Graft the first pack's signature block onto the second pack's body.
        var bodyLength = (int)BinaryPrimitives.ReadUInt64LittleEndian(other.AsSpan(16));
        var signedBody = (int)BinaryPrimitives.ReadUInt64LittleEndian(signed.AsSpan(16));
        byte[] grafted = [.. other.AsSpan(0, bodyLength), .. signed.AsSpan(signedBody)];

        var error = Assert.Throws<HpkFormatException>(() => HpkFile.FromBytes(grafted, HpkOpenMode.Full));
        Assert.Contains("signature", error.Message);
    }

    [Fact]
    public void Key_rotation_endorsement_is_verified()
    {
        using var previous = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var builder = HpkBuilder.WithDefaultSheet();
        builder.Signer = next;
        builder.PreviousSigner = previous;

        using (var file = HpkFile.FromBytes(builder.Build(), HpkOpenMode.Full))
            Assert.Equal(HpkBuilder.Fingerprint(previous), file.Signature!.PreviousFingerprint);

        // An endorsement made by a different key than the one it names.
        var bytes = builder.Build();
        var forged = HpkBuilder.PublicKey(stranger);
        var at = bytes.Length - 64 - 65;
        forged.CopyTo(bytes, at);
        Assert.Throws<HpkFormatException>(() => HpkFile.FromBytes(bytes, HpkOpenMode.Full));
    }

    [Fact]
    public void Manifest_rejects_duplicate_keys()
    {
        var json = """{"packId":"a","packId":"b"}""";
        Assert.Throws<HpkFormatException>(() => HpkManifest.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Pack_file_opens_through_a_memory_mapping_under_a_non_ascii_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Harmonia тест " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "пак.hpk");
            File.WriteAllBytes(path, HpkBuilder.WithDefaultSheet().Build());
            using var file = HpkFile.Open(path, HpkOpenMode.Full);
            Assert.True(file.TryGetSheet("Addon", out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Manifest_edit_helper_round_trips()
    {
        var builder = HpkBuilder.WithDefaultSheet();
        builder.EditManifest = m => m["license"] = new JValue("MIT");
        using var file = HpkFile.FromBytes(builder.Build(), HpkOpenMode.Full);
        Assert.Equal("MIT", file.Manifest.License);
    }
}
