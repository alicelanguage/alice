using System.Text;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;

namespace Alice.Compiler.Air.Passes;

/// <summary>The method a body belongs to: what the pass cannot see in the body itself.</summary>
public sealed class ClosureContext
{
    /// <summary>The enclosing method's CLR name; synthesized names embed it (`&lt;Main&gt;b__0`).</summary>
    public string Method = "m";
    /// <summary>The method's parameters (name, C# type or null), in order.</summary>
    public List<(string Name, string? TypeCs)> Params = new();
    /// <summary>An instance method: `this` and bare unresolved names (implicit `this.` members) are capturable.</summary>
    public bool Instance;
    public string? ThisTypeCs;
    /// <summary>Display structs for non-escaping closures (default on); off demotes every capturing closure to a
    /// display class (`ALICE_CLOSURE_STRUCTS=off`, the backend's `--closure-structs=off`).</summary>
    public bool StructDisplays = ClosureConvert.StructsDefault;
}

/// <summary>
/// Closure conversion (backend plan D1.5, ADR 0033 "Closure ABI"); IL backend only. Every <see cref="MLambda"/> of a
/// method body becomes a method:
/// <list type="number">
/// <item>Capture analysis: names are resolved through the body's scopes (declarations, loop and catch variables, lambda
/// parameters, the <see cref="ClosureContext"/> parameters and `this`); a capture is <em>written</em> when anything
/// assigns it after its declaration (in a closure or in the enclosing code).</item>
/// <item>Capture-free lambdas become static methods on the `&lt;&gt;c` holder with a lazily created, cached delegate
/// field per lambda (<see cref="AirDelegateCreate.CachedField"/>).</item>
/// <item>A capturing lambda that never escapes (a Par kernel, a Par.Range/Reduce/Fork argument, a fork branch, an
/// immediately invoked lambda; anything else escapes) gets a display <c>struct</c>: read-only captures by value, written
/// ones as `ref` fields to the locals themselves, the body a static method taking the display by ref.</item>
/// <item>Otherwise a display <c>class</c> with the captures as fields and the body as an instance method. A local or
/// parameter that is written and captured by an escaping closure is hoisted into a shared display of its block (fresh
/// per loop iteration when the block is a loop body); every access, in the method and in the closures, goes through
/// its field.</item>
/// </list>
/// Opaque <see cref="MCode"/> text is scanned for identifiers; an embedded captured name is rewritten in the text
/// (<see cref="OpaqueRewrites"/>). A body the pass cannot convert soundly is left untouched and the reason is recorded
/// in <see cref="Unsupported"/>.
/// </summary>
public sealed class ClosureConvert : IMirPass
{
    public string Name => "closure-convert";

    /// <summary>Set by the IL backend around each method body; the pipeline runs this pass only when it is set
    /// (helper fragments lowered standalone have no scope to resolve captures in).</summary>
    [ThreadStatic] public static ClosureContext? Current;

    readonly ClosureContext ctx;
    public readonly List<string> Unsupported = new();
    public int OpaqueRewrites;
    /// <summary>Display types and capture-free methods created by the last <see cref="Run"/>.</summary>
    public readonly List<AirDisplayType> Displays = new();
    public readonly List<AirClosureMethod> StaticMethods = new();

    public ClosureConvert(ClosureContext? context = null) => ctx = context ?? Current ?? new ClosureContext();

    /// <summary>`ALICE_CLOSURE_STRUCTS=off` (also `0`, `false`) turns display structs off for every body.</summary>
    public static readonly bool StructsDefault =
        Environment.GetEnvironmentVariable("ALICE_CLOSURE_STRUCTS")?.Trim().ToLowerInvariant() is not ("off" or "0" or "false");

    /// <summary>Where a lambda appears: whether and how it can escape.</summary>
    enum Pos { Escapes, Direct, Range, Reduce, Fork }

    public const string Holder = "<>c";
    const string StructSelf = "<>d";
    const string ThisField = "<>4__this";

    enum VarKind { Local, Param, LoopVar, Fixed, This, Link }

    sealed class Var
    {
        public string Name = "";
        public VarKind Kind;
        public Func Owner = null!;
        public List<MStmt>? Block;
        public MDecl? Decl;
        public string? TypeCs;
        public AirType? Type;
        public bool Written;
        public readonly List<Func> CapturedBy = new();
        public Hoist? Hoist;
    }

    sealed class Hoist
    {
        public AirDisplayType Display = null!;
        public Var Link = null!;
        public List<MStmt> Block = null!;
        public readonly List<Var> Params = new();
    }

