using System.Globalization;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Connection-Machine shapes (ADR 0039): counted loops `for i in 0...n` whose body is a scan, a segmented scan, a
/// combine (histogram / scatter-add), a compaction or a permutation lower to the runtime kernels of
/// <c>Alice.Runtime.Cm</c> (nodes in Mir/MExpr.Cm.cs). Tried after <see cref="TryParallelFor"/> (a proven-independent
/// loop keeps the `par for` lowering) and before the serial path; `cm for` demands one of the shapes (AL0247).
/// <para>
/// The matcher classifies every statement of the body, as LoopBody does, or declines: leading pure body-local
/// definitions, then exactly one shape. An element expression e(i) that is not a plain array read is <em>fused</em>:
/// a map pass (a synthetic loop over the same HIR, so <see cref="TryParallelFor"/> parallelizes it when it pays)
/// materializes it into the destination (scans, then scanned in place) or into pooled scratch (keys, values, flags).
/// </para>
/// <para>
/// Results never change silently. Inferred shapes are integer-only with wrapping or bitwise operators (`&amp;+ &amp;* &amp; | ^`:
/// exact under any association; checked `+` could trap where the kernel wraps), or exact copies (compaction,
/// permutation), and keep the serial loop as the fallback for every case the kernel cannot reproduce (a short trip
/// count, aliasing arrays, a nonzero compaction start, a too-short destination, a duplicate permutation target).
/// Under `cm for` floats are admitted with the fixed-chunking contract (bit-identical for any worker count, not the
/// serial fold), integer `+` and `*` wrap like `&amp;+`, a float combine needs `cm ordered for` (AL0248; it then runs
/// in index order) and a permutation needs a proof (AL0249).
/// </para>
/// </summary>
public sealed partial class Lowerer
{
    /// <summary>Inferred shapes below this trip count stay serial: the runtime's own serial cutoff (16K elements).</summary>
    const int CmThreshold = 16_384;

    /// <summary>Shape tags the matcher lowered, in order (diagnostics; tests read the emitted calls).</summary>
    internal readonly List<string> CmShapes = new();

    sealed class CmPlan
    {
        public ForStmt F = null!;
        public LocalSym I = null!;
        public readonly List<Stmt> Defs = new();
        public readonly HashSet<LocalSym> DefLocals = new();
        public string Kind = "";        // scan | exclusive | segmented | combine | compact | permute
        public bool Float, Checked;
        // scan / segmented
        public LocalSym? Acc, Out;
        public AirCmOp Op;
        public Syntax.Expr? Elem, Flag;
        public LocalSym? Src;           // library scan source (e(i) is src[i]); compaction source
        // combine
        public readonly List<(LocalSym H, Syntax.Expr K, Syntax.Expr? V, bool Ones, Stmt S)> Combines = new();
        // compaction
        public LocalSym? M;
        // permute
        public LocalSym? Perm;
        public readonly List<LocalSym> Written = new();
    }

    /// <summary>The CM lowering of <paramref name="f"/>, or null when it is not a CM shape (`cm for`: AL0247 instead).</summary>
    MStmt? TryCmFor(ForStmt f)
    {
        string? why = null;
        Stmt? at = null;
        var plan = MatchCm(f, ref why, ref at);
        if (plan == null)
        {
            if (!f.Cm) return null;
            int line = at?.Line > 0 ? at.Line : f.Line;
            throw new CompileException($"{File}:{line}:{Math.Max(1, at?.Col ?? f.Col)}: error AL0247: cannot lower `cm for`: {why ?? "the body is not a scan, combine, compaction or permutation"}");
        }
        if (f.Cm && plan.Float && plan.Kind == "combine" && !f.CmOrdered)
            throw new CompileException($"{File}:{f.Line}:{Math.Max(1, f.Col)}: error AL0248: combine on floats needs `ordered` (`cm ordered for`): the sum would depend on the block split");
        if (f.Cm && plan.Kind == "permute")
            throw new CompileException($"{File}:{f.Line}:{Math.Max(1, f.Col)}: error AL0249: permutation `{plan.Perm!.Name}` not provably in range and distinct (a plain `for` checks it at run time and falls back to the loop)");
        // `cm ordered for` over floats: index order is the serial loop
        if (f.CmOrdered && plan.Float) return SerialFor(f);
        CmShapes.Add(plan.Kind);
        return LowerCm(plan);
    }

    // ------------------------------------------------------------------ matcher

