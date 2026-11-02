using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;
using Alice.Runtime;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// One dynamic call site of a module: a static field <see cref="Field"/> of type <c>Alice.Runtime.DynSite</c> in the
/// holder class <see cref="DynSites.HolderName"/>, initialized once (<see cref="Init"/>) in the holder's cctor.
/// </summary>
public sealed record DynSiteDecl(int Id, DynKind Kind, string Name, int Arity, DynOp Op, bool Safe, AirType? To, Span? Span)
{
    public string Field => "site" + Id;

    /// <summary>The field's type.</summary>
    public static readonly AirType SiteType = new AirType.ClrRef("global::Alice.Runtime.DynSite", typeof(DynSite), []);

    static readonly AirType KindType = new AirType.ClrRef("global::Alice.Runtime.DynKind", typeof(DynKind), []);
    static readonly AirType OpType = new AirType.ClrRef("global::Alice.Runtime.DynOp", typeof(DynOp), []);

    /// <summary>`new DynSite(kind, name, arity, safe)` / `new DynSite(kind, op)` / `new DynSite(kind, typeof(T))`.
    /// Enum arguments are casts of their numeric value (`(DynKind)0`).</summary>
    public MNew Init()
    {
        var kind = new MCast { TypeCs = KindType.ToCs(), E = AirConst.Of((int)Kind), Type = KindType };
        var n = new MNew { TypeCs = SiteType.ToCs(), Type = SiteType, Args = { kind } };
        if (Kind is DynKind.BinaryOp or DynKind.UnaryOp) n.Args.Add(new MCast { TypeCs = OpType.ToCs(), E = AirConst.Of((int)Op), Type = OpType });
        else if (Kind is DynKind.Convert or DynKind.Cast) n.Args.Add(new AirTypeOf(To!));
        else { n.Args.Add(AirConst.Of(Name)); n.Args.Add(AirConst.Of(Arity)); n.Args.Add(AirConst.Bool(Safe)); }
        return n;
    }

    /// <summary>What the site does, for messages: `x.foo`, operator `+`, conversion to Int32.</summary>
    public string Describe() => Kind switch
    {
        DynKind.Call or DynKind.GetMember => "`." + Name + "`",
        DynKind.SetMember => "`." + Name + " =`",
        DynKind.GetIndex => "`[]`",
        DynKind.SetIndex => "`[]=`",
        DynKind.BinaryOp or DynKind.UnaryOp => "operator `" + OpSpelling(Op) + "`",
        DynKind.Convert or DynKind.Cast => "conversion to `" + (To != null ? Diagnostics.AliceNames.Type(To.ToCs()) : "?") + "`",
        _ => "`.()`",
    };

    static string OpSpelling(DynOp op) => op switch
    {
        DynOp.Add or DynOp.Plus => "+", DynOp.Sub or DynOp.Neg => "-", DynOp.Mul => "*", DynOp.Div => "/", DynOp.Mod => "%",
        DynOp.Shl => "<<", DynOp.Shr => ">>", DynOp.UShr => ">>>", DynOp.BitAnd => "&", DynOp.BitOr => "|", DynOp.BitXor => "^",
        DynOp.Eq => "==", DynOp.Ne => "!=", DynOp.Lt => "<", DynOp.Le => "<=", DynOp.Gt => ">", DynOp.Ge => ">=",
        DynOp.Not => "!", DynOp.BitNot => "~", _ => op.ToString(),
    };
}

