using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// `dataflow do ... end` (ADR 0038, ADR 0040 section 4): the network the binder proved (Binder.Dataflow.cs) becomes
/// one <see cref="AirDataflow"/> whose runner builds an `Alice.Runtime.Graph` and runs it once. Each node is a static
/// method over a batch, `(ref TState, ReadOnlySpan&lt;TIn&gt;, ref RingWriter&lt;TOut&gt;)`, registered through the
/// runtime's function-pointer overloads: no delegate and no lambda per item. A node's state is a tuple of its
/// `state:` value and the locals it captures, bound by `ref` inside the method, so a sink's writes land in its state
/// and are copied back to the enclosing method's locals after the run. A source block is an iterator method
/// (`out << v` is `yield return v`) drained by a pump while its ring has room, so a source of any length runs in
/// bounded memory. `stage(par: true)` must be pure (AL0245, the interprocedural gate of <see cref="MethodPure"/>).
/// </summary>
public sealed partial class Lowerer
{
    const string DfRt = "global::Alice.Runtime";

    MExpr Dataflow(DataflowExpr df)
    {
        var g = df.Graph ?? throw new CompileException($"{File}:{df.Line}:{Math.Max(1, df.Col)}: dataflow block was not bound");
        if (Method != null && Method.TypeParams.Count > 0)
            throw new CompileException($"{File}:{df.Line}:{Math.Max(1, df.Col)}: error AL0246: a `dataflow` block cannot be in a generic method yet");
        string runner = $"__df_{(Method?.CsName ?? "main").TrimStart('@')}_{++helperN}";
        var air = new AirDataflow { Runner = runner, Type = AirLeaf.Void, Capacity = g.Capacity, Batch = g.Batch };
        var passed = new HashSet<LocalSym>();
        foreach (var n in g.Nodes)
            foreach (var l in n.Captures)
                if (passed.Add(l))
                {
                    var t = AirOf(l.Type, LocalTypeCs(l));
                    air.Params.Add(new AirParamDecl(l.CsName, t));
                    air.Args.Add(new AirLocalRef(l.CsName, t));
                }

        var stageOf = new Dictionary<DataflowNode, AirStage>();
        var targets = new List<string>();
        for (int k = 0; k < g.Nodes.Count; k++)
        {
            var n = g.Nodes[k];
            var blk = n.Block;
            if (n.Par) CheckParPure(n);
            var st = new AirStage
            {
                Kind = n.Kind switch { DataflowKind.Source => AirStageKind.Source, DataflowKind.Sink => AirStageKind.Sink, _ => n.Par ? AirStageKind.Replicate : AirStageKind.Stage },
                Method = $"{runner}_n{k}",
                Parallel = n.Par,
                Ordered = n.Ordered,
                Workers = n.Workers,
                Batch = n.Batch,
                Item = n.ItemParam?.CsName,
            };
            if (n.State != null)
            {
                string pn = $"__df_state{k}";
                var t = AirOf(n.StateType, TypeCs(n.StateType));
                air.Params.Add(new AirParamDecl(pn, t));
                air.Args.Add(Expr(n.State));
                st.State.Add(new AirStageField(n.StateParam!.CsName, t, new AirLocalRef(pn, t)));
            }
            foreach (var l in n.Captures)
            {
                var t = AirOf(l.Type, LocalTypeCs(l));
                st.State.Add(new AirStageField(l.CsName, t, new AirLocalRef(l.CsName, t)));
            }
            if (n.Input != null)
            {
                var input = stageOf[n.Input];
                st.Input = input.Output;
                st.In = input.Out;
            }
            if (n.Kind != DataflowKind.Sink)
            {
                st.Out = AirOf(n.Out, TypeCs(n.Out));
                st.Output = air.Rings.Count;
                air.Rings.Add(new AirRing { Elem = st.Out, Capacity = n.Capacity });
                st.Writer = n.MapForm ? "__df_w" : n.OutParam!.CsName;
            }
            if (n.Kind == DataflowKind.Source) st.Pump = st.Method + "_pump";

            var body = new List<MStmt>();
            if (n.MapForm)
            {
                st.MapValue = "__df_v";
                body.Add(new MDecl { Name = st.MapValue, TypeCs = st.Out!.ToCs(), Init = new AirDefault(null, forgiving: true) });
                body.AddRange(NodeBody(blk, Tail.Assign, st.MapValue));
                body.Add(new MExprStmt { E = new MCall { Target = new AirLocalRef(st.Writer!, null), Name = "Write", Args = { new AirLocalRef(st.MapValue, st.Out) } } });
            }
            else body.AddRange(NodeBody(blk, Tail.None, null));
            if (n.Kind == DataflowKind.Source)
            {
                body = Yields(body, st.Writer!, n);
                if (st.State.Any(f => f.Name == st.Writer)) throw DfError(blk, "AL0246", "a source's state cannot share the name of its writer");
            }
            Optimizer.Pipeline.Run(body);
            st.Body = body;
            air.Stages.Add(st);
            stageOf[n] = st;

            foreach (var w in n.Writes)
            {
                int field = st.State.FindIndex(f => f.Name == w.CsName);
                air.Results.Add(new AirDataflowResult(k, field, st.State[field].Type));
                targets.Add(w.CsName);
            }
        }
        if (air.Results.Count > 0) air.Type = air.Results.Count == 1 ? air.Results[0].Type : AirType.Unknown;
        Extra.Add(Backend.Printer.DataflowMembers(air));
        switch (targets.Count)
        {
            case 0:
                return air;
            case 1:
                pre.Add(new MAssign { Target = new AirLocalRef(targets[0], air.Results[0].Type), Value = air });
                return MCode.Empty;
            default:
                pre.Add(new MDeconstruct { Names = targets, Value = air });
                return MCode.Empty;
        }
    }

