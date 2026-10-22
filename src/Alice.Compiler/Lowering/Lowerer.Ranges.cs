using System.Globalization;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Value-range analysis for integer locals (ADR 0014). Integer `+ - *` is overflow-checked (ADR 0013); when the
/// ranges of both operands prove the result fits its type, the check is dead and the operation is emitted
/// unchecked. Besides the instruction itself this removes the throw path, which lets the JIT turn conditionals
/// over such values into branch-free code (`z >= 2^31 ? z - 2^32 : z`).
///
/// Sources of ranges: literals, `e & mask` (mask >= 0), `e >> k`, `^`/`|` of non-negative values, widened
/// Int32 values (`x.to_i64`), floored `% d`, and interval arithmetic. A local's range is the union of every
/// value ever assigned to it anywhere in the method, blocks included (flow-insensitive, so always sound),
/// computed optimistically to a fixpoint with widening. Parameters and anything else get their type's range.
/// </summary>
public sealed partial class Lowerer
{
    readonly record struct Interval(Int128 Lo, Int128 Hi)
    {
        public static Interval Of(string cs) => cs switch
        {
            "int" => new(int.MinValue, int.MaxValue),
            "long" => new(long.MinValue, long.MaxValue),
            "short" => new(short.MinValue, short.MaxValue),
            "sbyte" => new(sbyte.MinValue, sbyte.MaxValue),
            "byte" => new(0, byte.MaxValue),
            "ushort" => new(0, ushort.MaxValue),
            "uint" => new(0, uint.MaxValue),
            _ => new(Int128.MinValue / 4, Int128.MaxValue / 4),   // unknown: wider than any 64-bit type, so nothing is proven
        };

        public bool Within(Interval t) => Lo >= t.Lo && Hi <= t.Hi;
        public Interval Union(Interval o) => new(Int128.Min(Lo, o.Lo), Int128.Max(Hi, o.Hi));
    }

    Dictionary<LocalSym, Interval> localRanges = new();
    HashSet<LocalSym> rangePending = new();   // candidates without a range yet, during the fixpoint

    void FindRanges(List<Stmt> body)
    {
        localRanges = new();
        var all = WalkStmts(body).ToList();
        foreach (var e in Walk(body))
            if (e is LambdaExpr lam) all.AddRange(WalkStmts(lam.Body));
            else if (e is CallExpr { Block: { } blk }) all.AddRange(WalkStmts(blk.Body));
        var assigned = new Dictionary<LocalSym, List<Syntax.Expr>>();
        var opaque = new HashSet<LocalSym>();
        foreach (var s in all)
        {
            switch (s)
            {
                case Assign { Declares: { } d } a when a.Op == "=": Add(d, a.Value); break;
                case Assign { Target: NameExpr { Sym: LocalSym t } } a when a.Op == "=": Add(t, a.Value); break;
                case Assign { Target: NameExpr { Sym: LocalSym t } }: opaque.Add(t); break;   // compound: not tracked
                case MultiAssign ma: opaque.UnionWith(ma.Locals); break;
                case ForStmt f: opaque.UnionWith(f.Locals); break;
            }
        }
        foreach (var e in Walk(body))
            if (e is CallExpr c) foreach (var arg in c.Args) if (arg.OutLocal != null) opaque.Add(arg.OutLocal);
        void Add(LocalSym l, Syntax.Expr v) { if (!assigned.TryGetValue(l, out var list)) assigned[l] = list = new(); list.Add(v); }

        var candidates = assigned.Keys.Where(l => !opaque.Contains(l) && l.Type.Cs is "int" or "long").ToList();
        rangePending = new HashSet<LocalSym>(candidates);
        var growth = new Dictionary<LocalSym, int>();
        for (int iter = 0; iter < 16; iter++)
        {
            bool changed = false;
            foreach (var l in candidates)
            {
                Interval? r = null;
                foreach (var v in assigned[l])
                    if (RangeOf(v, optimistic: true) is { } vr) r = r is { } acc ? acc.Union(vr) : vr;
                if (r is not { } nr) continue;
                var full = Interval.Of(l.Type.Cs);
                if (!nr.Within(full)) nr = full;
                if (localRanges.TryGetValue(l, out var old) && old == nr) continue;
                if (localRanges.ContainsKey(l) && (growth[l] = growth.GetValueOrDefault(l) + 1) > 3) nr = full;   // widening
                localRanges[l] = nr;
                rangePending.Remove(l);
                changed = true;
            }
            if (!changed) break;
        }
        foreach (var l in candidates)   // a cycle that never produced a value: no information
            if (!localRanges.ContainsKey(l)) localRanges[l] = Interval.Of(l.Type.Cs);
        rangePending.Clear();
    }

    /// <summary>The range of an integer expression; null only while the fixpoint has not seen a value yet.</summary>
    Interval? RangeOf(Syntax.Expr e, bool optimistic = false)
    {
        var full = Interval.Of(e.Type.Cs);
        switch (e)
        {
            case LitExpr { Kind: "int" } lit when IntValue(lit.Text) is { } v:
                return new(v, v);
            case NameExpr { Sym: LocalSym l }:
                if (localRanges.TryGetValue(l, out var lr)) return lr;
                return optimistic && rangePending.Contains(l) ? null : full;   // parameters and untracked locals: full range
            case CallExpr { Res: "ext", Args.Count: 0, Block: null, Recv: { } recv } c when Binder.Mangle(c.Name) is "to_i64" or "to_i" or "to_i32" && recv.Type.IsInteger:
                {
                    var src = RangeOf(recv, optimistic);
                    return src is { } s && s.Within(full) ? s : src == null ? null : full;
                }
            case UnaryExpr { Op: "-" } u when RangeOf(u.E, optimistic) is { } ur:
                return Clip(new(-ur.Hi, -ur.Lo), full);
            case TernaryExpr t:
                {
                    var a = RangeOf(t.A, optimistic);
                    var b = RangeOf(t.B, optimistic);
                    if (a == null) return b;
                    if (b == null) return a;
                    return a.Value.Union(b.Value);
                }
            case BinaryExpr b when b.Type.IsInteger:
                {
                    var l = RangeOf(b.L, optimistic);
                    var r = RangeOf(b.R, optimistic);
                    if (l == null || r == null) return null;
                    return Arith(b.Op, l.Value, r.Value) is { } res ? Clip(res, full) : full;
                }
        }
        return full;

    }

