namespace Alice.Fuzz;

/// <summary>
/// Delta debugging over lines (Zeller's ddmin) followed by two greedy passes: whole blocks (a line that opens a
/// block through its matching `end`) and single lines. <c>fails</c> must say whether a candidate still shows the same
/// failure (same finding category); the result is 1-minimal with respect to line removal.
/// </summary>
public static class Minimizer
{
    public static string Minimize(string src, Func<string, bool> fails, int maxTests = 600)
    {
        src = src.Replace("\r\n", "\n");
        // candidates end like the original (a lexer bug may need the file to end without a newline)
        string end = src.EndsWith('\n') ? "\n" : "";
        var lines = src.TrimEnd('\n').Split('\n').ToList();
        int tests = 0;
        bool Try(List<string> c)
        {
            if (tests++ >= maxTests) return false;
            return fails(string.Join('\n', c) + end);
        }

        // ddmin
        int n = 2;
        while (lines.Count >= 2 && tests < maxTests)
        {
            int chunk = (int)Math.Ceiling(lines.Count / (double)n);
            bool reduced = false;
            for (int start = 0; start < lines.Count; start += chunk)
            {
                var complement = lines.Take(start).Concat(lines.Skip(start + chunk)).ToList();
                if (complement.Count > 0 && Try(complement))
                {
                    lines = complement;
                    n = Math.Max(n - 1, 2);
                    reduced = true;
                    break;
                }
            }
            if (!reduced)
            {
                if (n >= lines.Count) break;
                n = Math.Min(lines.Count, n * 2);
            }
        }

        // blocks: an opener line through its matching `end`
        for (int i = 0; i < lines.Count && tests < maxTests; i++)
        {
            int j = MatchingEnd(lines, i);
            if (j <= i) continue;
            var c = lines.Take(i).Concat(lines.Skip(j + 1)).ToList();
            if (c.Count > 0 && Try(c)) { lines = c; i--; }
        }

        // single lines, until nothing more goes
        bool changed = true;
        while (changed && tests < maxTests)
        {
            changed = false;
            for (int i = lines.Count - 1; i >= 0 && tests < maxTests; i--)
            {
                var c = lines.Where((_, k) => k != i).ToList();
                if (c.Count > 0 && Try(c)) { lines = c; changed = true; }
            }
        }
        return string.Join('\n', lines) + end;
    }

    static readonly string[] Openers = ["def ", "class ", "struct ", "module ", "enum ", "affine enum ", "if ", "unless ", "while ", "until ", "for ", "par for ", "case", "begin", "record ", "pure def ", "safe def ", "safe pure def ", "interface ", "namespace "];

    static bool Opens(string l)
    {
        var t = l.TrimStart();
        if (t.EndsWith(" end") || t.StartsWith('#')) return false;
        if (t.EndsWith(" do") || System.Text.RegularExpressions.Regex.IsMatch(t, @" do \|[^|]*\|$")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\w+ = (case|if|unless|begin|while)\b")) return true;
        if (t.StartsWith("record ")) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^((pure|safe|private|inline|protected) )*def "))
        {
            // endless def: `def sq(x : Int32) : Int32 = x * x`
            var noParams = System.Text.RegularExpressions.Regex.Replace(t, @"\(.*\)", "()");
            return !noParams.Contains(" = ");
        }
        return Openers.Any(o => t.StartsWith(o));
    }

    /// <summary>The line of the `end` closing the block opened at <paramref name="i"/>, or -1.</summary>
    static int MatchingEnd(List<string> lines, int i)
    {
        if (!Opens(lines[i])) return -1;
        int depth = 0;
        for (int k = i; k < lines.Count; k++)
        {
            var t = lines[k].TrimStart();
            if (Opens(lines[k])) depth++;
            else if (t == "end" || t.StartsWith("end ") || t.StartsWith("end.") || t == "}") { depth--; if (depth == 0) return k; }
        }
        return -1;
    }
}

/// <summary>
/// Guards for shrinking generated typed programs (<see cref="TypedGen"/>) that are executed: a candidate must still
/// terminate by construction, or the minimizer would trade the bug for a hang or a stack overflow (which kills the
/// process). Every `while`/`until` on a counter keeps its `jN += 1`, and a recursive `buildN` keeps its base case.
/// </summary>
public static class Shrink
{
    public static bool Terminates(string src)
    {
        var lines = src.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"^(\s*)(while|until) (j[\d_]+) ");
            if (!m.Success) continue;
            string ind = m.Groups[1].Value, v = m.Groups[3].Value;
            bool inc = false;
            for (int k = i + 1; k < lines.Length && !(lines[k].StartsWith(ind + "end") && lines[k].Trim() == "end" && lines[k].Length - lines[k].TrimStart().Length == ind.Length); k++)
                if (lines[k].Trim() == v + " += 1") inc = true;
            if (!inc) return false;
            // the counter must still be declared right before its loop (an undeclared name is a dynamic call)
            if (i == 0 || lines[i - 1].Trim() != v + " = 0") return false;
        }
        foreach (System.Text.RegularExpressions.Match d in System.Text.RegularExpressions.Regex.Matches(src, @"def (build\d+)\("))
            if (!src.Contains("if d == 0")) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(src, @"def (fib\d+)\(") && !src.Contains("n < 2 ?")) return false;
        return true;
    }

    /// <summary>The program with its declared effects removed (`pure`, `safe`, `par for`, `par a, b =`): a
    /// rejection that survives must be about the effect, so shrinking keeps the rest of the program valid.</summary>
    public static string WithoutEffects(string src) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(src, @"\b(safe |pure )+def ", "def "),
            @"^(\s*)par (for |(?=\w+, \w+ = ))", "$1$2", System.Text.RegularExpressions.RegexOptions.Multiline);
}