    CompileException DfError(Node at, string code, string msg) => new($"{File}:{at.Line}:{Math.Max(1, at.Col)}: error {code}: {msg}");

    /// <summary>A node block's statements as the body of one item: `next` continues with the next item.</summary>
    List<MStmt> NodeBody(LambdaExpr blk, Tail tail, string? target)
    {
        var savedPre = pre;
        pre = new List<MStmt>();
        int savedNoHoist = noHoist;
        noHoist = 0;
        ctx.Add("loop");
        try { return Stmts(blk.Body, tail, target); }
        finally
        {
            ctx.RemoveAt(ctx.Count - 1);
            noHoist = savedNoHoist;
            pre = savedPre;
        }
    }

    /// <summary>A source body as an iterator: every `out.Write(v)` statement becomes `yield return v`.</summary>
    List<MStmt> Yields(List<MStmt> body, string writer, DataflowNode n)
    {
        var r = new List<MStmt>(body.Count);
        foreach (var s in body)
        {
            switch (s)
            {
                case MExprStmt { E: MCall { Target: MCode { Text: var t }, Name: "Write", Args: [var v] } } when t == writer:
                    r.Add(new MExprStmt { E = new MArg { Prefix = "yield return ", E = v }, Line = s.Line });
                    continue;
                case MIf i:
                    i.Then = Yields(i.Then, writer, n);
                    if (i.Else != null) i.Else = Yields(i.Else, writer, n);
                    break;
                case MWhile w: w.Body = Yields(w.Body, writer, n); break;
                case MFor f: f.Body = Yields(f.Body, writer, n); break;
                case MForeach fe: fe.Body = Yields(fe.Body, writer, n); break;
                case MBlock b: b.Body = Yields(b.Body, writer, n); break;
                case MUsing u: u.Body = Yields(u.Body, writer, n); break;
                case MTry tr:
                    tr.Body = Yields(tr.Body, writer, n);
                    foreach (var c in tr.Catches) c.Body = Yields(c.Body, writer, n);
                    if (tr.Finally != null) tr.Finally = Yields(tr.Finally, writer, n);
                    break;
            }
            if (Mentions(s, writer))
                throw DfError(n.Block, "AL0246", $"a source writes `{n.Block.Params[^1].Name}` only as a statement of its own body or of its loops (`for`, `while`, `each` on a range or array), not inside a block that is not inlined or as a value");
            r.Add(s);
        }
        return r;
    }

