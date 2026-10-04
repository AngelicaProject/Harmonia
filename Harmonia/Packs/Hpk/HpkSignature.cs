using System.Security.Cryptography;

namespace Harmonia.Packs.Hpk;

// Publisher signature block appended after the pack body. The signature
// covers packHash, which covers the manifest, so every release fact the
// installer relies on is authenticated.
public sealed class HpkSignature
{
    private HpkSignature(byte[] publicKey, byte[] signature, byte[]? previousPublicKey, byte[]? endorsement)
    {
        PublicKey = publicKey;
        Signature = signature;
        PreviousPublicKey = previousPublicKey;
        Endorsement = endorsement;
        Fingerprint = FingerprintOf(publicKey);
        PreviousFingerprint = previousPublicKey is null ? null : FingerprintOf(previousPublicKey);
    }

    public byte[] PublicKey { get; }
    public byte[] Signature { get; }
    public byte[]? PreviousPublicKey { get; }
    public byte[]? Endorsement { get; }
    public string Fingerprint { get; }
    public string? PreviousFingerprint { get; }

    public static string FingerprintOf(ReadOnlySpan<byte> publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(publicKey));

    // Short form for UI: first 16 hex digits in groups of four.
    public static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length < 16
            ? fingerprint
            : string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));

    public static HpkSignature Parse(ReadOnlySpan<byte> block)
    {
        const int fixedSize = 8 + 2 + 2 + HpkFormat.PublicKeySize + HpkFormat.SignatureSize + 1;
        if (block.Length < fixedSize || !block[..8].SequenceEqual(HpkFormat.SignatureMagic))
            throw new HpkFormatException("Invalid signature block.");

        var algorithm = BitConverter.ToUInt16(block[8..10]);
        var reserved = BitConverter.ToUInt16(block[10..12]);
        if (algorithm != HpkFormat.SignatureAlgorithmEcdsaP256 || reserved != 0)
            throw new HpkFormatException("Unsupported signature algorithm.");

        var offset = 12;
        var publicKey = block.Slice(offset, HpkFormat.PublicKeySize).ToArray();
        offset += HpkFormat.PublicKeySize;
        var signature = block.Slice(offset, HpkFormat.SignatureSize).ToArray();
        offset += HpkFormat.SignatureSize;
        var hasEndorsement = block[offset++];

        byte[]? previous = null;
        byte[]? endorsement = null;
        if (hasEndorsement == 1)
        {
            if (block.Length - offset != HpkFormat.PublicKeySize + HpkFormat.SignatureSize)
                throw new HpkFormatException("Invalid signature endorsement.");

            previous = block.Slice(offset, HpkFormat.PublicKeySize).ToArray();
            offset += HpkFormat.PublicKeySize;
            endorsement = block.Slice(offset, HpkFormat.SignatureSize).ToArray();
            offset += HpkFormat.SignatureSize;
        }
        else if (hasEndorsement != 0)
        {
            throw new HpkFormatException("Invalid signature endorsement flag.");
        }

        if (offset != block.Length)
            throw new HpkFormatException("Unexpected bytes after the signature block.");

        return new HpkSignature(publicKey, signature, previous, endorsement);
    }

    public void Verify(ReadOnlySpan<byte> packHash)
    {
        if (!VerifyWith(PublicKey, HpkFormat.SignatureDomain, packHash, Signature))
            throw new HpkFormatException("Pack signature is invalid.");

        if (PreviousPublicKey is not null &&
            !VerifyWith(PreviousPublicKey, HpkFormat.RotationDomain, PublicKey, Endorsement!))
            throw new HpkFormatException("Signing key endorsement is invalid.");
    }

    private static bool VerifyWith(byte[] publicKey, ReadOnlySpan<byte> domain, ReadOnlySpan<byte> payload, byte[] signature)
    {
        if (publicKey[0] != 0x04)
            throw new HpkFormatException("Signing key is not an uncompressed P-256 point.");
        if (!P256.IsValidPublicKey(publicKey))
            throw new HpkFormatException("Signing key is not a valid P-256 key.");

        var message = new byte[domain.Length + payload.Length];
        domain.CopyTo(message);
        payload.CopyTo(message.AsSpan(domain.Length));

        try
        {
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
            });
            return ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // The key is valid (checked above), so the platform cannot do ECDSA:
            // Wine's CNG rejects the import.
            return P256.Verify(publicKey, message, signature);
        }
    }
}
