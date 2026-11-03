using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Alice.Check;

/// <summary>
/// PROOF.cert (ADR 0042): Brotli of `alice-cert 1`, `sources &lt;sha256&gt;`, then per proven rule an entry
/// `rule NAME`, `ir N` + N lines, `cnf N` + N lines, `lrat N` + N lines, `sha256 H` where H hashes the entry's text
/// from `rule` to the last proof line. PROOF.al cites H for the rule.
/// </summary>
public sealed record CertEntry(string Rule, string Ir, string Cnf, string Proof)
{
    public string Body => $"rule {Rule}\n{Section("ir", Ir)}{Section("cnf", Cnf)}{Section("lrat", Proof)}";
    public string Sha256 => Cert.Sha256(Body);

    static string Section(string name, string text) => $"{name} {Lines(text).Count}\n{text}";

    internal static List<string> Lines(string text) => text.Length == 0 ? [] : [.. text.TrimEnd('\n').Split('\n')];
}

public static class Cert
{
    public const string FileName = "PROOF.cert";

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static byte[] Write(string sources, IEnumerable<CertEntry> entries)
    {
        var sb = new StringBuilder($"alice-cert 1\nsources {sources}\n");
        foreach (var e in entries.OrderBy(e => e.Rule, StringComparer.Ordinal)) sb.Append(e.Body).Append($"sha256 {e.Sha256}\n");
        var text = Encoding.UTF8.GetBytes(sb.ToString());
        using var enc = new BrotliEncoder(quality: 11, window: 24);   // LRAT hints are long and repetitive: a large window pays
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(text.Length)];
        if (enc.Compress(text, output, out _, out int written, isFinalBlock: true) != System.Buffers.OperationStatus.Done)
            throw new InvalidOperationException("Brotli could not compress the certificate");
        return output[..written];
    }

    /// <summary>The sources hash and the entries, each with the hash its file states (compare with <see cref="CertEntry.Sha256"/>).</summary>
    public static (string Sources, List<(CertEntry Entry, string Stated)> Entries) Read(byte[] file)
    {
        using var gz = new BrotliStream(new MemoryStream(file), CompressionMode.Decompress);
        using var rd = new StreamReader(gz, Encoding.UTF8);
        var lines = rd.ReadToEnd().Split('\n');
        int i = 0;
        string Next() => i < lines.Length ? lines[i++] : throw new FormatException("the certificate ends early");
        string Field(string name) => Next() is var l && l.StartsWith(name + " ") ? l[(name.Length + 1)..] : throw new FormatException($"expected `{name}` at line {i}");
        string Block(string name)
        {
            int n = int.TryParse(Field(name), out var c) && c >= 0 && c <= lines.Length - i ? c : throw new FormatException($"bad `{name}` count at line {i}");
            var sb = new StringBuilder();
            for (int k = 0; k < n; k++) sb.Append(Next()).Append('\n');
            return sb.ToString();
        }
        if (Next() != "alice-cert 1") throw new FormatException("not an Alice proof certificate (alice-cert 1)");
        string sources = Field("sources");
        var entries = new List<(CertEntry, string)>();
        while (i < lines.Length && lines[i].Length > 0)
        {
            var e = new CertEntry(Field("rule"), Block("ir"), Block("cnf"), Block("lrat"));
            entries.Add((e, Field("sha256")));
        }
        return (sources, entries);
    }

    /// <summary>Null when the entry holds: its hash is the stated one, its CNF is the bit-blast of its IR, and its
    /// proof refutes that CNF. Otherwise why not.</summary>
    public static string? Verify(CertEntry e, string stated)
    {
        if (e.Sha256 != stated) return $"rule {e.Rule}: the entry's sha256 is {e.Sha256}, the certificate states {stated}";
        Goal g;
        try { g = Ir.Read(CertEntry.Lines(e.Ir)); }
        catch (FormatException x) { return $"rule {e.Rule}: the IR is malformed: {x.Message}"; }
        if (Blaster.Cnf(g) != e.Cnf) return $"rule {e.Rule}: the CNF is not the bit-blast of the IR";
        try { return Lrat.Check(e.Cnf, e.Proof) is { } why ? $"rule {e.Rule}: the proof is rejected: {why}" : null; }
        catch (FormatException x) { return $"rule {e.Rule}: the proof is malformed: {x.Message}"; }
    }
}
