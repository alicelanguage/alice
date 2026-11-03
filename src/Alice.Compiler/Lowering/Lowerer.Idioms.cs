using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Idiom rewrites with identical semantics but less work than the code a typical C# developer writes:
/// - `d[k] = d.GetValueOrDefault(k) + e` -> one hash probe (CollectionsMarshal.GetValueRefOrAddDefault)
/// - `d[k] += e`                         -> one hash probe, still throws KeyNotFoundException
/// - `for w in s.Split(c)`               -> span split (no string[]); when `w` is only a dictionary key,
///                                          the .NET 9 alternate lookup avoids allocating a substring per word
/// </summary>
public sealed partial class Lowerer
{
    const string CM = "global::System.Runtime.InteropServices.CollectionsMarshal";

    sealed class SpanKey
    {
        public LocalSym Word = null!;
        public string SpanVar = "";
        public Dictionary<string, string> Alt = new();   // dictionary C# expr -> alternate-lookup local
    }

    SpanKey? spanKey;

    static readonly AirType ReadOnlyCharSpan = new AirType.ClrRef("global::System.ReadOnlySpan", typeof(ReadOnlySpan<char>), [new AirType.Prim(PrimKind.Char)]);

    public static bool IsDict(AType t, out AType key, out AType val) => IsDictionary(t, out key, out val);

    static bool IsDictionary(AType t, out AType key, out AType val)
    {
        key = val = AType.Unknown;
        if (t.Clr is not { IsGenericType: true } c || c.GetGenericTypeDefinition() != typeof(Dictionary<,>) || t.Args.Count != 2) return false;
        key = t.Args[0];
        val = t.Args[1];
        return true;
    }

    static bool SameExpr(Syntax.Expr a, Syntax.Expr b) => (a, b) switch
    {
        (NameExpr x, NameExpr y) => x.Name == y.Name && x.Sym == y.Sym,
        (IvarExpr x, IvarExpr y) => x.Name == y.Name,
        (LitExpr x, LitExpr y) => x.Kind == y.Kind && x.Text == y.Text,
        _ => false,
    };

    static bool Pure(Syntax.Expr e) => e is NameExpr { Res: "local" or "field" } or IvarExpr or LitExpr;

    /// <summary>`d[k] = d.GetValueOrDefault(k) (+|-) e` -> (dict, key, op, delta).</summary>
    static (IndexExpr Ix, string Op, Syntax.Expr Delta)? CounterUpdate(Assign a)
    {
        if (a.Target is not IndexExpr ix || ix.Args.Count != 1 || !IsDictionary(ix.Target.Type, out _, out var v) || !v.IsNumeric) return null;
        // a DefaultHash (`Hash.new(0)`) counts from its default: its own indexer, not a raw slot
        if (ix.Target.Type.Cs.StartsWith("global::Alice.Runtime.DefaultHash<")) return null;
        if (!Pure(ix.Target) || !Pure(ix.Args[0].Value!)) return null;
        if (a.Op is "+=" or "-=") return (ix, a.Op, a.Value);
        if (a.Op != "=" || a.Value is not BinaryExpr { Op: "+" or "-" } b) return null;
        if (b.L is not CallExpr { Name: "GetValueOrDefault", Args.Count: 1 } g || g.Recv == null || !SameExpr(g.Recv, ix.Target) || !SameExpr(g.Args[0].Value!, ix.Args[0].Value!)) return null;
        return (ix, b.Op + "=", b.R);
    }

