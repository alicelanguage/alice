using Alice.Check;

namespace Alice.Check.Tests;

/// <summary>The LRAT checker and the certificate file. fixtures/xor_twice.cert is `alice prove --smt` output for
/// `rule xor_twice(a : Int32, b : Int32)  (a ^ b) ^ b == a` (CaDiCaL 2.1.3), kept as data so these tests need no solver.</summary>
public class CertTests
{
    const string Contradiction = "p cnf 2 4\n1 2 0\n-1 2 0\n1 -2 0\n-1 -2 0\n";

    [Fact]
    public void Lrat_accepts_a_refutation()
    {
        Assert.Null(Lrat.Check(Contradiction, "5 2 0 1 2 0\n6 0 5 3 4 0\n"));
        Assert.Null(Lrat.Check(Contradiction, "5 2 0 1 2 0\n5 d 1 2 0\n6 0 5 3 4 0\n"));   // deletions are fine
    }

    [Theory]
    [InlineData("5 2 0 1 0\n6 0 5 3 4 0\n", "do not refute")]              // one hint short
    [InlineData("5 2 0 1 2 0\n6 0 5 3 0\n", "do not refute")]
    [InlineData("5 2 0 1 2 0\n6 0 5 9 4 0\n", "not a live clause")]         // unknown hint
    [InlineData("5 2 0 1 2 0\n5 d 5 0\n6 0 5 3 4 0\n", "not a live clause")]  // deleted, then used
    [InlineData("5 2 0 1 2 0\n", "does not derive the empty clause")]
    [InlineData("5 2 0 1 -2 0\n6 0 5 3 4 0\n", "RAT")]
    [InlineData("3 2 0 1 2 0\n6 0 3 3 4 0\n", "not new")]                    // reuses an input clause's id
    [InlineData("5 3 0 1 2 0\n", "new variable")]
    [InlineData("5 0 1 0\n", "not unit")]
    public void Lrat_rejects_a_bad_proof(string proof, string why)
    {
        Assert.Contains(why, Lrat.Check(Contradiction, proof));
    }

    [Fact]
    public void Lrat_rejects_a_satisfiable_cnf_whatever_the_proof()
    {
        const string Sat = "p cnf 2 3\n1 2 0\n-1 2 0\n1 -2 0\n";
        Assert.NotNull(Lrat.Check(Sat, "4 2 0 1 2 0\n5 0 4 3 0\n"));
    }

    static (string Sources, List<(CertEntry Entry, string Stated)> Entries) Fixture() =>
        Cert.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "xor_twice.cert")));

    [Fact]
    public void The_fixture_certificate_verifies_and_round_trips()
    {
        var (sources, entries) = Fixture();
        var (e, stated) = Assert.Single(entries);
        Assert.Equal("xor_twice", e.Rule);
        Assert.Null(Cert.Verify(e, stated));
        var again = Cert.Read(Cert.Write(sources, [e]));
        Assert.Equal(sources, again.Sources);
        Assert.Equal(e, Assert.Single(again.Entries).Entry);
    }

    /// <summary>Every way to alter a certificate is caught: a changed part with the old hash fails the hash, and with
    /// a recomputed hash fails the re-derived CNF (IR, CNF) or the proof check.</summary>
    [Theory]
    [InlineData("ir", "bxor 32 0", "bor 32 0", "not the bit-blast")]          // a different rule
    [InlineData("ir", "goal ", "goal 0 ", "not the bit-blast")]               // a different goal
    [InlineData("cnf", "\n1 0\n", "\n", "not the bit-blast")]                 // drop the constant-true clause
    public void A_tampered_certificate_is_rejected(string part, string from, string to, string why)
    {
        var (e, stated) = Assert.Single(Fixture().Entries);
        string Swap(string s) { int i = s.IndexOf(from, StringComparison.Ordinal); Assert.True(i >= 0); return s[..i] + to + s[(i + from.Length)..]; }
        var bad = part switch
        {
            "ir" => e with { Ir = Swap(e.Ir) },
            "cnf" => e with { Cnf = Swap(e.Cnf) },
            _ => e with { Proof = Swap(e.Proof) },
        };
        Assert.Contains("sha256", Cert.Verify(bad, stated));
        Assert.Contains(why, Cert.Verify(bad, bad.Sha256));
    }

    [Fact]
    public void A_truncated_or_altered_proof_is_rejected()
    {
        var (e, _) = Assert.Single(Fixture().Entries);
        var lines = e.Proof.TrimEnd('\n').Split('\n');
        var cut = e with { Proof = string.Concat(lines.SkipLast(1).Select(l => l + "\n")) };   // no empty clause
        Assert.Contains("does not derive the empty clause", Cert.Verify(cut, cut.Sha256));
        int first = Array.FindIndex(lines, l => !l.Contains(" d "));
        var p = lines[first].Split(' ');
        p[1] = (-long.Parse(p[1])).ToString();                                                    // a different lemma
        lines[first] = string.Join(' ', p);
        var flipped = e with { Proof = string.Concat(lines.Select(l => l + "\n")) };
        Assert.Contains("proof is rejected", Cert.Verify(flipped, flipped.Sha256));
    }

    [Fact]
    public void A_proof_for_another_rule_is_rejected()
    {
        // the IR claims `(a ^ b) ^ b == b`, which is false; the old proof cannot refute its (different) CNF
        var (e, _) = Assert.Single(Fixture().Entries);
        var g = Ir.Read(e.Ir.TrimEnd('\n').Split('\n'));
        var eq = g.Result;
        var wrong = new Goal(g.Domain, g.Fails, new Term(Op.Eq, 0, [eq.A[0], eq.A[0].A[1]]));
        var forged = e with { Ir = Ir.Write(wrong), Cnf = Blaster.Cnf(wrong) };
        Assert.Contains("proof is rejected", Cert.Verify(forged, forged.Sha256));
    }

    [Fact]
    public void Garbage_is_not_a_certificate()
    {
        Assert.ThrowsAny<Exception>(() => Cert.Read([1, 2, 3, 4]));
        Assert.Throws<FormatException>(() => Cert.Read(Brotli("hello\n")));
    }

    static byte[] Brotli(string text)
    {
        using var ms = new MemoryStream();
        using (var b = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionLevel.Fastest)) b.Write(System.Text.Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }
}