    /// <summary>Whether the statement's own expressions (not its nested statement lists, already rewritten) mention <paramref name="name"/>.</summary>
    static bool Mentions(MStmt s, string name)
    {
        IEnumerable<MExpr?> roots = s switch
        {
            MExprStmt es => [es.E],
            MDecl d => [d.Init],
            MAssign a => [a.Target, a.Value],
            MDeconstruct dc => [dc.Value],
            MIf i => [i.Cond],
            MWhile w => [w.Cond],
            MFor f => [f.Start, f.End],
            MForeach fe => [fe.Coll],
            MReturn r => [r.Value],
            MThrow t => [t.Value],
            MUsing u => [u.Value],
            _ => [],
        };
        return roots.Any(e => e != null && MentionsExpr(e, name));
    }

    static bool MentionsExpr(MExpr e, string name)
    {
        if (e is MCode c && System.Text.RegularExpressions.Regex.IsMatch(c.Text, $@"(?<![\w@])@?{System.Text.RegularExpressions.Regex.Escape(name.TrimStart('@'))}(?!\w)")) return true;
        if (e is MLambda l)
            return l.ExprBody != null ? MentionsExpr(l.ExprBody, name) : l.Body!.Any(s => Mentions(s, name) || NestedMentions(s, name));
        return Optimizer.MirFacts.Children(e).Any(x => MentionsExpr(x, name));
    }

    static bool NestedMentions(MStmt s, string name)
    {
        IEnumerable<List<MStmt>> kids = s switch
        {
            MIf i => i.Else != null ? [i.Then, i.Else] : [i.Then],
            MWhile w => [w.Body],
            MFor f => [f.Body],
            MForeach fe => [fe.Body],
            MBlock b => [b.Body],
            MUsing u => [u.Body],
            MTry tr => tr.Catches.Select(c => c.Body).Prepend(tr.Body).Concat(tr.Finally != null ? [tr.Finally] : []),
            _ => [],
        };
        return kids.Any(k => k.Any(x => Mentions(x, name) || NestedMentions(x, name)));
    }

    // ---------------------------------------------------------------- AL0245

    /// <summary>`stage(par: true)`: each batch runs on a fresh copy of the state on any worker, so the block must be
    /// pure (the gate of forks and parallel loops) and may not write its state.</summary>
    void CheckParPure(DataflowNode n)
    {
        var plan = new LoopPlan { LoopVar = new LocalSym(), Fresh = FreshArrays(n.Block.Body) };
        if (Impure(n.Block.Body, n, plan) is not { } bad) return;
        throw DfError(bad, "AL0245", $"stage(par: true) is not pure: line {bad.Line} has an effect (I/O, a write to the stage's state or to a shared object, or a call not proven pure); a replicated stage may only compute from its item and state; remove the effect, or drop `par: true`");
    }

    /// <summary>The first statement of a `par: true` block that is not pure, or null.</summary>
    Stmt? Impure(List<Stmt> body, DataflowNode n, LoopPlan plan)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case ExprStmt { Expr: CallExpr { Recv: NameExpr { Sym: LocalSym o }, Args: [{ Value: { } v }] } } when o == n.OutParam:
                    if (!PureRead(v, plan)) return s;
                    continue;
                case Assign { Target: NameExpr { Sym: LocalSym t } } when t == n.StateParam || n.Captures.Contains(t):
                    return s;
                case IfStmt i:
                    if (!i.Conds.All(c => PureRead(c, plan))) return s;
                    foreach (var b in i.Bodies) if (Impure(b, n, plan) is { } x) return x;
                    if (i.Else != null && Impure(i.Else, n, plan) is { } y) return y;
                    continue;
                case WhileStmt w:
                    if (!PureRead(w.Cond, plan)) return s;
                    if (Impure(w.Body, n, plan) is { } wx) return wx;
                    continue;
                case ForStmt { Iter: CallExpr { Name: "AsSpan", Recv: { } sr, Args: var sa, Block: null } } fs
                    when sr.Type.Kind == "array" && PureRead(sr, plan) && sa.All(x => x.Mod == "" && x.Name == null && PureRead(x.Value!, plan)):
                    // `for b in a.AsSpan(i, n)`: iterating a view only reads the array
                    if (Impure(fs.Body, n, plan) is { } sx) return sx;
                    continue;
                case ForStmt f:
                    if (!(f.Iter is RangeExpr rr ? PureRead(rr.A, plan) && PureRead(rr.B, plan) : PureRead(f.Iter, plan))) return s;
                    if (Impure(f.Body, n, plan) is { } fx) return fx;
                    continue;
            }
            if (!PureMethodBody([s], plan, false)) return s;
        }
        return null;
    }
}
