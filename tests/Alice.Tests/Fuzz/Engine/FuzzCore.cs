using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Alice.Compiler.Syntax;

namespace Alice.Fuzz;

// Shared by tests/Alice.Tests/Fuzz (short deterministic seed set, CI) and tools/fuzz (long runs, linked sources).

/// <summary>Seeded choices for the generators: the same seed always gives the same program.</summary>
public sealed class Rng(int seed)
{
    readonly Random r = new(seed);
    public int Seed { get; } = seed;
    public int Next(int n) => r.Next(n);
    public int Range(int lo, int hi) => r.Next(lo, hi + 1);
    public bool Chance(int percent) => r.Next(100) < percent;
    public T Pick<T>(params T[] xs) => xs[r.Next(xs.Length)];
    public T PickOf<T>(IReadOnlyList<T> xs) => xs[r.Next(xs.Count)];
}

/// <summary>One oracle violation. <see cref="Key"/> (the category, plus the position-free diagnostics for rejected
/// programs) is what the minimizer must keep reproducing.</summary>
public sealed record Finding(string Category, string Detail, string Source)
{
    string? key;
    public string Key { get => key ?? Category; init => key = value; }

    /// <summary>Category plus the distinct diagnostic messages without positions: shrinking a rejected program must
    /// keep the same complaint, not trade it for another (a removed declaration, a missing return).</summary>
    public static string KeyOf(string category, Alice.Compiler.CompileException e) => category + "|" + string.Join("|",
        e.Diagnostics.Select(d => System.Text.RegularExpressions.Regex.Replace(d.Message, @"main\.al[:(]\d+[,:]\d+\)?:? ?", "")).Distinct().Order(StringComparer.Ordinal));

    /// <summary>Stable id: category plus a hash of the (minimized) program.</summary>
    public string Id => Sanitize(Category) + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source)))[..8].ToLowerInvariant();

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    /// <summary>The finding as an .al file: the divergence as a comment header, then the program.</summary>
    public string ToFile()
    {
        var sb = new StringBuilder();
        sb.Append("# fuzz finding ").Append(Id).Append('\n').Append("# category: ").Append(Category).Append('\n');
        foreach (var l in Detail.Replace("\r\n", "\n").Split('\n').Take(40)) sb.Append("# ").Append(l.Length > 300 ? l[..300] + "..." : l).Append('\n');
        sb.Append('\n').Append(Source);
        // byte for byte: no newline is added (a lexer bug may need the file to end without one)
        return sb.ToString();
    }

    public string Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Id + ".al");
        File.WriteAllText(path, ToFile());
        return path;
    }
}

/// <summary>
/// Position-free AST dump: every public field of the syntax nodes, recursively, except source positions
/// (<c>Line</c>, <c>Col</c>), the raw mixin text (<c>SourceText</c>) and binder slots (<c>Sym</c>). Two parses of
/// the same program that differ only in layout give the same dump.
/// </summary>
public static class AstDump
{
    static readonly HashSet<string> Skip = ["Line", "Col", "SourceText", "Sym"];

    public static string Of(FileNode f)
    {
        var sb = new StringBuilder();
        Write(sb, f, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object? o, int depth, HashSet<object> seen)
    {
        if (depth > 400) { sb.Append("<deep>"); return; }
        switch (o)
        {
            case null: sb.Append("null"); return;
            case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\n")).Append('"'); return;
            case bool or char or int or long or double or float or short or byte or uint or ulong or decimal: sb.Append(o); return;
            case Enum e: sb.Append(e); return;
        }
        var t = o.GetType();
        if (o is IEnumerable en)
        {
            var items = en.Cast<object?>().ToList();
            // hash sets have no order: sort their dumps
            bool unordered = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>);
            var parts = items.Select(x => { var b = new StringBuilder(); Write(b, x, depth + 1, seen); return b.ToString(); }).ToList();
            if (unordered) parts.Sort(StringComparer.Ordinal);
            sb.Append('[').AppendJoin(", ", parts).Append(']');
            return;
        }
        if (t.Namespace == null || !t.Namespace.StartsWith("Alice.Compiler.Syntax"))
        {
            // binder objects reachable from the AST (hoisted locals): their name is the syntax
            var name = t.GetField("Name")?.GetValue(o) as string;
            sb.Append(t.Name).Append('(').Append(name ?? "").Append(')');
            return;
        }
        if (!seen.Add(o)) { sb.Append("<cycle ").Append(t.Name).Append('>'); return; }
        sb.Append(t.Name).Append('{');
        bool first = true;
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (Skip.Contains(f.Name)) continue;
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(f.Name).Append('=');
            Write(sb, f.GetValue(o), depth + 1, seen);
        }
        sb.Append('}');
        seen.Remove(o);
    }

    /// <summary>The first differing region of two dumps, for a readable finding.</summary>
    public static string FirstDifference(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 80);
        string Cut(string s) => i >= s.Length ? "<end>" : s.Substring(from, Math.Min(s.Length - from, 200));
        return "original:  ..." + Cut(a) + "\nformatted: ..." + Cut(b);
    }
}

/// <summary>Runs a function with a wall-clock limit; a hang is reported, never waited for.</summary>
public static class Watchdog
{
    /// <summary>Leaked threads of hung calls: the caller should stop fuzzing in this process after a few.</summary>
    public static int Leaked;

    public static (bool Done, T? Value, Exception? Error) Run<T>(Func<T> f, int ms)
    {
        T? value = default;
        Exception? error = null;
        var th = new Thread(() =>
        {
            try { value = f(); }
            catch (Exception e) { error = e; }
        }, 256 * 1024 * 1024) { IsBackground = true };
        th.Start();
        if (!th.Join(ms)) { Interlocked.Increment(ref Leaked); return (false, default, null); }
        return (true, value, error);
    }
}
