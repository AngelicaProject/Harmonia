namespace Harmonia.Packs.Hpk;

// Harmonia Pack Format v1 constants. The contract is owned by Aeria
// (docs/formats/pack-v1.md); keep this file in lockstep with it.
public static class HpkFormat
{
    public const ushort FormatMajor = 1;
    public const int HeaderSize = 64;
    public const int SectionEntrySize = 24;
    public const int DigestSize = 32;
    public const int MaxSections = 64;

    public const int SheetRecordSize = 32;
    public const int LayoutRecordSize = 8;
    public const int RowRecordSize = 16;
    public const int CellRecordSize = 24;
    public const int GuardSize = 8;

    public const uint KindManifest = 1;
    public const uint KindNames = 2;
    public const uint KindSheets = 3;
    public const uint KindLayout = 4;
    public const uint KindRows = 5;
    public const uint KindCells = 6;
    public const uint KindStrings = 7;
    public const uint FirstOptionalKind = 0x10000;

    public const byte StateReviewed = 1;
    public const byte StateUnreviewed = 2;

    public const int MaxStringLength = 65535;
    public const int MaxManifestBytes = 1 << 20;
    public const long MaxPackBytes = 1L << 30;

    public const ushort SignatureAlgorithmEcdsaP256 = 1;
    public const int PublicKeySize = 65;
    public const int SignatureSize = 64;

    public static ReadOnlySpan<byte> Magic => "AERIAHPK"u8;

    public static ReadOnlySpan<byte> SignatureMagic => "HPKSIG01"u8;

    public static ReadOnlySpan<byte> SignatureDomain => "AERIA-HPK-V1-SIGNATURE"u8;

    public static ReadOnlySpan<byte> RotationDomain => "AERIA-HPK-V1-KEY-ROTATION"u8;
}

public sealed class HpkFormatException : Exception
{
    public HpkFormatException(string message)
        : base(message)
    {
    }
}