    sealed class Func
    {
        public MLambda? Lambda;
        public Func? Parent;
        public bool Escapes;
        public bool HasFrame;
        public Pos Pos;
        public int Id;
        public readonly List<Var> Captures = new();
        public readonly List<Var> Params = new();
        public AirDisplayType? Display;
        public readonly Dictionary<Var, AirCapture> Fields = new();
        public void Capture(Var v) { if (!Captures.Contains(v)) Captures.Add(v); }
    }

    bool rewrite;
    Func cur = null!;
    Func root = null!;
    int counter;
    readonly List<Dictionary<string, Var>> scopes = new();
    readonly Dictionary<MLambda, Func> funcs = new();
    readonly Dictionary<(object Key, int Index), Var> vars = new();
    readonly List<(Var V, Func F, string What)> namePositions = new();
    readonly Dictionary<List<MStmt>, Hoist> hoistsByBlock = new(ReferenceEqualityComparer.Instance);
    readonly List<List<MStmt>> pending = new();
    Var? thisVar;

    public void Run(List<MStmt> body)
    {
        // 1. analysis
        rewrite = false;
        Begin(body);
        S(body);
        scopes.Clear();
        if (Unsupported.Count == 0) Decide();
        if (Unsupported.Count > 0 || funcs.Count == 0) return;
        // 2. rewrite (same walk; references resolve to the same Var objects by declaration key)
        rewrite = true;
        Begin(body);
        foreach (var h in hoistsByBlock.Values) Displays.Add(h.Display);
        S(body);
        scopes.Clear();
    }

    void Begin(List<MStmt> body)
    {
        if (!rewrite)
        {
            root = new Func { HasFrame = true, Escapes = true };
        }
        cur = root;
        scopes.Clear();
        scopes.Add(new());
        for (int i = 0; i < ctx.Params.Count; i++)
        {
            var (n, t) = ctx.Params[i];
            Declare(GetVar(ctx, i, n, VarKind.Param, body, null, t, null));
        }
        if (ctx.Instance)
        {
            thisVar = GetVar(root, -1, "this", VarKind.This, null, null, ctx.ThisTypeCs, null);
            Declare(thisVar);
        }
    }

    Var GetVar(object key, int index, string name, VarKind kind, List<MStmt>? block, MDecl? decl, string? typeCs, AirType? type)
    {
        if (!vars.TryGetValue((key, index), out var v))
        {
            v = new Var { Name = name, Kind = kind, Owner = cur, Block = block, Decl = decl, TypeCs = typeCs, Type = type };
            if (kind == VarKind.LoopVar) v.Written = true;   // the step writes it
            vars[(key, index)] = v;
        }
        return v;
    }

    void Declare(Var v) => scopes[^1][v.Name] = v;

