using System.Collections;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// Iterators (C#'s IteratorMethodToStateMachineRewriter shape): a sealed class that is its own enumerable and
/// enumerator. States: 0 before the first MoveNext, -1 running or done, -2 an enumerable not yet enumerated (or
/// disposed), 1.. suspended at a `yield`, -3, -4.. inside a try block that holds a yield. Such a try block is
/// flattened: entering it sets its state, its finally becomes `&lt;&gt;m__FinallyN` (which first restores the
/// enclosing state), called on normal exit, before a jump out of it, and by Dispose for the states inside it; MoveNext
/// runs Dispose when an exception escapes (C# uses a fault block).
/// </summary>
sealed partial class StateMachineRewriter
{
    sealed class IFrame
    {
        public int State;
        public IFrame? Parent;
        public string Method = "";
        public readonly List<IFrame> Children = new();
        public readonly List<int> Yields = new();
    }

    /// <summary>A real (not flattened) try block on the jump-crossing stack. A jump that leaves it and also a
    /// flattened frame outside it first leaves to <see cref="After"/> (the real finally runs), then continues there.</summary>
    sealed class RealTry
    {
        public bool Finally;
        public string After = "";
        public readonly List<(int Code, MStmt Jump)> Pending = new();
    }

    sealed class LoopMark
    {
        public int Depth;
        public string? Continue;
    }

    readonly List<IFrame> topFrames = new();
    int iterState = 1, frameState = -3, finallyN;

    List<MStmt> BuildIterator(List<MStmt> body)
    {
        var elem = ElemOf(m.Ret)!;
        bool enumerable = m.Ret.ToCs().StartsWith("global::System.Collections.Generic.IEnumerable") || m.Ret.ToCs() == "global::System.Collections.IEnumerable";
        var ienumT = SmTypes.Gen(typeof(IEnumerable<>), elem);
        var ienumeratorT = SmTypes.Gen(typeof(IEnumerator<>), elem);
        var ienum = SmTypes.Clr(typeof(IEnumerable));
        var ienumerator = SmTypes.Clr(typeof(IEnumerator));
        var idisp = SmTypes.Clr(typeof(IDisposable));

        stateF = AddField("<>1__state", SmTypes.Int);
        currentF = AddField("<>2__current", elem, AirVis.Private);
        if (enumerable) threadF = AddField("<>l__initialThreadId", SmTypes.Int, AirVis.Private);
        if (enumerable) decl.Interfaces.AddRange([ienumT, ienum]);
        decl.Interfaces.AddRange([ienumeratorT, ienumerator, idisp]);

        // MoveNext
        string start = Label("start");
        var marks = new List<object>();
        topEntries.Add((0, start));
        EmitIterator(body, null, marks, []);
        var core = new List<MStmt>();
        if (jumpCodes > 0) core.Add(Decl(JumpLocal, SmTypes.Int, I(0)));
        core.Add(Dispatch(topEntries)!);
        core.Add(new MReturn { Value = AirConst.Bool(false) });
        core.Add(new AirLabel { Name = start });
        core.Add(SetState(-1));
        core.AddRange(body);
        core.Add(new MReturn { Value = AirConst.Bool(false) });
        string disposeName = "System.IDisposable.Dispose";
        List<MStmt> mn;
        if (topFrames.Count == 0) mn = core;
        else
        {
            var edi = SmTypes.Clr(typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo));
            mn =
            [
                new MTry
                {
                    Body = core,
                    Catches =
                    [
                        new MCatch
                        {
                            TypeCs = "global::System.Exception", Name = "<>ex",
                            Body =
                            [
                                Do(Call(This(), disposeName, SmTypes.Void)),
                                Do(Call(Call(TypeRef(edi), "Capture", edi, Local("<>ex", SmTypes.Exception)), "Throw", SmTypes.Void)),
                            ],
                        },
                    ],
                },
                new MReturn { Value = AirConst.Bool(false) },
            ];
        }
        Impl(ienumerator, "MoveNext", "MoveNext", SmTypes.Bool, mn);

        // .ctor(int <>1__state)
        var stP = new AirParamDecl("<>1__state", SmTypes.Int);
        var ctor = new List<MStmt> { Set(State(), new AirParamRef(stP.Name, SmTypes.Int)) };
        if (threadF != null) ctor.Add(Set(F(threadF), ManagedThreadId()));
        Ctor(ctor, stP);