/// <summary>
/// Dynamic sites (backend plan D1.9, ADR 0033): rewrites every operation on an `Any` (C# `dynamic`) value into an
/// <see cref="AirDynCall"/> on its own <see cref="DynSite"/>, so the IL backend needs no C# runtime binder. IL backend
/// only: the C# backend never runs it and keeps C# `dynamic` (its output is unchanged). One instance per module: site
/// numbers are unique in it, <see cref="Sites"/> lists the fields to declare. Run it before closure conversion and
/// the IL lowering passes (it rewrites lambda bodies in place).
/// </summary>
/// <remarks>
/// Rewritten: member calls, member reads and writes, indexing, `.()` on a dynamic receiver; unary and binary
/// operators with a dynamic operand (`&amp;&amp;`/`||` convert their operands to Bool instead, `??` stays); compound
/// assignments and `x += 1`/`x++` statements through a dynamic target; implicit conversions into typed locals,
/// conditions, `throw` and `foreach`; explicit casts. Left to the IL emitter: a dynamic value passed to a statically
/// bound call or returned (it is an object; the emitter asks <see cref="Convert"/> for a site where the slot is typed).
/// What the pass cannot rewrite is listed in <see cref="Unsupported"/> (generic dynamic calls, `x++` inside an expression).
/// </remarks>
public sealed class DynSites : MirRewriter, IMirPass
{
    /// <summary>The holder class of the site fields.</summary>
    public const string HolderName = "<>DynSites";

    public string Name => "dyn-sites";

    /// <summary>Every site created so far, in id order.</summary>
    public readonly List<DynSiteDecl> Sites = new();

    /// <summary>Dynamic constructs left as they were, as `file:line: what` text; the IL backend reports them.</summary>
    public readonly List<string> Unsupported = new();

    readonly string file;
    readonly HashSet<string> dynLocals = new();
    readonly Dictionary<string, string> typedLocals = new();
    int temps;

    static readonly AirType DynamicType = new AirType.Prim(PrimKind.Dynamic);
    static readonly AirType BoolType = new AirType.Prim(PrimKind.Bool);
    static readonly AirType VoidType = new AirType.Prim(PrimKind.Void);

    /// <param name="file">Source file for sites whose nodes and statements carry only a line.</param>
    public DynSites(string file = "") => this.file = file;

    public new void Run(List<MStmt> body)
    {
        base.Run(body);
        FixList(body);
    }

    /// <summary>A Convert site for a dynamic value flowing into a slot of type <paramref name="to"/> (for the IL
    /// emitter: arguments of statically bound calls, returns).</summary>
    public AirDynCall Convert(MExpr value, AirType to, Span? at = null) => Make(DynKind.Convert, "", 1, DynOp.None, false, value, [], to, at ?? value.Span);

    /// <summary>The value is `dynamic`: declared Any, the result of a dynamic operation, or a local holding one.</summary>
    public bool IsDynamic(MExpr e) => e switch
    {
        AirTypeRef => false,
        AirDynCall d => d.Kind is not (DynKind.Convert or DynKind.Cast or DynKind.SetMember or DynKind.SetIndex),
        AirLocalRef l when dynLocals.Contains(l.Name) => true,
        _ => e.Type is AirType.Prim { Kind: PrimKind.Dynamic },
    };

    // ---- expressions (post-order: operands are already rewritten)

