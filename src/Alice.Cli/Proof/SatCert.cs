using System.Diagnostics;
using Alice.Check;

namespace Alice.Cli.Proof;

/// <summary>
/// Certificates for `alice prove --smt` (ADR 0042): the rule's IR is bit-blasted to CNF (Alice.Check.Blaster, the
/// same code alice-check re-derives it with), a SAT solver that writes LRAT proofs (CaDiCaL 1.7+, on PATH or
/// ALICE_SAT=/path/to/cadical) refutes it, and Alice.Check.Lrat checks the proof before the entry is accepted.
/// </summary>
public static class SatCert
{
    static string? solver;
    static string? unavailable;
    static bool probed;
    static readonly object Gate = new();

    /// <summary>Null when a proof-writing SAT solver is available, else why not.</summary>
    public static string? Unavailable()
    {
        lock (Gate)
        {
            if (probed) return unavailable;
            probed = true;
            string? path = Environment.GetEnvironmentVariable("ALICE_SAT") is { Length: > 0 } env ? env : Which("cadical");
            if (path == null) return unavailable = "no proof-writing SAT solver: install CaDiCaL 1.7+ (apt install cadical, brew install cadical) or set ALICE_SAT=/path/to/cadical";
            // a two-variable contradiction must come back UNSAT with an LRAT proof the checker accepts
            const string Cnf = "p cnf 2 4\n1 2 0\n-1 2 0\n1 -2 0\n-1 -2 0\n";
            var (status, proof, err) = Run(path, Cnf, TimeSpan.FromSeconds(30));
            if (status != 20 || proof == null || Lrat.Check(Cnf, proof) != null)
                return unavailable = $"{path} does not write LRAT proofs (CaDiCaL 1.7+ with --lrat is needed){(err is { Length: > 0 } ? ": " + err.Trim() : "")}";
            solver = path;
            return unavailable = null;
        }
    }

    static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe } : [exe])
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    /// <summary>The certificate of a translated rule, or why there is none: the solver found the CNF satisfiable (the
    /// rule fails; Z3 reports where), ran out of time, or wrote a proof the checker rejects.</summary>
    public static (CertEntry? Entry, string? Why) Certify(Translation t, TimeSpan timeout)
    {
        if (Unavailable() is { } why) return (null, why);
        var goal = t.Goal;
        string ir = Ir.Write(goal), cnf = Blaster.Cnf(goal);
        var (status, proof, err) = Run(solver!, cnf, timeout);
        if (status == 10) return (null, "the SAT solver found an input that fails the rule");
        if (status != 20 || proof == null) return (null, status == null ? $"the SAT solver ran out of time ({timeout.TotalSeconds:0.###}s)" : $"the SAT solver failed (exit {status}){(err is { Length: > 0 } ? ": " + err.Trim() : "")}");
        var entry = new CertEntry(t.Rule.RuleName!, ir, cnf, proof);
        return Cert.Verify(entry, entry.Sha256) is { } bad ? (null, "internal error: the SAT solver's proof does not check: " + bad) : (entry, null);
    }

    /// <summary>Runs the solver on a CNF; the exit status (10 SAT, 20 UNSAT, null on time-out) and the LRAT proof, one
    /// clause per line.</summary>
    static (int? Status, string? Proof, string? Err) Run(string exe, string cnf, TimeSpan timeout)
    {
        string dir = Path.Combine(Path.GetTempPath(), "alice-sat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "rule.cnf"), output = Path.Combine(dir, "rule.lrat");
            File.WriteAllText(input, cnf);
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-q", "--lrat=true", "--binary=false", "-t", Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(), input, output })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout + TimeSpan.FromSeconds(5)))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (null, null, null);
            }
            p.WaitForExit();
            int code = p.ExitCode;
            string? proof = code == 20 && File.Exists(output)
                ? string.Concat(File.ReadLines(output).Where(l => l.Length > 0).Select(l => l + "\n")) : null;
            return (code is 10 or 20 ? code : code == 0 ? null : code, proof, stderr.Result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
