using System.Globalization;
using Alice.Compiler.Backend.Il.Opt;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;
using Options = Alice.Compiler.Backend.Il.Opt.IlOpt;

namespace Alice.Compiler.Air.Passes.IlOpt;

/// <summary>
/// Proves array accesses in bounds for the IL backend (reports/il-shapes.md R2; IL backend only, runs per body after
/// IlLowering). An array local whose length is exact and fixed (`Own&lt;T&gt;.Rent(n)`, `new T[n]` or a literal with
/// a constant size, written only by its declaration) gives `[0, n)`; an index is proven when its value interval lies
/// inside. Intervals come from a flow-sensitive abstract interpretation of the body: assignments, branch and loop
/// conditions (`while j &lt; 256`, `for zc in 0...64`), loop fixpoints with widening, `continue`/`break` paths, and
/// symbolic definitions (`i = zc * 64 + xc` stays relational: `xc &gt; 0 ? i - 1 : i` is evaluated as
/// `zc * 64 + xc - 1` under `xc &gt;= 1`). Selects (`c ? a : b`, `Branchless.I32/U32`) split the evaluation on their
/// condition, so `i + (zc &gt; 0 ? -64 : 0)` is proven per case. Proven <see cref="MIndex"/> nodes are marked for
/// <see cref="UncheckedIndex"/>; with <see cref="IlOptPass.NoOverflow"/> the same intervals clear the overflow check of
/// `+ - *` (and compound `+= -= *=` on locals) whose exact result fits the type, e.g. every `i += 1` under
/// `while i &lt; n`. A bare loop counter (`a[i]` under `while i &lt; n`) is left to the JIT, which removes
/// or clones that check itself and keeps its IV widening (R1, R2). Anything not understood (labels, unknown
/// statements, a loop that does not converge) proves nothing in the whole body.
/// </summary>
public sealed class InBounds : IMirPass
{
    public string Name => "il-inbounds";

    /// <summary>Accesses proven by the last <see cref="Run"/> (for tests and `alice emit --il` statistics).</summary>
    public int Proven { get; private set; }

    /// <summary>Overflow checks removed by the last <see cref="Run"/>.</summary>
    public int Unchecked { get; private set; }

    public void Run(List<MStmt> body)
    {
        bool bounds = Options.On(IlOptPass.InBounds), overflow = Options.On(IlOptPass.NoOverflow);
        if (!bounds && !overflow) return;
        var a = new Analysis { WantOverflow = overflow };
        if (!a.Analyze(body)) return;
        if (bounds)
            foreach (var (ix, ok) in a.Results)
                if (ok) { UncheckedIndex.Prove(ix); Proven++; }
        if (!overflow) return;
        // `+ - *` whose exact result interval fits the type: the check is dead (no `add.ovf`, no `jo`), and the JIT
        // can widen and strength-reduce the induction variables it no longer has to keep 32-bit exact
        foreach (var (b, ok) in a.Overflow)
            if (ok) { b.Checked = false; Unchecked++; }
        foreach (var (asg, ok) in a.Compound)
            if (ok && (asg.Target is AirLocalRef { Narrowed: false } || asg.Target.GetType() == typeof(MCode)) && CompoundOp(asg.Op) is { } op)
            {
                asg.Value = new MBinary { Op = op, L = Copy(asg.Target), R = asg.Value, Checked = false, Type = asg.Target.Type };
                asg.Op = "=";
                Unchecked++;
            }
    }

    static MExpr Copy(MExpr target) => target switch
    {
        AirLocalRef l => new AirLocalRef(l.Name, l.Type) { Span = l.Span },
        MCode c => new MCode(c.Text) { Type = c.Type, Span = c.Span },
        _ => target,
    };

    static AirBinOp? CompoundOp(string op) => op switch { "+=" => AirBinOp.Add, "-=" => AirBinOp.Sub, "*=" => AirBinOp.Mul, _ => null };

    // ---------------------------------------------------------------- symbolic values

    abstract record S;
    sealed record SConst(Int128 V) : S;
    /// <summary>A local or parameter; <paramref name="Def"/> is its current symbolic definition, when known.</summary>
    sealed record SVar(string Name, S? Def) : S;
    sealed record SBin(AirBinOp Op, S L, S R, bool Checked, PrimKind Kind) : S;
    sealed record SSel(S Cond, S A, S B) : S;
    sealed record SNot(S E) : S;
    /// <summary>A conversion to <paramref name="Kind"/>: checked (the result is in range or it throws) or wrapping.</summary>
    sealed record SConv(S E, PrimKind Kind, bool Checked) : S;
    sealed record STop(PrimKind? Kind) : S;

    readonly record struct Iv(Int128 Lo, Int128 Hi)
    {
        public Iv Union(Iv o) => new(Int128.Min(Lo, o.Lo), Int128.Max(Hi, o.Hi));
        public bool Within(Iv o) => Lo >= o.Lo && Hi <= o.Hi;
    }

    static Iv? Range(PrimKind? k) => k switch
    {
        PrimKind.SByte => new(sbyte.MinValue, sbyte.MaxValue),
        PrimKind.Byte => new(0, byte.MaxValue),
        PrimKind.Short => new(short.MinValue, short.MaxValue),
        PrimKind.UShort or PrimKind.Char => new(0, ushort.MaxValue),
        PrimKind.Int => new(int.MinValue, int.MaxValue),
        PrimKind.UInt => new(0, uint.MaxValue),
        PrimKind.Long => new(long.MinValue, long.MaxValue),
        PrimKind.ULong => new(0, ulong.MaxValue),
        _ => null,
    };

    static PrimKind? KindOf(AirType? t) => t is AirType.Prim p && Range(p.Kind) != null ? p.Kind : null;

    static PrimKind? KindOfCs(string? cs) => cs switch
    {
        "int" => PrimKind.Int, "uint" => PrimKind.UInt, "long" => PrimKind.Long, "ulong" => PrimKind.ULong,
        "short" => PrimKind.Short, "ushort" => PrimKind.UShort, "byte" => PrimKind.Byte, "sbyte" => PrimKind.SByte,
        _ => null,
    };

