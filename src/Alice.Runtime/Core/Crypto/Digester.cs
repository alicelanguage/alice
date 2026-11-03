using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Alice.Runtime;

namespace Alice.Core.Crypto;

/// <summary>Hash algorithms of <see cref="Digester"/>, <see cref="Digests"/> and <see cref="Hmac"/>.</summary>
public enum DigestAlgorithm : byte
{
    /// <summary>MD5 (16 bytes; legacy checksums only).</summary>
    Md5,
    /// <summary>SHA-1 (20 bytes; legacy only).</summary>
    Sha1,
    /// <summary>SHA-256 (32 bytes).</summary>
    Sha256,
    /// <summary>SHA-384 (48 bytes).</summary>
    Sha384,
    /// <summary>SHA-512 (64 bytes).</summary>
    Sha512,
    /// <summary>SHA3-256 (32 bytes; needs OS support, see <see cref="Digests.IsSupported"/>).</summary>
    Sha3_256,
}

/// <summary>
/// One-shot digests over spans (Ruby `Digest::SHA256.digest/hexdigest`), forwarding to the hardware-accelerated BCL
/// <c>HashData</c>: the span overloads allocate 0 bytes.
/// </summary>
public static class Digests
{
    /// <summary>Digest length in bytes.</summary>
    [Pure]
    public static int Size(DigestAlgorithm a) => a switch
    {
        DigestAlgorithm.Md5 => 16,
        DigestAlgorithm.Sha1 => 20,
        DigestAlgorithm.Sha256 or DigestAlgorithm.Sha3_256 => 32,
        DigestAlgorithm.Sha384 => 48,
        _ => 64,
    };

    /// <summary>True when the platform implements <paramref name="a"/>.</summary>
    public static bool IsSupported(DigestAlgorithm a) => a != DigestAlgorithm.Sha3_256 || SHA3_256.IsSupported;

    /// <summary>Writes the digest of <paramref name="data"/> into <paramref name="destination"/>; returns its length.</summary>
    [Pure]
    public static int Hash(DigestAlgorithm a, ReadOnlySpan<byte> data, Span<byte> destination) => a switch
    {
        DigestAlgorithm.Md5 => MD5.HashData(data, destination),
        DigestAlgorithm.Sha1 => SHA1.HashData(data, destination),
        DigestAlgorithm.Sha256 => SHA256.HashData(data, destination),
        DigestAlgorithm.Sha384 => SHA384.HashData(data, destination),
        DigestAlgorithm.Sha512 => SHA512.HashData(data, destination),
        _ => SHA3_256.HashData(data, destination),
    };

    /// <summary>SHA-256 into <paramref name="destination"/> (32 bytes). 0 bytes allocated.</summary>
    [Pure] public static int Sha256(ReadOnlySpan<byte> data, Span<byte> destination) => SHA256.HashData(data, destination);

    /// <summary>SHA-512 into <paramref name="destination"/> (64 bytes). 0 bytes allocated.</summary>
    [Pure] public static int Sha512(ReadOnlySpan<byte> data, Span<byte> destination) => SHA512.HashData(data, destination);

    /// <summary>Lowercase hex digest of <paramref name="data"/> (allocates the string).</summary>
    [Pure]
    public static string HexDigest(DigestAlgorithm a, ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[64];
        int n = Hash(a, data, h);
        return Convert.ToHexStringLower(h[..n]);
    }

    /// <summary>Lowercase hex digest of the UTF-8 bytes of <paramref name="text"/>.</summary>
    [Pure]
    public static string HexDigest(DigestAlgorithm a, string text)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try { return HexDigest(a, rented.AsSpan(0, Encoding.UTF8.GetBytes(text, rented))); }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>Writes lowercase hex of <paramref name="bytes"/> into <paramref name="destination"/> (2 chars per byte).</summary>
    public static bool TryWriteHex(ReadOnlySpan<byte> bytes, Span<char> destination, out int charsWritten)
        => Convert.TryToHexStringLower(bytes, destination, out charsWritten);
}