    protected override MExpr Rewrite(MExpr e)
    {
        switch (e)
        {
            case MCall { Target: { } t } c when IsDynamic(t):
                if (c.Name.Contains('<')) { Note(c, "generic method call `" + c.Name + "` on an Any value"); return e; }
                return Make(DynKind.Call, c.Name, c.Args.Count, DynOp.None, c.Safe, t, c.Args, null, c.Span ?? t.Span);
            case MMember m when IsDynamic(m.Target):
                return Make(DynKind.GetMember, m.Name, 0, DynOp.None, m.Safe, m.Target, [], null, m.Span ?? m.Target.Span);
            case MIndex ix when IsDynamic(ix.Target):
                return Make(DynKind.GetIndex, "", ix.Args.Count, DynOp.None, false, ix.Target, ix.Args, null, ix.Span ?? ix.Target.Span);
            case MInvoke inv when IsDynamic(inv.Target):
                return Make(DynKind.Invoke, "", inv.Args.Count, DynOp.None, false, inv.Target, inv.Args, null, inv.Span ?? inv.Target.Span);
            case MBinary { Op: AirBinOp.And or AirBinOp.Or } b when IsDynamic(b.L) || IsDynamic(b.R):
                b.L = ToBool(b.L);
                b.R = ToBool(b.R);
                return b;
            case MBinary { Op: AirBinOp.Coalesce }:
                return e;
            case MBinary b when IsDynamic(b.L) || IsDynamic(b.R):
                return Make(DynKind.BinaryOp, "", 2, BinOp(b.Op), false, b.L, [b.R], null, b.Span ?? b.L.Span ?? b.R.Span);
            case MUnary { Op: AirUnOp.Neg or AirUnOp.Not or AirUnOp.BitNot } u when IsDynamic(u.E):
                return Make(DynKind.UnaryOp, "", 1, u.Op switch { AirUnOp.Neg => DynOp.Neg, AirUnOp.Not => DynOp.Not, _ => DynOp.BitNot }, false, u.E, [], null, u.Span ?? u.E.Span);
            case MUnary { Op: AirUnOp.Inc or AirUnOp.Dec } u when IsDynamic(u.E):
                return e;   // a statement `x++` is rewritten by FixList; inside an expression it is reported there
            case MUnary { Op: AirUnOp.Await } u when IsDynamic(u.E):
                Note(u, "`await` on an Any value");
                return e;
            case MCast c when IsDynamic(c.E) && !IsDynamicCs(c.TypeCs):
                return Make(DynKind.Cast, "", 1, DynOp.None, false, c.E, [], TypeOf(c.TypeCs), c.Span ?? c.E.Span);
            case MCond c when IsDynamic(c.C):
                c.C = ToBool(c.C);
                return c;
            case MLambda { Body: { } body }:
                FixList(body);
                return e;
        }
        return e;
    }

    // ---- statements: lists are fixed once their owner was visited (children first)

    protected override void Visit(MStmt s)
    {
        // locals: `dynamic x` and `var x = <dynamic>` hold dynamic values; typed ones convert what is assigned to them
        // (statements are visited in order, so a declaration is seen before its uses)
        if (s is MDecl decl)
        {
            if (decl.TypeCs == "dynamic" || decl.TypeCs is null or "var" && decl.Init != null && IsDynamic(decl.Init)) dynLocals.Add(decl.Name);
            else if (decl.TypeCs is { } dcs && dcs != "var") typedLocals[decl.Name] = dcs;
        }
        switch (s)
        {
            case MIf i: i.Cond = ToBool(i.Cond); FixList(i.Then); if (i.Else != null) FixList(i.Else); break;
            case MWhile w: w.Cond = ToBool(w.Cond); FixList(w.Body); break;
            case MFor f: FixList(f.Body); break;
            case MForeach fe:
                if (IsDynamic(fe.Coll)) fe.Coll = Make(DynKind.Convert, "", 1, DynOp.None, false, fe.Coll, [], Enumerable, fe.Coll.Span ?? fe.Span);
                FixList(fe.Body);
                break;
            case MTry t: FixList(t.Body); foreach (var c in t.Catches) FixList(c.Body); if (t.Finally != null) FixList(t.Finally); break;
            case MBlock b: FixList(b.Body); break;
            case MUsing u: FixList(u.Body); break;
            case MThrow th when IsDynamic(th.Value):
                th.Value = Make(DynKind.Convert, "", 1, DynOp.None, false, th.Value, [], ExceptionType, th.Value.Span ?? th.Span);
                break;
            case MDecl { TypeCs: { } cs, Init: { } init } d when IsDynamic(init) && cs != "var" && !IsDynamicCs(cs):
                d.Init = Make(DynKind.Convert, "", 1, DynOp.None, false, init, [], TypeOf(cs), init.Span ?? d.Span);
                break;
        }
    }

    static readonly AirType Enumerable = new AirType.ClrRef("global::System.Collections.IEnumerable", typeof(System.Collections.IEnumerable), []);
    static readonly AirType ExceptionType = new AirType.ClrRef("global::System.Exception", typeof(Exception), []);