    bool TryCounterUpdate(List<MStmt> o, Assign a)
    {
        var cu = CounterUpdate(a);
        if (cu == null) return false;
        var (ix, op, delta) = cu.Value;
        var dict = Expr(ix.Target);
        string dictCs = Backend.Printer.E(dict);
        var key = ix.Args[0].Value!;
        IsDictionary(ix.Target.Type, out _, out var vt);
        if (a.Op == "=")
        {
            // missing key starts at default(V): exactly GetValueOrDefault semantics, one probe
            var slotRef = new AirType.ByRef(vt.ToAir(), RefKind.Ref);
            var target = spanKey != null && key is NameExpr kn && kn.Sym == spanKey.Word && spanKey.Alt.TryGetValue(dictCs, out var alt)
                ? new AirRefOrAddDefault { Dict = new AirLocalRef(alt), Key = new AirLocalRef(spanKey.SpanVar, ReadOnlyCharSpan), Type = slotRef }
                : new AirRefOrAddDefault { Dict = dict, Key = Expr(key), Type = slotRef };
            o.Add(new MAssign { Target = target, Op = op, Value = Expr(delta) });
            return true;
        }
        string slot = Temp("slot");
        var refType = new AirType.ByRef(vt.ToAir(), RefKind.Ref);
        o.Add(new MDecl { Name = slot, TypeCs = "ref " + vt.Cs, Init = new MArg { Prefix = "ref ", E = new AirRefOrNullRef { Dict = dict, Key = Expr(key), Type = refType } } });
        o.Add(new MIf
        {
            Cond = new MCall { Target = AirTypeRef.Named("global::System.Runtime.CompilerServices.Unsafe"), Name = "IsNullRef", Args = { new MArg { Prefix = "ref ", E = new AirLocalRef(slot, refType) } }, Type = new AirType.Prim(PrimKind.Bool) },
            Then = { new MThrow { Value = new MNew { TypeCs = "global::System.Collections.Generic.KeyNotFoundException" } } },
        });
        o.Add(new MAssign { Target = new AirLocalRef(slot, refType), Op = op, Value = Expr(delta) });
        return true;
    }

    /// <summary>`for w in s.Split(c)` over a string with a one-char separator.</summary>
    MStmt? TrySplitLoop(ForStmt f)
    {
        if (Method?.IsAsync == true) return null;   // spans cannot live across await
        if (f.Names.Count != 1 || f.Iter is not CallExpr { Name: "Split", Args.Count: 1, Block: null } c || c.Recv == null || !c.Recv.Type.IsString) return null;
        MExpr? sep = c.Args[0].Value switch
        {
            LitExpr { Kind: "char" } l => Expr(l),
            LitExpr { Kind: "str" } sl when sl.Text.Length == 1 && sl.Text != "\\" => AirConst.Of(sl.Text[0]),
            _ => null,
        };
        if (sep == null) return null;
        var w = f.Locals[0];
        var outp = new List<MStmt>();
        var str = Expr(c.Recv);
        if (!SimpleLeaf(str))
        {
            string tmp = Temp("str");
            pre.Add(new MDecl { Name = tmp, Init = str });
            str = new AirLocalRef(tmp, new AirType.Prim(PrimKind.String));
        }
        string range = Temp("rg");
        var rangeRef = new AirLocalRef(range, new AirType.ClrRef("global::System.Range", typeof(Range), []));
        var uses = new List<NameExpr>();
        Collect(f.Body, w, uses);
        var counters = new List<Assign>();
        CollectCounters(f.Body, w, counters);
        int keyUses = counters.Count;
        bool spanOnly = uses.Count > 0 && uses.Count == keyUses * 2 - counters.Count(a => a.Op != "=") && counters.All(a => IsDictionary(((IndexExpr)a.Target).Target.Type, out var k, out _) && k.IsString);
        var body = new List<MStmt>();
        var saved = spanKey;
        if (spanOnly)
        {
            var sk = new SpanKey { Word = w, SpanVar = Temp("w") };
            foreach (var a in counters)
            {
                var dictExpr = Expr(((IndexExpr)a.Target).Target);
                string d = Backend.Printer.E(dictExpr);
                if (sk.Alt.ContainsKey(d)) continue;
                string alt = Temp("alt");
                pre.Add(new MDecl { Name = alt, Init = new AirAlternateLookup { Dict = dictExpr } });
                sk.Alt[d] = alt;
            }
            body.Add(new MDecl { Name = sk.SpanVar, TypeCs = "global::System.ReadOnlySpan<char>", Init = new MIndex { Target = new MCall { Target = str, Name = "AsSpan", Type = ReadOnlyCharSpan }, Args = { rangeRef }, Type = ReadOnlyCharSpan } });
            spanKey = sk;
        }
        else body.Add(new MDecl { Name = w.CsName, Init = new MIndex { Target = str, Args = { rangeRef }, Type = new AirType.Prim(PrimKind.String) } });
        ctx.Add("loop");
        body.AddRange(Stmts(f.Body, Tail.None, null));
        ctx.RemoveAt(ctx.Count - 1);
        spanKey = saved;
        MStmt serial = new MForeach { Vars = { range }, Coll = new AirSpanSplit { Str = str, Sep = sep }, Body = body };
        if (spanOnly && f.Body.Count == 1 && counters.Count == 1 && ParallelWordCount(counters[0], str, sep) is { } par)
            return new MIf { Cond = new AirParGuard { Count = new MMember { Target = str, Name = "Length", Type = new AirType.Prim(PrimKind.Int) }, Min = Threshold, Type = new AirType.Prim(PrimKind.Bool) }, Then = { par }, Else = new List<MStmt> { serial } };
        return serial;
    }

