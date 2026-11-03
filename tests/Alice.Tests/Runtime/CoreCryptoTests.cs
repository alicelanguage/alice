using System.Security.Cryptography;
using System.Text;
using Alice.Core.Crypto;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Crypto: CRC-32C, XXH3 (reference vectors from xxHash 0.8.2), digests, HMAC, SecureRandom.</summary>
public class CoreCryptoTests
{
    static uint BitwiseCrc32C(ReadOnlySpan<byte> d)
    {
        uint c = ~0u;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0x82F63B78 : c >> 1;
        }
        return ~c;
    }

    static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    [Fact]
    public void Crc32C_matches_the_standard_check_value_and_a_bitwise_model()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
        Assert.Equal(0x8A9136AAu, Crc32C.Compute(new byte[32]));
        Assert.Equal(0u, Crc32C.Compute([]));
        var rng = new Random(3);
        foreach (int n in new[] { 1, 7, 8, 9, 63, 1000, 3 * 8192 - 1, 3 * 8192, 3 * 8192 + 5, 100_000 })
        {
            var d = new byte[n];
            rng.NextBytes(d);
            Assert.Equal(BitwiseCrc32C(d), Crc32C.Compute(d));
            int cut = n / 3;
            Assert.Equal(Crc32C.Compute(d), Crc32C.Append(Crc32C.Compute(d.AsSpan(0, cut)), d.AsSpan(cut)));
            Assert.Equal(Crc32C.Compute(d), Crc32C.Combine(Crc32C.Compute(d.AsSpan(0, cut)), Crc32C.Compute(d.AsSpan(cut)), n - cut));
        }
        Assert.Equal("e3069283", Crc32C.HexDigest("123456789"u8));
    }

    public static TheoryData<int, ulong, ulong> Xxh3Vectors => new()
    {
        { 0, 0x2d06800538d394c2UL, 0x602b0e2cd6662c8bUL },
        { 1, 0x4c5cca45d0f4811fUL, 0x2f3acd3805f81de3UL },
        { 3, 0x15f7093b173d005cUL, 0x079dd5d54d89480aUL },
        { 4, 0xdca012f95811b6b9UL, 0x1a246e2efb9c9b2eUL },
        { 8, 0xdec6a9a43575982eUL, 0x19ef7d3919108affUL },
        { 9, 0xcbe393399f17ffbdUL, 0x9c98d3e24dc54d34UL },
        { 16, 0x7e484c18d74895d0UL, 0xa106510078b0a252UL },
        { 17, 0x208bde5ee2bed407UL, 0x0b2caf8bf9648effUL },
        { 64, 0xdd30702ab46b3745UL, 0x4490c19c7048a1a1UL },
        { 97, 0x60e3e1d0d43785b3UL, 0x9e127e846b5494c9UL },
        { 128, 0xf92b70eaa21a6288UL, 0x95425530beb89fe8UL },
        { 129, 0xf8f76713f2bb60faUL, 0x29fa850b97ed9666UL },
        { 200, 0x12fdb864685f344dUL, 0x49dff623641b01b4UL },
        { 240, 0xccc7375172c41f03UL, 0x2d882e7899ff64ccUL },
        { 241, 0x0b3b630948ce4a00UL, 0x422e82e8913e49e0UL },
        { 256, 0xec85b75bafe6ca74UL, 0xb4dbe810e81c3d97UL },
        { 1023, 0xf0d330ce2b3300fbUL, 0x642b8b12a22cac34UL },
        { 1024, 0x23bc880ebf0d29c6UL, 0x7e249adc60e1f9b4UL },
        { 1025, 0xc09fdfbc398c7d82UL, 0x16cfe055154ff1ddUL },
        { 4096, 0xa3c19f8174cde0bbUL, 0x224e1aff9c0f0707UL },
        { 4999, 0x00710881668d48ebUL, 0xfc5dc94bed0a8103UL },
    };

    [Theory]
    [MemberData(nameof(Xxh3Vectors))]
    public void XxHash3_matches_the_reference(int length, ulong unseeded, ulong seeded)
    {
        var d = Pattern(length);
        Assert.Equal(unseeded, XxHash3.Hash64(d));
        Assert.Equal(seeded, XxHash3.Hash64(d, 0x9E3779B97F4A7C15UL));
    }

    [Fact]
    public void Digests_and_hmac_match_the_bcl_and_rfc_4231()
    {
        var data = Encoding.UTF8.GetBytes("what do ya want for nothing?");
        Span<byte> h = stackalloc byte[64];
        Assert.Equal(32, Digests.Sha256(data, h));
        Assert.True(h[..32].SequenceEqual(SHA256.HashData(data)));
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(data)), Digests.HexDigest(DigestAlgorithm.Sha512, data));
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(data)), Digests.HexDigest(DigestAlgorithm.Md5, "what do ya want for nothing?"));
        // RFC 4231 test case 2
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", Hmac.HexDigest(DigestAlgorithm.Sha256, "Jefe", "what do ya want for nothing?"));
        using var d = Digester.Sha256();
        d.Update("what do ya ").Update("want for nothing?"u8);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), d.HexDigest());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), d.HexDigest());   // finishing resets
        using var m = Digester.CreateHmac(DigestAlgorithm.Sha256, "Jefe"u8);
        m.Update(data);
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", m.HexDigest());
        var big = new string('é', 5000);
        using var d2 = Digester.Sha256();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(big))), d2.Update(big).HexDigest());
        Assert.True(Hmac.Verify(h[..4], h[..4]));
    }

    [Fact]
    public void Secure_random_has_ruby_shapes()
    {
        Assert.Equal(32, SecureRandom.Hex(16).Length);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", SecureRandom.Uuid());
        Assert.InRange(SecureRandom.RandomNumber(10), 0, 9);
        Assert.InRange(SecureRandom.RandomNumber(10_000_000_000L), 0, 9_999_999_999L);
        double r = SecureRandom.RandomNumber();
        Assert.InRange(r, 0.0, 0.9999999999);
        Assert.Matches("^[A-Za-z0-9]{12}$", SecureRandom.Alphanumeric(12));
        Assert.DoesNotContain('=', SecureRandom.UrlsafeBase64(10));
    }
}

/// <summary>Span overloads of Crypto allocate nothing.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class CoreCryptoAllocationTests
{
    [Fact]
    public void Span_hashes_allocate_nothing()
    {
        var data = new byte[100_000];
        new Random(1).NextBytes(data);
        var key = new byte[32];
        var outBuf = new byte[64];
        ulong sink = 0;
        void Body()
        {
            for (int i = 0; i < 20; i++)
            {
                sink += Crc32C.Compute(data);
                sink += XxHash3.Hash64(data);
                sink += XxHash3.Hash64(data.AsSpan(0, 100));
                sink += (ulong)Digests.Sha256(data, outBuf);
                sink += (ulong)Hmac.Sha256(key, data.AsSpan(0, 1000), outBuf);
                SecureRandom.Fill(outBuf);
            }
        }
        Body();
        Assert.Equal(0, AllocationProbe.Least(Body));
        Assert.NotEqual(0UL, sink);
    }
}
