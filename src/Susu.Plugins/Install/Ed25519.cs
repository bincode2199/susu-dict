using System.Numerics;
using System.Security.Cryptography;

namespace Susu.Plugins.Install;

/// <summary>
/// Ed25519 (RFC 8032, pure signature) over BigInteger. The BCL has no Ed25519 and the host is NativeAOT, so a managed
/// verifier avoids a native dependency in the install path. Verification is cofactorless-checked as RFC 8032 5.1.7
/// ([S]B = R + [k]A with S &lt; L and canonical encodings). Not constant time: it handles only public data
/// (public keys and signatures); signing exists for tools and tests that hold their own seed.
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * ModInverse(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Base = MakeBase();

    public const int PublicKeyBytes = 32, SignatureBytes = 64, SeedBytes = 32;

    private static BigInteger Mod(BigInteger v) { var r = v % P; return r.Sign < 0 ? r + P : r; }
    private static BigInteger ModInverse(BigInteger v) => BigInteger.ModPow(Mod(v), P - 2, P);

    private static (BigInteger, BigInteger, BigInteger, BigInteger) MakeBase()
    {
        BigInteger y = Mod(4 * ModInverse(5));
        var x = RecoverX(y, 0) ?? throw new InvalidOperationException("base point");
        return (x, y, 1, Mod(x * y));
    }

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) return null;
        BigInteger x2 = Mod((y * y - 1) * ModInverse(D * y * y + 1));
        if (x2.IsZero) return sign == 1 ? null : BigInteger.Zero;
        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtM1);
        if (Mod(x * x - x2) != 0) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Add((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X)), b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(2 * p.T * q.T * D), d = Mod(2 * p.Z * q.Z);
        BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
        return (Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Multiply(BigInteger s, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) point)
    {
        var result = (X: BigInteger.Zero, Y: BigInteger.One, Z: BigInteger.One, T: BigInteger.Zero);
        var q = point;
        while (s > 0)
        {
            if (!s.IsEven) result = Add(result, q);
            q = Add(q, q);
            s >>= 1;
        }
        return result;
    }

    private static bool Equal((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) q)
        => Mod(p.X * q.Z - q.X * p.Z) == 0 && Mod(p.Y * q.Z - q.Y * p.Z) == 0;

    private static byte[] Compress((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p)
    {
        BigInteger zi = ModInverse(p.Z), x = Mod(p.X * zi), y = Mod(p.Y * zi);
        var bytes = new byte[32];
        y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        if (!x.IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    private static (BigInteger, BigInteger, BigInteger, BigInteger)? Decompress(ReadOnlySpan<byte> s)
    {
        if (s.Length != 32) return null;
        int sign = s[31] >> 7;
        var copy = s.ToArray();
        copy[31] &= 0x7F;
        BigInteger y = new(copy, isUnsigned: true, isBigEndian: false);
        var x = RecoverX(y, sign);
        if (x is null) return null;
        return (x.Value, y, 1, Mod(x.Value * y));
    }

    private static BigInteger Scalar(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static BigInteger HashScalar(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts) sha.AppendData(part);
        return Scalar(sha.GetHashAndReset()) % L;
    }

    private static (BigInteger Scalar, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        byte[] h = SHA512.HashData(seed);
        var a = h[..32];
        a[0] &= 248; a[31] &= 127; a[31] |= 64;
        return (Scalar(a), h[32..]);
    }

    public static byte[] PublicKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedBytes) throw new ArgumentException("seed must be 32 bytes", nameof(seed));
        return Compress(Multiply(Expand(seed).Scalar, Base));
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = Expand(seed);
        byte[] pub = Compress(Multiply(a, Base)), msg = message.ToArray();
        BigInteger r = HashScalar(prefix, msg);
        byte[] rEnc = Compress(Multiply(r, Base));
        BigInteger k = HashScalar(rEnc, pub, msg);
        BigInteger s = (r + k * a) % L;
        var sig = new byte[SignatureBytes];
        rEnc.CopyTo(sig, 0);
        s.TryWriteBytes(sig.AsSpan(32), out _, isUnsigned: true, isBigEndian: false);
        return sig;
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyBytes || signature.Length != SignatureBytes) return false;
        var a = Decompress(publicKey);
        var r = Decompress(signature[..32]);
        if (a is null || r is null) return false;
        BigInteger s = Scalar(signature[32..]);
        if (s >= L) return false;
        BigInteger k = HashScalar(signature[..32].ToArray(), publicKey.ToArray(), message.ToArray());
        var left = Multiply(s, Base);
        var right = Add(r.Value, Multiply(k, a.Value));
        return Equal(left, right);
    }
}