/// <summary>
/// Streaming digest (Ruby `Digest::SHA256.new` then `update`/`<<` and `hexdigest`), optionally keyed (HMAC). Wraps
/// <see cref="IncrementalHash"/>: <see cref="Update(ReadOnlySpan{byte})"/> and <see cref="TryFinish"/> allocate
/// 0 bytes; finishing resets the digester for reuse.
/// </summary>
public sealed class Digester : IDisposable
{
    readonly IncrementalHash h;

    /// <summary>The algorithm.</summary>
    public DigestAlgorithm Algorithm { get; }

    /// <summary>Digest length in bytes.</summary>
    public int Size => Digests.Size(Algorithm);

    Digester(DigestAlgorithm a, IncrementalHash h) { Algorithm = a; this.h = h; }

    static HashAlgorithmName Name(DigestAlgorithm a) => a switch
    {
        DigestAlgorithm.Md5 => HashAlgorithmName.MD5,
        DigestAlgorithm.Sha1 => HashAlgorithmName.SHA1,
        DigestAlgorithm.Sha256 => HashAlgorithmName.SHA256,
        DigestAlgorithm.Sha384 => HashAlgorithmName.SHA384,
        DigestAlgorithm.Sha512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA3_256,
    };

    /// <summary>A plain digester.</summary>
    public static Digester Create(DigestAlgorithm a) => new(a, IncrementalHash.CreateHash(Name(a)));

    /// <summary>An HMAC digester keyed with <paramref name="key"/>.</summary>
    public static Digester CreateHmac(DigestAlgorithm a, ReadOnlySpan<byte> key) => new(a, IncrementalHash.CreateHMAC(Name(a), key));

    /// <summary>Ruby `Digest::SHA256.new`.</summary>
    public static Digester Sha256() => Create(DigestAlgorithm.Sha256);

    /// <summary>Ruby `Digest::SHA512.new`.</summary>
    public static Digester Sha512() => Create(DigestAlgorithm.Sha512);

    /// <summary>Ruby `Digest::SHA1.new`.</summary>
    public static Digester Sha1() => Create(DigestAlgorithm.Sha1);

    /// <summary>Ruby `Digest::MD5.new`.</summary>
    public static Digester Md5() => Create(DigestAlgorithm.Md5);

    /// <summary>Ruby `update` / `<<`: feeds bytes. Returns this for chaining.</summary>
    public Digester Update(ReadOnlySpan<byte> data) { h.AppendData(data); return this; }

    /// <summary>Feeds the UTF-8 bytes of <paramref name="text"/> (chunked through the stack: 0 bytes allocated).</summary>
    public Digester Update(string text)
    {
        Span<byte> buf = stackalloc byte[1024];
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            int take = Math.Min(rest.Length, 256);
            if (take < rest.Length && char.IsHighSurrogate(rest[take - 1])) take--;
            int n = Encoding.UTF8.GetBytes(rest[..take], buf);
            h.AppendData(buf[..n]);
            rest = rest[take..];
        }
        return this;
    }

    /// <summary>Writes the digest and resets; false when <paramref name="destination"/> is too short.</summary>
    public bool TryFinish(Span<byte> destination, out int bytesWritten) => h.TryGetHashAndReset(destination, out bytesWritten);

    /// <summary>Ruby `digest`: the digest bytes; resets.</summary>
    public byte[] Digest() => h.GetHashAndReset();

    /// <summary>Ruby `hexdigest`: lowercase hex; resets.</summary>
    public string HexDigest()
    {
        Span<byte> d = stackalloc byte[64];
        h.TryGetHashAndReset(d, out int n);
        return Convert.ToHexStringLower(d[..n]);
    }

    /// <summary>Releases the native hash state.</summary>
    public void Dispose() => h.Dispose();
}

