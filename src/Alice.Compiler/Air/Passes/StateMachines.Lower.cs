using System.Text.RegularExpressions;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// One state-machine build. This file holds the kind-independent phases, run in order on a copy of the body:
/// `await foreach` / `await using` lowering, awaits in finally blocks (pending exception and branch), counted loops
/// with a suspension turned into while loops, await spilling (every await becomes a statement whose operands are
/// already evaluated, so the evaluation stack is empty at each resume point), and local hoisting. The kinds build
/// MoveNext in StateMachines.Async.cs and StateMachines.Iterator.cs.
/// </summary>
sealed partial class StateMachineRewriter
{
    readonly StateMachineMethod m;
    readonly StateMachineOptions o;
    readonly StateMachineKind kind;
    public readonly List<string> Unsupported = new();

    readonly TypeSym smSym;
    readonly AirType smType;
    readonly AirTypeDecl decl;
    int tempN, labelN, hoistN;

    bool IsAsync => kind is StateMachineKind.Async or StateMachineKind.AsyncIterator;
    bool Yields => kind is StateMachineKind.Iterator or StateMachineKind.AsyncIterator;
    /// <summary>C# Release: an async method's machine is a struct (boxed by the builder on the first real suspension).</summary>
    bool IsStruct => kind == StateMachineKind.Async && !o.Debug;

    public StateMachineRewriter(StateMachineMethod m, StateMachineOptions o, StateMachineKind kind)
    {
        this.m = m;
        this.o = o;
        this.kind = kind;
        string name = $"<{m.Name}>d__{m.Ordinal}";
        string k = IsStruct ? "struct" : "class";
        smSym = new TypeSym { Name = name, Kind = k, Type = new AType(k, name), TypeParams = [.. m.TypeParams] };
        smType = new AirType.AliceType(smSym, m.TypeParams.Select(t => (AirType)new AirType.TypeParam(t)).ToList());
        decl = new AirTypeDecl
        {
            Sym = smSym, Name = name, Kind = IsStruct ? AirTypeKind.Struct : AirTypeKind.Class, Sealed = !IsStruct,
            Base = IsStruct ? null : SmTypes.Obj, Self = smType, TypeParams = [.. m.TypeParams],
            Enclosing = m.Owner?.Synth,
        };
    }

    // ---------------------------------------------------------------- driver

    public (AirTypeDecl Decl, List<MStmt> Kickoff) Build(List<MStmt> body)
    {
        Validate(body);
        if (Unsupported.Count > 0) return (decl, []);
        if (IsAsync) LowerAsyncLoops(body);
        AwaitsInFinally(body);
        NormalizeFors(body);
        Spill(body);
        if (Unsupported.Count > 0) return (decl, []);
        Hoist(body);
        if (Unsupported.Count > 0) return (decl, []);
        var kickoff = kind switch
        {
            StateMachineKind.Async => BuildAsync(body),
            StateMachineKind.Iterator => BuildIterator(body),
            _ => BuildAsyncIterator(body),
        };
        foreach (var md in decl.Methods)
            if (md.Body != null) ComputeLeaves(md.Body);
        return (decl, kickoff);
    }

    void No(string why) => Unsupported.Add(why);

    static string At(MStmt s) => s.Line > 0 ? $"line {s.Line}: " : "";

    void Validate(List<MStmt> body)
    {
        foreach (var p in m.Params)
            if (p.Type is AirType.ByRef) No($"parameter `{p.Name}` is by reference: async methods and iterators cannot have ref/out parameters");
        if (kind == StateMachineKind.Async && m.Ret is AirType.Prim { Kind: PrimKind.Void } && !m.Lambda)
            No("async void is not supported: an async method returns Task (Alice wraps a void async def in Task)");
        if (kind == StateMachineKind.Async && Builder() == null) No($"no async method builder for return type {m.Ret.ToCs()}");
        if (kind == StateMachineKind.Iterator && ElemOf(m.Ret) == null) No($"iterator return type {m.Ret.ToCs()} is not IEnumerable/IEnumerator");
        foreach (var n in SmTree.All(body))
            switch (n)
            {
                case MUnary { Op: AirUnOp.Await } when !IsAsync: No("await outside an async method"); break;
                case MExprStmt { E: MArg { Prefix: "yield return " } } ys when !Yields: No(At(ys) + "yield outside an iterator"); break;
                case MBlock { Lock: not null } lk when Suspends(lk): No(At(lk) + "await or yield inside a lock"); break;
                case AirBase: No("`super` call inside an async method or iterator (needs a base-call proxy method)"); break;
            }
    }

    // ---------------------------------------------------------------- shared predicates and builders

    static bool IsYieldReturn(MStmt s) => s is MExprStmt { E: MArg { Prefix: "yield return " } } || s is SYield;

    /// <summary>Contains a suspension point (await, yield return, await foreach/using) outside lambda bodies.</summary>
    static bool Suspends(object node) => SmTree.All(node).Any(n => n switch
    {
        MUnary { Op: AirUnOp.Await } => true,
        SAwait or SYield => true,
        MForeach { Await: true } or MUsing { Await: true } => true,
        MStmt s => IsYieldReturn(s),
        _ => false,
    });

    static bool Suspends(IEnumerable<MStmt> list) => list.Any(s => Suspends((object)s));

    static bool HasAwait(MExpr e) => SmTree.All(e).Any(n => n is MUnary { Op: AirUnOp.Await });

    string Temp(string hint) => $"<>{hint}__{++tempN}";
    string Label(string hint) => $"<>{hint}_{++labelN}";

