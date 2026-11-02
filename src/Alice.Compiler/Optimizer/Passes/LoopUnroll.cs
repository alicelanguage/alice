using System.Globalization;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// Full unrolling of a counted loop with a small literal trip count whose body is straight-line arithmetic:
/// `var i = a; while (i &lt; N) { body; i += 1; }` (and `for (var i = a; i &lt; N; i++)`) becomes N - a copies of the
/// body with `i` replaced by its literal value, each copy in its own block. RyuJIT does not unroll a loop with a
/// body (only empty constant loops), so the counter update, the compare and the jump stay on every round; with
/// literal indices `rk[i]` becomes `rk[3]`, a constant-index load. merkle's 22 Speck rounds (main.c asks clang for
/// `unroll(full)`): 3.74 s -> 3.32 s serial at 2^20 leaves.
/// Only when: the body never writes `i` before its final `+= 1`, has no break, continue, nested loop, lambda or call
/// other than `unchecked`, conversions and the branchless selects, every mention of `i` is a plain name (not
/// inside opaque code), and the copies stay under <see cref="MaxNodes"/> MIR nodes. A `while` counter keeps its
/// final value (`var i = N;`) for code after the loop.
/// </summary>
public sealed class LoopUnroll : IMirPass
{
    public string Name => "unroll";

    /// <summary>Largest trip count unrolled.</summary>
    public const int MaxTrips = 32;

    /// <summary>Largest unrolled size, in MIR nodes (merkle's Speck round is ~45, x 22).</summary>
    public const int MaxNodes = 1200;

    static readonly HashSet<string> Conversions = ["to_i", "to_f", "to_i8", "to_i16", "to_i32", "to_i64", "to_u8", "to_u16", "to_u32", "to_u64", "to_f32", "to_f64"];

    public void Run(List<MStmt> body) => List(body);

    void List(List<MStmt>? body)
    {
        if (body == null) return;
        for (int j = 0; j < body.Count; j++)
        {
            foreach (var inner in Bodies(body[j])) List(inner);
            if (body[j] is MFor f && Counted(f) is { } fc && Unrolled(f.Var, fc.Start, fc.End, f.Body, false) is { } fcopies)
            {
                body.RemoveAt(j);
                body.InsertRange(j, fcopies);
                j += fcopies.Count - 1;
            }
            else if (j + 1 < body.Count && body[j] is MDecl { Init: { } init, TypeCs: null or "int" } d && MirFacts.IntLiteral(init, out long a) && Plain(init)
                && body[j + 1] is MWhile { Cond: MBinary { Op: AirBinOp.Lt, L: MCode { Text: var v }, R: { } end } } w && v == d.Name
                && MirFacts.IntLiteral(end, out long n) && Plain(end) && Unrolled(d.Name, a, n, w.Body, true) is { } wcopies)
            {
                d.Init = Lit(Math.Max(a, n));
                body.RemoveAt(j + 1);
                body.InsertRange(j + 1, wcopies);
                j += wcopies.Count;
            }
        }
    }

    static (long Start, long End)? Counted(MFor f)
    {
        if (f.Cmp != "<" || f.VarType is not (null or "int") || !(f.Step == "++" || f.Step == $" = unchecked({f.Var} + 1)")) return null;
        if (!MirFacts.IntLiteral(f.Start, out long a) || !Plain(f.Start) || !MirFacts.IntLiteral(f.End, out long n) || !Plain(f.End)) return null;
        return (a, n);
    }

    /// <summary>A decimal Int32 literal (no suffix, no hex).</summary>
    static bool Plain(MExpr e) => e is MCode { Text: var t } && t.TrimStart('-').All(char.IsAsciiDigit) && MirFacts.IntLiteral(e, out long v) && v is >= int.MinValue and <= int.MaxValue;

    static MCode Lit(long v) => new(v.ToString(CultureInfo.InvariantCulture));

    /// <summary>The copies of `body` for i = a .. n - 1, or null when the loop does not qualify.</summary>
    static List<MStmt>? Unrolled(string i, long a, long n, List<MStmt> body, bool stepInBody)
    {
        long trips = n - a;
        if (trips < 1 || trips > MaxTrips) return null;
        var core = body;
        if (stepInBody)
        {
            if (body.Count == 0 || !Step(body[^1], i)) return null;
            core = body.GetRange(0, body.Count - 1);
        }
        if (MirFacts.WritesNumber(core, i)) return null;
        int size = 0;
        foreach (var s in core)
            if (!Straight(s, i, ref size)) return null;
        if (size * trips > MaxNodes) return null;
        var copies = new List<MStmt>();
        if (core.Count == 0) return copies;   // a counting loop: only the final value remains
        for (long k = a; k < n; k++)
        {
            var block = new MBlock { Line = body.Count > 0 ? body[0].Line : 0 };
            foreach (var s in core) block.Body.Add(Clone(s, i, k));
            copies.Add(block);
        }
        return copies;
    }

    /// <summary>`i += 1`, `i = i + 1` (checked or not), `i++` / `++i`.</summary>
    static bool Step(MStmt s, string i) => s switch
    {
        MAssign { Op: "+=", Target: MCode { Text: var t }, Value: MCode { Text: "1" } } => t == i,
        MAssign { Op: "=", Target: MCode { Text: var t }, Value: var v } when t == i => Inc(v, i),
        MExprStmt { E: MUnary { Op: AirUnOp.Inc, E: MCode { Text: var t } } } => t == i,
        _ => false,
    };