/// <summary>HMAC one-shots (Ruby `OpenSSL::HMAC.hexdigest("SHA256", key, data)`); span overloads allocate 0 bytes.</summary>
public static class Hmac
{
    /// <summary>Writes HMAC(<paramref name="key"/>, <paramref name="data"/>) into <paramref name="destination"/>; returns its length.</summary>
    [Pure]
    public static int Compute(DigestAlgorithm a, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination) => a switch
    {
        DigestAlgorithm.Md5 => HMACMD5.HashData(key, data, destination),
        DigestAlgorithm.Sha1 => HMACSHA1.HashData(key, data, destination),
        DigestAlgorithm.Sha256 => HMACSHA256.HashData(key, data, destination),
        DigestAlgorithm.Sha384 => HMACSHA384.HashData(key, data, destination),
        DigestAlgorithm.Sha512 => HMACSHA512.HashData(key, data, destination),
        _ => HMACSHA3_256.HashData(key, data, destination),
    };

    /// <summary>HMAC-SHA256 into <paramref name="destination"/> (32 bytes).</summary>
    [Pure] public static int Sha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination) => HMACSHA256.HashData(key, data, destination);

    /// <summary>Lowercase hex HMAC of UTF-8 <paramref name="key"/> and <paramref name="data"/>.</summary>
    [Pure]
    public static string HexDigest(DigestAlgorithm a, string key, string data)
    {
        Span<byte> d = stackalloc byte[64];
        int n = Compute(a, Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data), d);
        return Convert.ToHexStringLower(d[..n]);
    }

    /// <summary>Constant-time comparison of two MACs (Ruby `Rack::Utils.secure_compare`).</summary>
    [Pure] public static bool Verify(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) => CryptographicOperations.FixedTimeEquals(expected, actual);
}

/// <summary>Ruby `SecureRandom`: cryptographically secure randomness from the OS (<see cref="RandomNumberGenerator"/>).</summary>
public static class SecureRandom
{
    /// <summary>Fills <paramref name="destination"/> with random bytes (0 bytes allocated).</summary>
    public static void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);

    /// <summary>Ruby `SecureRandom.random_bytes(n)`.</summary>
    public static byte[] RandomBytes(int n = 16) => RandomNumberGenerator.GetBytes(n);

    /// <summary>Ruby `SecureRandom.hex(n)`: 2n lowercase hex digits.</summary>
    public static string Hex(int n = 16)
    {
        Span<byte> b = n <= 256 ? stackalloc byte[n] : new byte[n];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexStringLower(b);
    }

    /// <summary>Ruby `SecureRandom.base64(n)`.</summary>
    public static string Base64(int n = 16) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(n));

    /// <summary>Ruby `SecureRandom.urlsafe_base64(n)` (no padding).</summary>
    public static string UrlsafeBase64(int n = 16) => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(n));

    /// <summary>Ruby `SecureRandom.uuid` (version 4).</summary>
    public static string Uuid() => Guid.NewGuid().ToString();

    /// <summary>Ruby `SecureRandom.random_number(n)`: uniform integer in [0, n).</summary>
    public static long RandomNumber(long n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n <= int.MaxValue) return RandomNumberGenerator.GetInt32((int)n);
        Span<byte> b = stackalloc byte[8];
        ulong limit = ulong.MaxValue - ulong.MaxValue % (ulong)n;
        while (true)
        {
            RandomNumberGenerator.Fill(b);
            ulong v = BitConverter.ToUInt64(b);
            if (v < limit) return (long)(v % (ulong)n);
        }
    }

    /// <summary>Ruby `SecureRandom.random_number`: uniform double in [0, 1).</summary>
    public static double RandomNumber()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return (BitConverter.ToUInt64(b) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Ruby `SecureRandom.alphanumeric(n)`.</summary>
    public static string Alphanumeric(int n = 16) => RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", n);
}