    MExpr This() => new AirThis { Type = smType };
    MMember F(string name, AirType t) => new() { Target = This(), Name = name, Type = t };
    MMember F(AirFieldDecl f) => F(f.Name, f.Type);
    static MMember Member(MExpr target, string name, AirType? t) => new() { Target = target, Name = name, Type = t };
    static MCall Call(MExpr? target, string name, AirType? t, params MExpr[] args) => new() { Target = target, Name = name, Type = t, Args = [.. args] };
    static MAssign Set(MExpr target, MExpr value) => new() { Target = target, Value = value };
    static MExprStmt Do(MExpr e) => new() { E = e };
    static AirLocalRef Local(string name, AirType? t) => new(name, t);
    static MDecl Decl(string name, AirType t, MExpr? init = null) => new() { Name = name, TypeCs = t.ToCs(), Init = init };
    static MBinary Bin(AirBinOp op, MExpr l, MExpr r, AirType? t = null) => new() { Op = op, L = l, R = r, Type = t ?? (op is >= AirBinOp.And and <= AirBinOp.Ge ? SmTypes.Bool : l.Type) };
    static MUnary Not(MExpr e) => new() { Op = AirUnOp.Not, E = e, Type = SmTypes.Bool };
    static AirConst I(int v) => AirConst.Of(v);
    static MArg Ref(MExpr e) => new() { Prefix = "ref ", E = e, Type = e.Type };
    static AirTypeRef TypeRef(AirType t) => new(t);
    static AirTypeRef TypeRef(Type t) => new(SmTypes.Clr(t));
    MAssign SetState(int s) => Set(State(), I(s));
    MMember State() => F("<>1__state", SmTypes.Int);

    AirFieldDecl AddField(string name, AirType t, AirVis vis = AirVis.Public, AirSynth synth = AirSynth.None)
    {
        var f = new AirFieldDecl { Name = name, Type = t, Vis = vis, Synth = synth };
        decl.Fields.Add(f);
        return f;
    }

    AirMethodDecl AddMethod(string name, AirType ret, List<MStmt> body, AirVis vis = AirVis.Private, AirMethodFlags flags = AirMethodFlags.None,
        (AirType, string)? impl = null, params AirParamDecl[] ps)
    {
        var md = new AirMethodDecl { Name = name, Ret = ret, Body = body, Vis = vis, Flags = flags, Implements = impl, Params = [.. ps] };
        decl.Methods.Add(md);
        return md;
    }

    /// <summary>An explicit interface implementation (private, virtual final, a MethodImpl row).</summary>
    AirMethodDecl Impl(AirType iface, string method, string name, AirType ret, List<MStmt> body, params AirParamDecl[] ps) =>
        AddMethod(name, ret, body, AirVis.Private, AirMethodFlags.Virtual | AirMethodFlags.Final, (iface, method), ps);

    /// <summary>`public .ctor(...)`: the base object constructor, then <paramref name="body"/>.</summary>
    void Ctor(List<MStmt> body, params AirParamDecl[] ps)
    {
        var all = new List<MStmt> { Do(Call(new AirBase { Type = SmTypes.Obj }, ".ctor", SmTypes.Void)) };
        all.AddRange(body);
        all.Add(new MReturn());
        AddMethod(".ctor", SmTypes.Void, all, AirVis.Public, AirMethodFlags.SpecialName | AirMethodFlags.Ctor, null, ps);
    }

    void Property(string name, AirType t, AirMethodDecl get) =>
        decl.Properties.Add(new AirPropertyDecl { Name = name, Type = t, Get = get });

    /// <summary>The static type of <paramref name="e"/>: its own, the emitter's inference, then reflection on calls.</summary>
    AirType? TypeOf(MExpr e)
    {
        if (e.Type is { } t && t.ToCs() != "var") return t;
        if (o.TypeOf?.Invoke(e) is { } h) return h;
        switch (e)
        {
            case MCall { Target: null or AirTypeRef { Ref: AirType.AliceType } } ac when AliceCall(ac) is { } am:
                return am.IsAsync || am.IsIterator ? StateMachineMethod.For(am, 0).Ret : am.Ret.IsUnknown ? null : am.Ret.ToAir();
            case MCall c when c.Target is AirTypeRef tr:
                return SmTypes.MethodRet(tr.Ref, c.Name, c.Args.Select(TypeOf).ToList(), isStatic: true);
            case MCall { Target: { } target } c when TypeOf(target) is { } tt:
                return SmTypes.MethodRet(tt, c.Name, c.Args.Select(TypeOf).ToList(), isStatic: false);
            case MMember { Target: AirTypeRef tr } mm:
                return SmTypes.PropertyType(tr.Ref, mm.Name, isStatic: true);
            case MMember mm when TypeOf(mm.Target) is { } mt:
                return SmTypes.PropertyType(mt, mm.Name, isStatic: false);
            case MUnary { Op: AirUnOp.Await } u when TypeOf(u.E) is { } at:
                return SmTypes.Await(at)?.Result;
            case MNew n when n.TypeCs.Length > 0:
                return FromCs(n.TypeCs, null);
            case MCast c when c.TypeCs.Length > 0:
                return FromCs(c.TypeCs, null);
            case MBinary b when b.Op is >= AirBinOp.And and <= AirBinOp.Ge:
                return SmTypes.Bool;
            case MBinary b:
                return TypeOf(b.L) is { } lt && TypeOf(b.R) is { } rt && lt.Same(rt) ? lt : null;
        }
        return null;
    }

    /// <summary>The Alice method a call names: on the owner (implicit receiver) or a static call on an Alice type;
    /// null when unknown or overloaded on the argument count.</summary>
    MethodSym? AliceCall(MCall c)
    {
        var owner = c.Target is AirTypeRef { Ref: AirType.AliceType at } ? at.Sym : m.Owner;
        if (owner == null) return null;
        var ms = owner.Methods.Values.SelectMany(x => x).Where(x => x.CsName == c.Name && x.Accepts(c.Args.Count) && !x.IsCtor).ToList();
        return ms.Count == 1 ? ms[0] : null;
    }

    static readonly Dictionary<string, PrimKind> keywords = new()
    {
        ["bool"] = PrimKind.Bool, ["char"] = PrimKind.Char, ["sbyte"] = PrimKind.SByte, ["byte"] = PrimKind.Byte,
        ["short"] = PrimKind.Short, ["ushort"] = PrimKind.UShort, ["int"] = PrimKind.Int, ["uint"] = PrimKind.UInt,
        ["long"] = PrimKind.Long, ["ulong"] = PrimKind.ULong, ["float"] = PrimKind.Float, ["double"] = PrimKind.Double,
        ["decimal"] = PrimKind.Decimal, ["string"] = PrimKind.String, ["object"] = PrimKind.Object,
    };