    Var? Lookup(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(name, out var v)) return v;
        return null;
    }

    // ---- decisions between the two walks ----

    void Decide()
    {
        // a kernel or fork branch runs on other threads: its display is copied by value into the runtime's job, so it
        // cannot write a capture through a ref field; it becomes a display class (the capture is hoisted)
        foreach (var f in funcs.Values)
            if (!f.Escapes && (!ctx.StructDisplays || f.Pos >= Pos.Range && f.Captures.Any(v => v.Written && v.Kind != VarKind.Link)))
                f.Escapes = true;
        foreach (var v in vars.Values.Distinct().ToList())
        {
            if (!v.Written || !v.CapturedBy.Any(f => f.Escapes)) continue;
            if (v.Kind == VarKind.Local && v.Decl is { RentLength: null } && v.Block != null) { }
            else if (v.Kind == VarKind.Param && v.Block != null) { }
            else
            {
                Unsupported.Add($"written {v.Kind} `{v.Name}` captured by an escaping closure (cannot be hoisted)");
                continue;
            }
            if (!hoistsByBlock.TryGetValue(v.Block, out var h))
            {
                int k = counter++;
                h = new Hoist
                {
                    Display = new AirDisplayType { Name = $"<>c__DisplayClass_{ctx.Method}_{k}", Kind = AirDisplayKind.Class, Hoisted = true },
                    Block = v.Block,
                };
                h.Link = new Var { Name = $"<>8__{k}", Kind = VarKind.Link, Owner = v.Owner, Block = v.Block, TypeCs = h.Display.Name };
                hoistsByBlock[v.Block] = h;
            }
            v.Hoist = h;
            h.Display.Fields.Add(new AirCapture { Field = v.Name, Source = new AirLocalRef(v.Name, v.Type), Type = v.Type, TypeCs = v.TypeCs, Written = true });
            if (v.Kind == VarKind.Param) h.Params.Add(v);
            foreach (var f in v.CapturedBy)
            {
                int at = f.Captures.IndexOf(v);
                if (f.Captures.Contains(h.Link)) f.Captures.RemoveAt(at);
                else f.Captures[at] = h.Link;
                if (!h.Link.CapturedBy.Contains(f)) h.Link.CapturedBy.Add(f);
            }
        }
        foreach (var (v, f, what) in namePositions)
            if (v.Hoist != null || v.Owner != f)
                Unsupported.Add($"{what} `{v.Name}` names a captured variable");
    }

    // ---- references ----

    /// <summary>A use of `name` in the current function; returns the replacement in the rewrite walk (null: keep).</summary>
    MExpr? Ref(string name, bool write, AirType? type, bool bare = true)
    {
        var v = Lookup(name);
        if (v == null && bare && ctx.Instance && thisVar != null && !Keywords.Contains(name) && !InStaticLambda()) v = thisVar;   // implicit `this.` member
        if (v == null) return null;
        bool implicitThis = v == thisVar && name != "this";
        if (!rewrite)
        {
            if (write && !implicitThis) v.Written = true;
            for (var f = cur; f != v.Owner && f != null; f = f.Parent)
            {
                f.Capture(v);
                if (!v.CapturedBy.Contains(f)) v.CapturedBy.Add(f);
            }
            return null;
        }
        if (implicitThis)
            return cur == v.Owner ? null : new MMember { Target = Access(v, cur), Name = name, Type = type };
        if (v.Hoist is { } h) return new AirDisplayField { Display = Access(h.Link, cur), Owner = h.Display, Field = v.Name, Type = type ?? v.Type };
        if (v.Owner == cur) return null;
        var e = Access(v, cur);
        e.Type = type ?? v.Type;
        return e;
    }

    /// <summary>Inside a lambda the lowerer marked `static` (it cannot reach `this`).</summary>
    bool InStaticLambda()
    {
        for (var f = cur; f != null; f = f.Parent)
            if (f.Lambda is { Static: true }) return true;
        return false;
    }

    /// <summary>The expression that reads `v` from function `f` (its own local, or a field of f's display).</summary>
    MExpr Access(Var v, Func f)
    {
        if (v.Owner == f) return v.Kind == VarKind.This ? new AirThis() : new AirLocalRef(v.Name, v.Type);
        var d = f.Display!;
        MExpr self = d.Kind == AirDisplayKind.Class ? new AirThis() : new AirLocalRef(StructSelf);
        return new AirDisplayField { Display = self, Owner = d, Field = f.Fields[v].Field, Type = v.Type };
    }

    static string Spell(MExpr e) => e switch
    {
        AirLocalRef l => l.Name,
        AirThis => "this",
        AirDisplayField f => Spell(f.Display) + "." + f.Field,
        MMember m => Spell(m.Target) + "." + m.Name,
        _ => "?",
    };

    // ---- statements ----

    void S(List<MStmt>? block, bool keepScope = false)
    {
        if (block == null) return;
        scopes.Add(new());
        int start = 0;
        if (rewrite && hoistsByBlock.TryGetValue(block, out var h))
        {
            var link = new MDecl { Name = h.Link.Name, TypeCs = h.Display.Name, Init = new AirDisplayNew { Display = h.Display } };
            block.Insert(0, link);
            for (int k = 0; k < h.Params.Count; k++)
                block.Insert(1 + k, new MAssign
                {
                    Target = new AirDisplayField { Display = new AirLocalRef(h.Link.Name), Owner = h.Display, Field = h.Params[k].Name },
                    Value = new AirLocalRef(h.Params[k].Name, h.Params[k].Type),
                });
            Declare(h.Link);
            start = 1 + h.Params.Count;   // the prologue is already converted
        }
        for (int i = start; i < block.Count; i++)
        {
            pending.Add(new());
            block[i] = Stmt(block[i], block);
            var ins = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (ins.Count > 0)
            {
                block.InsertRange(i, ins);
                i += ins.Count;
            }
        }
        if (!keepScope) scopes.RemoveAt(scopes.Count - 1);
    }

    MStmt Stmt(MStmt s, List<MStmt> block)
    {
        switch (s)
        {
            case MDecl d:
                {
                    if (d.Init != null) d.Init = X(d.Init);
                    var v = GetVar(d, 0, d.Name, VarKind.Local, block, d, d.TypeCs, d.Init?.Type);
                    Declare(v);
                    if (rewrite && v.Hoist is { } h)
                        return new MAssign
                        {
                            Target = new AirDisplayField { Display = Access(h.Link, cur), Owner = h.Display, Field = v.Name, Type = v.Type },
                            Value = d.Init ?? new AirDefault(null),
                            Span = d.Span,
                            Line = d.Line,
                        };
                    break;
                }
            case MDeconstruct dc:
                dc.Value = X(dc.Value);
                for (int k = 0; k < dc.Names.Count; k++)
                {
                    if (dc.Declare) Declare(GetVar(dc, k, dc.Names[k], VarKind.Fixed, block, null, null, null));
                    else NamePosition(dc.Names[k], "deconstruction target");
                }
                break;
            case MAssign a: a.Target = X(a.Target, write: true); a.Value = X(a.Value); break;
            case MExprStmt es: es.E = X(es.E); break;
            case MIf i: i.Cond = X(i.Cond); S(i.Then); S(i.Else); break;
            case MWhile w: w.Cond = X(w.Cond); S(w.Body); break;
            case MFor f:
                f.Start = X(f.Start);
                scopes.Add(new());
                Declare(GetVar(f, 0, f.Var, VarKind.LoopVar, null, null, f.VarType, f.Start.Type));
                NamePosition(f.Var, "loop variable");
                f.End = X(f.End);
                S(f.Body);
                scopes.RemoveAt(scopes.Count - 1);
                break;
            case MForeach fe:
                fe.Coll = X(fe.Coll);
                scopes.Add(new());
                for (int k = 0; k < fe.Vars.Count; k++) Declare(GetVar(fe, k, fe.Vars[k], VarKind.Fixed, null, null, null, null));
                S(fe.Body);
                scopes.RemoveAt(scopes.Count - 1);
                break;
            case MReturn r: if (r.Value != null) r.Value = X(r.Value); break;
            case MThrow t: t.Value = X(t.Value); break;
            case MTry t:
                S(t.Body);
                foreach (var c in t.Catches)
                {
                    scopes.Add(new());
                    if (c.Name != null) Declare(GetVar(c, 0, c.Name, VarKind.Fixed, null, null, c.TypeCs, null));
                    S(c.Body);
                    scopes.RemoveAt(scopes.Count - 1);
                }
                S(t.Finally);
                break;
            case MBlock b: if (b.Lock != null) b.Lock = X(b.Lock); S(b.Body); break;
            case MUsing u:
                u.Value = X(u.Value);
                scopes.Add(new());
                Declare(GetVar(u, 0, u.Name, VarKind.Fixed, null, null, null, u.Value.Type));
                S(u.Body);
                scopes.RemoveAt(scopes.Count - 1);
                break;
            case AirSwitch sw:
                sw.Value = X(sw.Value);
                foreach (var c in sw.Cases) S(c);
                S(sw.Default);
                break;
            case MBreak or MContinue: break;
            default:
                if (!rewrite) Unsupported.Add("statement " + s.GetType().Name);
                break;
        }
        return s;
    }

    /// <summary>A variable named by a string field of a statement (cannot be redirected to a display field).</summary>
    void NamePosition(string name, string what)
    {
        if (!rewrite && Lookup(name) is { } v) namePositions.Add((v, cur, what));
    }

    // ---- expressions ----

    static bool IsPar(MExpr? t) => t is MCode { Text: "global::Alice.Runtime.Par" };

    /// <summary>The position of argument <paramref name="k"/> of a Par entry point that runs its kernel before
    /// returning and never stores it (the struct-kernel overloads of ParKernels.cs exist for these); anything else escapes.</summary>
    static Pos CallPos(MCall c, int k)
    {
        if (!IsPar(c.Target)) return Pos.Escapes;
        if (c.Name == "Range" && k == 1) return Pos.Range;
        if (c.Name.StartsWith("Reduce", StringComparison.Ordinal) && k == 1) return Pos.Reduce;
        if (c.Name == "Fork") return Pos.Fork;
        return Pos.Escapes;
    }

    MExpr X(MExpr e, bool write = false, Pos pos = Pos.Escapes)
    {
        switch (e)
        {
            case AirLocalRef l:
                {
                    var r = Ref(l.Name, write, l.Narrowed ? null : l.Type, bare: false);
                    return r == null ? l : l.Narrowed ? new MMember { Target = r, Name = "Value", Type = l.Type } : r;
                }
            case AirParamRef pr: return Ref(pr.Name, write, pr.Type, bare: false) ?? pr;
            case AirThis th: return Ref(th.Extension ? "self" : "this", false, th.Type, bare: false) ?? th;
            case AirFieldRef fr:
                {
                    // a bare field of the current type: an implicit `this.` member in an instance method
                    if (Lookup(fr.Name) == null && Ref(fr.Name, write, fr.Type) is { } t) return t;
                    return fr;
                }
            case AirArrayLength al:
                {
                    var r = Ref(al.Array, false, null, bare: false);
                    return r == null ? al : new AirLength { Target = r, Type = al.Type };
                }
            case AirBase:
                if (!rewrite && cur != root) Unsupported.Add("`base` inside a closure");
                return e;
            case AirConst or AirNull or AirTypeRef or AirMethodGroup or AirDefault or AirTypeOf or AirSizeOf or AirDiscard or AirYieldBreak:
                return e;
            case MCode c: return Code(c, write);   // AirName, AirRawCs and untyped text
            case MCall c:
                {
                    if (c.Target == null && Lookup(c.Name) is { Kind: not VarKind.This })
                    {
                        // `f(x)` of a local delegate: a use of the local
                        List(c.Args);
                        var callee = Ref(c.Name, false, null);
                        return callee == null ? c : new MInvoke { Target = callee, Args = c.Args, Type = c.Type };
                    }
                    if (c.Target != null) c.Target = X(c.Target);
                    else if (ctx.Instance && thisVar != null && !InStaticLambda())
                    {
                        // an unqualified call in an instance method may be an instance member
                        var t = Ref("this", false, null);
                        if (t != null) c.Target = t;
                    }
                    for (int k = 0; k < c.Args.Count; k++) c.Args[k] = X(c.Args[k], pos: CallPos(c, k));
                    return c;
                }
            case MMember m: m.Target = X(m.Target); return m;
            case MInvoke i: i.Target = X(i.Target, pos: i.Target is MLambda ? Pos.Direct : Pos.Escapes); List(i.Args); return i;
            case MNew n: List(n.Args); if (n.Init != null) List(n.Init); return n;
            case MNewArray a: if (a.Size != null) a.Size = X(a.Size); if (a.Items != null) List(a.Items); return a;
            case MIndex ix: ix.Target = X(ix.Target); List(ix.Args); return ix;
            case MBinary b: b.L = X(b.L); b.R = X(b.R); return b;
            case MUnary u: u.E = X(u.E, write: u.Op is AirUnOp.Inc or AirUnOp.Dec); return u;
            case MCond c: c.C = X(c.C); c.A = X(c.A); c.B = X(c.B); return c;
            case MCast c: c.E = X(c.E); return c;
            case MIs i:
                {
                    i.E = X(i.E);
                    // `x is T y` declares the pattern variable y in the enclosing scope
                    var words = i.TypeCs.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 1 && IsIdent(words[^1]) && !Keywords.Contains(words[^1]))
                        Declare(GetVar(i, 0, words[^1], VarKind.Fixed, null, null, string.Join(' ', words[..^1]), null));
                    return i;
                }
            case MInterp ip: List(ip.Holes); return ip;
            case MTuple t: List(t.Items); return t;
            case MPair p: p.K = X(p.K); p.V = X(p.V); return p;
            case MArg { Prefix: "out var " or "out var" } od when NameOfLocal(od.E) is { } declared:
                // `out var x` declares x in the enclosing scope
                Declare(GetVar(od, 0, declared, VarKind.Fixed, null, null, null, od.E.Type));
                return od;
            case MArg a: a.E = X(a.E, write: a.Prefix.TrimStart() is var pf && (pf.StartsWith("ref ") || pf.StartsWith("out ") || pf.StartsWith("in "))); return a;
            case MLambda l: return Lambda(l, pos);
            case AirPar p:
                p.Count = X(p.Count);
                p.Kernel = X(p.Kernel, pos: p.Kind == AirParKind.Range ? Pos.Range : Pos.Reduce);
                if (p.Combine != null) p.Combine = X(p.Combine);   // a Func<T, T, T> in every overload
                return p;
            case AirForkJoin f:
                for (int k = 0; k < f.Branches.Count; k++) f.Branches[k] = X(f.Branches[k], pos: Pos.Fork);
                return f;
            case AirParGuard g:
                g.Count = X(g.Count);
                foreach (var d in g.Distinct) X(d);
                return g;
            case AirReferenceEqualsGuard r: r.W = X(r.W); r.R = X(r.R); return r;
            case AirParProbe pr:
                if (pr.I != null) pr.I = X(pr.I);
                if (pr.Start != null) pr.Start = X(pr.Start);
                if (pr.T0 != null) pr.T0 = X(pr.T0);
                if (pr.Count != null) pr.Count = X(pr.Count);
                return pr;
            case AirRent r: List(r.Args); return r;
            case AirRelease r: r.Target = X(r.Target); return r;
            case AirLength l: l.Target = X(l.Target); return l;
            case AirTripCount t: t.End = X(t.End); t.Start = X(t.Start); return t;
            case AirOffset o: o.Base = X(o.Base); o.Offset = X(o.Offset); return o;
            case AirPresizeCount pc: pc.From = X(pc.From); pc.To = X(pc.To); return pc;
            case AirRangeLength rl: rl.Lo = X(rl.Lo); rl.Hi = X(rl.Hi); return rl;
            case AirRefOrAddDefault ra: ra.Dict = X(ra.Dict); ra.Key = X(ra.Key); return ra;
            case AirRefOrNullRef rn: rn.Dict = X(rn.Dict); rn.Key = X(rn.Key); return rn;
            case AirAlternateLookup al: al.Dict = X(al.Dict); return al;
            case AirSpanSplit sp: sp.Str = X(sp.Str); sp.Sep = X(sp.Sep); return sp;
            case AirSeq sq:
                {
                    // the prelude's declarations are visible to the value
                    int depth = scopes.Count;
                    S(sq.Pre, keepScope: true);
                    sq.Value = X(sq.Value);
                    while (scopes.Count > depth) scopes.RemoveAt(scopes.Count - 1);
                    return sq;
                }
            case AirAs a: a.E = X(a.E); return a;
            case AirVectorOp vo: List(vo.Args); return vo;
            case AirVectorLoad vl: vl.Array = X(vl.Array); vl.Index = X(vl.Index); return vl;
            case AirVectorStore vs: vs.Value = X(vs.Value); vs.Array = X(vs.Array); vs.Index = X(vs.Index); return vs;
            case AirVectorReduce vr: vr.Arg = X(vr.Arg); return vr;
            case AirVectorFits vf: vf.Index = X(vf.Index); vf.End = X(vf.End); return vf;
            case AirStackAlloc sa: sa.Count = X(sa.Count); return sa;
            case AirStackBuffer or AirForkable:
            case AirDelegateCreate or AirDisplayNew or AirDisplayField:   // already converted
                return e;
            default:
                if (!rewrite) Unsupported.Add("expression " + e.GetType().Name);
                return e;
        }
    }

    void List(List<MExpr> xs)
    {
        for (int k = 0; k < xs.Count; k++) xs[k] = X(xs[k]);
    }

    // ---- opaque text ----

    static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while", "var", "nameof", "global", "dynamic", "nint", "nuint", "when",
        "and", "or", "not", "with", "await", "async", "value", "_",
    ];

    MExpr Code(MCode c, bool write)
    {
        var t = c.Text;
        if (IsIdent(t)) return Ref(t, write, c.Type) ?? c;
        if (t.Length == 0) return c;
        var outText = rewrite ? new StringBuilder() : null;
        bool changed = false;
        int i = 0;
        while (i < t.Length)
        {
            char ch = t[i];
            if (ch == '"' || ch == '\'' || (ch is '@' or '$' && i + 1 < t.Length && t[i + 1] is '"' or '@' or '$'))
            {
                int end = SkipLiteral(t, i);
                outText?.Append(t, i, end - i);
                i = end;
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int j = i;
                while (j < t.Length && (char.IsLetterOrDigit(t[j]) || t[j] == '_')) j++;
                string word = t[i..j];
                int p = i - 1;
                while (p >= 0 && t[p] == ' ') p--;
                bool member = p >= 0 && (t[p] == '.' || (t[p] == ':' && p > 0 && t[p - 1] == ':')) || (p >= 0 && t[p] == '@');
                bool qualifier = j + 1 < t.Length && t[j] == ':' && t[j + 1] == ':';
                bool named = !qualifier && j < t.Length && t[j] == ':' && (j + 1 >= t.Length || t[j + 1] != ':');
                MExpr? rep = null;
                if (!member && !qualifier && !named && !Keywords.Contains(word) || word == "this" && !member)
                    rep = Ref(word, MirFacts.WriteForm(t, word), null);
                if (rep != null) { outText!.Append(Spell(rep)); changed = true; }
                else outText?.Append(word);
                i = j;
                continue;
            }
            if (char.IsDigit(ch))
            {
                int j = i;
                while (j < t.Length && (char.IsLetterOrDigit(t[j]) || t[j] == '_' || t[j] == '.' && j + 1 < t.Length && char.IsDigit(t[j + 1]))) j++;
                outText?.Append(t, i, j - i);
                i = j;
                continue;
            }
            outText?.Append(ch);
            i++;
        }
        if (!changed) return c;
        OpaqueRewrites++;
        return new MCode(outText!.ToString()) { Type = c.Type };
    }

    static string? NameOfLocal(MExpr e) => e switch
    {
        AirLocalRef l => l.Name,
        MCode c when IsIdent(c.Text) => c.Text,
        _ => null,
    };

    static bool IsIdent(string t) =>
        t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    /// <summary>The index after a string or char literal starting at <paramref name="i"/> (interpolations are skipped
    /// whole: their holes are opaque to the pass and conservatively not rewritten).</summary>
    static int SkipLiteral(string t, int i)
    {
        bool verbatim = false;
        while (i < t.Length && t[i] is '@' or '$') { if (t[i] == '@') verbatim = true; i++; }
        if (i >= t.Length) return i;
        char q = t[i++];
        int depth = 0;
        while (i < t.Length)
        {
            char ch = t[i];
            if (ch == '\\' && !verbatim) { i += 2; continue; }
            if (ch == '{') depth++;
            else if (ch == '}' && depth > 0) depth--;
            else if (ch == q && depth == 0)
            {
                if (verbatim && i + 1 < t.Length && t[i + 1] == q) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return i;
    }

    // ---- lambdas ----

    static string ParamName(string p)
    {
        var parts = p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? p : parts[^1];
    }

    static string? ParamType(string p)
    {
        var parts = p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? string.Join(' ', parts[..^1]) : null;
    }

    MExpr Lambda(MLambda l, Pos pos)
    {
        Func f;
        if (!rewrite)
        {
            f = new Func { Lambda = l, Parent = cur, Id = counter++, HasFrame = l.Body != null };
            f.Pos = cur.HasFrame ? pos : Pos.Escapes;
            f.Escapes = f.Pos == Pos.Escapes;
            funcs[l] = f;
        }
        else f = funcs[l];
        var outer = cur;
        int depth = scopes.Count;
        cur = f;
        scopes.Add(new());
        for (int k = 0; k < l.Params.Count; k++)
        {
            var v = GetVar(l, k, ParamName(l.Params[k]), VarKind.Param, l.Body, null, ParamType(l.Params[k]), null);
            if (!rewrite) f.Params.Add(v);
            Declare(v);
        }
        AirClosureMethod? method = null;
        if (rewrite)
        {
            method = MakeMethod(f, outer);
            Displays.AddRange(f.Display is { } d ? [d] : []);
        }
        if (l.Body != null) S(l.Body);
        else
        {
            pending.Add(new());
            l.ExprBody = X(l.ExprBody!);
            if (pending[^1].Count > 0 && rewrite) throw new InvalidOperationException("closure-convert: display local in an expression body");
            pending.RemoveAt(pending.Count - 1);
        }
        while (scopes.Count > depth) scopes.RemoveAt(scopes.Count - 1);
        cur = outer;
        if (!rewrite) return l;
        method!.ExprBody = l.ExprBody;
        method.Body = l.Body;
        return Create(f, method, outer, l);
    }

    AirClosureMethod MakeMethod(Func f, Func outer)
    {
        var l = f.Lambda!;
        var m = new AirClosureMethod { Name = $"<{ctx.Method}>b__{f.Id}", Hot = l.Hot };
        m.Params.AddRange(l.Params);
        if (f.Captures.Count == 0)
        {
            m.Static = true;
            StaticMethods.Add(m);
            return m;
        }
        var kind = f.Escapes ? AirDisplayKind.Class : AirDisplayKind.Struct;
        var d = new AirDisplayType
        {
            Name = $"<>c__DisplayClass_{ctx.Method}_{f.Id}", Kind = kind,
            Role = kind == AirDisplayKind.Class ? AirKernelRole.None : f.Pos switch
            {
                Pos.Range => AirKernelRole.Range,
                Pos.Reduce => AirKernelRole.Reduce,
                Pos.Fork => AirKernelRole.Fork,
                _ => AirKernelRole.None,
            },
        };
        var used = new HashSet<string>();
        foreach (var v in f.Captures)
        {
            string field = v.Kind == VarKind.This ? ThisField : v.Name;
            while (!used.Add(field)) field += "_";
            var cap = new AirCapture
            {
                Field = field, Source = Access(v, outer), Type = v.Type, TypeCs = v.TypeCs,
                Written = v.Written && v.Kind != VarKind.Link, IsThis = v.Kind == VarKind.This, IsLink = v.Kind == VarKind.Link,
            };
            cap.ByRef = kind == AirDisplayKind.Struct && cap.Written;
            f.Fields[v] = cap;
            d.Fields.Add(cap);
        }
        d.Methods.Add(m);
        m.Owner = d;
        if (kind == AirDisplayKind.Struct)
        {
            m.Static = true;
            m.Params.Insert(0, $"ref {d.Name} {StructSelf}");
        }
        f.Display = d;
        return m;
    }

    MExpr Create(Func f, AirClosureMethod m, Func outer, MLambda l)
    {
        if (m.Owner == null)
        {
            m.CachedField = $"<>9__{ctx.Method}_{f.Id}";
            return new AirDelegateCreate { Method = m, CachedField = m.CachedField, Type = l.Type, Span = l.Span };
        }
        var d = m.Owner;
        if (d.Kind == AirDisplayKind.Class)
            return new AirDelegateCreate { Method = m, Target = new AirDisplayNew { Display = d }, Type = l.Type, Span = l.Span };
        string local = $"<>d__{f.Id}";
        pending[^1].Add(new MDecl { Name = local, TypeCs = d.Name, Init = new AirDisplayNew { Display = d } });
        return new AirDelegateCreate { Method = m, Target = new AirLocalRef(local), ByRefTarget = true, Type = l.Type, Span = l.Span };
    }

    // ---- declarations for the backend ----

    /// <summary>Every display type and capture-free closure method a converted body uses (nested ones included), in
    /// first-use order: what the IL backend declares next to the method.</summary>
    public static (List<AirDisplayType> Displays, List<AirClosureMethod> Static) Declarations(List<MStmt> body)
    {
        var ds = new List<AirDisplayType>();
        var ms = new List<AirClosureMethod>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Method(AirClosureMethod m)
        {
            if (!seen.Add(m)) return;
            if (m.Owner == null) ms.Add(m);
            else Display(m.Owner);
            if (m.ExprBody != null) Expr(m.ExprBody);
            Stmts(m.Body);
        }
        void Display(AirDisplayType d)
        {
            if (!seen.Add(d)) return;
            ds.Add(d);
            foreach (var f in d.Fields) Expr(f.Source);
            foreach (var m in d.Methods) Method(m);
        }
        void Expr(MExpr e)
        {
            switch (e)
            {
                case AirDelegateCreate dc: Method(dc.Method); if (dc.Target != null) Expr(dc.Target); return;
                case AirDisplayNew dn: Display(dn.Display); return;
                case AirDisplayField df: Expr(df.Display); return;
                case MLambda l: if (l.ExprBody != null) Expr(l.ExprBody); Stmts(l.Body); return;
                case AirPar p: Expr(p.Count); Expr(p.Kernel); if (p.Combine != null) Expr(p.Combine); return;
                case AirSeq sq: Stmts(sq.Pre); Expr(sq.Value); return;
            }
            foreach (var c in MirFacts.Children(e)) Expr(c);
        }
        void Stmts(List<MStmt>? list)
        {
            if (list == null) return;
            foreach (var s in list)
                switch (s)
                {
                    case MDecl d: if (d.Init != null) Expr(d.Init); break;
                    case MDeconstruct dc: Expr(dc.Value); break;
                    case MAssign a: Expr(a.Target); Expr(a.Value); break;
                    case MExprStmt es: Expr(es.E); break;
                    case MIf i: Expr(i.Cond); Stmts(i.Then); Stmts(i.Else); break;
                    case MWhile w: Expr(w.Cond); Stmts(w.Body); break;
                    case MFor f: Expr(f.Start); Expr(f.End); Stmts(f.Body); break;
                    case MForeach fe: Expr(fe.Coll); Stmts(fe.Body); break;
                    case MReturn r: if (r.Value != null) Expr(r.Value); break;
                    case MThrow t: Expr(t.Value); break;
                    case MTry t: Stmts(t.Body); foreach (var c in t.Catches) Stmts(c.Body); Stmts(t.Finally); break;
                    case MBlock b: if (b.Lock != null) Expr(b.Lock); Stmts(b.Body); break;
                    case MUsing u: Expr(u.Value); Stmts(u.Body); break;
                    case AirSwitch sw: Expr(sw.Value); foreach (var c in sw.Cases) Stmts(c); Stmts(sw.Default); break;
                }
        }
        Stmts(body);
        return (ds, ms);
    }
}
