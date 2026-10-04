using System.Numerics;
using System.Security.Cryptography;

namespace Harmonia.Packs.Hpk;

// ECDSA P-256 / SHA-256 verification in managed code, for platforms whose
// crypto provider cannot import an ECC public key: there the system ECDsa
// throws for every key, valid or not. Verification only touches public data,
// so it does not need to run in constant time.
public static class P256
{
    private static readonly BigInteger P = Parse("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly BigInteger N = Parse("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
    private static readonly BigInteger B = Parse("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
    private static readonly BigInteger Gx = Parse("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
    private static readonly BigInteger Gy = Parse("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

    private static readonly Point Infinity = new(BigInteger.One, BigInteger.One, BigInteger.Zero);

    // An uncompressed SEC1 point (0x04 || X || Y) on the curve. The cofactor is 1,
    // so every point on the curve other than infinity is a valid public key.
    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != HpkFormat.PublicKeySize || publicKey[0] != 0x04)
            return false;

        var x = Unsigned(publicKey[1..33]);
        var y = Unsigned(publicKey[33..65]);
        if (x >= P || y >= P)
            return false;

        return Mod(y * y - (x * x * x - 3 * x + B), P).IsZero;
    }

    // signature is r || s, 32 bytes each (IEEE P1363). The caller checks the key
    // with IsValidPublicKey first.
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (!IsValidPublicKey(publicKey))
            throw new ArgumentException("Not a valid P-256 public key.", nameof(publicKey));
        if (signature.Length != HpkFormat.SignatureSize)
            return false;

        var r = Unsigned(signature[..32]);
        var s = Unsigned(signature[32..]);
        if (r.IsZero || r >= N || s.IsZero || s >= N)
            return false;

        var e = Unsigned(SHA256.HashData(message));
        var w = BigInteger.ModPow(s, N - 2, N);
        var u1 = e * w % N;
        var u2 = r * w % N;

        var q = new Point(Unsigned(publicKey[1..33]), Unsigned(publicKey[33..65]), BigInteger.One);
        var g = new Point(Gx, Gy, BigInteger.One);
        var sum = MultiplyAdd(u1, g, u2, q);
        if (sum.Z.IsZero)
            return false;

        var zInverse = BigInteger.ModPow(sum.Z, P - 2, P);
        var x = sum.X * zInverse % P * zInverse % P;
        return x % N == r;
    }

    // u1*G + u2*Q in Jacobian coordinates (Shamir's trick).
    private static Point MultiplyAdd(BigInteger u1, Point g, BigInteger u2, Point q)
    {
        var gq = Add(g, q);
        var result = Infinity;
        for (var bit = 255; bit >= 0; bit--)
        {
            result = Double(result);
            var a = !(u1 >> bit).IsEven;
            var b = !(u2 >> bit).IsEven;
            if (a && b)
                result = Add(result, gq);
            else if (a)
                result = Add(result, g);
            else if (b)
                result = Add(result, q);
        }

        return result;
    }

    private static Point Double(Point p)
    {
        if (p.Z.IsZero || p.Y.IsZero)
            return Infinity;

        // a = -3: alpha = 3 (X - Z^2)(X + Z^2).
        var delta = p.Z * p.Z % P;
        var gamma = p.Y * p.Y % P;
        var beta = p.X * gamma % P;
        var alpha = 3 * Mod(p.X - delta, P) * (p.X + delta) % P;
        var x = Mod(alpha * alpha - 8 * beta, P);
        var z = Mod((p.Y + p.Z) * (p.Y + p.Z) - gamma - delta, P);
        var y = Mod(alpha * (4 * beta - x) - 8 * gamma * gamma, P);
        return new Point(x, y, z);
    }

    private static Point Add(Point a, Point b)
    {
        if (a.Z.IsZero)
            return b;
        if (b.Z.IsZero)
            return a;

        var za2 = a.Z * a.Z % P;
        var zb2 = b.Z * b.Z % P;
        var u1 = a.X * zb2 % P;
        var u2 = b.X * za2 % P;
        var s1 = a.Y * zb2 % P * b.Z % P;
        var s2 = b.Y * za2 % P * a.Z % P;

        if (u1 == u2)
            return s1 == s2 ? Double(a) : Infinity;

        var h = Mod(u2 - u1, P);
        var r = Mod(s2 - s1, P);
        var h2 = h * h % P;
        var h3 = h2 * h % P;
        var u1h2 = u1 * h2 % P;
        var x = Mod(r * r - h3 - 2 * u1h2, P);
        var y = Mod(r * (u1h2 - x) - s1 * h3, P);
        var z = h * a.Z % P * b.Z % P;
        return new Point(x, y, z);
    }

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var result = value % modulus;
        return result.Sign < 0 ? result + modulus : result;
    }

    private static BigInteger Unsigned(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    private static BigInteger Parse(string hex) => Unsigned(Convert.FromHexString(hex));

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z);
}