    CmPlan? MatchCm(ForStmt f, ref string? why, ref Stmt? at)
    {
        if (inParallelGen || ctx.Contains("lambda") || noHoist > 0 || soa != null || Method is { TypeParams.Count: > 0 } || Method?.IsAsync == true)
        { why = "inside a lambda, a generic or an async method"; return null; }
        if (f.Await || f.Iter is not RangeExpr r || f.Locals.Count != 1 || f.Locals[0].Type.Cs != "int")
        { why = "the loop must count an Int32 over a range (`for i in 0...n`)"; return null; }
        if (r.A is not LitExpr { Kind: "int" } la || LitValue(la) != 0) { why = "the range must start at 0"; return null; }
        var plan = new CmPlan { F = f, I = f.Locals[0] };
        var lp = new LoopPlan { LoopVar = plan.I };
        lp.Inner.Add(plan.I);
        if (!PureRead(r.B, lp)) { why = "the range end has an effect"; return null; }
        if (WalkStmts(f.Body).Any(s => s is Assign { Target: NameExpr { Sym: LocalSym c } } && c == plan.I)) { why = "the body assigns the loop counter"; return null; }

        int k = 0;
        for (; k < f.Body.Count; k++)
        {
            var s = f.Body[k];
            LocalSym? d = s switch
            {
                Assign { Declares: { } dl, Op: "=" } a when PureRead(a.Value, lp) => dl,
                LocalDecl { Local: { } ll, Value: { } v } when PureRead(v, lp) => ll,
                _ => null,
            };
            if (d == null) break;
            if (hoisted.Contains(d) || plan.DefLocals.Contains(d)) { why = $"`{d.Name}` is visible after the loop"; at = s; return null; }
            plan.Defs.Add(s);
            plan.DefLocals.Add(d);
            lp.Inner.Add(d);
        }
        var rest = f.Body.Skip(k).ToList();
        if (rest.Count == 0) { why = "the body has no scan, combine, compaction or permutation"; return null; }
        // a body local reassigned later is not a definition (and is not one of the shapes either)
        foreach (var s in rest)
            if (s is Assign a && Assigned(a) is { } w && plan.DefLocals.Contains(w)) { why = $"`{w.Name}` is assigned twice"; at = s; return null; }

        bool ok = rest switch
        {
            [IfStmt c] when IsCompaction(c, plan, lp, ref why) => true,
            [Assign p] when IsPermute(p, plan, lp) => true,
            _ when IsCombine(rest, plan, lp, ref why) => true,
            _ => IsScan(rest, plan, lp, ref why, ref at),
        };
        if (!ok)
        {
            at ??= rest.FirstOrDefault(s => s is not Assign) ?? rest[0];
            return null;
        }
        // integers wrap in the kernels: inferred shapes take only operators that wrap or never overflow
        if (!f.Cm && (plan.Float && plan.Kind is "scan" or "exclusive" or "segmented" or "combine" || plan.Checked))
        { why = plan.Float ? "a float scan or combine is not inferred (it would not equal the serial fold)" : "checked `+`/`*` may trap where the kernel wraps"; return null; }
        // every array the body reads beside the written ones: a local, never the written object (checked at run time)
        foreach (var w in plan.Written)
            if (stackArrays.ContainsKey(w) || w.Type.Elem is not { Kind: "prim" }) { why = $"`{w.Name}` is not a plain array"; return null; }
        foreach (var e in Walk(f.Body))
        {
            if (Callee(e) is { } m && !Overrides(m).All(o => ArraysOnlyThroughParams(o, new()))) { why = "a call reads arrays the loop cannot name"; return null; }
            if (e.Type?.Kind != "array") continue;
            if (e is NameExpr { Res: "local", Sym: LocalSym l } && !stackArrays.ContainsKey(l))
            {
                if (l.Type.Elem is not { Kind: "prim" } && plan.Written.Any(w => MayAlias(w.Type, l.Type))) { why = $"`{l.Name}` may hold the written array"; return null; }
                continue;
            }
            if (e is NewExpr or ArrayLit) continue;
            if (plan.Written.Any(w => MayAlias(w.Type, e.Type))) { why = "an array read through an expression may be the written one"; return null; }
        }
        return plan;
    }

    static readonly string[] CmElems = ["int", "uint", "long", "ulong", "float", "double"];

    static bool CmElem(AType t) => t.Kind == "prim" && CmElems.Contains(t.Cs);

    static bool IsLocal(Syntax.Expr? e, out LocalSym l)
    {
        l = null!;
        if (e is NameExpr { Res: "local", Sym: LocalSym s }) { l = s; return true; }
        return false;
    }

    static bool IsLocal(Syntax.Expr? e, LocalSym l) => IsLocal(e, out var x) && x == l;

