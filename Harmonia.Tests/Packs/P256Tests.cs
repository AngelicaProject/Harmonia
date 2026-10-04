using System.Numerics;
using System.Security.Cryptography;
using Harmonia.Packs.Hpk;
using Xunit;

namespace Harmonia.Tests.Packs;

// The managed verifier is checked against the platform's ECDsa, which signs.
public sealed class P256Tests
{
    private static readonly BigInteger N = new(
        Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);

    [Fact]
    public void Accepts_signatures_made_by_the_platform()
    {
        for (var i = 0; i < 20; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var message = RandomNumberGenerator.GetBytes(i * 7);
            var signature = Sign(key, message);

            Assert.True(P256.IsValidPublicKey(HpkBuilder.PublicKey(key)));
            Assert.True(P256.Verify(HpkBuilder.PublicKey(key), message, signature));
        }
    }

    [Fact]
    public void Rejects_another_message_key_or_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = HpkBuilder.PublicKey(key);
        var message = "AERIA-HPK-V1-SIGNATURE payload"u8.ToArray();
        var signature = Sign(key, message);

        Assert.False(P256.Verify(publicKey, "AERIA-HPK-V1-SIGNATURE payloae"u8, signature));
        Assert.False(P256.Verify(HpkBuilder.PublicKey(other), message, signature));

        for (var i = 0; i < signature.Length; i += 5)
        {
            var tampered = signature.ToArray();
            tampered[i] ^= 0x01;
            Assert.False(P256.Verify(publicKey, message, tampered));
            Assert.Equal(
                key.VerifyData(message, tampered, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
                P256.Verify(publicKey, message, tampered));
        }
    }

    [Fact]
    public void Accepts_the_high_s_form_like_the_platform()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var message = "high s"u8.ToArray();
        var signature = Sign(key, message);
        var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        var flipped = signature.ToArray();
        Write32(flipped.AsSpan(32), N - s);

        Assert.True(key.VerifyData(message, flipped, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.True(P256.Verify(HpkBuilder.PublicKey(key), message, flipped));
    }

    [Fact]
    public void Rejects_out_of_range_r_and_s()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = HpkBuilder.PublicKey(key);
        var message = "range"u8.ToArray();
        var signature = Sign(key, message);

        foreach (var offset in new[] { 0, 32 })
        {
            var zero = signature.ToArray();
            zero.AsSpan(offset, 32).Clear();
            Assert.False(P256.Verify(publicKey, message, zero));

            // r + n and s + n would verify if the range were not checked; n itself is out of range.
            var order = signature.ToArray();
            Write32(order.AsSpan(offset, 32), N);
            Assert.False(P256.Verify(publicKey, message, order));
        }

        Assert.False(P256.Verify(publicKey, message, signature[..63]));
    }

    [Fact]
    public void Rejects_points_off_the_curve()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = HpkBuilder.PublicKey(key);

        var offCurve = publicKey.ToArray();
        offCurve[64] ^= 0x01;
        Assert.False(P256.IsValidPublicKey(offCurve));

        var compressed = publicKey.ToArray();
        compressed[0] = 0x02;
        Assert.False(P256.IsValidPublicKey(compressed));

        var outOfField = publicKey.ToArray();
        outOfField.AsSpan(1, 32).Fill(0xFF);
        Assert.False(P256.IsValidPublicKey(outOfField));

        Assert.False(P256.IsValidPublicKey(new byte[65]));
        Assert.False(P256.IsValidPublicKey(publicKey[..64]));
        Assert.Throws<ArgumentException>(() => P256.Verify(offCurve, "x"u8, new byte[64]));
    }

    private static byte[] Sign(ECDsa key, byte[] message) =>
        key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static void Write32(Span<byte> destination, BigInteger value)
    {
        destination.Clear();
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        bytes.CopyTo(destination[(32 - bytes.Length)..]);
    }
}
