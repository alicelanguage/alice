using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// Async methods and async iterators (C#'s AsyncMethodToStateMachineRewriter / AsyncIteratorMethodToStateMachineRewriter
/// shapes). States: -1 running, -2 finished, 0.. suspended at an await; async iterators also -3 not started and
/// -4, -5.. suspended at a `yield`. A try block that holds a suspension gets a label before it and a second dispatch
/// table at the start of its body, and its finally runs only when the state is -1 (not when MoveNext returns to
/// suspend). Exceptions end the machine through the builder (SetException) or the promise (async iterators).
/// </summary>
sealed partial class StateMachineRewriter
{
    sealed class Frame
    {
        public MTry Try = null!;
        public string Head = "";
        public readonly List<(int State, string Label)> Entries = new();
        /// <summary>Async iterators: the end of the try body, where a resume in dispose mode goes to run the finally.</summary>
        public string? DisposeLabel;
    }

    readonly List<(int State, string Label)> topEntries = new();
    readonly List<MStmt> moveNextLocals = new();
    readonly Dictionary<string, (AirFieldDecl Field, string Local)> awaiters = new(StringComparer.Ordinal);
    readonly Dictionary<MTry, Frame> frames = new(ReferenceEqualityComparer.Instance);
    AirFieldDecl stateF = null!, builderF = null!;
    AirFieldDecl? currentF, promiseF, disposeModeF, threadF;
    string? resultLocal, smLocal;
    int awaitState, yieldState = -4;
    const string ExitLabel = "<>exit";

    /// <summary>T of Task&lt;T&gt; / ValueTask&lt;T&gt;, else void.</summary>
    AirType ResultType()
    {
        if (SmTypes.Generic(m.Ret) is { } g && (g.Def == typeof(Task<>) || g.Def == typeof(ValueTask<>))) return g.Args[0];
        return SmTypes.Void;
    }

    /// <summary>The method builder of an async method's return type (null: not a supported task type).</summary>
    AirType? Builder()
    {
        string cs = m.Ret.ToCs();
        if (m.Lambda && m.Ret is AirType.Prim { Kind: PrimKind.Void }) return SmTypes.Clr(typeof(AsyncVoidMethodBuilder));   // async void lambda (Action)
        if (cs == "global::System.Threading.Tasks.Task") return SmTypes.Clr(typeof(AsyncTaskMethodBuilder));
        if (cs == "global::System.Threading.Tasks.ValueTask") return SmTypes.Clr(typeof(AsyncValueTaskMethodBuilder));
        if (SmTypes.Generic(m.Ret) is { } g)
        {
            if (g.Def == typeof(Task<>)) return SmTypes.Gen(typeof(AsyncTaskMethodBuilder<>), g.Args[0]);
            if (g.Def == typeof(ValueTask<>)) return SmTypes.Gen(typeof(AsyncValueTaskMethodBuilder<>), g.Args[0]);
        }
        return null;
    }

    static readonly AirType IAsyncStateMachineT = SmTypes.Clr(typeof(IAsyncStateMachine));

    // ---------------------------------------------------------------- async method