        // Dispose: the finally blocks of the try blocks the current state is inside, innermost first
        var dispose = DisposeFrames(topFrames);
        dispose.Add(SetState(-2));
        dispose.Add(new MReturn());
        Impl(idisp, "Dispose", disposeName, SmTypes.Void, dispose);

        // Current, Reset
        string gcur = $"System.Collections.Generic.IEnumerator<{elem.ToCs()}>.get_Current";
        var getCur = Impl(ienumeratorT, "get_Current", gcur, elem, [new MReturn { Value = F(currentF) }]);
        getCur.Flags |= AirMethodFlags.SpecialName;
        Property($"System.Collections.Generic.IEnumerator<{elem.ToCs()}>.Current", elem, getCur);
        var getCur0 = Impl(ienumerator, "get_Current", "System.Collections.IEnumerator.get_Current", SmTypes.Obj,
            [new MReturn { Value = new MCast { TypeCs = "object", E = F(currentF), Type = SmTypes.Obj } }]);
        getCur0.Flags |= AirMethodFlags.SpecialName;
        Property("System.Collections.IEnumerator.Current", SmTypes.Obj, getCur0);
        Impl(ienumerator, "Reset", "System.Collections.IEnumerator.Reset", SmTypes.Void,
            [new MThrow { Value = new MNew { TypeCs = "global::System.NotSupportedException", Type = SmTypes.Clr(typeof(NotSupportedException)) } }]);

        if (enumerable)
        {
            string gen = $"System.Collections.Generic.IEnumerable<{elem.ToCs()}>.GetEnumerator";
            Impl(ienumT, "GetEnumerator", gen, ienumeratorT, GetEnumeratorBody(0, ienumeratorT, []));
            Impl(ienum, "GetEnumerator", "System.Collections.IEnumerable.GetEnumerator", ienumerator,
                [new MReturn { Value = Call(This(), gen, ienumeratorT) }]);
        }