    /// <summary>
    /// Parallel word count: the text is cut at separator positions (so every chunk splits exactly like the whole
    /// string), each worker counts into its own dictionary via the span lookup, and partials are merged in chunk
    /// order, which reproduces the serial insertion (and therefore iteration) order.
    /// </summary>
    MStmt? ParallelWordCount(Assign counter, MExpr str, MExpr sep)
    {
        if (inParallelGen || ctx.Contains("lambda") || Method is { TypeParams.Count: > 0 }) return null;
        var cu = CounterUpdate(counter);
        if (cu == null || cu.Value.Ix.Target is not NameExpr { Sym: LocalSym dict }) return null;
        var (_, op, delta) = cu.Value;
        if (!PureExpr(delta, new()) || WalkExpr(delta).Any(e => e is NameExpr { Sym: LocalSym l } && l == spanKey?.Word)) return null;
        var captured = WalkExpr(delta).OfType<NameExpr>().Where(n => n is { Res: "local" }).Select(n => (LocalSym)n.Sym!).Distinct().ToList();
        if (captured.Any(l => !l.Type.IsKnown) || captured.Contains(dict)) return null;
        IsDictionary(dict.Type, out _, out var vt);
        string V = TypeCs(vt);
        string helper = $"__parwords_{(Method?.CsName ?? "main").TrimStart('@')}_{++helperN}";
        var strT = new AirType.Prim(PrimKind.String);
        var intT = new AirType.Prim(PrimKind.Int);
        var dictT = new AirType.ClrRef("global::System.Collections.Generic.Dictionary", null, [strT, AirOf(vt, V)]);
        var ps = new List<AirParamDecl> { new("__text", strT), new("__dict", dictT) };
        ps.AddRange(captured.Select(HelperParam));
        AirLocalRef L(string n, AirType? t = null) => new(n, t);
        MExpr Text() => L("__text", strT);
        MExpr Len(MExpr e) => new MMember { Target = e, Name = "Length", Type = intT };
        MExpr Count(MExpr e) => new MMember { Target = e, Name = "Count", Type = intT };
        MBinary Bin(AirBinOp op, MExpr l, MExpr r) => new() { Op = op, L = l, R = r };
        MExpr I(int v) => AirConst.Int(v);
        MExpr Cut(MExpr k) => new MIndex { Target = L("cuts"), Args = { k }, Type = intT };
        var math = AirTypeRef.Named("global::System.Math");
        var par = AirTypeRef.Named("global::Alice.Runtime.Par");
        var cm = AirTypeRef.Named(CM);
        MExpr RefOrAdd(MExpr dictE, MExpr key) => new MCall { Target = cm, Name = "GetValueRefOrAddDefault", Args = { dictE, key, new MArg { Prefix = "out ", E = new AirDiscard() } } };
        string dictCs = dictT.ToCs();
        // the chunk body: count one chunk's words into its own dictionary through the span lookup
        var chunk = new List<MStmt>
        {
            new MDecl { Name = "len", TypeCs = "int", Init = Bin(AirBinOp.Sub, Bin(AirBinOp.Sub, Cut(Bin(AirBinOp.Add, L("c"), I(1))), Cut(L("c"))), I(1)) },
            new MDecl { Name = "local", Init = new MNew { TypeCs = dictCs, Args = { new MCall { Target = math, Name = "Min", Args = { Bin(AirBinOp.Add, Bin(AirBinOp.Div, L("len"), I(64)), I(16)), I(16384) } } } } },   // pre-sized (bounded): few rehashes while counting
            new MDecl { Name = "alt", Init = new AirAlternateLookup { Dict = L("local") } },
            new MDecl { Name = "span", Init = new MCall { Target = Text(), Name = "AsSpan", Args = { Bin(AirBinOp.Add, Cut(L("c")), I(1)), Bin(AirBinOp.Sub, Bin(AirBinOp.Sub, Cut(Bin(AirBinOp.Add, L("c"), I(1))), Cut(L("c"))), I(1)) } } },
            new MForeach
            {
                Vars = { "r" }, Coll = new MCall { Target = L("span"), Name = "Split", Args = { sep } },
                Body = { new MAssign { Target = RefOrAdd(L("alt"), new MIndex { Target = L("span"), Args = { L("r") } }), Op = op, Value = Expr(delta) } },
            },
            new MAssign { Target = new MIndex { Target = L("locals"), Args = { L("c") } }, Value = L("local") },
        };
        var body = new List<MStmt>
        {
            // one partial dictionary per core keeps the merge small
            new MDecl { Name = "parts", TypeCs = "int", Init = new MCall { Target = math, Name = "Min", Args = { new MMember { Target = par, Name = "Workers" }, Bin(AirBinOp.Add, Bin(AirBinOp.Div, Len(Text()), I(16384)), I(1)) } } },
            new MDecl { Name = "cuts", Init = new MNew { TypeCs = "global::System.Collections.Generic.List<int>", Init = [I(-1)] } },
            new MFor
            {
                VarType = "int", Var = "k", Start = I(1), Cmp = "<", End = L("parts"),
                Body =
                {
                    new MDecl { Name = "at", TypeCs = "int", Init = new MCall { Target = Text(), Name = "IndexOf", Args = { sep, new MCast { TypeCs = "int", E = Bin(AirBinOp.Div, Bin(AirBinOp.Mul, new MCast { TypeCs = "long", E = Len(Text()) }, L("k")), L("parts")) } } } },
                    new MIf { Cond = Bin(AirBinOp.Lt, L("at"), I(0)), Then = { new MBreak() } },
                    new MIf { Cond = Bin(AirBinOp.Gt, L("at"), new MIndex { Target = L("cuts"), Args = { new MUnary { Op = AirUnOp.FromEnd, E = I(1) } } }), Then = { new MExprStmt { E = new MCall { Target = L("cuts"), Name = "Add", Args = { L("at") } } } } },
                },
            },
            new MExprStmt { E = new MCall { Target = L("cuts"), Name = "Add", Args = { Len(Text()) } } },
            new MDecl { Name = "locals", Init = new MNewArray { ElemCs = dictCs, Size = Bin(AirBinOp.Sub, Count(L("cuts")), I(1)) } },
            new MExprStmt
            {
                E = new MCall
                {
                    Target = par, Name = "Range",
                    Args = { Len(L("locals")), new MLambda { Params = { "lo", "hi" }, Body = [new MFor { VarType = "int", Var = "c", Start = L("lo"), Cmp = "<", End = L("hi"), Body = chunk }] }, I(1) },
                },
            },
            new MDecl { Name = "total", TypeCs = "int", Init = Count(L("__dict", dictT)) },
            new MForeach { Vars = { "local" }, Coll = L("locals"), Body = { new MAssign { Target = L("total"), Op = "+=", Value = Count(L("local")) } } },
            new MExprStmt { E = new MCall { Target = L("__dict", dictT), Name = "EnsureCapacity", Args = { L("total") } } },
            new MForeach
            {
                Vars = { "local" }, Coll = L("locals"),
                Body = { new MForeach { Vars = { "kv" }, Coll = L("local"), Body = { new MAssign { Target = RefOrAdd(L("__dict", dictT), new MMember { Target = L("kv"), Name = "Key" }), Op = "+=", Value = new MMember { Target = L("kv"), Name = "Value" } } } } },
            },
        };
        // not run through the MIR passes: this body was always emitted as written
        AddHelper(new AirHelperDecl { Method = new AirMethodDecl { Name = helper, Vis = AirVis.Private, Flags = AirMethodFlags.Static, Params = ps, Ret = AirLeaf.Void, Body = body } });
        var call = new MCall { Name = helper, Args = { str, new AirLocalRef(dict.CsName, dict.Type.ToAir()) }, Type = new AirType.Prim(PrimKind.Void) };
        call.Args.AddRange(captured.Select(l => (MExpr)new AirLocalRef(l.CsName, l.Type.ToAir())));
        return new MExprStmt { E = call };
    }