    List<MStmt> BuildAsync(List<MStmt> body)
    {
        var builderT = Builder()!;
        var resultT = ResultType();
        stateF = AddField("<>1__state", SmTypes.Int);
        builderF = AddField("<>t__builder", builderT);
        if (resultT is not AirType.Prim { Kind: PrimKind.Void }) resultLocal = "<>r";
        decl.Interfaces.Add(IAsyncStateMachineT);

        EmitAsync(body, []);
        var mn = new List<MStmt>();
        if (resultLocal != null) mn.Add(Decl(resultLocal, resultT));
        mn.AddRange(moveNextLocals);
        var tryBody = new List<MStmt>();
        if (Dispatch(topEntries) is { } table) tryBody.Add(table);
        tryBody.AddRange(body);
        string ex = "<>ex";
        mn.Add(new MTry
        {
            Body = tryBody,
            Catches =
            [
                new MCatch
                {
                    TypeCs = "global::System.Exception", Name = ex,
                    Body = [SetState(-2), Do(Call(F(builderF), "SetException", SmTypes.Void, Local(ex, SmTypes.Exception))), new MReturn()],
                },
            ],
        });
        mn.Add(new AirLabel { Name = ExitLabel });
        mn.Add(SetState(-2));
        mn.Add(Do(resultLocal != null ? Call(F(builderF), "SetResult", SmTypes.Void, Local(resultLocal, resultT)) : Call(F(builderF), "SetResult", SmTypes.Void)));
        mn.Add(new MReturn());
        Impl(IAsyncStateMachineT, "MoveNext", "MoveNext", SmTypes.Void, mn);

        var sm = new AirParamDecl("stateMachine", IAsyncStateMachineT);
        Impl(IAsyncStateMachineT, "SetStateMachine", "SetStateMachine", SmTypes.Void,
            IsStruct ? [Do(Call(F(builderF), "SetStateMachine", SmTypes.Void, new AirParamRef(sm.Name, sm.Type))), new MReturn()] : [new MReturn()], sm);
        if (!IsStruct) Ctor([]);

        // kickoff: create, copy the arguments, start, return the builder's task
        var d = Local("<>sm", smType);
        var k = new List<MStmt> { Decl("<>sm", smType, IsStruct ? new AirDefault(smType) : new MNew { TypeCs = smType.ToCs(), Type = smType }) };
        k.Add(Set(Member(d, builderF.Name, builderT), Call(TypeRef(builderT), "Create", builderT)));
        CopyArguments(k, d, "");
        k.Add(Set(Member(d, stateF.Name, SmTypes.Int), I(-1)));
        k.Add(Do(Call(Member(d, builderF.Name, builderT), "Start", SmTypes.Void, Ref(Local("<>sm", smType)))));
        k.Add(m.Ret is AirType.Prim { Kind: PrimKind.Void } ? new MReturn() : new MReturn { Value = Member(Member(d, builderF.Name, builderT), "Task", m.Ret) });
        OrderFields();
        return k;
    }

    /// <summary>`d.p = p` for every parameter (`d.&lt;&gt;3__p` for an enumerable's pristine copy) and `d.&lt;&gt;4__this = this`.</summary>
    void CopyArguments(List<MStmt> k, AirLocalRef d, string prefix)
    {
        if (m.This != null && thisField != null) k.Add(Set(Member(d, thisField.Name, m.This), new AirThis { Type = m.This }));
        foreach (var p in m.Params)
        {
            var f = prefix == "" ? ParamField(p.Name) : CopyField(p);
            k.Add(Set(Member(d, f.Name, p.Type), new AirParamRef(p.Name, p.Type)));
        }
    }

    AirFieldDecl CopyField(AirParamDecl p) =>
        decl.Fields.FirstOrDefault(f => f.Name == "<>3__" + p.Name) ?? AddField("<>3__" + p.Name, p.Type);

    /// <summary>The fields in C#'s order: state, builder/current/promise, thread id, parameters, this, hoisted locals, awaiters.</summary>
    void OrderFields()
    {
        static int Rank(AirFieldDecl f) => f.Name switch
        {
            "<>1__state" => 0,
            "<>t__builder" => 1,
            "<>v__promiseOfValueOrEnd" => 2,
            "<>2__current" => 3,
            "<>w__disposeMode" => 4,
            "<>l__initialThreadId" => 5,
            "<>4__this" => 8,
            _ when f.Name.StartsWith("<>u__") => 11,
            _ when f.Name.StartsWith("<>3__") => 7,
            _ when f.Name.StartsWith("<") => 10,
            _ => 6,
        };
        var sorted = decl.Fields.Select((f, i) => (f, i)).OrderBy(x => Rank(x.f)).ThenBy(x => x.i).Select(x => x.f).ToList();
        decl.Fields.Clear();
        decl.Fields.AddRange(sorted);
    }

    MStmt? Dispatch(List<(int State, string Label)> entries)
    {
        if (entries.Count == 0) return null;
        int min = entries.Min(e => e.State), max = entries.Max(e => e.State);
        var labels = Enumerable.Repeat<string?>(null, max - min + 1).ToList();
        foreach (var (s, l) in entries) labels[s - min] = l;
        return new AirGotoTable { Value = State(), Min = min, Labels = labels };
    }

    /// <summary>A resume point inside <paramref name="fs"/> (outermost first): each enclosing try dispatches to the next.</summary>
    void Register(int state, string label, List<Frame> fs)
    {
        if (fs.Count == 0) { topEntries.Add((state, label)); return; }
        fs[^1].Entries.Add((state, label));
        for (int j = fs.Count - 2; j >= 0; j--) fs[j].Entries.Add((state, fs[j + 1].Head));
        topEntries.Add((state, fs[0].Head));
    }

