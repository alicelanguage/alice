using System.Diagnostics;
using System.Text.RegularExpressions;
using Alice.Check;

// alice-check PROOF.cert [PROOF.al]: re-checks every certificate entry without Alice's compiler, runtime, Z3 or SAT
// solver (ADR 0042). With PROOF.al, also checks that each rule it marks `cert sha256 H` has a verified entry H and
// that both files name the same sources.
if (args.Length is < 1 or > 2 || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("usage: alice-check PROOF.cert [PROOF.al]");
    return 2;
}
(string Sources, List<(CertEntry Entry, string Stated)> Entries) cert;
try { cert = Cert.Read(File.ReadAllBytes(args[0])); }
catch (Exception e) when (e is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"{args[0]}: {e.Message}");
    return 1;
}
int bad = 0;
var verified = new HashSet<(string, string)>();
foreach (var (e, stated) in cert.Entries)
{
    var sw = Stopwatch.StartNew();
    if (Cert.Verify(e, stated) is { } why) { Console.WriteLine($"FAIL {why}"); bad++; continue; }
    verified.Add((e.Rule, stated));
    Console.WriteLine($"ok   rule {e.Rule}: certificate verified ({e.Cnf.Count(c => c == '\n') - 1} clauses, {e.Proof.Count(c => c == '\n')} proof lines, {sw.Elapsed.TotalMilliseconds:0} ms)");
}
if (args.Length == 2)
{
    string proof = File.ReadAllText(args[1]).Replace("\r\n", "\n");
    if (Regex.Match(proof, @"^# sources sha256 (\w+)$", RegexOptions.Multiline) is not { Success: true } src || src.Groups[1].Value != cert.Sources)
    {
        Console.WriteLine($"FAIL {args[1]} and {args[0]} are not about the same sources");
        bad++;
    }
    foreach (Match m in Regex.Matches(proof, @"^# rule (\S+): proven .*cert sha256 (\w+)\)$", RegexOptions.Multiline))
        if (!verified.Contains((m.Groups[1].Value, m.Groups[2].Value)))
        {
            Console.WriteLine($"FAIL rule {m.Groups[1].Value}: {args[1]} cites certificate {m.Groups[2].Value}, which {args[0]} does not verify");
            bad++;
        }
}
Console.WriteLine(bad == 0 ? $"{cert.Entries.Count} certificate(s) verified" : $"{bad} problem(s)");
return bad == 0 ? 0 : 1;