/// <summary>
/// Triaged findings (tools/fuzz/findings, /work/reports/fuzz-findings.md, tests/Alice.Tests/Fuzz/Regressions): a new
/// occurrence is counted under its id instead of being minimized and written again, so a long run keeps finding
/// new bugs instead of the same one. Matched on the category and the finding detail (diagnostic text).
/// </summary>
public static class Known
{
    public static readonly List<(string Id, string Category, System.Text.RegularExpressions.Regex Detail, System.Text.RegularExpressions.Regex? Source)> Issues =
    [
        ("F1-strict-range-pattern", "strict-rejects:", new(@"this range expression has no static type"), null),
        ("F2-initialize-not-inherited", "roslyn-reject", new(@"does not contain a constructor that takes"), null),
        ("F3-subclass-assigns-inherited-getter", "roslyn-reject", new(@"set accessor is inaccessible"), null),
        ("F4-pure-range-pattern", "rejected:AL0210", new(@"is not pure"), new(@"^\s*when -?\d+\.\.", System.Text.RegularExpressions.RegexOptions.Multiline)),
        ("F5-local-redeclared-in-sibling-block", "roslyn-reject", new(@"cannot be declared in this scope because that name is used in an enclosing local scope"), null),
        ("P1-lexer-crash-unterminated-interpolation", "parse-crash:IndexOutOfRangeException@Lexer.Adv", new(""), null),
        ("P2-lexer-crash-backslash-at-eof", "parse-crash:IndexOutOfRangeException@Lexer.Str", new(""), null),
        // a string literal spanning lines: the dump shows the changed text with an escaped newline
        ("P3-fmt-reindents-multiline-string", "fmt-changes-ast", new(@"\\n"), null),
        ("P4-fmt-heredoc-without-body", "fmt-not-idempotent", new(""), new(@"<<[~-]?'?[A-Za-z_]")),
        ("P5-record-one-liner-then-def", "fmt-breaks-parse", new(""),
            new(@"^\s*record [^\n]*\n(\s*(#[^\n]*)?\n)*\s*(\w+ )*def ", System.Text.RegularExpressions.RegexOptions.Multiline)),
        // by design (ADR 0013): checked arithmetic on constants is folded by Roslyn, an overflow is a compile error
        ("P11-fmt-breaks-record-continuation", "fmt-breaks-parse", new(@"record.*\n.*def self\."), null),
        ("P12-fmt-not-idempotent-ensure", "fmt-not-idempotent", new(@"begin.*ensure"), null),
        ("D1-constant-overflow", "roslyn-reject", new(@"overflows at compile time in checked mode"), null),
    ];

    public static string? Match(Finding f) =>
        Issues.FirstOrDefault(k => f.Category.StartsWith(k.Category, StringComparison.Ordinal) && k.Detail.IsMatch(f.Detail)
            && (k.Source == null || k.Source.IsMatch(f.Source))).Id;

    /// <summary>I1 (fixed on main by fix-il, so no longer suppressed: a recurrence is a new finding): every line where the backends differ is the same Float64 rounded through Float32 on one side.</summary>
    public static bool Float32Rounding(Finding f)
    {
        if (!f.Category.StartsWith("diff-il", StringComparison.Ordinal)) return false;
        int c = f.Detail.IndexOf("C# backend:\n", StringComparison.Ordinal), i = f.Detail.IndexOf("\nIL backend:\n", StringComparison.Ordinal);
        if (c < 0 || i < 0) return false;
        var a = f.Detail[(c + 12)..i].Split('\n');
        var b = f.Detail[(i + 13)..].Split('\n');
        if (a.Length != b.Length) return false;
        bool any = false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int k = 0; k < a.Length; k++)
        {
            if (a[k] == b[k]) continue;
            if (!double.TryParse(a[k], System.Globalization.NumberStyles.Float, inv, out var x) || !double.TryParse(b[k], System.Globalization.NumberStyles.Float, inv, out var y)) return false;
            if ((float)x != (float)y) return false;
            any = true;
        }
        return any;
    }
}

public static partial class ShrinkTyping
{
    /// <summary>Whether a shrinking candidate is still statically typed: strict mode (AL0220) has no complaint other
    /// than a known false positive. Removing a declaration silently turns its uses into dynamic calls otherwise.</summary>
    public static bool WellTyped(string src)
    {
        try { Alice.Compiler.AliceCompiler.TranspileStrict([new Alice.Compiler.SourceFile("main.al", Shrink.WithoutEffects(src))]); return true; }
        catch (Alice.Compiler.CompileException e)
        {
            return !e.Diagnostics.Any(d => d.Code == "AL0220" && !Known.Issues.Any(k => k.Category == "strict-rejects:" && k.Detail.IsMatch(d.Message)));
        }
        catch (Exception) { return true; }
    }
}