    static Interval Clip(Interval r, Interval full) => r.Within(full) ? r : full;

    /// <summary>Exact interval of `l op r` (before any overflow), or null when the operation gives no bound.</summary>
    static Interval? Arith(string op, Interval l, Interval r)
    {
        switch (op)
        {
            case "+": return new(l.Lo + r.Lo, l.Hi + r.Hi);
            case "-": return new(l.Lo - r.Hi, l.Hi - r.Lo);
            case "*":
                {
                    Int128 a = l.Lo * r.Lo, b = l.Lo * r.Hi, c = l.Hi * r.Lo, d = l.Hi * r.Hi;
                    return new(Int128.Min(Int128.Min(a, b), Int128.Min(c, d)), Int128.Max(Int128.Max(a, b), Int128.Max(c, d)));
                }
            case "&":
                if (r.Lo == r.Hi && r.Lo >= 0) return new(0, r.Lo);   // e & mask
                if (l.Lo == l.Hi && l.Lo >= 0) return new(0, l.Lo);
                if (l.Lo >= 0 && r.Lo >= 0) return new(0, Int128.Min(l.Hi, r.Hi));
                return null;
            case ">>" when r.Lo == r.Hi && r.Lo >= 0 && r.Lo < 64:
                return new(l.Lo >> (int)r.Lo, l.Hi >> (int)r.Lo);   // arithmetic shift is monotonic
            case "^" or "|" when l.Lo >= 0 && r.Lo >= 0:
                return new(0, OnesUpTo(Int128.Max(l.Hi, r.Hi)));
            case "%" when r.Lo == r.Hi && r.Lo > 0:
                return new(0, r.Lo - 1);   // floored: the sign follows the positive divisor
        }
        return null;
    }

    static Int128 OnesUpTo(Int128 v)
    {
        Int128 m = 0;
        while (m < v) m = (m << 1) | 1;
        return m;
    }

    static Int128? IntValue(string text)
    {
        string t = text.Replace("_", "").TrimEnd('L', 'l', 'U', 'u');
        if (t.StartsWith("((")) return null;   // cast-wrapped suffix literals: no range claim
        bool neg = t.StartsWith('-');
        if (neg) t = t[1..];
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? UInt128.TryParse(t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && (t = hex.ToString()) != null
            : t.Length > 0 && t.All(char.IsAsciiDigit);
        if (!ok || !Int128.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
        return neg ? -v : v;
    }

    /// <summary>`l op r` on Int32/Int64 whose operand ranges prove no overflow: emitted without the check.</summary>
    MExpr? UncheckedIfProven(BinaryExpr b, MExpr l, MExpr r)
    {
        if (b.Op is not ("+" or "-" or "*") || b.Type.Cs is not ("int" or "long") || b.L.Type.Cs != b.Type.Cs && b.R.Type.Cs != b.Type.Cs) return null;
        if (RangeOf(b.L) is not { } lr || RangeOf(b.R) is not { } rr || Arith(b.Op, lr, rr) is not { } res || !res.Within(Interval.Of(b.Type.Cs))) return null;
        return new MCall { Name = "unchecked", Args = { new MBinary { Op = b.Op, L = l, R = r } } };
    }

    /// <summary>
    /// `x >= 2^(k-1) ? x - 2^k : x` (or `x < 2^(k-1) ? x : x - 2^k`) with x proven in [0, 2^k) reinterprets the low
    /// k bits as signed: exactly `(T)(intK)x`, one sign-extending move instead of a data-dependent branch.
    /// </summary>
    MExpr? TrySignExtend(TernaryExpr t)
    {
        if (t.C is not BinaryExpr { Op: ">=" or "<" } c || c.R is not LitExpr cl || IntValue(cl.Text) is not { } half) return null;
        var (neg, pos) = c.Op == ">=" ? (t.A, t.B) : (t.B, t.A);
        if (neg is not BinaryExpr { Op: "-" } sub || sub.R is not LitExpr sl || IntValue(sl.Text) is not { } span || span != half * 2) return null;
        string? narrow = span == 256 ? "sbyte" : span == 65536 ? "short" : span == 4294967296 ? "int" : null;
        if (narrow == null || c.L is not NameExpr { Sym: LocalSym x } || !Same(sub.L, x) || !Same(pos, x) || x.Type.Cs is not ("int" or "long")) return null;
        if (RangeOf(c.L) is not { } r || r.Lo < 0 || r.Hi >= span) return null;
        return new MCast { TypeCs = x.Type.Cs, E = new MCall { Name = "unchecked", Args = { new MCast { TypeCs = narrow, E = new MCode(x.CsName) } } } };

        static bool Same(Syntax.Expr e, LocalSym l) => e is NameExpr { Sym: LocalSym s } && s == l;
    }
}
