using System.Numerics;
using System.Security.Cryptography;

namespace RouteFinder.Service;

public sealed record WireGuardKeyPair(string PrivateKey, string PublicKey)
{
    public override string ToString() => "WireGuardKeyPair([redacted])";
}

/// <summary>
/// RFC 7748 X25519 base-point multiplication used only to generate a fresh WireGuard key pair.
/// BigInteger arithmetic is not constant-time. This helper is not a general key-agreement API.
/// </summary>
public static class WireGuardKeys
{
    public static WireGuardKeyPair Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            Clamp(secret);
            var publicKey = PublicFromPrivate(secret);
            return new(Convert.ToBase64String(secret), Convert.ToBase64String(publicKey));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public static byte[] PublicFromPrivate(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != 32) throw new ArgumentException("X25519 private keys must have 32 bytes.");
        var scalar = privateKey.ToArray();
        try
        {
            Clamp(scalar);
            BigInteger p = (BigInteger.One << 255) - 19;
            BigInteger M(BigInteger value) { value %= p; return value.Sign < 0 ? value + p : value; }
            BigInteger x1 = 9, x2 = 1, z2 = 0, x3 = 9, z3 = 1;
            int swap = 0;
            for (int bit = 254; bit >= 0; bit--)
            {
                int k = (scalar[bit / 8] >> (bit & 7)) & 1;
                swap ^= k;
                if (swap != 0) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
                swap = k;
                var a = M(x2 + z2); var aa = M(a * a); var b = M(x2 - z2); var bb = M(b * b);
                var e = M(aa - bb); var c = M(x3 + z3); var d = M(x3 - z3); var da = M(d * a); var cb = M(c * b);
                x3 = M((da + cb) * (da + cb)); z3 = M(x1 * (da - cb) * (da - cb));
                x2 = M(aa * bb); z2 = M(e * (aa + 121665 * e));
            }
            if (swap != 0) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            var result = M(x2 * BigInteger.ModPow(z2, p - 2, p)).ToByteArray(isUnsigned: true, isBigEndian: false);
            Array.Resize(ref result, 32); return result;
        }
        finally { CryptographicOperations.ZeroMemory(scalar); }
    }
    static void Clamp(byte[] scalar) { scalar[0] &= 248; scalar[31] &= 127; scalar[31] |= 64; }
}