    /// <summary>`arr[i]` with arr a local array (not one of the body's own locals).</summary>
    static bool AtCounter(Syntax.Expr? e, CmPlan plan, out LocalSym arr)
    {
        arr = null!;
        return e is IndexExpr { Slice: null, FromEnd: 0, TupleElement: null, Args: [{ Mod: "", Name: null, Value: var iv }] } ix
            && IsLocal(iv, plan.I) && IsLocal(ix.Target, out arr) && arr.Type.Kind == "array" && !plan.DefLocals.Contains(arr);
    }

    static bool Mentions(Syntax.Expr e, LocalSym l) => WalkExpr(e).Any(x => IsLocal(x, l));

    static bool MentionsAny(CmPlan plan, IEnumerable<Syntax.Expr> roots, params LocalSym?[] ls) =>
        roots.Concat(plan.Defs.SelectMany(DefValue)).Any(e => ls.Any(l => l != null && Mentions(e, l)));

    static IEnumerable<Syntax.Expr> DefValue(Stmt s) => s switch
    {
        Assign a => [a.Value],
        LocalDecl { Value: { } v } => [v],
        _ => [],
    };

    /// <summary>The value of a numeric literal (`0`, `1U`, `0.0`), or null.</summary>
    static double? LitValue(Syntax.Expr? e)
    {
        if (e is not LitExpr { Kind: "int" or "float" } l) return null;
        string t = l.Text.Replace("_", "").TrimEnd('u', 'U', 'l', 'L', 'f', 'F', 'd', 'D', 'm', 'M');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static AirCmOp? CmOpOf(string op) => op switch
    {
        "&+" or "+" or "+=" => AirCmOp.Add,
        "&*" or "*" or "*=" => AirCmOp.Mul,
        "&" or "&=" => AirCmOp.And,
        "|" or "|=" => AirCmOp.Or,
        "^" or "^=" => AirCmOp.Xor,
        _ => null,
    };

    static bool CheckedOp(string op, AType t) => t.IsInteger && op is "+" or "*" or "+=" or "*=";

    /// <summary>`acc = acc op e`, `acc = e op acc` or `acc op= e`: the element e, or null.</summary>
    static Syntax.Expr? Fold(Assign a, LocalSym acc, out AirCmOp op, out bool chk)
    {
        op = default;
        chk = false;
        if (!IsLocal(a.Target, acc)) return null;
        if (a.Op != "=")
        {
            if (CmOpOf(a.Op) is not { } o) return null;
            op = o;
            chk = CheckedOp(a.Op, acc.Type);
            return a.Value;
        }
        if (a.Value is not BinaryExpr b || CmOpOf(b.Op) is not { } bo || b.Res is "append" or "usercmp" or "coalesce") return null;
        op = bo;
        chk = CheckedOp(b.Op, acc.Type);
        if (IsLocal(b.L, acc) && !Mentions(b.R, acc)) return b.R;
        if (IsLocal(b.R, acc) && !Mentions(b.L, acc)) return b.L;
        return null;
    }

    bool IsScan(List<Stmt> rest, CmPlan plan, LoopPlan lp, ref string? why, ref Stmt? at)
    {
        IfStmt? seg = null;
        if (rest is [IfStmt sg, _, _]) { seg = sg; rest = rest.Skip(1).ToList(); }
        if (rest is not [Assign s1, Assign s2]) { why = "the body is not a scan, combine, compaction or permutation"; return false; }
        bool inclusive = s2 is { Op: "=", Target: IndexExpr, Value: NameExpr };
        var (upd, wr) = inclusive ? (s1, s2) : (s2, s1);
        if (!AtCounter(wr.Target, plan, out var outArr) || wr.Op != "=" || !IsLocal(wr.Value, out var acc) || plan.DefLocals.Contains(acc) || acc == plan.I)
        { why = "a scan writes `out[i] = acc` next to the `acc` update"; at = wr; return false; }
        if (!CmElem(acc.Type) || outArr.Type.Elem?.Cs != acc.Type.Cs) { why = $"`{acc.Name}` is not an Int32, UInt32, Int64, UInt64, Float32 or Float64 matching `{outArr.Name}`"; at = wr; return false; }
        var e = Fold(upd, acc, out var op, out bool chk);
        if (e == null || e.Type.Cs != acc.Type.Cs || !PureRead(e, lp)) { why = $"`{acc.Name}` is not updated by an associative operator (`+ * & | ^`, wrapping `&+ &*`) of a pure element"; at = upd; return false; }
        bool isFloat = acc.Type.Cs is "float" or "double";
        if (isFloat && op is AirCmOp.And or AirCmOp.Or or AirCmOp.Xor) { why = "bitwise operators need integers"; at = upd; return false; }
        if (seg != null)
        {
            if (!inclusive) { why = "a segmented scan writes after the update"; at = seg; return false; }
            double id = op == AirCmOp.Mul ? 1 : 0;
            if (seg is not { Conds: [var c], Bodies: [[Assign { Op: "=" } reset]], Else: null } || !IsLocal(reset.Target, acc) || LitValue(reset.Value) != id
                || op is AirCmOp.And || !c.Type.IsBool || !PureRead(c, lp))
            { why = $"a segment boundary resets `{acc.Name}` to the operator's identity in `if flag(i) then {acc.Name} = {id} end`"; at = seg; return false; }
            plan.Flag = c;
        }
        // the element must not see the running value or the destination (except reading `out[i]` itself, in place)
        bool inPlace = AtCounter(e, plan, out var src0) && src0 == outArr;
        var roots = new List<Syntax.Expr> { e };
        if (plan.Flag != null) roots.Add(plan.Flag);
        if (MentionsAny(plan, roots, acc) || !inPlace && MentionsAny(plan, roots, outArr) || inPlace && (plan.Defs.Count > 0 || plan.Flag != null && Mentions(plan.Flag, outArr)))
        { why = $"the element reads `{acc.Name}` or `{outArr.Name}` (a loop-carried dependency the kernel does not model)"; at = upd; return false; }
        plan.Kind = seg != null ? "segmented" : inclusive ? "scan" : "exclusive";
        plan.Acc = acc;
        plan.Out = outArr;
        plan.Op = op;
        plan.Elem = e;
        plan.Float = isFloat;
        plan.Checked = chk;
        if (plan.Defs.Count == 0 && seg == null && AtCounter(e, plan, out var src) && src.Type.Elem?.Cs == acc.Type.Cs) plan.Src = src;
        plan.Written.Add(outArr);
        return true;
    }

    /// <summary>Every statement is `h[k(i)] = h[k(i)] &amp;+ v(i)` (or `+=`) on its own target.</summary>
    bool IsCombine(List<Stmt> rest, CmPlan plan, LoopPlan lp, ref string? why)
    {
        foreach (var s in rest)
            if (s is not Assign a || !IsCombineStmt(a, plan, lp, ref why))
            {
                plan.Combines.Clear();
                plan.Float = plan.Checked = false;
                return false;
            }
        plan.Kind = "combine";
        plan.Written.AddRange(plan.Combines.Select(c => c.H));
        return true;
    }

    bool IsCombineStmt(Assign a, CmPlan plan, LoopPlan lp, ref string? why)
    {
        if (a.Target is not IndexExpr { Slice: null, FromEnd: 0, TupleElement: null, Args: [{ Mod: "", Name: null, Value: { } key }] } t || !IsLocal(t.Target, out var h)
            || h.Type.Kind != "array" || plan.DefLocals.Contains(h) || h.Type.Elem is not { } he || !CmElem(he))
            return false;
        Syntax.Expr? v = null;
        bool chk;
        if (a.Op == "+=") { v = a.Value; chk = he.IsInteger; }
        else if (a.Op == "=" && a.Value is BinaryExpr { Op: "&+" or "+" } b && b.Res is not ("append" or "usercmp"))
        {
            chk = b.Op == "+" && he.IsInteger;
            bool Self(Syntax.Expr x) => x is IndexExpr { Args: [{ Mod: "", Value: { } k2 }] } ix && IsLocal(ix.Target, h) && CmSame(k2, key);
            if (Self(b.L)) v = b.R; else if (Self(b.R)) v = b.L;
        }
        else return false;
        if (v == null || key.Type.Cs is not ("int" or "uint") || !PureRead(key, lp) || !PureRead(v, lp)) return false;
        // `h[base + i]`: distinct cells, an elementwise update (the `par for` and vectorizer shapes), not a combine
        if (IsLocal(key, plan.I) || key is BinaryExpr { Op: "+" or "&+" or "-" or "&-" } kb
            && (IsLocal(kb.L, plan.I) && Invariant(kb.R, plan) || IsLocal(kb.R, plan.I) && Invariant(kb.L, plan))) return false;
        bool ones = he.IsInteger && LitValue(v) == 1;
        if (!ones && v.Type.Cs != he.Cs) return false;
        if (plan.Combines.Any(c => c.H == h) || plan.Defs.SelectMany(DefValue).Append(key).Append(v).Any(x => Mentions(x, h))
            || plan.Combines.Any(c => Mentions(c.K, h) || c.V != null && Mentions(c.V, h) || Mentions(key, c.H) || Mentions(v, c.H)))
        { why = $"the combine target `{h.Name}` is read in the loop"; return false; }
        plan.Combines.Add((h, key, ones ? null : v, ones, a));
        plan.Float |= he.Cs is "float" or "double";
        plan.Checked |= chk;
        return true;
    }

    /// <summary>Mentions neither the counter nor a body local.</summary>
    static bool Invariant(Syntax.Expr e, CmPlan plan) => !WalkExpr(e).Any(x => IsLocal(x, out var l) && (l == plan.I || plan.DefLocals.Contains(l)));

    /// <summary>Structural equality of two pure index expressions (`h[k[i]] = h[k[i]] &amp;+ 1` written out by hand).</summary>
    static bool CmSame(Syntax.Expr a, Syntax.Expr b)
    {
        if (ReferenceEquals(a, b)) return true;
        return (a, b) switch
        {
            (NameExpr x, NameExpr y) => x.Res == y.Res && x.Sym != null && x.Sym == y.Sym,
            (LitExpr x, LitExpr y) => x.Kind == y.Kind && x.Text == y.Text,
            (IndexExpr x, IndexExpr y) => x.Slice == y.Slice && x.FromEnd == y.FromEnd && x.Args.Count == y.Args.Count && CmSame(x.Target, y.Target)
                && x.Args.Zip(y.Args).All(p => p.First.Mod == p.Second.Mod && p.First.Value != null && p.Second.Value != null && CmSame(p.First.Value, p.Second.Value)),
            (BinaryExpr x, BinaryExpr y) => x.Op == y.Op && x.Res == y.Res && CmSame(x.L, y.L) && CmSame(x.R, y.R),
            (UnaryExpr x, UnaryExpr y) => x.Op == y.Op && CmSame(x.E, y.E),
            (CallExpr x, CallExpr y) => x.Res == y.Res && x.Sym != null && Equals(x.Sym, y.Sym) && x.Name == y.Name && x.Block == null && y.Block == null
                && (x.Recv == null ? y.Recv == null : y.Recv != null && CmSame(x.Recv, y.Recv)) && x.Args.Count == y.Args.Count
                && x.Args.Zip(y.Args).All(p => p.First.Mod == "" && p.Second.Mod == "" && p.First.Value != null && p.Second.Value != null && CmSame(p.First.Value, p.Second.Value)),
            _ => false,
        };
    }

    /// <summary>`if c(i) then out[m] = xs[i]; m += 1 end`, m written only there.</summary>
    bool IsCompaction(IfStmt s, CmPlan plan, LoopPlan lp, ref string? why)
    {
        if (s is not { Conds: [var c], Bodies: [[Assign w, Assign inc]], Else: null } || !c.Type.IsBool || !PureRead(c, lp)) return false;
        if (w is not { Op: "=", Target: IndexExpr { Slice: null, FromEnd: 0, TupleElement: null, Args: [{ Mod: "", Name: null, Value: { } mi }] } t } || !IsLocal(t.Target, out var outArr)
            || outArr.Type.Kind != "array" || !IsLocal(mi, out var m) || m.Type.Cs != "int" || plan.DefLocals.Contains(m) || plan.DefLocals.Contains(outArr) || m == plan.I)
            return false;
        bool step = inc is { Op: "+=" } && LitValue(inc.Value) == 1
            || inc is { Op: "=", Value: BinaryExpr { Op: "+" or "&+" } b } && IsLocal(b.L, m) && LitValue(b.R) == 1;
        if (!step || !IsLocal(inc.Target, m)) return false;
        if (!AtCounter(w.Value, plan, out var src) || src.Type.Elem?.Cs != outArr.Type.Elem?.Cs || outArr.Type.Elem is not { } oe || !CmElem(oe))
        { why = "a compaction copies `xs[i]` of an Int32, UInt32, Int64, UInt64, Float32 or Float64 array"; return false; }
        if (MentionsAny(plan, [c], m, outArr)) { why = $"the condition reads `{m.Name}` or `{outArr.Name}`"; return false; }
        plan.Kind = "compact";
        plan.M = m;
        plan.Out = outArr;
        plan.Src = src;
        plan.Flag = c;
        plan.Written.Add(outArr);
        return true;
    }

    /// <summary>`out[p[i]] = xs[i]` (no definitions).</summary>
    bool IsPermute(Assign a, CmPlan plan, LoopPlan lp)
    {
        if (plan.Defs.Count > 0 || a.Op != "=" || a.Target is not IndexExpr { Slice: null, FromEnd: 0, TupleElement: null, Args: [{ Mod: "", Name: null, Value: { } pi }] } t
            || !IsLocal(t.Target, out var outArr) || outArr.Type.Kind != "array" || !AtCounter(pi, plan, out var perm) || perm.Type.Elem?.Cs != "int"
            || !AtCounter(a.Value, plan, out var src) || src.Type.Elem?.Cs != outArr.Type.Elem?.Cs || outArr.Type.Elem is not { } oe || !CmElem(oe) || outArr == src || outArr == perm)
            return false;
        plan.Kind = "permute";
        plan.Out = outArr;
        plan.Src = src;
        plan.Perm = perm;
        plan.Written.Add(outArr);
        return true;
    }

    // ------------------------------------------------------------------ lowering

    static NameExpr CmRef(LocalSym l, int line) => new() { Name = l.Name, Res = "local", Sym = l, Type = l.Type, Line = line };

    /// <summary>`arr[i] = v` as HIR: a store of the map pass.</summary>
    static Assign CmStore(LocalSym arr, LocalSym i, Syntax.Expr v, int line) => new()
    {
        Target = new IndexExpr { Target = CmRef(arr, line), Args = { new Arg { Value = CmRef(i, line) } }, Type = arr.Type.Elem!, Line = line },
        Value = v,
        Line = line,
    };

    /// <summary>The map pass: a loop over the same range running the definitions and the stores, parallel when that pays.</summary>
    MStmt CmMap(CmPlan p, List<Stmt> stores)
    {
        var body = new List<Stmt>(p.Defs);
        body.AddRange(stores);
        var synth = new ForStmt { Names = p.F.Names, Iter = p.F.Iter, Locals = p.F.Locals, Body = body, Line = p.F.Line, Col = p.F.Col };
        return TryParallelFor(synth) ?? SerialFor(synth);
    }

    /// <summary>A zeroed scratch array of exactly n elements from the owned-array pool (ADR 0016: no GC allocation once warm).</summary>
    LocalSym CmScratch(AType elem, List<MStmt> o, List<MStmt> frees, MExpr n)
    {
        string name = Temp("cms");
        var l = new LocalSym { Name = name, CsName = name, Type = AType.Array(elem) };
        var ea = elem.ToAir();
        string pool = "global::Alice.Runtime.Own<" + elem.Cs + ">";
        o.Add(new MDecl { Name = name, TypeCs = elem.Cs + "[]", Init = new AirRent { PoolCs = pool, Args = { n }, Type = new AirType.Array(ea) } });
        frees.Add(new MExprStmt { E = new AirRelease { PoolCs = pool, Target = AirLeaf.Name(name, new AirType.Array(ea)), Type = AirLeaf.Void } });
        return l;
    }

    static MExpr CmName(LocalSym l) => AirLeaf.Name(l.CsName, l.Type.ToAir());

    static MExpr CmAt(LocalSym arr, MExpr idx) => new MIndex { Target = CmName(arr), Args = { idx }, Type = arr.Type.Elem!.ToAir() };

    /// <summary>`a op b` in the element type, wrapping for integers (the kernels wrap).</summary>
    static MExpr CmApply(AirCmOp op, MExpr a, MExpr b, AType t)
    {
        var bin = new MBinary
        {
            Op = op switch { AirCmOp.Add => AirBinOp.Add, AirCmOp.Mul => AirBinOp.Mul, AirCmOp.And => AirBinOp.BitAnd, AirCmOp.Or => AirBinOp.BitOr, _ => AirBinOp.BitXor },
            L = a, R = b, Type = t.ToAir(),
        };
        return t.IsInteger && op is AirCmOp.Add or AirCmOp.Mul ? new MCall { Name = "unchecked", Args = { Optimizer.MirFacts.Unchecked(bin) }, Type = t.ToAir() } : bin;
    }

    static MExpr Cmp(AirBinOp op, MExpr a, MExpr b) => new MBinary { Op = op, L = a, R = b, Type = AirLeaf.Bool };

    static MExpr AndAll(IEnumerable<MExpr> cs) => cs.Aggregate((x, y) => new MBinary { Op = AirBinOp.And, L = x, R = y, Type = AirLeaf.Bool });

    static MExpr Len(LocalSym arr) => new AirLength { Target = CmName(arr), Type = AirLeaf.Int };

    MStmt LowerCm(CmPlan p)
    {
        var f = p.F;
        var r = (RangeExpr)f.Iter;
        string n = Temp("cmn");
        MExpr N() => AirLeaf.Name(n, AirLeaf.Int);
        var end = Expr(r.B);
        pre.Add(new MDecl { Name = n, TypeCs = "int", Init = r.Excl ? end : new MBinary { Op = AirBinOp.Add, L = end, R = AirConst.Int(1), Type = AirLeaf.Int } });
        int line = f.Line;

        // when the kernels apply: long enough (inferred), and the written arrays are not read through another name
        var guard = new List<MExpr> { Cmp(f.Cm ? AirBinOp.Gt : AirBinOp.Ge, N(), AirConst.Int(f.Cm ? 0 : CmThreshold)) };
        // inferred: only with the parallel runtime on (ALICE_PAR=0, one core: the loop as written is faster)
        if (!f.Cm) guard.Add(Cmp(AirBinOp.Gt, new MMember { Target = AirTypeRef.Named("global::Alice.Runtime.Par"), Name = "Workers", Type = AirLeaf.Int }, AirConst.Int(1)));
        var reads = Walk(f.Body).Select(e => IsLocal(e, out var l) && l.Type.Kind == "array" && !p.DefLocals.Contains(l) ? l : null).OfType<LocalSym>().Distinct().ToList();
        foreach (var w in p.Written)
            foreach (var rd in reads)
                if (rd != w && !p.Written.Contains(rd) && MayAlias(w.Type, rd.Type))
                    guard.Add(new AirCmDistinct { W = CmName(w), R = CmName(rd), Type = AirLeaf.Bool });
        var then = new List<MStmt>();
        var frees = new List<MStmt>();
        bool ordered = !p.Float || p.Kind is "compact" or "permute";
        MExpr Last(LocalSym arr) => CmAt(arr, new MBinary { Op = AirBinOp.Sub, L = N(), R = AirConst.Int(1), Type = AirLeaf.Int });
        MStmt Set(MExpr target, MExpr v) => new MAssign { Target = target, Value = v };
        MExpr AccRef() => CmName(p.Acc!);

        switch (p.Kind)
        {
            case "scan" or "exclusive":
                {
                    var t = p.Acc!.Type;
                    var outArr = p.Out!;
                    guard.Add(Cmp(AirBinOp.Le, N(), Len(outArr)));
                    bool excl = p.Kind == "exclusive";
                    List<MStmt> Finish(LocalSym src, bool fused)
                    {
                        var o = new List<MStmt>();
                        string? last = null;
                        if (excl)
                        {
                            last = Temp("cml");
                            o.Add(new MDecl { Name = last, TypeCs = t.Cs, Init = Last(src) });
                        }
                        o.Add(new MExprStmt { E = new AirScan { Op = p.Op, Inclusive = !excl, Src = CmName(src), Dst = CmName(outArr), Count = N(), Elem = t.ToAir(), Ordered = ordered, Fused = fused, Type = AirLeaf.Void } });
                        if (!excl) o.Add(Set(AccRef(), Last(outArr)));
                        else
                        {
                            // out[0] holds the identity, out[k] = acc op e(0..k-1) (the seed was folded into the first element)
                            string fin = Temp("cmf");
                            o.Add(new MDecl { Name = fin, TypeCs = t.Cs, Init = CmApply(p.Op, Last(outArr), AirLeaf.Name(last!, t.ToAir()), t) });
                            o.Add(Set(CmAt(outArr, AirConst.Int(0)), AccRef()));
                            o.Add(Set(AccRef(), AirLeaf.Name(fin, t.ToAir())));
                        }
                        return o;
                    }
                    MStmt Seed() => Set(CmAt(outArr, AirConst.Int(0)), CmApply(p.Op, AccRef(), CmAt(outArr, AirConst.Int(0)), t));
                    List<MStmt> FusedPath()
                    {
                        var o = new List<MStmt> { CmMap(p, [CmStore(outArr, p.I, p.Elem!, line)]), Seed() };
                        o.AddRange(Finish(outArr, true));
                        return o;
                    }
                    if (p.Src is { } xs && !p.Float && p.Op is not AirCmOp.And)
                    {
                        // library form: scan xs straight into out when acc starts at the identity
                        guard.Add(Cmp(AirBinOp.Le, N(), Len(xs)));
                        var id = new MCast { TypeCs = t.Cs, E = AirConst.Int(p.Op == AirCmOp.Mul ? 1 : 0), Type = t.ToAir() };
                        then.Add(new MIf { Cond = Cmp(AirBinOp.Eq, AccRef(), id), Then = Finish(xs, false), Else = xs == outArr ? Finish(xs, false).Prepend(Seed()).ToList() : FusedPath() });
                    }
                    else if (p.Src is { } same && same == outArr)
                    {
                        then.Add(Seed());
                        then.AddRange(Finish(outArr, false));
                    }
                    else then.AddRange(FusedPath());
                    break;
                }
            case "segmented":
                {
                    var t = p.Acc!.Type;
                    var outArr = p.Out!;
                    guard.Add(Cmp(AirBinOp.Le, N(), Len(outArr)));
                    var fl = CmScratch(AType.Bool, then, frees, N());
                    then.Add(CmMap(p, [CmStore(outArr, p.I, p.Elem!, line), CmStore(fl, p.I, p.Flag!, line)]));
                    // a flag at 0 restarts there; else the running value seeds the first element
                    then.Add(Set(CmAt(outArr, AirConst.Int(0)), new MCond { C = CmAt(fl, AirConst.Int(0)), A = CmAt(outArr, AirConst.Int(0)), B = CmApply(p.Op, AccRef(), CmAt(outArr, AirConst.Int(0)), t), Type = t.ToAir() }));
                    then.Add(new MExprStmt { E = new AirSegmented { Op = p.Op, Src = CmName(outArr), Flags = CmName(fl), Dst = CmName(outArr), Count = N(), Elem = t.ToAir(), Ordered = ordered, Type = AirLeaf.Void } });
                    then.AddRange(frees);
                    frees.Clear();
                    then.Add(Set(AccRef(), Last(outArr)));
                    break;
                }
            case "combine":
                {
                    var stores = new List<Stmt>();
                    var keys = new List<(Syntax.Expr K, LocalSym Arr)>();
                    var lens = new HashSet<LocalSym>();
                    var calls = new List<MStmt>();
                    foreach (var (h, key, v, ones, s) in p.Combines)
                    {
                        LocalSym ka;
                        if (AtCounter(key, p, out var ks) && ks.Type.Elem?.Cs == key.Type.Cs)
                        {
                            ka = ks;
                            if (lens.Add(ks)) guard.Add(Cmp(AirBinOp.Le, N(), Len(ks)));
                        }
                        else if (keys.FirstOrDefault(x => CmSame(x.K, key)).Arr is { } shared) ka = shared;
                        else
                        {
                            ka = CmScratch(key.Type, then, frees, N());
                            stores.Add(CmStore(ka, p.I, key, s.Line));
                            keys.Add((key, ka));
                        }
                        LocalSym? va = null;
                        if (!ones)
                        {
                            if (AtCounter(v, p, out var vs) && vs.Type.Elem?.Cs == h.Type.Elem!.Cs)
                            {
                                va = vs;
                                if (lens.Add(vs)) guard.Add(Cmp(AirBinOp.Le, N(), Len(vs)));
                            }
                            else
                            {
                                va = CmScratch(h.Type.Elem!, then, frees, N());
                                stores.Add(CmStore(va, p.I, v!, s.Line));
                            }
                        }
                        calls.Add(new MExprStmt { E = new AirCombine { Target = CmName(h), Idx = CmName(ka), Vals = va != null ? CmName(va) : null, IdxElem = key.Type.ToAir(), Count = N(), Elem = h.Type.Elem!.ToAir(), Ordered = ordered, Fused = stores.Count > 0, Type = AirLeaf.Void } });
                    }
                    if (stores.Count > 0) then.Add(CmMap(p, stores));
                    then.AddRange(calls);
                    break;
                }
            case "compact":
                {
                    var outArr = p.Out!;
                    var xs = p.Src!;
                    guard.Add(Cmp(AirBinOp.Eq, CmName(p.M!), AirConst.Int(0)));
                    guard.Add(Cmp(AirBinOp.Eq, N(), Len(xs)));
                    if (!guard.OfType<AirCmDistinct>().Any(g => g.R is MCode { Text: var tx } && tx == xs.CsName))
                        guard.Add(new AirCmDistinct { W = CmName(outArr), R = CmName(xs), Type = AirLeaf.Bool });
                    var fl = CmScratch(AType.Bool, then, frees, N());
                    then.Add(CmMap(p, [CmStore(fl, p.I, p.Flag!, line)]));
                    // too many for out: the kernel raises and leaves out untouched (m is still 0); the loop then raises where it would have
                    then.Add(new MTry
                    {
                        Body = { Set(CmName(p.M!), new AirCompact { Src = CmName(xs), Flags = CmName(fl), Dst = CmName(outArr), Count = N(), Elem = outArr.Type.Elem!.ToAir(), Type = AirLeaf.Int }) },
                        Catches = { new MCatch { TypeCs = "global::System.ArgumentException", Body = { SerialFor(f) } } },
                    });
                    break;
                }
            case "permute":
                {
                    guard.Add(Cmp(AirBinOp.Eq, N(), Len(p.Src!)));
                    guard.Add(Cmp(AirBinOp.Le, N(), Len(p.Perm!)));
                    // a duplicate or out-of-range target: the loop's own semantics (the last write wins, or it raises)
                    then.Add(new MTry
                    {
                        Body = { new MExprStmt { E = new AirPermute { Src = CmName(p.Src!), Perm = CmName(p.Perm!), Dst = CmName(p.Out!), Count = N(), Elem = p.Out!.Type.Elem!.ToAir(), Type = AirLeaf.Void } } },
                        Catches = { new MCatch { TypeCs = "global::System.ArgumentException", Body = { SerialFor(f) } } },
                    });
                    break;
                }
        }
        then.AddRange(frees);
        return new MIf { Cond = AndAll(guard), Then = then, Else = [SerialFor(f)] };
    }
}