    string DisposeTarget(List<Frame> fs)
    {
        for (int j = fs.Count - 1; j >= 0; j--)
            if (fs[j].DisposeLabel != null) return fs[j].DisposeLabel!;
        return ExitLabel;
    }

    static bool HasYield(List<MStmt> body) => SmTree.All(body).Any(n => n is SYield);

    void EmitAsync(List<MStmt> list, List<Frame> fs)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            List<MStmt>? rep = s switch
            {
                SAwait a => AwaitCode(a, fs),
                SYield y => YieldCode(y, fs),
                SYieldBreak => [Set(F(disposeModeF!), AirConst.Bool(true)), new AirGoto { Label = DisposeTarget(fs) }],
                MReturn r when kind == StateMachineKind.Async => r.Value != null
                    ? [Set(Local(resultLocal!, ResultType()), r.Value), new AirGoto { Label = ExitLabel }]
                    : [new AirGoto { Label = ExitLabel }],
                _ => null,
            };
            if (rep != null)
            {
                foreach (var x in rep)
                    if (x.Line == 0) x.Line = s.Line;
                list.RemoveAt(i);
                list.InsertRange(i, rep);
                i += rep.Count - 1;
                continue;
            }
            if (s is MTry t && Suspends(t.Body))
            {
                var f = new Frame { Try = t, Head = Label("try") };
                frames[t] = f;
                if (Yields && HasYield(t.Body)) f.DisposeLabel = Label("dispose");
                var inner = new List<Frame>(fs) { f };
                EmitAsync(t.Body, inner);
                foreach (var c in t.Catches) EmitAsync(c.Body, fs);
                if (t.Finally != null) EmitAsync(t.Finally, fs);
                if (f.DisposeLabel != null) t.Body.Add(new AirLabel { Name = f.DisposeLabel });
                if (Dispatch(f.Entries) is { } table) t.Body.Insert(0, table);
                if (t.Finally != null) t.Finally = [new MIf { Cond = Bin(AirBinOp.Eq, State(), I(-1)), Then = t.Finally }];
                list.Insert(i, new AirLabel { Name = f.Head });
                i++;
                if (f.DisposeLabel != null && !pendingTries.Contains(t))
                {
                    list.Insert(i + 1, DisposeCheck(fs));
                    i++;
                }
                continue;
            }
            foreach (var l in SmTree.Lists(s)) EmitAsync(l, fs);
            if (s is MBlock blk && pendingBlocks.FirstOrDefault(kv => ReferenceEquals(kv.Value, blk)).Key is { } ptry
                && frames.TryGetValue(ptry, out var pf) && pf.DisposeLabel != null)
            {
                list.Insert(i + 1, DisposeCheck(fs));
                i++;
            }
        }
    }

    MStmt DisposeCheck(List<Frame> fs) => new MIf { Cond = F(disposeModeF!), Then = [new AirGoto { Label = DisposeTarget(fs) }] };

    (AirFieldDecl Field, string Local) Awaiter(AirType t)
    {
        string key = t.ToCs();
        if (awaiters.TryGetValue(key, out var a)) return a;
        int n = awaiters.Count + 1;
        a = (AddField("<>u__" + n, t, AirVis.Private), "<>a__" + n);
        moveNextLocals.Add(Decl(a.Local, t));
        return awaiters[key] = a;
    }

    MExpr SmRef()
    {
        if (IsStruct) return This();
        if (smLocal == null)
        {
            smLocal = "<>sm";
            moveNextLocals.Add(Decl(smLocal, smType));
        }
        return Local(smLocal, smType);
    }

    List<MStmt> AwaitCode(SAwait a, List<Frame> fs)
    {
        var at = TypeOf(a.Awaitable);
        var info = SmTypes.Await(at);
        if (info is not { } inf) { No(At(a) + $"cannot find GetAwaiter for an awaited value of type {at?.ToCs() ?? "unknown"}"); return []; }
        var outp = new List<MStmt>();
        var awaitable = a.Awaitable;
        if (SmTypes.IsValueType(at) == true && awaitable is not (AirLocalRef or MMember))
        {
            string v = Temp("v");
            outp.Add(Decl(v, at!, awaitable));
            awaitable = Local(v, at);
        }
        var (field, local) = Awaiter(inf.Awaiter);
        var loc = Local(local, inf.Awaiter);
        int state = awaitState++;
        string resume = Label("resume");
        Register(state, resume, fs);
        outp.Add(Set(loc, Call(awaitable, "GetAwaiter", inf.Awaiter)));
        var suspend = new List<MStmt> { SetState(state), Set(F(field), Local(local, inf.Awaiter)) };
        if (!IsStruct) suspend.Add(Set(SmRef(), This()));
        suspend.Add(Do(Call(F(builderF), inf.Critical ? "AwaitUnsafeOnCompleted" : "AwaitOnCompleted", SmTypes.Void, Ref(F(field)), Ref(SmRef()))));
        suspend.Add(new MReturn());
        suspend.Add(new AirLabel { Name = resume });
        suspend.Add(Set(Local(local, inf.Awaiter), F(field)));
        suspend.Add(Set(F(field), new AirDefault(inf.Awaiter)));
        suspend.Add(SetState(-1));
        outp.Add(new MIf { Cond = Not(Member(Local(local, inf.Awaiter), "IsCompleted", SmTypes.Bool)), Then = suspend });
        var get = Call(Local(local, inf.Awaiter), "GetResult", inf.Result);
        outp.Add(a.Target != null ? Set(a.Target, get) : Do(get));
        return outp;
    }

    List<MStmt> YieldCode(SYield y, List<Frame> fs)
    {
        int state = yieldState--;
        string resume = Label("yield");
        Register(state, resume, fs);
        return
        [
            Set(F(currentF!), y.Value),
            SetState(state),
            Do(Call(F(promiseF!), "SetResult", SmTypes.Void, AirConst.Bool(true))),
            new MReturn(),
            new AirLabel { Name = resume },
            SetState(-1),
            DisposeCheck(fs),
        ];
    }

    // ---------------------------------------------------------------- async iterator

    static readonly AirType ThreadIdT = SmTypes.Int;

    MMember ManagedThreadId() => Member(TypeRef(typeof(Environment)), "CurrentManagedThreadId", SmTypes.Int);

    List<MStmt> BuildAsyncIterator(List<MStmt> body)
    {
        var elem = ElemOf(m.Ret)!;
        bool enumerable = SmTypes.IsDef(m.Ret, typeof(IAsyncEnumerable<>));
        var builderT = SmTypes.Clr(typeof(AsyncIteratorMethodBuilder));
        var promiseT = SmTypes.Gen(typeof(ManualResetValueTaskSourceCore<>), SmTypes.Bool);
        var enumeratorT = SmTypes.Gen(typeof(IAsyncEnumerator<>), elem);
        var enumerableT = SmTypes.Gen(typeof(IAsyncEnumerable<>), elem);
        var vtsBool = SmTypes.Gen(typeof(IValueTaskSource<>), SmTypes.Bool);
        var vts = SmTypes.Clr(typeof(IValueTaskSource));
        var asyncDisposable = SmTypes.Clr(typeof(IAsyncDisposable));
        var statusT = SmTypes.Clr(typeof(ValueTaskSourceStatus));
        var flagsT = SmTypes.Clr(typeof(ValueTaskSourceOnCompletedFlags));
        var actionT = SmTypes.Gen(typeof(Action<>), SmTypes.Obj with { Nullable = true });
        var objQ = SmTypes.Obj with { Nullable = true };

        stateF = AddField("<>1__state", SmTypes.Int);
        builderF = AddField("<>t__builder", builderT);
        promiseF = AddField("<>v__promiseOfValueOrEnd", promiseT);
        currentF = AddField("<>2__current", elem, AirVis.Private);
        disposeModeF = AddField("<>w__disposeMode", SmTypes.Bool, AirVis.Private);
        if (enumerable) threadF = AddField("<>l__initialThreadId", ThreadIdT, AirVis.Private);
        if (enumerable) decl.Interfaces.Add(enumerableT);
        decl.Interfaces.AddRange([enumeratorT, asyncDisposable, vtsBool, vts, IAsyncStateMachineT]);

        // MoveNext
        EmitAsync(body, []);
        var mn = new List<MStmt>(moveNextLocals);
        var tryBody = new List<MStmt>();
        if (Dispatch(topEntries) is { } table) tryBody.Add(table);
        tryBody.Add(new MIf { Cond = F(disposeModeF), Then = [new AirGoto { Label = ExitLabel }] });
        tryBody.Add(SetState(-1));
        tryBody.AddRange(body);
        string ex = "<>ex";
        mn.Add(new MTry
        {
            Body = tryBody,
            Catches =
            [
                new MCatch
                {
                    TypeCs = "global::System.Exception", Name = ex,
                    Body =
                    [
                        SetState(-2), Set(F(currentF), new AirDefault(elem)), Do(Call(F(builderF), "Complete", SmTypes.Void)),
                        Do(Call(F(promiseF), "SetException", SmTypes.Void, Local(ex, SmTypes.Exception))), new MReturn(),
                    ],
                },
            ],
        });
        mn.Add(new AirLabel { Name = ExitLabel });
        mn.Add(SetState(-2));
        mn.Add(Set(F(currentF), new AirDefault(elem)));
        mn.Add(Do(Call(F(builderF), "Complete", SmTypes.Void)));
        mn.Add(Do(Call(F(promiseF), "SetResult", SmTypes.Void, AirConst.Bool(false))));
        mn.Add(new MReturn());
        Impl(IAsyncStateMachineT, "MoveNext", "MoveNext", SmTypes.Void, mn);
        Impl(IAsyncStateMachineT, "SetStateMachine", "SetStateMachine", SmTypes.Void, [new MReturn()], new AirParamDecl("stateMachine", IAsyncStateMachineT));

        // .ctor(int <>1__state)
        var stP = new AirParamDecl("<>1__state", SmTypes.Int);
        var ctor = new List<MStmt> { Set(State(), new AirParamRef(stP.Name, SmTypes.Int)) };
        if (threadF != null) ctor.Add(Set(F(threadF), ManagedThreadId()));
        ctor.Add(Set(F(builderF), Call(TypeRef(builderT), "Create", builderT)));
        Ctor(ctor, stP);

        if (enumerable)
        {
            var tok = new AirParamDecl("cancellationToken", SmTypes.Clr(typeof(CancellationToken)));
            Impl(enumerableT, "GetAsyncEnumerator", $"System.Collections.Generic.IAsyncEnumerable<{elem.ToCs()}>.GetAsyncEnumerator", enumeratorT,
                GetEnumeratorBody(-3, enumeratorT, [Set(F(builderF), Call(TypeRef(builderT), "Create", builderT)), Set(F(disposeModeF), AirConst.Bool(false))]), tok);
        }

        // MoveNextAsync
        var vtb = ValueTaskBool;
        var sm = Local("<>sm", smType);
        var ver = Local("<>ver", SmTypes.Short);
        Impl(enumeratorT, "MoveNextAsync", $"System.Collections.Generic.IAsyncEnumerator<{elem.ToCs()}>.MoveNextAsync", vtb,
        [
            new MIf { Cond = Bin(AirBinOp.Eq, State(), I(-2)), Then = [new MReturn { Value = new AirDefault(vtb) }] },
            Do(Call(F(promiseF), "Reset", SmTypes.Void)),
            Decl("<>sm", smType, This()),
            Do(Call(F(builderF), "MoveNext", SmTypes.Void, Ref(sm))),
            Decl("<>ver", SmTypes.Short, Member(F(promiseF), "Version", SmTypes.Short)),
            new MIf
            {
                Cond = Bin(AirBinOp.Eq, Call(F(promiseF), "GetStatus", statusT, ver), Member(TypeRef(statusT), "Succeeded", statusT)),
                Then = [new MReturn { Value = new MNew { TypeCs = vtb.ToCs(), Type = vtb, Args = [Call(F(promiseF), "GetResult", SmTypes.Bool, ver)] } }],
            },
            new MReturn { Value = new MNew { TypeCs = vtb.ToCs(), Type = vtb, Args = [new MCast { TypeCs = vtsBool.ToCs(), E = This(), Type = vtsBool }, ver] } },
        ]);
        var getCur = Impl(enumeratorT, "get_Current", $"System.Collections.Generic.IAsyncEnumerator<{elem.ToCs()}>.get_Current", elem, [new MReturn { Value = F(currentF) }]);
        getCur.Flags |= AirMethodFlags.SpecialName;
        Property($"System.Collections.Generic.IAsyncEnumerator<{elem.ToCs()}>.Current", elem, getCur);

        // DisposeAsync
        Impl(asyncDisposable, "DisposeAsync", "System.IAsyncDisposable.DisposeAsync", ValueTaskT,
        [
            new MIf { Cond = Bin(AirBinOp.Ge, State(), I(-1)), Then = [new MThrow { Value = new MNew { TypeCs = "global::System.NotSupportedException", Type = SmTypes.Clr(typeof(NotSupportedException)) } }] },
            new MIf { Cond = Bin(AirBinOp.Eq, State(), I(-2)), Then = [new MReturn { Value = new AirDefault(ValueTaskT) }] },
            Set(F(disposeModeF), AirConst.Bool(true)),
            Do(Call(F(promiseF), "Reset", SmTypes.Void)),
            Decl("<>sm", smType, This()),
            Do(Call(F(builderF), "MoveNext", SmTypes.Void, Ref(sm))),
            new MReturn { Value = new MNew { TypeCs = ValueTaskT.ToCs(), Type = ValueTaskT, Args = [new MCast { TypeCs = vts.ToCs(), E = This(), Type = vts }, Member(F(promiseF), "Version", SmTypes.Short)] } },
        ]);

        // IValueTaskSource<bool> / IValueTaskSource: the promise answers
        var token = new AirParamDecl("token", SmTypes.Short);
        var tok2 = new AirParamRef("token", SmTypes.Short);
        var cont = new AirParamDecl("continuation", actionT);
        var st = new AirParamDecl("state", objQ);
        var fl = new AirParamDecl("flags", flagsT);
        MStmt OnCompleted() => Do(Call(F(promiseF!), "OnCompleted", SmTypes.Void, new AirParamRef(cont.Name, cont.Type), new AirParamRef(st.Name, st.Type), tok2, new AirParamRef(fl.Name, fl.Type)));
        string vb = "System.Threading.Tasks.Sources.IValueTaskSource<bool>.";
        Impl(vtsBool, "GetResult", vb + "GetResult", SmTypes.Bool, [new MReturn { Value = Call(F(promiseF), "GetResult", SmTypes.Bool, tok2) }], token);
        Impl(vtsBool, "GetStatus", vb + "GetStatus", statusT, [new MReturn { Value = Call(F(promiseF), "GetStatus", statusT, tok2) }], token);
        Impl(vtsBool, "OnCompleted", vb + "OnCompleted", SmTypes.Void, [OnCompleted(), new MReturn()], cont, st, token, fl);
        string v0 = "System.Threading.Tasks.Sources.IValueTaskSource.";
        Impl(vts, "GetResult", v0 + "GetResult", SmTypes.Void, [Do(Call(F(promiseF), "GetResult", SmTypes.Bool, tok2)), new MReturn()], token);
        Impl(vts, "GetStatus", v0 + "GetStatus", statusT, [new MReturn { Value = Call(F(promiseF), "GetStatus", statusT, tok2) }], token);
        Impl(vts, "OnCompleted", v0 + "OnCompleted", SmTypes.Void, [OnCompleted(), new MReturn()], cont, st, token, fl);

        var k = Kickoff(enumerable ? -2 : -3, enumerable);
        OrderFields();
        return k;
    }

    /// <summary>The kickoff of an iterator: `new SM(state)`, arguments copied (to `&lt;&gt;3__p` for an enumerable), returned.</summary>
    List<MStmt> Kickoff(int initial, bool enumerable)
    {
        var d = Local("<>d", smType);
        var k = new List<MStmt> { Decl("<>d", smType, new MNew { TypeCs = smType.ToCs(), Type = smType, Args = [I(initial)] }) };
        CopyArguments(k, d, enumerable ? "<>3__" : "");
        k.Add(new MReturn { Value = d });
        return k;
    }

    /// <summary>GetEnumerator / GetAsyncEnumerator: reuse this object on its creating thread before the first call,
    /// else a fresh machine; then copy the pristine arguments into the working fields.</summary>
    List<MStmt> GetEnumeratorBody(int startState, AirType ret, List<MStmt> reuse)
    {
        var d = Local("<>d", smType);
        var then = new List<MStmt> { SetState(startState) };
        then.AddRange(reuse);
        then.Add(Set(d, This()));
        var els = new List<MStmt> { Set(d, new MNew { TypeCs = smType.ToCs(), Type = smType, Args = [I(startState)] }) };
        if (thisField != null) els.Add(Set(Member(d, thisField.Name, thisField.Type), F(thisField)));
        var b = new List<MStmt>
        {
            Decl("<>d", smType),
            new MIf
            {
                Cond = Bin(AirBinOp.And, Bin(AirBinOp.Eq, State(), I(-2)), Bin(AirBinOp.Eq, F(threadF!), ManagedThreadId())),
                Then = then, Else = els,
            },
        };
        foreach (var p in m.Params)
            b.Add(Set(Member(d, ParamField(p.Name).Name, p.Type), F(CopyField(p))));
        b.Add(new MReturn { Value = d });
        return b;
    }
}