    static bool Inc(MExpr v, string i) => v switch
    {
        MCall { Target: null, Name: "unchecked", Args: [var x] } => Inc(x, i),
        MBinary { Op: AirBinOp.Add, L: MCode { Text: var l }, R: MCode { Text: "1" } } => l == i,
        _ => false,
    };

    /// <summary>Straight-line statements over simple expressions; counts nodes into `size`.</summary>
    static bool Straight(MStmt s, string i, ref int size)
    {
        size++;
        switch (s)
        {
            case MDecl d: return d.Init == null || Simple(d.Init, i, ref size);
            case MAssign a: return Simple(a.Target, i, ref size) && Simple(a.Value, i, ref size);
            case MExprStmt es: return Simple(es.E, i, ref size);
            case MIf c:
                if (!Simple(c.Cond, i, ref size)) return false;
                foreach (var x in c.Then) if (!Straight(x, i, ref size)) return false;
                foreach (var x in c.Else ?? []) if (!Straight(x, i, ref size)) return false;
                return true;
            case MBlock { Lock: null } b:
                foreach (var x in b.Body) if (!Straight(x, i, ref size)) return false;
                return true;
            default: return false;
        }
    }

    static bool Simple(MExpr e, string i, ref int size)
    {
        size++;
        switch (e)
        {
            case MCode { Text: var t }:
                return t == i || !Word(t, i) && !t.Contains('(');
            case MCall { Target: null, Name: "unchecked" }:
            case MCall { Target: MCode { Text: "global::Alice.Runtime.Branchless" } }:
            case MCall { Args.Count: 0, Target: not null } c when Conversions.Contains(c.Name):
            case MBinary or MUnary or MCond or MIndex or MCast:
                foreach (var x in MirFacts.Children(e)) if (!Simple(x, i, ref size)) return false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>`name` as a whole word inside opaque text.</summary>
    static bool Word(string text, string name)
    {
        for (int p = text.IndexOf(name, StringComparison.Ordinal); p >= 0; p = text.IndexOf(name, p + 1, StringComparison.Ordinal))
        {
            bool before = p == 0 || !(char.IsLetterOrDigit(text[p - 1]) || text[p - 1] == '_');
            int q = p + name.Length;
            if (before && (q >= text.Length || !(char.IsLetterOrDigit(text[q]) || text[q] == '_'))) return true;
        }
        return false;
    }

    static MStmt Clone(MStmt s, string i, long k)
    {
        MStmt c = s switch
        {
            MDecl d => new MDecl { Name = d.Name, TypeCs = d.TypeCs, Init = d.Init == null ? null : Clone(d.Init, i, k), RentLength = d.RentLength },
            MAssign a => new MAssign { Target = Clone(a.Target, i, k), Op = a.Op, Value = Clone(a.Value, i, k) },
            MExprStmt es => new MExprStmt { E = Clone(es.E, i, k) },
            MIf f => new MIf { Cond = Clone(f.Cond, i, k), Then = f.Then.Select(x => Clone(x, i, k)).ToList(), Else = f.Else?.Select(x => Clone(x, i, k)).ToList() },
            MBlock b => new MBlock { Body = b.Body.Select(x => Clone(x, i, k)).ToList(), Unchecked = b.Unchecked, Unsafe = b.Unsafe },
            _ => throw new InvalidOperationException(s.GetType().Name),   // Straight admits only the kinds above
        };
        c.Span = s.Span;
        c.Line = s.Line;
        return c;
    }

    static MExpr Clone(MExpr e, string i, long k)
    {
        MExpr c = e switch
        {
            MCode { Text: var t } => t == i ? Lit(k) : new MCode(t),
            MCall m => new MCall { Target = m.Target == null ? null : Clone(m.Target, i, k), Name = m.Name, Args = m.Args.Select(x => Clone(x, i, k)).ToList(), Safe = m.Safe },
            MIndex ix => new MIndex { Target = Clone(ix.Target, i, k), Args = ix.Args.Select(x => Clone(x, i, k)).ToList() },
            MBinary b => new MBinary { Op = b.Op, Checked = b.Checked, L = Clone(b.L, i, k), R = Clone(b.R, i, k) },
            MUnary u => new MUnary { Op = u.Op, Checked = u.Checked, E = Clone(u.E, i, k), Postfix = u.Postfix },
            MCond q => new MCond { C = Clone(q.C, i, k), A = Clone(q.A, i, k), B = Clone(q.B, i, k) },
            MCast ca => new MCast { TypeCs = ca.TypeCs, E = Clone(ca.E, i, k) },
            _ => throw new InvalidOperationException(e.GetType().Name),   // Simple admits only the kinds above
        };
        c.Type = e.Type;
        c.Span = e.Span;
        return c;
    }

    static IEnumerable<List<MStmt>> Bodies(MStmt s) => s switch
    {
        MIf i => new[] { i.Then, i.Else }.OfType<List<MStmt>>(),
        MWhile w => [w.Body],
        MFor f => [f.Body],
        MForeach fe => [fe.Body],
        MTry t => t.Catches.Select(c => c.Body).Prepend(t.Body).Append(t.Finally).OfType<List<MStmt>>(),
        MBlock b => [b.Body],
        MUsing u => [u.Body],
        _ => [],
    };
}
