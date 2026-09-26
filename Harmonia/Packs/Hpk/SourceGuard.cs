using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Harmonia.Packs.Hpk;

// First 8 bytes of SHA-256("HARMONIA-HXS-V1-RAW-STRING" || u32le(len) || raw)
// over the source string's bytes without the terminator; the domain keeps its
// historical spelling. Both sides read
// the 8 bytes as a little-endian integer, so equal values mean equal bytes.
public static class SourceGuard
{
    private static ReadOnlySpan<byte> Domain => "HARMONIA-HXS-V1-RAW-STRING"u8;

    // Row rewrites run on game threads; one hasher per thread avoids locks
    // and per-call allocations.
    [ThreadStatic]
    private static IncrementalHash? hasher;

    public static ulong Compute(ReadOnlySpan<byte> raw)
    {
        var hash = hasher ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)raw.Length);
        hash.AppendData(Domain);
        hash.AppendData(length);
        hash.AppendData(raw);

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }
}