    void FixList(List<MStmt> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var pre = new List<MStmt>();
            var repl = Statement(s, pre);
            if (repl != null) { repl.Span = s.Span; if (s.Span == null) repl.Line = s.Line; list[i] = repl; }
            foreach (var p in pre) { p.Span = s.Span; if (s.Span == null) p.Line = s.Line; }
            list.InsertRange(i, pre);
            i += pre.Count;
            Locate(list[i], list[i].Span, list[i].Line);
            foreach (var p in pre) Locate(p, s.Span, s.Line);
            ReportNested(list[i]);
        }
    }

    /// <summary>A statement that must change shape: a dynamic store, a compound assignment through a dynamic target,
    /// `x++` on a dynamic value, a typed local assigned a dynamic value. Null: unchanged.</summary>
    MStmt? Statement(MStmt s, List<MStmt> pre)
    {
        switch (s)
        {
            case MAssign { Target: AirDynCall { Kind: DynKind.GetMember or DynKind.GetIndex } get } a:
                {
                    var value = a.Value;
                    if (a.Op != "=")
                    {
                        get.Target = Stable(get.Target!, pre);
                        for (int k = 0; k < get.Args.Count; k++) get.Args[k] = Stable(get.Args[k], pre);
                        value = Compound(a.Op, get, value);
                    }
                    var target = get.Target!;
                    var args = get.Kind == DynKind.GetIndex ? get.Args.Select(x => Copy(x)).Append(value).ToList() : new List<MExpr> { value };
                    if (a.Op == "=")
                    {
                        // the get site becomes the set site
                        get.Kind = get.Kind == DynKind.GetMember ? DynKind.SetMember : DynKind.SetIndex;
                        get.Args = args;
                        get.Type = VoidType;
                        Sites[get.SiteId] = Sites[get.SiteId] with { Kind = get.Kind };
                        return new MExprStmt { E = get };
                    }
                    var set = Make(get.Kind == DynKind.GetMember ? DynKind.SetMember : DynKind.SetIndex, get.Name, get.Kind == DynKind.GetMember ? 0 : get.Args.Count,
                        DynOp.None, get.Safe, Copy(target), args, null, get.Span);
                    return new MExprStmt { E = set };
                }
            case MAssign { Op: not "=" } a when IsDynamic(a.Target) || IsDynamic(a.Value):
                {
                    MExpr value = Compound(a.Op, a.Target, a.Value);
                    if (!IsDynamic(a.Target) && SlotType(a.Target) is { } to) value = Make(DynKind.Convert, "", 1, DynOp.None, false, value, [], to, a.Span);
                    return new MAssign { Target = a.Target, Op = "=", Value = value };
                }
            case MAssign { Op: "=" } a when IsDynamic(a.Value) && !IsDynamic(a.Target) && SlotType(a.Target) is { } to:
                a.Value = Make(DynKind.Convert, "", 1, DynOp.None, false, a.Value, [], to, a.Value.Span ?? a.Span);
                return null;
            case MExprStmt { E: MUnary { Op: AirUnOp.Inc or AirUnOp.Dec } u } when IsDynamic(u.E):
                {
                    var one = AirConst.Int(1);
                    var asg = new MAssign { Target = u.E, Op = u.Op == AirUnOp.Inc ? "+=" : "-=", Value = one };
                    return Statement(asg, pre) ?? asg;
                }
        }
        return null;
    }

    /// <summary>`t op= v` as the value `t op v` (`??=`: `t ?? v`).</summary>
    MExpr Compound(string op, MExpr current, MExpr value)
    {
        string bin = op[..^1];
        if (bin == "??") return new MBinary { Op = AirBinOp.Coalesce, L = current, R = value, Type = DynamicType };
        var o = BinOp(AirOps.ParseBin(bin));
        return Make(DynKind.BinaryOp, "", 2, o, false, current, [value], null, current.Span ?? value.Span);
    }

    /// <summary>A leaf is evaluated once wherever it is copied; anything else goes into a `dynamic` temp first.</summary>
    MExpr Stable(MExpr e, List<MStmt> pre)
    {
        if (e is AirLocalRef or AirParamRef or AirThis or AirFieldRef or AirConst or AirNull or AirTypeRef) return e;
        string name = "__dyn" + ++temps;
        pre.Add(new MDecl { Name = name, TypeCs = "dynamic", Init = e });
        dynLocals.Add(name);
        return new AirLocalRef(name, DynamicType);
    }

    static MExpr Copy(MExpr e) => e switch
    {
        AirLocalRef l => new AirLocalRef(l.Name, l.Type, l.Narrowed) { Span = l.Span },
        _ => e,
    };

    AirType? SlotType(MExpr target) => target switch
    {
        AirLocalRef l when typedLocals.TryGetValue(l.Name, out var cs) && !IsDynamicCs(cs) => TypeOf(cs),
        { Type: { } t } when !IsDynamicCs(t.ToCs()) && t != AirType.Unknown => t,
        _ => null,
    };

    MExpr ToBool(MExpr c) => IsDynamic(c) ? Make(DynKind.Convert, "", 1, DynOp.None, false, c, [], BoolType, c.Span) : c;

    static bool IsDynamicCs(string cs) => cs is "dynamic" or "dynamic?" or "object" or "object?";

    static readonly Dictionary<string, PrimKind> Keywords = new()
    {
        ["bool"] = PrimKind.Bool, ["char"] = PrimKind.Char, ["sbyte"] = PrimKind.SByte, ["byte"] = PrimKind.Byte,
        ["short"] = PrimKind.Short, ["ushort"] = PrimKind.UShort, ["int"] = PrimKind.Int, ["uint"] = PrimKind.UInt,
        ["long"] = PrimKind.Long, ["ulong"] = PrimKind.ULong, ["float"] = PrimKind.Float, ["double"] = PrimKind.Double,
        ["decimal"] = PrimKind.Decimal, ["string"] = PrimKind.String, ["object"] = PrimKind.Object,
    };

    /// <summary>The AIR type of a C# type spelling (MDecl/MCast carry text until D0.9).</summary>
    static AirType TypeOf(string cs)
    {
        bool q = cs.EndsWith('?');
        string bare = q ? cs[..^1] : cs;
        AirType t = Keywords.TryGetValue(bare, out var k) ? new AirType.Prim(k) : new AirType.ClrRef(bare, null, []);
        return q ? t with { Nullable = true } : t;
    }

    static DynOp BinOp(AirBinOp op) => op switch
    {
        AirBinOp.Add => DynOp.Add, AirBinOp.Sub => DynOp.Sub, AirBinOp.Mul => DynOp.Mul, AirBinOp.Div => DynOp.Div, AirBinOp.Mod => DynOp.Mod,
        AirBinOp.Shl => DynOp.Shl, AirBinOp.Shr => DynOp.Shr, AirBinOp.UShr => DynOp.UShr,
        AirBinOp.BitAnd => DynOp.BitAnd, AirBinOp.BitOr => DynOp.BitOr, AirBinOp.BitXor => DynOp.BitXor,
        AirBinOp.Eq => DynOp.Eq, AirBinOp.Ne => DynOp.Ne, AirBinOp.Lt => DynOp.Lt, AirBinOp.Le => DynOp.Le, AirBinOp.Gt => DynOp.Gt, AirBinOp.Ge => DynOp.Ge,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "no dynamic operator"),
    };

    AirDynCall Make(DynKind kind, string name, int arity, DynOp op, bool safe, MExpr target, List<MExpr> args, AirType? to, Span? at)
    {
        int id = Sites.Count;
        string n = name.StartsWith('@') ? name[1..] : name;
        Sites.Add(new DynSiteDecl(id, kind, n, arity, op, safe, to, at));
        return new AirDynCall
        {
            SiteId = id, Kind = kind, Name = n, Op = op, Safe = safe, Target = target, Args = args, To = to, Span = at,
            Type = kind is DynKind.Convert or DynKind.Cast ? to : kind is DynKind.SetMember or DynKind.SetIndex ? VoidType : DynamicType,
        };
    }

    void Note(MExpr e, string what)
    {
        var at = e.Span;
        Unsupported.Add((at is { } p ? $"{p.File}:{p.Line}" : file) + ": " + what);
    }

    // ---- positions: a site without a span takes its statement's (diagnostics point at a line)

    void Locate(MStmt s, Span? span, int line)
    {
        var at = span ?? (line > 0 ? new Span(file, line, 1, 0) : null);
        if (at == null) return;
        foreach (var d in DynCalls(s))
            if (Sites[d.SiteId].Span == null)
            {
                d.Span = at;
                Sites[d.SiteId] = Sites[d.SiteId] with { Span = at };
            }
    }

    void ReportNested(MStmt s)
    {
        foreach (var e in Exprs(s))
            foreach (var x in Walk(e))
                if (x is MUnary { Op: AirUnOp.Inc or AirUnOp.Dec } u && IsDynamic(u.E)) Note(u.Span != null ? u : u.E, "`++`/`--` on an Any value inside an expression");
    }

    static IEnumerable<AirDynCall> DynCalls(MStmt s) => Exprs(s).SelectMany(Walk).OfType<AirDynCall>();

    /// <summary>The statement's own expressions (nested bodies have their own statements).</summary>
    static IEnumerable<MExpr> Exprs(MStmt s) => s switch
    {
        MDecl { Init: { } i } => [i],
        MDeconstruct dc => [dc.Value],
        MAssign a => [a.Target, a.Value],
        MExprStmt es => [es.E],
        MIf i => [i.Cond],
        MWhile w => [w.Cond],
        MFor f => [f.Start, f.End],
        MForeach fe => [fe.Coll],
        MReturn { Value: { } v } => [v],
        MThrow t => [t.Value],
        MUsing u => [u.Value],
        _ => [],
    };

    /// <summary>The expression and its sub-expressions, not entering lambda bodies.</summary>
    public static IEnumerable<MExpr> Walk(MExpr e)
    {
        yield return e;
        IEnumerable<MExpr?> kids = e switch
        {
            AirDynCall d => d.Args.Prepend(d.Target),
            MCall c => c.Args.Prepend(c.Target),
            MMember m => [m.Target],
            MInvoke i => i.Args.Prepend(i.Target),
            MNew n => n.Init != null ? n.Args.Concat(n.Init) : n.Args,
            MNewArray a => (a.Items ?? []).Prepend(a.Size),
            MIndex ix => ix.Args.Prepend(ix.Target),
            MBinary b => [b.L, b.R],
            MUnary u => [u.E],
            MCond c => [c.C, c.A, c.B],
            MLambda { ExprBody: { } eb } => [eb],
            MCast c => [c.E],
            MIs i => [i.E],
            MInterp ip => ip.Holes,
            MTuple t => t.Items,
            MPair p => [p.K, p.V],
            MArg a => [a.E],
            AirParExpr p => p.Children(),
            _ => [],
        };
        foreach (var k in kids)
            if (k != null)
                foreach (var x in Walk(k)) yield return x;
    }

    // ---- AL0232: no dynamic dispatch under NativeAOT or on wasm

    /// <summary>AL0232 for every site when the build is `native = true` (NativeAOT) or targets wasm: neither has the
    /// reflection metadata <see cref="Dyn"/> resolves members from. Empty otherwise.</summary>
    public static List<Diagnostic> Check(IEnumerable<DynSiteDecl> sites, bool native, bool wasm, string? fallbackFile = null)
    {
        var r = new List<Diagnostic>();
        if (!native && !wasm) return r;
        string target = native ? "`native = true` (NativeAOT)" : "the wasm target";
        foreach (var s in sites.OrderBy(s => s.Span?.File).ThenBy(s => s.Span?.Line).ThenBy(s => s.Span?.Col))
        {
            var p = s.Span ?? new Span(fallbackFile ?? "", 0, 0, 0);
            r.Add(new Diagnostic("error", "AL0232", p.File, p.Line, Math.Max(p.Col, 1),
                $"{s.Describe()} on an `Any` value needs dynamic dispatch, which {target} does not support; give the value a static type (`x : T = ...`, `def f(x : T)`) or convert it (`x.as(T)`)",
                Math.Max(p.Len, 1)));
        }
        return r;
    }
}