    /// <summary>An AIR type for C# text: the hint's type when it spells the same, a keyword, else a by-name CLR type.</summary>
    internal static AirType? FromCs(string? cs, AirType? hint)
    {
        if (cs == null || cs == "var") return hint;
        if (hint != null && hint.ToCs() == cs) return hint;
        bool q = cs.EndsWith('?');
        string bare = q ? cs[..^1] : cs;
        if (keywords.TryGetValue(bare, out var k)) return new AirType.Prim(k) { Nullable = q };
        return new AirType.ClrRef(bare, null, []) { Nullable = q };
    }

    /// <summary>The element type of an iterator's return type (object for the non-generic interfaces).</summary>
    static AirType? ElemOf(AirType ret)
    {
        foreach (var def in new[] { typeof(IEnumerable<>), typeof(IEnumerator<>), typeof(IAsyncEnumerable<>), typeof(IAsyncEnumerator<>) })
            if (SmTypes.Generic(ret) is { } g && g.Def == def) return g.Args[0];
        string cs = ret.ToCs();
        return cs is "global::System.Collections.IEnumerable" or "global::System.Collections.IEnumerator" ? SmTypes.Obj : null;
    }

    /// <summary>Lists in post-order (children before the list that holds them), statement lists only (not lambdas).</summary>
    static void EachList(List<MStmt> list, Action<List<MStmt>> f)
    {
        foreach (var s in list.ToList())
            foreach (var l in SmTree.Lists(s)) EachList(l, f);
        f(list);
    }