        var k = Kickoff(enumerable ? -2 : 0, enumerable);
        OrderFields();
        return k;
    }

    /// <summary>Real try blocks holding a yield (outermost first): each dispatches to the resume points inside it.</summary>
    readonly List<Frame> iterReal = new();
    int jumpCodes;
    const string JumpLocal = "<>jb";

    /// <summary>Rewrites yields, yield breaks, flattened try blocks and the jumps that leave them.
    /// <paramref name="marks"/> holds the open flattened frames and real try blocks (outermost first).</summary>
    void EmitIterator(List<MStmt> list, IFrame? frame, List<object> marks, List<LoopMark> loops)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            List<MStmt>? rep = null;
            switch (s)
            {
                case SYield y:
                    {
                        int state = iterState++;
                        string resume = Label("yield");
                        Register(state, resume, iterReal);
                        frame?.Yields.Add(state);
                        rep =
                        [
                            Set(F(currentF!), y.Value),
                            SetState(state),
                            new MReturn { Value = AirConst.Bool(true) },
                            new AirLabel { Name = resume },
                            SetState(frame?.State ?? -1),
                        ];
                        break;
                    }
                case SYieldBreak:
                    rep = Jump(marks, 0, s, () => new MReturn { Value = AirConst.Bool(false) }) ?? [new MReturn { Value = AirConst.Bool(false) }];
                    break;
                case MBreak or MContinue when loops.Count > 0:
                    rep = Jump(marks, loops[^1].Depth, s, () => s);
                    break;
                case AirGoto g when loops.FindLast(l => l.Continue == g.Label) is { } lm:
                    rep = Jump(marks, lm.Depth, s, () => s);
                    break;
                case MTry t when Flattened(t):
                    {
                        var f = new IFrame { State = frameState--, Parent = frame, Method = $"<>m__Finally{++finallyN}" };
                        (frame?.Children ?? topFrames).Add(f);
                        marks.Add(f);
                        EmitIterator(t.Body, f, marks, loops);
                        marks.RemoveAt(marks.Count - 1);
                        var fin = new List<MStmt> { SetState(frame?.State ?? -1) };
                        fin.AddRange(t.Finally!);
                        fin.Add(new MReturn());
                        AddMethod(f.Method, SmTypes.Void, fin);
                        var block = new MBlock { Line = t.Line };
                        block.Body.Add(SetState(f.State));
                        block.Body.AddRange(t.Body);
                        block.Body.Add(Do(Call(This(), f.Method, SmTypes.Void)));
                        rep = [block];
                        break;
                    }
                case MTry t:
                    {
                        // a try with rescue clauses around a yield stays a real try: a label before it, a second
                        // dispatch at the start of its body (the clauses never hold a yield: AwaitsInFinally moved them)
                        var rf = Suspends(t.Body) ? new Frame { Try = t, Head = Label("try") } : null;
                        var rt = new RealTry { Finally = t.Finally != null, After = Label("aftertry") };
                        marks.Add(rt);
                        if (rf != null) iterReal.Add(rf);
                        EmitIterator(t.Body, frame, marks, loops);
                        if (rf != null) iterReal.RemoveAt(iterReal.Count - 1);
                        foreach (var c in t.Catches) EmitIterator(c.Body, frame, marks, loops);
                        marks.RemoveAt(marks.Count - 1);
                        if (t.Finally != null) EmitIterator(t.Finally, frame, marks, []);
                        if (rf != null)
                        {
                            if (Dispatch(rf.Entries) is { } table) t.Body.Insert(0, table);
                            list.Insert(i, new AirLabel { Name = rf.Head });
                            i++;
                        }
                        if (rt.Pending.Count > 0)
                        {
                            var after = new List<MStmt> { new AirLabel { Name = rt.After } };
                            foreach (var (code, jump) in rt.Pending)
                                after.Add(new MIf
                                {
                                    Cond = Bin(AirBinOp.Eq, Local(JumpLocal, SmTypes.Int), I(code)),
                                    Then = [Set(Local(JumpLocal, SmTypes.Int), I(0)), jump],
                                });
                            list.InsertRange(i + 1, after);   // visited next, outside this try: the jump continues from there
                        }
                        continue;
                    }
            }
            if (rep != null)
            {
                foreach (var x in rep)
                    if (x.Line == 0) x.Line = s.Line;
                list.RemoveAt(i);
                list.InsertRange(i, rep);
                i += rep.Count - 1;
                continue;
            }
            if (s is MWhile or MFor or MForeach)
            {
                var lm = new LoopMark { Depth = marks.Count, Continue = s is MWhile w ? continueLabels.GetValueOrDefault(w) : null };
                loops.Add(lm);
                foreach (var l in SmTree.Lists(s)) EmitIterator(l, frame, marks, loops);
                loops.RemoveAt(loops.Count - 1);
                continue;
            }
            foreach (var l in SmTree.Lists(s)) EmitIterator(l, frame, marks, loops);
        }
    }

    /// <summary>
    /// A jump (break, continue, yield break) that leaves the open blocks above <paramref name="depth"/>: the
    /// flattened frames it leaves get their finally method called, innermost first. When a real try with a finally
    /// lies between, the jump first leaves to just after that try (its finally runs in order) and is replayed there.
    /// Null: nothing flattened is left, the jump stays as it is.
    /// </summary>
    List<MStmt>? Jump(List<object> marks, int depth, MStmt jump, Func<MStmt> terminal)
    {
        var crossed = marks.Skip(depth).ToList();
        if (!crossed.Any(x => x is IFrame)) return null;
        int r = crossed.FindLastIndex(x => x is RealTry { Finally: true });
        var calls = crossed.Skip(r + 1).OfType<IFrame>().Reverse().Select(f => (MStmt)Do(Call(This(), f.Method, SmTypes.Void))).ToList();
        if (r < 0) return [.. calls, terminal()];
        var rt = (RealTry)crossed[r];
        int code = ++jumpCodes;
        rt.Pending.Add((code, SmTree.Clone(jump)));
        return [.. calls, Set(Local(JumpLocal, SmTypes.Int), I(code)), new AirGoto { Label = rt.After }];
    }

    static IEnumerable<int> StatesOf(IFrame f) => f.Children.SelectMany(StatesOf).Concat(f.Yields).Prepend(f.State);

    List<MStmt> DisposeFrames(List<IFrame> fs)
    {
        var outp = new List<MStmt>();
        foreach (var f in fs)
        {
            MExpr? cond = null;
            foreach (int s in StatesOf(f).Distinct())
            {
                var eq = Bin(AirBinOp.Eq, State(), I(s));
                cond = cond == null ? eq : Bin(AirBinOp.Or, cond, eq);
            }
            outp.Add(new MIf
            {
                Cond = cond!,
                Then = [new MTry { Body = DisposeFrames(f.Children), Finally = [Do(Call(This(), f.Method, SmTypes.Void))] }],
            });
        }
        return outp;
    }
}