    static PrimKind? ConvKind(string name) => name switch
    {
        "to_i" or "to_i32" or "to_i32_b" or "to_i_b" => PrimKind.Int,
        "to_u32" or "to_u32_b" => PrimKind.UInt,
        "to_i64" or "to_i64_b" => PrimKind.Long,
        "to_u64" or "to_u64_b" => PrimKind.ULong,
        "to_i16" or "to_i16_b" => PrimKind.Short,
        "to_u16" or "to_u16_b" => PrimKind.UShort,
        "to_u8" or "to_u8_b" => PrimKind.Byte,
        "to_i8" or "to_i8_b" => PrimKind.SByte,
        _ => null,
    };

    static Int128? Literal(AirConst c)
    {
        if (c.Kind != AirConstKind.Int) return c.Kind == AirConstKind.Bool ? (c.Literal == "true" ? 1 : 0) : null;
        switch (c.Value)
        {
            case int i: return i;
            case long l: return l;
            case uint u: return u;
            case ulong ul: return ul;
            case short s: return s;
            case ushort us: return us;
            case byte b: return b;
            case sbyte sb: return sb;
        }
        var t = c.Literal.Replace("_", "").TrimEnd('u', 'U', 'l', 'L');
        bool neg = t.StartsWith('-');
        if (neg) t = t[1..];
        Int128 v;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return null;
            v = h;
        }
        else if (ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var d)) v = d;
        else return null;
        return neg ? -v : v;
    }

    // ---------------------------------------------------------------- analysis

    sealed class State
    {
        public Dictionary<string, Iv> Env = new();
        public Dictionary<string, S> Defs = new();

        public State Clone() => new() { Env = new(Env), Defs = new(Defs) };
    }

    sealed class LoopCtx
    {
        public readonly List<State> Breaks = [];
        public readonly List<State> Continues = [];
        public bool IsSwitch;
    }

    sealed class Bail : Exception { }

    sealed class Analysis
    {
        public readonly Dictionary<MIndex, bool> Results = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<MBinary, bool> Overflow = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<MAssign, bool> Compound = new(ReferenceEqualityComparer.Instance);
        public bool WantOverflow;
        readonly Dictionary<string, PrimKind> kinds = new();
        readonly Dictionary<string, long> lengths = new();
        readonly HashSet<string> volatileNames = new();
        readonly Stack<LoopCtx> loops = new();
        readonly List<string> counters = [];
        int uncheckedDepth;

        public bool Analyze(List<MStmt> body)
        {
            try
            {
                var decls = new Dictionary<string, int>();
                Prescan(body, decls, inLambda: false);
                // a name declared twice (sibling scopes, unrolled bodies) is fine for the flow-sensitive walk, but
                // not as a fixed-length array
                foreach (var (n, c) in decls) if (c > 1) lengths.Remove(n);
                foreach (var n in volatileNames) lengths.Remove(n);
                if (lengths.Count == 0 && !WantOverflow) return false;
                Walk(body, new State());
                return true;
            }
            catch (Bail) { return false; }
        }

        // ---- prescan: kinds, fixed-length arrays, names written in ways the walk does not model

        void Prescan(List<MStmt>? body, Dictionary<string, int> decls, bool inLambda)
        {
            foreach (var s in body ?? [])
            {
                switch (s)
                {
                    case MDecl d:
                        decls[d.Name] = decls.GetValueOrDefault(d.Name) + 1;
                        if (inLambda) volatileNames.Add(d.Name);
                        if ((KindOfCs(d.TypeCs) ?? KindOf(d.Init?.Type)) is { } k) kinds.TryAdd(d.Name, k);
                        if (FixedLength(d.Init) is { } len) lengths[d.Name] = len;
                        if (d.Init != null) PrescanExpr(d.Init, decls, inLambda);
                        break;
                    case MAssign a:
                        if (a.Target is AirLocalRef { Narrowed: false } t) { if (inLambda) volatileNames.Add(t.Name); lengths.Remove(t.Name); NoteKind(t); }
                        else if (a.Target is AirParamRef p) { if (inLambda) volatileNames.Add(p.Name); NoteKind(p); }
                        else if (a.Target is MCode { } c && a.Target.GetType() == typeof(MCode))
                        {
                            if (!IsIdent(c.Text)) throw new Bail();
                            if (inLambda) volatileNames.Add(c.Text);
                            lengths.Remove(c.Text);
                            if (KindOf(c.Type) is { } ck) kinds.TryAdd(c.Text, ck);
                        }
                        else PrescanExpr(a.Target, decls, inLambda);
                        PrescanExpr(a.Value, decls, inLambda);
                        break;
                    case MDeconstruct dc:
                        foreach (var n in dc.Names) { volatileNames.Add(n); lengths.Remove(n); }
                        PrescanExpr(dc.Value, decls, inLambda);
                        break;
                    case MForeach fe:
                        foreach (var n in fe.Vars) volatileNames.Add(n);
                        PrescanExpr(fe.Coll, decls, inLambda);
                        Prescan(fe.Body, decls, inLambda);
                        break;
                    case MFor f:
                        decls[f.Var] = decls.GetValueOrDefault(f.Var) + 1;
                        if (inLambda) volatileNames.Add(f.Var);
                        if ((KindOfCs(f.VarType) ?? KindOf(f.Start.Type)) is { } fk) kinds.TryAdd(f.Var, fk);
                        PrescanExpr(f.Start, decls, inLambda); PrescanExpr(f.End, decls, inLambda);
                        Prescan(f.Body, decls, inLambda);
                        break;
                    case MUsing u:
                        volatileNames.Add(u.Name);
                        PrescanExpr(u.Value, decls, inLambda);
                        Prescan(u.Body, decls, inLambda);
                        break;
                    case MTry tr:
                        Prescan(tr.Body, decls, inLambda);
                        foreach (var c in tr.Catches) { if (c.Name != null) volatileNames.Add(c.Name); Prescan(c.Body, decls, inLambda); }
                        Prescan(tr.Finally, decls, inLambda);
                        break;
                    case MIf i: PrescanExpr(i.Cond, decls, inLambda); Prescan(i.Then, decls, inLambda); Prescan(i.Else, decls, inLambda); break;
                    case MWhile w: PrescanExpr(w.Cond, decls, inLambda); Prescan(w.Body, decls, inLambda); break;
                    case MBlock b: if (b.Lock != null) PrescanExpr(b.Lock, decls, inLambda); Prescan(b.Body, decls, inLambda); break;
                    case MExprStmt es: PrescanExpr(es.E, decls, inLambda); break;
                    case MReturn r: if (r.Value != null) PrescanExpr(r.Value, decls, inLambda); break;
                    case MThrow th: PrescanExpr(th.Value, decls, inLambda); break;
                    case AirSwitch sw:
                        PrescanExpr(sw.Value, decls, inLambda);
                        foreach (var c in sw.Cases) Prescan(c, decls, inLambda);
                        Prescan(sw.Default, decls, inLambda);
                        break;
                    case MBreak or MContinue: break;
                    default: throw new Bail();
                }
            }
        }

        void NoteKind(MExpr e)
        {
            string? n = e is AirLocalRef l ? l.Name : e is AirParamRef p ? p.Name : null;
            if (n != null && KindOf(e.Type) is { } k) kinds.TryAdd(n, k);
        }

        void PrescanExpr(MExpr e, Dictionary<string, int> decls, bool inLambda)
        {
            switch (e)
            {
                case AirLocalRef or AirParamRef: NoteKind(e); return;
                case MLambda l:
                    if (l.ExprBody != null) PrescanExpr(l.ExprBody, decls, true);
                    Prescan(l.Body, decls, true);
                    foreach (var p in l.Params) volatileNames.Add(p);
                    // whatever a lambda writes is written at unknown times
                    MarkWritten(l);
                    return;
                case AirSeq q:
                    // statements inside an expression: what they write is not tracked by the walk
                    var inner = new Dictionary<string, int>();
                    Prescan(q.Pre, inner, inLambda);
                    foreach (var n in Written(q.Pre)) { volatileNames.Add(n); lengths.Remove(n); }
                    foreach (var (n, c) in inner) decls[n] = decls.GetValueOrDefault(n) + c;
                    PrescanExpr(q.Value, decls, inLambda);
                    return;
                case MArg { Prefix: var pre } a when pre.Contains("ref") || pre.Contains("out"):
                    if (Name(a.E) is { } rn) { volatileNames.Add(rn); lengths.Remove(rn); }
                    PrescanExpr(a.E, decls, inLambda);
                    return;
                case MUnary { Op: AirUnOp.Inc or AirUnOp.Dec or AirUnOp.AddressOf } u:
                    if (Name(u.E) is { } un) { volatileNames.Add(un); lengths.Remove(un); }
                    PrescanExpr(u.E, decls, inLambda);
                    return;
            }
            foreach (var c in Children(e)) PrescanExpr(c, decls, inLambda);
        }

        void MarkWritten(MLambda l)
        {
            var body = l.Body ?? (l.ExprBody != null ? [new MExprStmt { E = l.ExprBody }] : []);
            foreach (var n in Written(body)) { volatileNames.Add(n); lengths.Remove(n); }
        }

        static string? Name(MExpr e) => e switch
        {
            AirLocalRef l => l.Name,
            AirParamRef p => p.Name,
            MCode c when e.GetType() == typeof(MCode) => c.Text,
            _ => null,
        };

        /// <summary>Every name assigned anywhere inside (any depth, lambdas included).</summary>
        static HashSet<string> Written(List<MStmt> body)
        {
            var w = new HashSet<string>();
            void S(List<MStmt>? b)
            {
                foreach (var s in b ?? [])
                {
                    switch (s)
                    {
                        case MDecl d: w.Add(d.Name); if (d.Init != null) E(d.Init); break;
                        case MAssign a: if (Name(a.Target) is { } an) w.Add(an); E(a.Target); E(a.Value); break;
                        case MDeconstruct dc: foreach (var dn in dc.Names) w.Add(dn); E(dc.Value); break;
                        case MFor f: w.Add(f.Var); E(f.Start); E(f.End); S(f.Body); break;
                        case MForeach fe: foreach (var fv in fe.Vars) w.Add(fv); E(fe.Coll); S(fe.Body); break;
                        case MUsing u: w.Add(u.Name); E(u.Value); S(u.Body); break;
                        case MIf i: E(i.Cond); S(i.Then); S(i.Else); break;
                        case MWhile wh: E(wh.Cond); S(wh.Body); break;
                        case MBlock bl: S(bl.Body); break;
                        case MTry t: S(t.Body); foreach (var c in t.Catches) { if (c.Name != null) w.Add(c.Name); S(c.Body); } S(t.Finally); break;
                        case MExprStmt es: E(es.E); break;
                        case MReturn r: if (r.Value != null) E(r.Value); break;
                        case MThrow th: E(th.Value); break;
                        case AirSwitch sw: E(sw.Value); foreach (var c in sw.Cases) S(c); S(sw.Default); break;
                    }
                }
            }
            void E(MExpr e)
            {
                switch (e)
                {
                    case MLambda l: if (l.ExprBody != null) E(l.ExprBody); S(l.Body); return;
                    case AirSeq q: S(q.Pre); E(q.Value); return;
                    case MArg { Prefix: var pre } a when pre.Contains("ref") || pre.Contains("out"): if (Name(a.E) is { } n) w.Add(n); break;
                    case MUnary { Op: AirUnOp.Inc or AirUnOp.Dec or AirUnOp.AddressOf } u: if (Name(u.E) is { } un) w.Add(un); break;
                }
                foreach (var c in Children(e)) E(c);
            }
            S(body);
            return w;
        }

        static IEnumerable<MExpr> Children(MExpr e) => e switch
        {
            MCall c => c.Target != null ? c.Args.Prepend(c.Target) : c.Args,
            MMember m => [m.Target],
            MInvoke i => i.Args.Prepend(i.Target),
            MNew n => n.Init != null ? n.Args.Concat(n.Init) : n.Args,
            MNewArray a => (a.Size != null ? [a.Size] : Array.Empty<MExpr>()).Concat(a.Items ?? []),
            MIndex ix => ix.Args.Prepend(ix.Target),
            MBinary b => [b.L, b.R],
            MUnary u => [u.E],
            MCond c => [c.C, c.A, c.B],
            MCast c => [c.E],
            MIs i => [i.E],
            MInterp ip => ip.Holes,
            MTuple t => t.Items,
            MPair p => [p.K, p.V],
            MArg a => [a.E],
            AirSeq q => [q.Value],
            AirAs aa => [aa.E],
            AirParExpr p => p.Children(),
            _ => [],
        };

        static long? FixedLength(MExpr? init) => init switch
        {
            AirRent { Args: [AirConst c, ..] } when Literal(c) is { } n && n >= 0 && n <= int.MaxValue => (long)n,
            MNewArray { Size: AirConst c, Items: null } when Literal(c) is { } n && n >= 0 && n <= int.MaxValue => (long)n,
            MNewArray { Size: null, Items: { } items } => items.Count,
            _ => null,
        };

        // ---- translation to symbolic values

        S Sym(MExpr e, State st, int depth = 0)
        {
            switch (e)
            {
                case AirConst c when Literal(c) is { } v: return new SConst(v);
                // plain text leaves (the unroller's copies): integer literals and local names
                case MCode pc when e.GetType() == typeof(MCode) && TextLiteral(pc.Text) is { } tv: return new SConst(tv);
                case MCode pn when e.GetType() == typeof(MCode) && IsIdent(pn.Text):
                    if (KindOf(pn.Type) is { } pk) kinds.TryAdd(pn.Text, pk);
                    return Var(pn.Text, st, depth);
                case AirLocalRef { Narrowed: false } l: return Var(l.Name, st, depth);
                case AirParamRef p: return Var(p.Name, st, depth);
                case AirLength { Target: AirLocalRef { Narrowed: false } t, Member: "Length" } when lengths.TryGetValue(t.Name, out var n): return new SConst(n);
                case MMember { Name: "Length" } mm when ArrayName(mm.Target) is { } an && lengths.TryGetValue(an, out var n2): return new SConst(n2);
                case MBinary b:
                    {
                        var k = KindOf(b.Type) ?? KindOfS(Sym(b.L, st, depth), st) ?? PrimKind.Int;
                        if (b.Op is AirBinOp.And or AirBinOp.Or or AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge) k = PrimKind.Int;
                        return new SBin(b.Op, Sym(b.L, st, depth), Sym(b.R, st, depth), b.Checked && uncheckedDepth == 0, k);
                    }
                case MCall { Name: "unchecked", Target: null, Args: [var x] }: return Sym(x, st, depth);
                case MCall { Target: { } t, Args: [] } c when ConvKind(c.Name) is { } ck:
                    return new SConv(Sym(t, st, depth), ck, !c.Name.EndsWith("_b", StringComparison.Ordinal));
                case MCall { Target: MCode { Text: "global::Alice.Runtime.Branchless" }, Name: "I32" or "U32" or "I64" or "U64", Args: [var c0, var a0, var b0] }:
                    return new SSel(Sym(c0, st, depth), Sym(a0, st, depth), Sym(b0, st, depth));
                case MCond c: return new SSel(Sym(c.C, st, depth), Sym(c.A, st, depth), Sym(c.B, st, depth));
                case MUnary { Op: AirUnOp.Not } u: return new SNot(Sym(u.E, st, depth));
                case MUnary { Op: AirUnOp.Neg } u:
                    return new SBin(AirBinOp.Sub, new SConst(0), Sym(u.E, st, depth), u.Checked && uncheckedDepth == 0, KindOf(u.Type) ?? PrimKind.Int);
                case AirSeq { Pre.Count: 0 } q: return Sym(q.Value, st, depth);
            }
            return new STop(KindOf(e.Type));
        }

        static Int128? TextLiteral(string t) => t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '-' && t.Length > 1 && char.IsDigit(t[1]))
            ? Literal(new AirConst(AirConstKind.Int, t)) : null;

        static bool IsIdent(string t) => t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

        S Var(string name, State st, int depth)
        {
            if (volatileNames.Contains(name)) return new STop(kinds.TryGetValue(name, out var vk) ? vk : null);
            S? def = depth < 6 && st.Defs.TryGetValue(name, out var d) ? d : null;
            return new SVar(name, def);
        }

        PrimKind? KindOfS(S s, State st) => s switch
        {
            SVar v => kinds.TryGetValue(v.Name, out var k) ? k : null,
            SBin b => b.Kind,
            SConv c => c.Kind,
            STop t => t.Kind,
            SSel sel => KindOfS(sel.A, st),
            _ => null,
        };

        // ---- evaluation

        Iv? Eval(S s, State st, int splits = 3)
        {
            if (splits > 0 && FirstSel(s) is { } sel)
            {
                var t = Refine(st, sel.Cond, true);
                var f = Refine(st, sel.Cond, false);
                Iv? a = t == null ? null : Eval(Subst(s, sel, sel.A), t, splits - 1);
                Iv? b = f == null ? null : Eval(Subst(s, sel, sel.B), f, splits - 1);
                if (t != null && a == null || f != null && b == null) return null;
                return a is { } x ? (b is { } y ? x.Union(y) : x) : b;
            }
            return Plain(s, st);
        }

        Iv? Plain(S s, State st)
        {
            switch (s)
            {
                case SConst c: return new Iv(c.V, c.V);
                case STop t: return Range(t.Kind);
                case SVar v:
                    {
                        Iv? r = st.Env.TryGetValue(v.Name, out var e) ? e : Range(kinds.TryGetValue(v.Name, out var k) ? k : null);
                        if (v.Def != null && Plain(v.Def, st) is { } dv) r = r is { } rr ? Intersect(rr, dv) : dv;
                        return r;
                    }
                case SConv c:
                    {
                        var k = Range(c.Kind)!.Value;
                        var x = Plain(c.E, st);
                        if (x is { } xv && xv.Within(k)) return xv;
                        return c.Checked && x is { } cx ? Intersect(cx, k) : k;
                    }
                case SNot n: return new Iv(0, 1);
                case SSel sel:
                    {
                        var a = Plain(sel.A, st);
                        var b = Plain(sel.B, st);
                        return a is { } x && b is { } y ? x.Union(y) : null;
                    }
                case SBin b: return Bin(b, st);
            }
            return null;
        }

        static Iv? Intersect(Iv a, Iv b)
        {
            var lo = Int128.Max(a.Lo, b.Lo);
            var hi = Int128.Min(a.Hi, b.Hi);
            return lo <= hi ? new Iv(lo, hi) : null;
        }

        Iv? Bin(SBin b, State st)
        {
            if (b.Op is AirBinOp.And or AirBinOp.Or or AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge)
                return new Iv(0, 1);
            var k = Range(b.Kind);
            if (k == null) return null;
            Iv? exact = Exact(b, st);
            if (exact is not { } ev) return k;
            if (ev.Within(k.Value)) return ev;
            // out of the type's range: a checked operation throws (the access is not reached), a wrapping one is anything
            return b.Checked ? Intersect(ev, k.Value) : k;
        }

        /// <summary>The mathematical result interval of <paramref name="b"/> (before any wrap or overflow check).</summary>
        Iv? Exact(SBin b, State st)
        {
            var l = Plain(b.L, st);
            var r = Plain(b.R, st);
            Iv? exact = null;
            if (l is { } x && r is { } y)
            {
                switch (b.Op)
                {
                    case AirBinOp.Add: exact = new(x.Lo + y.Lo, x.Hi + y.Hi); break;
                    case AirBinOp.Sub: exact = new(x.Lo - y.Hi, x.Hi - y.Lo); break;
                    case AirBinOp.Mul:
                        {
                            Int128[] p = [x.Lo * y.Lo, x.Lo * y.Hi, x.Hi * y.Lo, x.Hi * y.Hi];
                            exact = new(p.Min(), p.Max());
                            break;
                        }
                    case AirBinOp.Div when y.Lo > 0: exact = new(Int128.Min(x.Lo / y.Lo, x.Lo / y.Hi), Int128.Max(x.Hi / y.Lo, x.Hi / y.Hi)); break;
                    case AirBinOp.Mod when y.Lo > 0: exact = x.Lo >= 0 ? new(0, Int128.Min(x.Hi, y.Hi - 1)) : new(-(y.Hi - 1), y.Hi - 1); break;
                    case AirBinOp.BitAnd when y.Lo >= 0 && y.Lo == y.Hi: exact = new(0, x.Lo >= 0 ? Int128.Min(x.Hi, y.Hi) : y.Hi); break;
                    case AirBinOp.BitAnd when x.Lo >= 0 && x.Lo == x.Hi: exact = new(0, y.Lo >= 0 ? Int128.Min(y.Hi, x.Hi) : x.Hi); break;
                    case AirBinOp.BitAnd when x.Lo >= 0 && y.Lo >= 0: exact = new(0, Int128.Min(x.Hi, y.Hi)); break;
                    case AirBinOp.BitOr or AirBinOp.BitXor when x.Lo >= 0 && y.Lo >= 0: exact = new(0, Pow2Above(Int128.Max(x.Hi, y.Hi)) - 1); break;
                    case AirBinOp.Shr or AirBinOp.UShr when x.Lo >= 0 && y.Lo == y.Hi && y.Lo >= 0 && y.Lo < 64: exact = new(x.Lo >> (int)y.Lo, x.Hi >> (int)y.Hi); break;
                    case AirBinOp.Shl when x.Lo >= 0 && y.Lo == y.Hi && y.Lo >= 0 && y.Lo < 32: exact = new(x.Lo << (int)y.Lo, x.Hi << (int)y.Hi); break;
                }
            }
            return exact;
        }

        /// <summary>Whether a checked `+ - *` of integral <paramref name="kind"/> cannot overflow in <paramref name="st"/>.</summary>
        bool NoOverflow(AirBinOp op, S l, S r, PrimKind kind, State st) =>
            Range(kind) is { } k && Exact(new SBin(op, l, r, true, kind), st) is { } ev && ev.Within(k);

        static bool Integral(AirType? t) => KindOf(t) is not null;

        static Int128 Pow2Above(Int128 v)
        {
            Int128 p = 1;
            while (p <= v) p <<= 1;
            return p;
        }

        static SSel? FirstSel(S s) => s switch
        {
            SSel sel => sel,
            SBin b => FirstSel(b.L) ?? FirstSel(b.R),
            SConv c => FirstSel(c.E),
            SVar { Def: { } d } => FirstSel(d),
            _ => null,
        };

        static S Subst(S s, SSel sel, S with) => s switch
        {
            _ when ReferenceEquals(s, sel) => with,
            SBin b => b with { L = Subst(b.L, sel, with), R = Subst(b.R, sel, with) },
            SConv c => c with { E = Subst(c.E, sel, with) },
            SVar { Def: { } d } v => v with { Def = Subst(d, sel, with) },
            _ => s,
        };

        // ---- conditions

        /// <summary>The state where <paramref name="cond"/> has value <paramref name="truth"/>; null when impossible.</summary>
        State? Refine(State st, S cond, bool truth)
        {
            switch (cond)
            {
                case SConst c: return (c.V != 0) == truth ? st : null;
                case SNot n: return Refine(st, n.E, !truth);
                case SBin { Op: AirBinOp.And } a when truth:
                    return Refine(st, a.L, true) is { } l ? Refine(l, a.R, true) : null;
                case SBin { Op: AirBinOp.Or } o when !truth:
                    return Refine(st, o.L, false) is { } l2 ? Refine(l2, o.R, false) : null;
                case SBin { Op: AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge } c:
                    {
                        var op = truth ? c.Op : Negate(c.Op);
                        if (Decide(op, Plain(c.L, st), Plain(c.R, st)) is { } known) return known ? st : null;
                        var r = st;
                        if (Root(c.L) is { } lv) r = Constrain(r, lv, op, Plain(c.R, r));
                        if (r != null && Root(c.R) is { } rv) r = Constrain(r, rv, Flip(op), Plain(c.L, r));
                        return r;
                    }
            }
            return st;
        }

        /// <summary>The comparison's value when the intervals decide it (`0 &gt; 0` in an unrolled copy), else null.</summary>
        static bool? Decide(AirBinOp op, Iv? l, Iv? r)
        {
            if (l is not { } a || r is not { } b) return null;
            return op switch
            {
                AirBinOp.Lt => a.Hi < b.Lo ? true : a.Lo >= b.Hi ? false : null,
                AirBinOp.Le => a.Hi <= b.Lo ? true : a.Lo > b.Hi ? false : null,
                AirBinOp.Gt => a.Lo > b.Hi ? true : a.Hi <= b.Lo ? false : null,
                AirBinOp.Ge => a.Lo >= b.Hi ? true : a.Hi < b.Lo ? false : null,
                AirBinOp.Eq => a.Lo == a.Hi && b.Lo == b.Hi && a.Lo == b.Lo ? true : a.Hi < b.Lo || b.Hi < a.Lo ? false : null,
                AirBinOp.Ne => a.Hi < b.Lo || b.Hi < a.Lo ? true : a.Lo == a.Hi && b.Lo == b.Hi && a.Lo == b.Lo ? false : null,
                _ => null,
            };
        }

        /// <summary>The variable a comparison side is (through a checked conversion of it, which keeps its value).</summary>
        static SVar? Root(S s) => s switch
        {
            SVar v => v,
            SConv { Checked: true, E: SVar v2 } => v2,
            _ => null,
        };

        State? Constrain(State st, SVar v, AirBinOp op, Iv? bound)
        {
            if (bound is not { } b) return st;
            Iv? cur = Plain(v, st) ?? Range(kinds.TryGetValue(v.Name, out var k) ? k : null);
            if (cur is not { } c) return st;
            Iv? n = op switch
            {
                AirBinOp.Lt => Intersect(c, new Iv(c.Lo, b.Hi - 1)),
                AirBinOp.Le => Intersect(c, new Iv(c.Lo, b.Hi)),
                AirBinOp.Gt => Intersect(c, new Iv(b.Lo + 1, c.Hi)),
                AirBinOp.Ge => Intersect(c, new Iv(b.Lo, c.Hi)),
                AirBinOp.Eq => Intersect(c, b),
                AirBinOp.Ne when b.Lo == b.Hi && c.Lo == b.Lo => c.Lo == c.Hi ? null : new Iv(c.Lo + 1, c.Hi),
                AirBinOp.Ne when b.Lo == b.Hi && c.Hi == b.Lo => c.Lo == c.Hi ? null : new Iv(c.Lo, c.Hi - 1),
                _ => c,
            };
            if (n == null) return null;
            if (n == c) return st;
            var r = st.Clone();
            r.Env[v.Name] = n.Value;
            return r;
        }

        static AirBinOp Negate(AirBinOp op) => op switch
        {
            AirBinOp.Lt => AirBinOp.Ge, AirBinOp.Le => AirBinOp.Gt, AirBinOp.Gt => AirBinOp.Le, AirBinOp.Ge => AirBinOp.Lt,
            AirBinOp.Eq => AirBinOp.Ne, AirBinOp.Ne => AirBinOp.Eq, _ => op,
        };

        static AirBinOp Flip(AirBinOp op) => op switch
        {
            AirBinOp.Lt => AirBinOp.Gt, AirBinOp.Le => AirBinOp.Ge, AirBinOp.Gt => AirBinOp.Lt, AirBinOp.Ge => AirBinOp.Le, _ => op,
        };

        // ---- states

        static State? Join(State? a, State? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            var r = new State();
            foreach (var (n, iv) in a.Env) if (b.Env.TryGetValue(n, out var jv)) r.Env[n] = iv.Union(jv);
            foreach (var (n, d) in a.Defs) if (b.Defs.TryGetValue(n, out var e) && Equals(d, e)) r.Defs[n] = d;
            return r;
        }

        State Widen(State old, State next)
        {
            var r = next.Clone();
            foreach (var (n, iv) in next.Env)
            {
                if (!old.Env.TryGetValue(n, out var o)) continue;
                var k = Range(kinds.TryGetValue(n, out var kk) ? kk : null);
                if (k == null) { r.Env.Remove(n); continue; }
                r.Env[n] = new Iv(iv.Lo < o.Lo ? k.Value.Lo : iv.Lo, iv.Hi > o.Hi ? k.Value.Hi : iv.Hi);
            }
            return r;
        }

        static bool Same(State a, State b)
        {
            if (a.Env.Count != b.Env.Count || a.Defs.Count != b.Defs.Count) return false;
            foreach (var (n, iv) in a.Env) if (!b.Env.TryGetValue(n, out var jv) || iv != jv) return false;
            foreach (var (n, d) in a.Defs) if (!b.Defs.TryGetValue(n, out var e) || !Equals(d, e)) return false;
            return true;
        }

        /// <summary><paramref name="name"/> := <paramref name="value"/>: its interval, its definition, and the
        /// definitions that read it are invalidated.</summary>
        State Assign(State st, string name, S? value)
        {
            var r = st.Clone();
            foreach (var (n, d) in st.Defs) if (Mentions(d, name)) r.Defs.Remove(n);
            r.Defs.Remove(name);
            r.Env.Remove(name);
            if (volatileNames.Contains(name) || value == null) return r;
            if (Plain(value, st) is { } iv)
            {
                if (kinds.TryGetValue(name, out var k) && Range(k) is { } kr) iv = Intersect(iv, kr) ?? kr;
                r.Env[name] = iv;
            }
            if (!Mentions(value, name) && Size(value) <= 24 && value is not (SConst or STop)) r.Defs[name] = value;
            return r;
        }

        static bool Mentions(S s, string name) => s switch
        {
            SVar v => v.Name == name || v.Def != null && Mentions(v.Def, name),
            SBin b => Mentions(b.L, name) || Mentions(b.R, name),
            SSel sel => Mentions(sel.Cond, name) || Mentions(sel.A, name) || Mentions(sel.B, name),
            SNot n => Mentions(n.E, name),
            SConv c => Mentions(c.E, name),
            _ => false,
        };

        static int Size(S s) => s switch
        {
            SVar v => 1 + (v.Def != null ? Size(v.Def) : 0),
            SBin b => 1 + Size(b.L) + Size(b.R),
            SSel sel => 1 + Size(sel.Cond) + Size(sel.A) + Size(sel.B),
            SNot n => 1 + Size(n.E),
            SConv c => 1 + Size(c.E),
            _ => 1,
        };

        // ---- statements

        State? Walk(List<MStmt>? body, State? st)
        {
            foreach (var s in body ?? [])
            {
                if (st == null) return null;   // unreachable rest: nothing is proven there
                st = Stmt(s, st);
            }
            return st;
        }

        State? Stmt(MStmt s, State st)
        {
            switch (s)
            {
                case MDecl d:
                    if (d.Init != null) Visit(d.Init, st);
                    if ((KindOfCs(d.TypeCs) ?? KindOf(d.Init?.Type)) is { } dk) kinds.TryAdd(d.Name, dk);
                    return Assign(st, d.Name, d.Init != null ? Sym(d.Init, st) : kinds.ContainsKey(d.Name) ? new SConst(0) : null);
                case MAssign a:
                    {
                        Visit(a.Value, st);
                        if (a.Target is AirLocalRef { Narrowed: false } or AirParamRef || a.Target.GetType() == typeof(MCode) && IsIdent(((MCode)a.Target).Text))
                        {
                            string n = Name(a.Target)!;
                            if (KindOf(a.Target.Type) is { } tk) kinds.TryAdd(n, tk);
                            S? v = a.Op == "=" ? Sym(a.Value, st)
                                : CompoundOp(a.Op) is { } op && kinds.TryGetValue(n, out var k)
                                    ? new SBin(op, Var(n, st, 0), Sym(a.Value, st), uncheckedDepth == 0, k)
                                    : null;
                            if (WantOverflow && uncheckedDepth == 0 && a.Op is "+=" or "-=" or "*=" && v is SBin cb && Integral(a.Target.Type) && !volatileNames.Contains(n))
                                Compound[a] = (!Compound.TryGetValue(a, out var cprev) || cprev) && NoOverflow(cb.Op, cb.L, cb.R, cb.Kind, st);
                            return Assign(st, n, v);
                        }
                        Visit(a.Target, st);
                        return Name(a.Target) is { } other ? Assign(st, other, null) : st;
                    }
                case MExprStmt es: Visit(es.E, st); return st;
                case MDeconstruct dc:
                    Visit(dc.Value, st);
                    foreach (var n in dc.Names) st = Assign(st, n, null);
                    return st;
                case MIf i:
                    {
                        Visit(i.Cond, st);
                        var c = Sym(i.Cond, st, 6);
                        var t = Refine(st, c, true) is { } ts ? Walk(i.Then, ts) : null;
                        var e = Refine(st, c, false) is { } fs ? Walk(i.Else, fs) : null;
                        return Join(t, e);
                    }
                case MWhile w: return Loop(w.Cond, null, w.Body, st);
                case MFor f:
                    {
                        Visit(f.Start, st);
                        if (!UnitStep(f) || f.Cmp is not ("<" or "<=")) return Opaque(s, st);
                        var init = Assign(st, f.Var, Sym(f.Start, st));
                        var cond = new MBinary { Op = f.Cmp == "<" ? AirBinOp.Lt : AirBinOp.Le, L = new AirLocalRef(f.Var, f.Start.Type), R = f.End };
                        var exit = Loop(cond, f.Var, f.Body, init);
                        return exit == null ? null : Assign(exit, f.Var, null);
                    }
                case MForeach fe:
                    Visit(fe.Coll, st);
                    foreach (var n in fe.Vars) st = Assign(st, n, null);
                    return Loop(null, null, fe.Body, st, mayExit: true);
                case MReturn r: if (r.Value != null) Visit(r.Value, st); return null;
                case MThrow t: Visit(t.Value, st); return null;
                case MBreak:
                    if (loops.Count == 0) throw new Bail();
                    loops.Peek().Breaks.Add(st);
                    return null;
                case MContinue:
                    {
                        var target = loops.FirstOrDefault(l => !l.IsSwitch) ?? throw new Bail();
                        target.Continues.Add(st);
                        return null;
                    }
                case MBlock b:
                    {
                        if (b.Lock != null) Visit(b.Lock, st);
                        if (b.Unchecked) uncheckedDepth++;
                        try { return Walk(b.Body, st); }
                        finally { if (b.Unchecked) uncheckedDepth--; }
                    }
                case MUsing u: Visit(u.Value, st); return Walk(u.Body, Assign(st, u.Name, null));
                case MTry tr:
                    {
                        var all = Written([tr]);
                        State Havoc(State x) { foreach (var n in all) x = Assign(x, n, null); return x; }
                        var after = Walk(tr.Body, st);
                        State? res = after;
                        foreach (var c in tr.Catches) res = Join(res, Walk(c.Body, Havoc(st)));
                        if (tr.Finally != null)
                        {
                            Walk(tr.Finally, Havoc(st));
                            res = res == null ? null : Havoc(res);
                        }
                        return res;
                    }
                case AirSwitch sw:
                    {
                        Visit(sw.Value, st);
                        var ctx = new LoopCtx { IsSwitch = true };
                        loops.Push(ctx);
                        State? res = null;
                        try
                        {
                            foreach (var c in sw.Cases) res = Join(res, Walk(c, st));
                            res = Join(res, sw.Default != null ? Walk(sw.Default, st) : st);
                        }
                        finally { loops.Pop(); }
                        foreach (var b in ctx.Breaks) res = Join(res, b);
                        return res;
                    }
            }
            throw new Bail();
        }

        /// <summary>`v++`, `v += 1` or `v = unchecked(v + 1)` (the lowerer's spelling for proven loops).</summary>
        static bool UnitStep(MFor f) =>
            f.Step.Replace(" ", "") is var st && (st == "++" || st == "+=1" || st == "=unchecked(" + f.Var + "+1)" || st == "=" + f.Var + "+1");

        /// <summary>A statement whose effect is not modelled: everything it writes is unknown afterwards, and no
        /// access inside it is proven.</summary>
        State Opaque(MStmt s, State st)
        {
            foreach (var n in Written([s])) st = Assign(st, n, null);
            return st;
        }

        static AirBinOp? CompoundOp(string op) => op switch
        {
            "+=" => AirBinOp.Add, "-=" => AirBinOp.Sub, "*=" => AirBinOp.Mul, "/=" => AirBinOp.Div, "%=" => AirBinOp.Mod,
            "&=" => AirBinOp.BitAnd, "|=" => AirBinOp.BitOr, "^=" => AirBinOp.BitXor, "<<=" => AirBinOp.Shl, ">>=" => AirBinOp.Shr,
            _ => null,
        };

        /// <summary>A loop: fixpoint of the head state (widened after two rounds), the body walked in the stable
        /// state last, so the recorded proofs hold for every iteration.</summary>
        State? Loop(MExpr? cond, string? counter, List<MStmt> body, State st, bool mayExit = false)
        {
            var head = st;
            LoopCtx ctx;
            for (int round = 0; ; round++)
            {
                if (round > 24) throw new Bail();
                ctx = new LoopCtx();
                loops.Push(ctx);
                State? bodyIn;
                string? bare = cond is MBinary { L: AirLocalRef cl } ? cl.Name : counter;
                if (bare != null) counters.Add(bare);
                try
                {
                    // the condition runs before every iteration: its accesses are proven in the head state
                    if (cond != null) Visit(cond, head);
                    bodyIn = cond != null ? Refine(head, Sym(cond, head, 6), true) : head;
                    var output = bodyIn == null ? null : Walk(body, bodyIn);
                    foreach (var c in ctx.Continues) output = Join(output, c);
                    if (counter != null && output != null && kinds.TryGetValue(counter, out var ck))
                        output = Assign(output, counter, new SBin(AirBinOp.Add, Var(counter, output, 0), new SConst(1), false, ck));
                    var next = Join(head, output)!;
                    if (round >= 2) next = Widen(head, next);
                    if (Same(next, head)) break;
                    head = next;
                }
                finally
                {
                    loops.Pop();
                    if (bare != null) counters.RemoveAt(counters.Count - 1);
                }
            }
            State? exit = cond != null ? Refine(head, Sym(cond, head, 6), false) : mayExit ? head : null;
            foreach (var b in ctx.Breaks) exit = Join(exit, b);
            return exit;
        }

        // ---- expressions: find the accesses and prove them in the current state

        static string? ArrayName(MExpr e) => e switch
        {
            AirLocalRef { Narrowed: false } l => l.Name,
            MCode c when e.GetType() == typeof(MCode) && IsIdent(c.Text) => c.Text,
            _ => null,
        };

        void Visit(MExpr e, State st)
        {
            switch (e)
            {
                case MIndex { Args: [var idx] } ix when ArrayName(ix.Target) is { } arr && lengths.TryGetValue(arr, out var len):
                    {
                        Visit(idx, st);
                        if (Name(idx) is { } bareIv && counters.Contains(bareIv)) return;   // R1: the JIT's own shape
                        bool ok = Eval(Sym(idx, st), st) is { } iv && iv.Lo >= 0 && iv.Hi < len;
                        Results[ix] = (!Results.TryGetValue(ix, out var prev) || prev) && ok;
                        return;
                    }
                case MCond c:
                    {
                        Visit(c.C, st);
                        var cs = Sym(c.C, st, 6);
                        if (Refine(st, cs, true) is { } t) Visit(c.A, t);
                        if (Refine(st, cs, false) is { } f) Visit(c.B, f);
                        return;
                    }
                case MBinary { Op: AirBinOp.Add or AirBinOp.Sub or AirBinOp.Mul, Checked: true } ob when WantOverflow && uncheckedDepth == 0 && KindOf(ob.Type) is { } ok:
                    {
                        Visit(ob.L, st);
                        Visit(ob.R, st);
                        bool fits = NoOverflow(ob.Op, Sym(ob.L, st), Sym(ob.R, st), ok, st);
                        Overflow[ob] = (!Overflow.TryGetValue(ob, out var oprev) || oprev) && fits;
                        return;
                    }
                case MBinary { Op: AirBinOp.And or AirBinOp.Or } b:
                    {
                        Visit(b.L, st);
                        if (Refine(st, Sym(b.L, st, 6), b.Op == AirBinOp.And) is { } r) Visit(b.R, r);
                        return;
                    }
                case MLambda: return;          // other frames: nothing proven inside
                case AirSeq q:
                    {
                        // its statements only write volatile names (prescan); walk them for the accesses they hold
                        var inner = Walk(q.Pre, st);
                        if (inner != null) Visit(q.Value, inner);
                        return;
                    }
            }
            foreach (var c in Children(e)) Visit(c, st);
        }
    }
}