    static void Collect(List<Stmt> body, LocalSym w, List<NameExpr> into)
    {
        foreach (var n in Walk(body)) if (n is NameExpr ne && ne.Sym == w) into.Add(ne);
    }

    static void CollectCounters(List<Stmt> body, LocalSym w, List<Assign> into)
    {
        foreach (var s in WalkStmts(body))
            if (s is Assign a && a.Op == "=" && CounterUpdate(a) is { } cu && cu.Ix.Args[0].Value is NameExpr k && k.Sym == w) into.Add(a);
    }

    /// <summary>
    /// Every statement reachable from `body`: nested blocks, `using` bodies, and the statements of if/case/begin used
    /// as a value (StmtExpr, also an inlined `instance_eval` body). Not the bodies of blocks, lambdas or `spawn`: those
    /// run later or elsewhere and are separate scopes (Walk reaches their expressions).
    /// </summary>
    internal static IEnumerable<Stmt> WalkStmts(List<Stmt> body)
    {
        foreach (var s in body)
        {
            yield return s;
            IEnumerable<List<Stmt>> kids = s switch
            {
                IfStmt i => i.Bodies.Concat(i.Else != null ? [i.Else] : []),
                WhileStmt wl => [wl.Body],
                ForStmt fr => [fr.Body],
                CaseStmt cs => cs.Whens.Select(x => x.Body).Concat(cs.Else != null ? [cs.Else] : []),
                BeginStmt b => new[] { b.Body }.Concat(b.Rescues.Select(r => r.Body)).Concat(b.Ensure != null ? [b.Ensure] : []),
                UsingStmt u => [u.Body],
                _ => [],
            };
            foreach (var k in kids)
                foreach (var x in WalkStmts(k)) yield return x;
            foreach (var r in Roots(s))
                if (r != null)
                    foreach (var se in StmtValues(r))
                        foreach (var x in WalkStmts([se.Stmt])) yield return x;
        }
    }