    /// <summary>Replaces statements of every list (post-order) by <paramref name="f"/>'s result when it is not null.</summary>
    static void Replace(List<MStmt> body, Func<MStmt, List<MStmt>?> f) => EachList(body, list =>
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (f(list[i]) is not { } rep) continue;
            foreach (var r in rep)
                if (r.Line == 0 && r.Span == null) r.Line = list[i].Line;
            list.RemoveAt(i);
            list.InsertRange(i, rep);
            i += rep.Count - 1;
        }
    });

    // ---------------------------------------------------------------- await foreach / await using

    void LowerAsyncLoops(List<MStmt> body) => Replace(body, s => s switch
    {
        MForeach { Await: true } fe => AwaitForeach(fe),
        MUsing { Await: true } u => AwaitUsing(u),
        _ => null,
    });

    static readonly AirType ValueTaskBool = SmTypes.Gen(typeof(ValueTask<>), SmTypes.Bool);
    static readonly AirType ValueTaskT = SmTypes.Clr(typeof(ValueTask));

    List<MStmt>? AwaitForeach(MForeach fe)
    {
        if (fe.Vars.Count != 1) { No(At(fe) + "for await with several variables"); return null; }
        var ct = TypeOf(fe.Coll);
        AirType? elem = null, enumT = null;
        MExpr? getEnum = null;
        var token = new AirDefault(SmTypes.Clr(typeof(CancellationToken)));
        if (SmTypes.IsDef(ct, typeof(IAsyncEnumerable<>)))
        {
            elem = SmTypes.Generic(ct)!.Value.Args[0];
            enumT = SmTypes.Gen(typeof(IAsyncEnumerator<>), elem);
        }
        else if (SmTypes.ClrOf(ct) is { } clr && clr.GetMethods().FirstOrDefault(x => x.Name == "GetAsyncEnumerator") is { } ge)
        {
            enumT = SmTypes.Clr(ge.ReturnType);
            elem = SmTypes.PropertyType(enumT, "Current", false);
        }
        if (elem == null || enumT == null) { No(At(fe) + "for await over a collection of unknown type"); return null; }
        getEnum = Call(fe.Coll, "GetAsyncEnumerator", enumT, token);
        string e = Temp("e");
        var loopBody = new List<MStmt> { Decl(fe.Vars[0], elem, Member(Local(e, enumT), "Current", elem)) };
        loopBody.AddRange(fe.Body);
        var move = new MUnary { Op = AirUnOp.Await, E = Call(Local(e, enumT), "MoveNextAsync", ValueTaskBool), Type = SmTypes.Bool };
        return
        [
            Decl(e, enumT, getEnum),
            new MTry { Body = [new MWhile { Cond = move, Body = loopBody }], Finally = DisposeAsync(Local(e, enumT)) },
        ];
    }

    List<MStmt>? AwaitUsing(MUsing u)
    {
        var t = TypeOf(u.Value);
        if (t == null) { No(At(u) + "await using of a value of unknown type"); return null; }
        string name = u.Name.Length > 0 ? u.Name : Temp("u");
        return [Decl(name, t, u.Value), new MTry { Body = u.Body, Finally = DisposeAsync(Local(name, t)) }];
    }

    List<MStmt> DisposeAsync(AirLocalRef x)
    {
        var dispose = Do(new MUnary { Op = AirUnOp.Await, E = Call(x, "DisposeAsync", ValueTaskT), Type = SmTypes.Void });
        if (SmTypes.IsValueType(x.Type) == true) return [dispose];
        return [new MIf { Cond = Bin(AirBinOp.Ne, x, new AirNull()), Then = [dispose] }];
    }

    // ---------------------------------------------------------------- awaits in finally blocks

    /// <summary>Tries rewritten by <see cref="AwaitsInFinally"/>: their catch only records the pending exception.</summary>
    readonly HashSet<MTry> pendingTries = new(ReferenceEqualityComparer.Instance);
    /// <summary>The block that replaced a try with an awaiting finally, by the try it contains.</summary>
    readonly Dictionary<MTry, MBlock> pendingBlocks = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// `try B finally F` where F awaits: a suspension cannot run inside a CLR finally, so (as Roslyn's
    /// AsyncExceptionHandlerRewriter) B runs in a try whose catch stores the exception, F runs after it as ordinary
    /// code, then the exception is rethrown (ExceptionDispatchInfo keeps its stack) and any return/break/continue/
    /// `yield break` that left B is replayed.
    /// </summary>
    void AwaitsInFinally(List<MStmt> body) => Replace(body, s =>
    {
        if (s is not MTry t) return null;
        bool handlers = t.Catches.Any(c => Suspends(c.Body));
        bool fin = t.Finally != null && Suspends(t.Finally);
        if (fin && kind == StateMachineKind.Iterator) { No(At(t) + "yield inside an ensure clause (Dispose could not run it; C# forbids it too)"); return null; }
        // a try with both handlers and a finally that holds a suspension: the handlers go on an inner try
        if (t.Catches.Count > 0 && t.Finally != null && (handlers || fin || kind == StateMachineKind.Iterator && Suspends(t.Body)))
        {
            var inner = new MTry { Body = t.Body, Catches = t.Catches, Line = t.Line };
            t = new MTry { Body = [handlers ? PendingCatch(inner) : inner], Finally = t.Finally, Line = t.Line };
        }
        else if (handlers) return [PendingCatch(t)];
        if (t.Finally != null && fin) return [PendingFinally(t.Body, t.Finally, t.Line)];
        return ReferenceEquals(t, s) ? null : [t];
    });

    /// <summary>
    /// `try B rescue T e => H` where H suspends: a suspension cannot run inside a CLR handler, so (as Roslyn) the
    /// catch only records the exception and which clause took it; the clause's body runs after the try as ordinary
    /// code, with its variable bound to the recorded exception.
    /// </summary>
    MStmt PendingCatch(MTry t)
    {
        string ex = Temp("cx"), which = Temp("ci");
        var exT = SmTypes.Exception with { Nullable = true };
        var block = new MBlock { Line = t.Line };
        block.Body.Add(Decl(ex, exT, new AirNull { Type = exT }));
        block.Body.Add(Decl(which, SmTypes.Int, I(0)));
        var tr = new MTry { Body = t.Body, Line = t.Line };
        block.Body.Add(tr);
        for (int k = 0; k < t.Catches.Count; k++)
        {
            var c = t.Catches[k];
            string caught = Temp("c");
            var ct = FromCs(c.TypeCs, null) ?? SmTypes.Exception;
            tr.Catches.Add(new MCatch
            {
                TypeCs = c.TypeCs, Name = caught,
                Body = [Set(Local(ex, exT), Local(caught, ct)), Set(Local(which, SmTypes.Int), I(k + 1))],
            });
            var run = new List<MStmt>();
            if (c.Name != null) run.Add(Decl(c.Name, ct, new MCast { TypeCs = c.TypeCs, E = Local(ex, exT), Type = ct }));
            run.AddRange(c.Body);
            block.Body.Add(new MIf { Cond = Bin(AirBinOp.Eq, Local(which, SmTypes.Int), I(k + 1)), Then = run });
        }
        return block;
    }

    MStmt PendingFinally(List<MStmt> b, List<MStmt> fin, int line)
    {
        string ex = Temp("ex"), pb = Temp("pb"), caught = Temp("c"), end = Label("tryend");
        var exT = SmTypes.Exception with { Nullable = true };
        var branches = new List<(int Code, MStmt Replay)>();
        string? rv = null;
        var resultT = kind == StateMachineKind.Async ? ResultType() : null;

        List<MStmt>? Exit(MStmt s, int loops)
        {
            MStmt? replay = null;
            var pre = new List<MStmt>();
            switch (s)
            {
                case MReturn r when kind == StateMachineKind.Async:
                    if (r.Value != null)
                    {
                        rv ??= Temp("rv");
                        pre.Add(Set(Local(rv, resultT), r.Value));
                        replay = new MReturn { Value = Local(rv, resultT) };
                    }
                    else replay = new MReturn();
                    break;
                case MReturn or MExprStmt { E: AirYieldBreak }:
                    replay = Do(new AirYieldBreak());
                    break;
                case MBreak when loops == 0: replay = new MBreak(); break;
                case MContinue when loops == 0: replay = new MContinue(); break;
                default: return null;
            }
            int code = branches.FirstOrDefault(x => x.Replay.GetType() == replay.GetType() && x.Replay is not MReturn).Code;
            if (code == 0 || replay is MReturn) { code = branches.Count + 1; branches.Add((code, replay)); }
            pre.Add(Set(Local(pb, SmTypes.Int), I(code)));
            pre.Add(new AirGoto { Label = end });
            return pre;
        }

        void Walk(List<MStmt> list, int loops)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (Exit(s, loops) is { } rep)
                {
                    list.RemoveAt(i);
                    list.InsertRange(i, rep);
                    i += rep.Count - 1;
                    continue;
                }
                bool loop = s is MWhile or MFor or MForeach;
                foreach (var l in SmTree.Lists(s)) Walk(l, loops + (loop ? 1 : 0));
            }
        }

        Walk(b, 0);
        b.Add(new AirLabel { Name = end });
        var tr = new MTry
        {
            Body = b, Line = line,
            Catches = [new MCatch { TypeCs = "global::System.Exception", Name = caught, Body = [Set(Local(ex, exT), Local(caught, SmTypes.Exception))] }],
        };
        var block = new MBlock { Line = line };
        block.Body.Add(Decl(ex, exT, new AirNull { Type = exT }));
        if (branches.Count > 0) block.Body.Add(Decl(pb, SmTypes.Int, I(0)));
        if (rv != null) block.Body.Add(Decl(rv, resultT!));
        block.Body.Add(tr);
        block.Body.AddRange(fin);
        var edi = SmTypes.Clr(typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo));
        block.Body.Add(new MIf
        {
            Cond = Bin(AirBinOp.Ne, Local(ex, exT), new AirNull()),
            Then = [Do(Call(Call(TypeRef(edi), "Capture", edi, Local(ex, exT)), "Throw", SmTypes.Void))],
        });
        foreach (var (code, replay) in branches)
            block.Body.Add(new MIf { Cond = Bin(AirBinOp.Eq, Local(pb, SmTypes.Int), I(code)), Then = [replay] });
        pendingTries.Add(tr);
        pendingBlocks[tr] = block;
        return block;
    }

    // ---------------------------------------------------------------- counted loops

    /// <summary>Continue labels of counted loops turned into while loops (a `continue` there must run the step).</summary>
    readonly Dictionary<MWhile, string> continueLabels = new(ReferenceEqualityComparer.Instance);

    static readonly Regex StepAdd = new(@"^\s*([+-])=\s*(\w+)\s*$", RegexOptions.CultureInvariant);
    static readonly Regex StepUnchecked = new(@"^\s*=\s*unchecked\(\s*(\w+)\s*([+-])\s*(\w+)\s*\)\s*$", RegexOptions.CultureInvariant);

    void NormalizeFors(List<MStmt> body) => Replace(body, s =>
    {
        if (s is not MFor f || !Suspends(f)) return null;
        var vt = FromCs(f.VarType, TypeOf(f.Start)) ?? TypeOf(f.Start);
        if (vt == null) { No(At(f) + $"loop variable `{f.Var}` of unknown type"); return null; }
        var v = Local(f.Var, vt);
        AirBinOp cmp = f.Cmp switch { "<" => AirBinOp.Lt, "<=" => AirBinOp.Le, ">" => AirBinOp.Gt, ">=" => AirBinOp.Ge, "!=" => AirBinOp.Ne, _ => (AirBinOp)(-1) };
        if ((int)cmp < 0) { No(At(f) + $"loop comparison `{f.Cmp}`"); return null; }
        MStmt step;
        if (f.Step.Op is "++" or "--")
            step = Set(v, new MBinary { Op = f.Step.Op == "++" ? AirBinOp.Add : AirBinOp.Sub, L = v, R = I(1), Type = vt });
        else if (f.Step is { Op: "+=" or "-=", Value: MCode sv } && sv.Text.Length > 0 && sv.Text.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            step = Set(v, new MBinary { Op = f.Step.Op == "+=" ? AirBinOp.Add : AirBinOp.Sub, L = v, R = Operand(sv.Text), Type = vt });
        else if (f.Step.UncheckedStep(f.Var) is { R: MCode ur } ub && ur.Text.Length > 0 && ur.Text.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            step = Set(v, new MBinary { Op = ub.Op, Checked = false, L = v, R = Operand(ur.Text), Type = vt });
        else { No(At(f) + $"loop step `{f.Step.Op}`"); return null; }
        string cont = Label("continue");
        ContinueToGoto(f.Body, cont, 0);
        var loop = new MWhile { Cond = Bin(cmp, v, f.End), Body = [new MBlock { Body = f.Body }, new AirLabel { Name = cont }, step], Line = f.Line };
        continueLabels[loop] = cont;
        return [new MBlock { Body = [Decl(f.Var, vt, f.Start), loop], Line = f.Line }];

        MExpr Operand(string t) => int.TryParse(t, out int k) ? I(k) : Local(t, null);
    });

    static void ContinueToGoto(List<MStmt> list, string label, int loops)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is MContinue && loops == 0) { list[i] = new AirGoto { Label = label, Line = list[i].Line }; continue; }
            bool loop = list[i] is MWhile or MFor or MForeach;
            foreach (var l in SmTree.Lists(list[i])) ContinueToGoto(l, label, loops + (loop ? 1 : 0));
        }
    }

    // ---------------------------------------------------------------- spilling

    /// <summary>
    /// Every await becomes an <see cref="SAwait"/> statement and every yield an <see cref="SYield"/>/<see cref="SYieldBreak"/>;
    /// operands evaluated before an await in the same expression are first stored in temporaries (C# order).
    /// </summary>
    void Spill(List<MStmt> body) => Replace(body, s =>
    {
        var pre = new List<MStmt>();
        switch (s)
        {
            case MExprStmt { E: MArg { Prefix: "yield return " } y }:
                pre.Add(new SYield { Value = SpillExpr(y.E, pre), Line = s.Line });
                return pre;
            case MExprStmt { E: AirYieldBreak }:
                return [new SYieldBreak()];
            // `return await t` of a void awaitable in an async method returning Task: await, then return
            case MReturn { Value: MUnary { Op: AirUnOp.Await } ru } when kind == StateMachineKind.Async && ResultType() is AirType.Prim { Kind: PrimKind.Void }:
                pre.Add(new SAwait { Awaitable = SpillExpr(ru.E, pre), Line = s.Line });
                pre.Add(new MReturn { Line = s.Line });
                return pre;
            case MReturn r when Yields:
                if (r.Value != null) { No(At(r) + "return with a value inside an iterator"); return null; }
                return [new SYieldBreak()];
            case MExprStmt { E: MUnary { Op: AirUnOp.Await } u }:
                pre.Add(new SAwait { Awaitable = SpillExpr(u.E, pre), Line = s.Line });
                return pre;
            case MDecl { Init: MUnary { Op: AirUnOp.Await } du } d:
                {
                    var aw = SpillExpr(du.E, pre);
                    var rt = TypeOf(aw) is { } at ? SmTypes.Await(at)?.Result : null;
                    var t = FromCs(d.TypeCs, d.Init.Type ?? rt) ?? rt;
                    if (t == null) { No(At(d) + $"cannot type `{d.Name}` (an awaited value)"); return null; }
                    if (d.TypeCs == null) d.TypeCs = t.ToCs();
                    d.Init = null;
                    pre.Add(d);
                    pre.Add(new SAwait { Awaitable = aw, Target = Local(d.Name, t), Line = s.Line });
                    return pre;
                }
            case MDecl { Init: { } init } d when HasAwait(init):
                d.Init = SpillExpr(init, pre);
                pre.Add(d);
                return pre;
            case MAssign a when HasAwait(a.Value) || HasAwait(a.Target):
                {
                    var target = SpillTarget(a.Target, pre);
                    var value = a.Value;
                    if (a.Op != "=")
                    {
                        var op = BinOpOf(a.Op);
                        if (op == null) { No(At(a) + $"`{a.Op}` with an await operand"); return null; }
                        value = new MBinary { Op = op.Value, L = SmTree.Clone(target), R = value, Type = target.Type };
                    }
                    if (value is MUnary { Op: AirUnOp.Await } au)
                        pre.Add(new SAwait { Awaitable = SpillExpr(au.E, pre), Target = target, Line = s.Line });
                    else
                        pre.Add(new MAssign { Target = target, Value = SpillExpr(value, pre), Line = s.Line });
                    return pre;
                }
            case MWhile w when HasAwait(w.Cond):
                {
                    var cond = SpillExpr(w.Cond, pre);
                    pre.Add(new MIf { Cond = Not(cond), Then = [new MBreak()] });
                    w.Body.InsertRange(0, pre);
                    w.Cond = AirConst.Bool(true);
                    return null;
                }
            case MFor f when HasAwait(f.Start) || HasAwait(f.End):
                No(At(f) + "await in a loop bound");
                return null;
            case MDecl or MAssign or MWhile or MFor:
                return null;
            case MBlock { Lock: not null }:
                return null;
        }
        // the remaining statements evaluate their own expressions first
        bool any = SmTree.Exprs(s).Any(HasAwait);
        if (!any) return null;
        if (s is not (MExprStmt or MReturn or MThrow or MIf))
        {
            No(At(s) + $"await inside {s.GetType().Name}");
            return null;
        }
        SmTree.MapExprs(s, e => SpillExpr(e, pre));
        pre.Add(s);
        return pre;
    });

    static AirBinOp? BinOpOf(string op) => op switch
    {
        "+=" => AirBinOp.Add, "-=" => AirBinOp.Sub, "*=" => AirBinOp.Mul, "/=" => AirBinOp.Div, "%=" => AirBinOp.Mod,
        "&=" => AirBinOp.BitAnd, "|=" => AirBinOp.BitOr, "^=" => AirBinOp.BitXor, "<<=" => AirBinOp.Shl, ">>=" => AirBinOp.Shr,
        "??=" => AirBinOp.Coalesce, _ => null,
    };

    static bool IsSimple(MExpr e) => e is MCode || e is MArg { Prefix: "ref " or "out " or "in " };

    /// <summary>An assignment target whose own operands (receiver, indices) are evaluated before the await.</summary>
    MExpr SpillTarget(MExpr t, List<MStmt> pre)
    {
        switch (t)
        {
            case MMember mm when !IsSimple(mm.Target):
                mm.Target = Stash(SpillExpr(mm.Target, pre), pre);
                return mm;
            case MIndex ix:
                ix.Target = IsSimple(ix.Target) ? ix.Target : Stash(SpillExpr(ix.Target, pre), pre);
                for (int i = 0; i < ix.Args.Count; i++) ix.Args[i] = IsSimple(ix.Args[i]) ? ix.Args[i] : Stash(SpillExpr(ix.Args[i], pre), pre);
                return ix;
            default:
                return t;
        }
    }

    /// <summary>Stores an already spilled operand in a new temporary.</summary>
    MExpr Stash(MExpr e, List<MStmt> pre)
    {
        if (IsSimple(e)) return e;
        if (e is MArg named) { named.E = Stash(named.E, pre); return named; }   // `name: value`
        var t = TypeOf(e);
        if (t == null) { No("cannot type an operand evaluated before an await"); return e; }
        string n = Temp("s");
        pre.Add(Decl(n, t, e));
        return Local(n, t);
    }

    MExpr SpillExpr(MExpr e, List<MStmt> pre)
    {
        if (!HasAwait(e)) return e;
        switch (e)
        {
            case MUnary { Op: AirUnOp.Await } u:
                {
                    var aw = SpillExpr(u.E, pre);
                    var info = TypeOf(aw) is { } at ? SmTypes.Await(at) : null;
                    var rt = info?.Result ?? u.Type;
                    if (rt == null || rt is AirType.Prim { Kind: PrimKind.Void }) { No("cannot type an awaited value used in an expression"); return e; }
                    string n = Temp("r");
                    pre.Add(Decl(n, rt));
                    pre.Add(new SAwait { Awaitable = aw, Target = Local(n, rt) });
                    return Local(n, rt);
                }
            case MBinary { Op: AirBinOp.And or AirBinOp.Or } b when HasAwait(b.R):
                {
                    string n = Temp("b");
                    pre.Add(Decl(n, SmTypes.Bool, SpillExpr(b.L, pre)));
                    var inner = new List<MStmt>();
                    var r = SpillExpr(b.R, inner);
                    inner.Add(Set(Local(n, SmTypes.Bool), r));
                    MExpr c = Local(n, SmTypes.Bool);
                    pre.Add(new MIf { Cond = b.Op == AirBinOp.And ? c : Not(c), Then = inner });
                    return Local(n, SmTypes.Bool);
                }
            case MCond c when HasAwait(c.A) || HasAwait(c.B):
                {
                    var t = TypeOf(c) ?? TypeOf(c.A) ?? TypeOf(c.B);
                    if (t == null) { No("cannot type a conditional with an await"); return e; }
                    string n = Temp("c");
                    var cond = SpillExpr(c.C, pre);
                    pre.Add(Decl(n, t));
                    var a = new List<MStmt>();
                    a.Add(Set(Local(n, t), SpillExpr(c.A, a)));
                    var b2 = new List<MStmt>();
                    b2.Add(Set(Local(n, t), SpillExpr(c.B, b2)));
                    pre.Add(new MIf { Cond = cond, Then = a, Else = b2 });
                    return Local(n, t);
                }
            case MBinary { Op: AirBinOp.Coalesce } co when HasAwait(co.R):
                No("await on the right of `??`");
                return e;
            case MArg { Prefix: "ref " or "out " or "in " }:
                No("await inside a by-reference argument");
                return e;
        }
        if (SmTree.Lists(e).Any(l => SmTree.All(l).Any(n => n is MUnary { Op: AirUnOp.Await })))
        {
            No($"await inside {e.GetType().Name}");
            return e;
        }
        var kids = SmTree.Exprs(e).ToList();
        int last = kids.FindLastIndex(HasAwait);
        int k = 0;
        SmTree.MapExprs(e, x =>
        {
            int i = k++;
            if (i < last) return Stash(x, pre);
            if (i == last) return SpillExpr(x, pre);
            return x;
        });
        return e;
    }

    // ---------------------------------------------------------------- hoisting

    sealed class LocalInfo
    {
        public string Name = "";
        public MDecl? Decl;
        public bool Param;
        public bool Hoist;
        public bool OutVar;
        public AirType? Type;
        public AirFieldDecl? Field;
    }

    readonly Dictionary<MDecl, LocalInfo> declInfo = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<MExpr, LocalInfo> refInfo = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, AirFieldDecl> paramFields = new(StringComparer.Ordinal);
    AirFieldDecl? thisField;

    /// <summary>Iterator try blocks whose finally becomes a method (the try holds a yield).</summary>
    bool Flattened(MTry t) => kind == StateMachineKind.Iterator && t.Finally != null && Suspends(t.Body);

    static string? RefName(MExpr e) => e switch
    {
        AirLocalRef l => l.Name,
        AirParamRef p => p.Name,
        MCode c when c.GetType() == typeof(MCode) && IsIdent(c.Text) => c.Text,
        _ => null,
    };

    static readonly Regex Ident = new(@"^@?[A-Za-z_<][A-Za-z0-9_<>]*$", RegexOptions.CultureInvariant);
    static bool IsIdent(string s) => Ident.IsMatch(s) && s is not ("true" or "false" or "null" or "this" or "base" or "default");

    static bool Uses(IEnumerable<MStmt> list, string name) =>
        list.Any(s => SmTree.All(s, lambdas: true).Any(n => n is MExpr e && RefName(e) == name || n is MFor { Step.Value: { } sv } && StepMentions(sv, name)));

    static bool StepMentions(MExpr e, string name) => RefName(e) == name || Optimizer.MirFacts.Children(e).Any(c => StepMentions(c, name));

    /// <summary>
    /// Decides which locals become fields (Release: declared before a suspension and used at or after it; inside an
    /// iterator finally that becomes a method: every outer local it reads; Debug: every local), then rewrites
    /// declarations into field stores and references into field reads. Parameters and `this` always become fields.
    /// </summary>
    void Hoist(List<MStmt> body)
    {
        var scopes = new List<Dictionary<string, LocalInfo>> { new(StringComparer.Ordinal) };
        foreach (var p in m.Params) scopes[0][p.Name] = new LocalInfo { Name = p.Name, Param = true, Type = p.Type };
        Resolve(body, scopes, -1, false);
        foreach (var li in declInfo.Values.Where(l => l.Hoist))
        {
            if (li.OutVar) { No($"`out var {li.Name}` lives across a suspension"); continue; }
            var d = li.Decl!;
            var t = li.Type ?? FromCs(d.TypeCs, d.Init?.Type) ?? (d.Init != null ? TypeOf(d.Init) : null)
                ?? refInfo.Where(kv => kv.Value == li && kv.Key.Type != null && kv.Key is not AirLocalRef { Narrowed: true }).Select(kv => kv.Key.Type).FirstOrDefault();
            if (t == null) { No(At(d) + $"cannot type local `{li.Name}`, which lives across a suspension"); continue; }
            li.Type = t;
            string fname = li.Name.StartsWith("<>") ? li.Name : $"<{li.Name}>5__{++hoistN}";
            li.Field = AddField(fname, t, AirVis.Private);
        }
        if (Unsupported.Count > 0) return;
        Rewrite(body, false);
    }

    void Resolve(List<MStmt> list, List<Dictionary<string, LocalInfo>> scopes, int finallyMark, bool lambda)
    {
        scopes.Add(new(StringComparer.Ordinal));
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            foreach (var e in SmTree.Exprs(s)) ResolveExpr(e, scopes, finallyMark, lambda);
            if (s is MDecl d)
            {
                var li = new LocalInfo { Name = d.Name, Decl = d };
                declInfo[d] = li;
                if (!lambda)
                {
                    if (o.Debug) li.Hoist = true;
                    else
                    {
                        int k = list.FindIndex(i + 1, x => Suspends((object)x));
                        // `decl t; t = await e`: t has no value until the resume, so that await does not count
                        if (k >= 0 && d.Init == null && list[k] is SAwait { Target: AirLocalRef tl } && tl.Name == d.Name)
                            k = list.FindIndex(k + 1, x => Suspends((object)x));
                        if (k >= 0 && Uses(list.Skip(k), d.Name)) li.Hoist = true;
                    }
                }
                scopes[^1][d.Name] = li;
                continue;
            }
            if (s is MFor f)
            {
                scopes.Add(new(StringComparer.Ordinal) { [f.Var] = new LocalInfo { Name = f.Var } });
                Resolve(f.Body, scopes, finallyMark, lambda);
                scopes.RemoveAt(scopes.Count - 1);
                continue;
            }
            if (s is MTry t)
            {
                Resolve(t.Body, scopes, finallyMark, lambda);
                foreach (var c in t.Catches)
                {
                    var sc = new Dictionary<string, LocalInfo>(StringComparer.Ordinal);
                    if (c.Name != null) sc[c.Name] = new LocalInfo { Name = c.Name };
                    scopes.Add(sc);
                    Resolve(c.Body, scopes, finallyMark, lambda);
                    scopes.RemoveAt(scopes.Count - 1);
                }
                if (t.Finally != null) Resolve(t.Finally, scopes, Flattened(t) && !lambda ? scopes.Count : finallyMark, lambda);
                continue;
            }
            foreach (var l in SmTree.Lists(s)) Resolve(l, scopes, finallyMark, lambda);
        }
        scopes.RemoveAt(scopes.Count - 1);
    }

    void ResolveExpr(MExpr e, List<Dictionary<string, LocalInfo>> scopes, int finallyMark, bool lambda)
    {
        if (RefName(e) is { } name)
        {
            for (int k = scopes.Count - 1; k >= 0; k--)
                if (scopes[k].TryGetValue(name, out var li))
                {
                    refInfo[e] = li;
                    if (finallyMark >= 0 && k < finallyMark && li.Decl != null) li.Hoist = true;
                    break;
                }
            return;
        }
        if (e is MArg { Prefix: var pf, E: { } oe } && pf.Contains("var ") && RefName(oe) is { } on)
        {
            var li = new LocalInfo { Name = on, OutVar = true, Decl = new MDecl { Name = on } };
            scopes[^1][on] = li;
            refInfo[oe] = li;
            return;
        }
        if (e is MLambda lam)
        {
            var sc = new Dictionary<string, LocalInfo>(StringComparer.Ordinal);
            foreach (var p in lam.Params) { var pn = p.Split(' ')[^1]; sc[pn] = new LocalInfo { Name = pn }; }
            scopes.Add(sc);
            if (lam.ExprBody != null) ResolveExpr(lam.ExprBody, scopes, finallyMark, true);
            if (lam.Body != null) Resolve(lam.Body, scopes, finallyMark, true);
            scopes.RemoveAt(scopes.Count - 1);
            return;
        }
        foreach (var x in SmTree.Exprs(e)) ResolveExpr(x, scopes, finallyMark, lambda);
        foreach (var l in SmTree.Lists(e)) Resolve(l, scopes, finallyMark, lambda);
    }

    AirFieldDecl ParamField(string name)
    {
        if (paramFields.TryGetValue(name, out var f)) return f;
        var p = m.Params.First(x => x.Name == name);
        return paramFields[name] = AddField(name, p.Type);
    }

    MExpr ThisField()
    {
        if (m.This == null) { No("`this` in a static method"); return This(); }
        thisField ??= AddField("<>4__this", m.This);
        return F(thisField);
    }

    void Rewrite(List<MStmt> list, bool lambda)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            SmTree.MapExprs(s, e => RewriteExpr(e, lambda));
            if (s is MDecl d && declInfo.TryGetValue(d, out var li) && li.Field != null)
            {
                if (d.Init == null) { list.RemoveAt(i--); continue; }
                var init = d.Init is MCode { Text: "default!" or "default" } && d.Init.GetType() == typeof(MCode) ? new AirDefault(li.Type) : d.Init;
                list[i] = new MAssign { Target = F(li.Field), Value = init, Line = d.Line, Span = d.Span };
                continue;
            }
            foreach (var l in SmTree.Lists(s)) Rewrite(l, lambda);
        }
    }

    MExpr RewriteExpr(MExpr e, bool lambda)
    {
        if (refInfo.TryGetValue(e, out var li) && (li.Field != null || li.Param))
        {
            if (lambda) { No($"a lambda uses `{li.Name}`, which lives in the state machine (closure conversion must run first)"); return e; }
            MExpr f = li.Param ? F(ParamField(li.Name)) : F(li.Field!);
            if (e is AirLocalRef { Narrowed: true }) return Member(f, "Value", e.Type);
            if (e.Type != null && f.Type != null && !e.Type.Same(f.Type)) f.Type = e.Type;
            return f;
        }
        if (e is AirThis { Type: AirType.AliceType { Sym: var own } } && own == smSym) return e;   // the machine's own `this`
        switch (e)
        {
            case AirThis { Extension: true }:
                return m.Params.Any(p => p.Name == "self") ? F(ParamField("self")) : e;
            case AirThis:
                return ThisField();
            case AirFieldRef fr:
                {
                    var fs = m.Owner?.Fields.Values.FirstOrDefault(x => x.CsName == fr.Name);
                    if (fs is { IsStatic: true }) return Member(TypeRef(m.Owner!.Type.ToAir()), fr.Name, fr.Type);
                    return Member(ThisField(), fr.Name, fr.Type);
                }
            case MCall { Target: null } c when m.Owner != null:
                {
                    var ms = m.Owner.Methods.Values.SelectMany(x => x).Where(x => x.CsName == c.Name).ToList();
                    if (ms.Count > 0)
                        c.Target = ms.All(x => x.IsStatic) || m.Owner.IsStatic ? TypeRef(m.Owner.Type.ToAir()) : ThisField();
                    break;
                }
            case MLambda lam:
                if (lam.ExprBody != null) lam.ExprBody = RewriteExpr(lam.ExprBody, true);
                if (lam.Body != null) Rewrite(lam.Body, true);
                return e;
        }
        SmTree.MapExprs(e, x => RewriteExpr(x, lambda));
        foreach (var l in SmTree.Lists(e)) Rewrite(l, lambda);
        return e;
    }

    // ---------------------------------------------------------------- leave / br

    /// <summary>Marks each <see cref="AirGoto"/> that exits a protected region (IL `leave`), and checks that no jump
    /// or table enters one.</summary>
    void ComputeLeaves(List<MStmt> body)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var gotos = new List<(AirGoto G, string Path)>();
        var tables = new List<(AirGotoTable T, string Path)>();
        int region = 0;

        void Walk(List<MStmt> list, string path)
        {
            foreach (var s in list)
                switch (s)
                {
                    case AirLabel l: labels[l.Name] = path; break;
                    case AirGoto g: gotos.Add((g, path)); break;
                    case AirGotoTable t: tables.Add((t, path)); break;
                    case MTry t:
                        Walk(t.Body, path + (++region) + "/");
                        foreach (var c in t.Catches) Walk(c.Body, path + (++region) + "/");
                        if (t.Finally != null) Walk(t.Finally, path + (++region) + "/");
                        break;
                    default:
                        foreach (var l in SmTree.Lists(s)) Walk(l, path);
                        break;
                }
        }

        Walk(body, "/");
        foreach (var (g, path) in gotos)
        {
            if (!labels.TryGetValue(g.Label, out var lp)) throw new InvalidOperationException("state machine: missing label " + g.Label);
            if (!path.StartsWith(lp, StringComparison.Ordinal)) throw new InvalidOperationException("state machine: jump into a try block: " + g.Label);
            g.Leave = path != lp;
        }
        foreach (var (t, path) in tables)
            foreach (var l in t.Labels)
                if (l != null && labels[l] != path) throw new InvalidOperationException("state machine: dispatch across a try block: " + l);
    }
}