    /// <summary>The expressions a statement evaluates itself (not those of its nested statement lists).</summary>
    static IEnumerable<Syntax.Expr?> Roots(Stmt s) => s switch
    {
        ExprStmt es => [es.Expr],
        Assign a => [a.Target, a.Value],
        LocalDecl d => [d.Value],
        MultiAssign m => m.Values,
        IfStmt i => i.Conds,
        WhileStmt wl => [wl.Cond],
        ForStmt fr => [fr.Iter],
        CaseStmt cs => cs.Whens.SelectMany(x => x.Patterns.Append(x.Guard)).Prepend(cs.Subject),
        ReturnStmt r => [r.Value],
        NextStmt n => [n.Value],
        RaiseStmt rs => [rs.Value],
        UsingStmt u => [u.Value],
        BeginStmt b => [b.Lock],
        _ => [],
    };

    /// <summary>if/case/begin used as a value directly inside e (not inside blocks, lambdas or spawn, nor nested ones).</summary>
    static IEnumerable<StmtExpr> StmtValues(Syntax.Expr e)
    {
        if (StmtValue(e) is { } se) { yield return se; yield break; }
        if (e is LambdaExpr) yield break;
        foreach (var k in Kids(e))
            if (k != null)
                foreach (var x in StmtValues(k)) yield return x;
    }

    static StmtExpr? StmtValue(Syntax.Expr e) => e as StmtExpr ?? (e is CallExpr { Res: "inline", Sym: StmtExpr inl } ? inl : null);

    /// <summary>All expression nodes reachable from statements (used for simple use-analysis).</summary>
    internal static IEnumerable<Syntax.Expr> Walk(List<Stmt> body)
    {
        // WalkStmts already reaches the statements of if/case/begin values: their expressions are walked once, there
        foreach (var s in WalkStmts(body))
            foreach (var r in Roots(s))
                if (r != null)
                    foreach (var e in WalkExpr(r, stmtValues: false)) yield return e;
    }

    /// <summary>
    /// Every expression node under e, including the bodies of blocks, lambdas and `spawn` and the statements of
    /// if/case/begin used as a value. A use analysis must see all of them: a mention it skips is a use it never checks.
    /// </summary>
    static IEnumerable<Syntax.Expr> WalkExpr(Syntax.Expr e) => WalkExpr(e, stmtValues: true);

    static IEnumerable<Syntax.Expr> WalkExpr(Syntax.Expr e, bool stmtValues)
    {
        yield return e;
        foreach (var k in Kids(e))
            if (k != null)
                foreach (var x in WalkExpr(k, stmtValues)) yield return x;
        if (e is LambdaExpr lam)
            foreach (var x in Walk(lam.Body)) yield return x;
        if (e is CallExpr { Block: { } blk })
            foreach (var x in Walk(blk.Body)) yield return x;
        if (e is NewExpr { AsCall: null, Block: { } nblk })
            foreach (var x in Walk(nblk.Body)) yield return x;
        if (stmtValues && StmtValue(e) is { } se)
            foreach (var x in Walk([se.Stmt])) yield return x;
    }

    /// <summary>Direct sub-expressions (block, lambda and statement bodies excluded).</summary>
    static IEnumerable<Syntax.Expr?> Kids(Syntax.Expr e) => e switch
    {
        CallExpr c => c.Args.Select(a => a.Value).Prepend(c.Recv),
        NewExpr { AsCall: { } ac } => [ac],   // `Add(5)`: the call shares the arguments
        NewExpr n => n.Args.Select(a => a.Value),
        IndexExpr ix => ix.Args.Select(a => a.Value).Prepend(ix.Target),
        BinaryExpr b => [b.L, b.R],
        UnaryExpr u => [u.E],
        TernaryExpr t => [t.C, t.A, t.B],
        RangeExpr r => [r.A, r.B],
        ArrayLit a => a.Items,
        HashLit h => h.Keys.Concat(h.Values),
        TupleLit tl => tl.Items,
        InterpExpr ie => ie.Holes,
        TypeTestExpr tt => [tt.E],
        InvokeExpr inv => inv.Args.Select(a => a.Value).Prepend(inv.Target),
        YieldExpr y => y.Args.Select(a => a.Value),
        SuperExpr { Args: { } sa } => sa.Select(a => a.Value),
        AwaitExpr aw => [aw.E],
        SpawnExpr sp => [sp.Body],   // the task body is a lambda: every mention inside is a capture
        DataflowExpr df => [df.Body],   // ADR 0038: node blocks capture like lambdas
        _ => [],
    };
}
