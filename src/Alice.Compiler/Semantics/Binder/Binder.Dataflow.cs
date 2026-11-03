using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public enum DataflowKind { Source, Stage, Sink }

/// <summary>
/// One node of a `dataflow do ... end` network (ADR 0038): a `source`, `stage` or `sink` call with its block. The
/// binder fills the types and captures on every pass; the lowerer (Lowering/Lowerer.Dataflow.cs) turns it into a
/// static method over a batch.
/// </summary>
public sealed class DataflowNode
{
    public DataflowKind Kind;
    /// <summary>The pipe this node produces (`lines = source { }`); null for a sink or an unnamed chain link.</summary>
    public string? Name;
    public DataflowNode? Input;
    public CallExpr Call = null!;
    public LambdaExpr Block => Call.Block!;
    /// <summary>`state:`: evaluated once before the run, owned by the node (Kahn process state).</summary>
    public Expr? State;
    /// <summary>`par: true`: replicated over the pool (pure blocks only, AL0245).</summary>
    public bool Par;
    /// <summary>`ordered: false`: a replicated stage merges batches as they finish.</summary>
    public bool Ordered = true;
    /// <summary>`capacity:` of the output ring (0: the graph default).</summary>
    public int Capacity;
    /// <summary>`workers:` of a replicated stage (0: one per processor).</summary>
    public int Workers;
    /// <summary>`batch:` of a replicated stage: items per numbered batch (0: the graph's).</summary>
    public int Batch;
    /// <summary>Element type of the output edge (source and stage).</summary>
    public AType Out = AType.Unknown;
    public AType StateType = AType.Unknown;
    public LocalSym? StateParam, ItemParam, OutParam;
    /// <summary>A stage whose block value is its output (`stage { |x| f(x) }`), no `out` parameter.</summary>
    public bool MapForm;
    /// <summary>Outer locals the block reads (copied into its state before the run).</summary>
    public List<LocalSym> Captures = new();
    /// <summary>Outer locals a sink writes (copied back after the run).</summary>
    public List<LocalSym> Writes = new();
    /// <summary>The binder's lambda depth outside the block: locals declared deeper are the block's own.</summary>
    internal int Depth;
}

/// <summary>The checked network: nodes in source order, each input defined before its consumer (acyclic, SPSC).</summary>
public sealed class DataflowGraph
{
    public readonly List<DataflowNode> Nodes = new();
    /// <summary>`dataflow(capacity:, batch:)`: default ring capacity and items per body call (0: the runtime's).</summary>
    public int Capacity, Batch;
}

/// <summary>
/// `dataflow do ... end` (ADR 0038). The body is a static network: `name = source { |out| ... }`,
/// `name = pipe.stage(state: s, par: b) { |st, x, out| ... }` (or `{ |x| value }`), `pipe.sink { |x| ... }`, chained
/// or named. Checks:
/// - AL0246: every pipe has exactly one producer (one assignment) and one consumer, is defined before it is read
///   (so the graph is acyclic), and the block holds nothing but nodes.
/// - AL0244: a node's block captures only sendable or thread-safe locals (the ADR 0028 AL0230/AL0231 rule); a sink
///   may also write (and read mutable) locals no other node mentions, copied back after the run. `self` is not
///   available inside nodes (they run as static methods on the pool).
/// AL0245 (purity of `par: true`) is decided by the lowering, which owns the interprocedural purity gate.
/// `stream def` bodies follow the same capture rule: no `self` or instance state of a type that is not sendable.
/// </summary>
public sealed partial class Binder
{
    static CompileException DfErr(string file, Node n, string code, string msg) => new($"{file}:{n.Line}:{Math.Max(1, n.Col)}: error {code}: {msg}");

    AType BindDataflow(DataflowExpr df)
    {
        df.Graph ??= BuildDataflowGraph(df);
        foreach (var n in df.Graph.Nodes) BindDataflowNode(n);
        if (finalPass) CheckDataflowCaptures(df.Graph);
        return AType.Void;
    }

    // ---------------------------------------------------------------- structure (AL0246)

    DataflowGraph BuildDataflowGraph(DataflowExpr df)
    {
        var g = new DataflowGraph();
        foreach (var a in df.Args)
        {
            if (a.Name == "capacity") g.Capacity = IntArg(a);
            else if (a.Name == "batch") g.Batch = IntArg(a);
            else throw DfErr(file, a, "AL0246", $"unknown `dataflow` argument `{a.Name ?? "positional"}`: use `capacity:` or `batch:`");
        }
        var pipes = new Dictionary<string, DataflowNode>();
        var consumed = new HashSet<DataflowNode>();
        var later = new HashSet<string>();
        foreach (var s in df.Body.Body)
            if (s is Assign { Target: NameExpr ln }) later.Add(ln.Name);

        DataflowNode NodeOf(Expr e, string? name)
        {
            if (e is not CallExpr { Block: not null } c || !(c.Recv == null ? c.Name == "source" : c.Name is "stage" or "sink"))
                throw DfErr(file, e, "AL0246", "not a static network: a `dataflow` block holds only `source { |out| ... }`, `pipe.stage { |x, out| ... }` and `pipe.sink { |x| ... }` nodes (each with a block)");
            var n = new DataflowNode { Call = c, Name = name, Kind = c.Name switch { "source" => DataflowKind.Source, "stage" => DataflowKind.Stage, _ => DataflowKind.Sink } };
            if (c.Recv != null)
            {
                DataflowNode input;
                if (c.Recv is NameExpr pn)
                {
                    if (!pipes.TryGetValue(pn.Name, out input!))
                        throw DfErr(file, pn, "AL0246", later.Contains(pn.Name)
                            ? $"dataflow graph has a cycle: `{pn.Name}` is read before it is produced (bounded rings could deadlock; use actors for feedback)"
                            : $"`{pn.Name}` is not a pipe of this dataflow block");
                }
                else input = NodeOf(c.Recv, null);
                if (!consumed.Add(input))
                    throw DfErr(file, c.Recv, "AL0246", $"dataflow edge `{input.Name ?? "pipe"}` has more than one consumer (single-producer single-consumer); split the stream explicitly with two stages");
                n.Input = input;
            }
            foreach (var a in c.Args)
            {
                switch (a.Name)
                {
                    case "state": n.State = a.Value; break;
                    case "par": n.Par = BoolArg(a); break;
                    case "ordered": n.Ordered = BoolArg(a); break;
                    case "capacity": n.Capacity = IntArg(a); break;
                    case "workers": n.Workers = IntArg(a); break;
                    case "batch": n.Batch = IntArg(a); break;
                    default:
                        throw DfErr(file, a, "AL0246", $"unknown dataflow argument `{a.Name ?? "positional"}`: use `state:`, `par:`, `ordered:`, `workers:`, `batch:` or `capacity:`");
                }
            }
            if (n.Kind != DataflowKind.Stage && n.Par || !n.Par && (!n.Ordered || n.Workers > 0 || n.Batch > 0))
                throw DfErr(file, c, "AL0246", "`par:` applies to a `stage`, and `ordered:`, `workers:` and `batch:` to a `stage(par: true)`");
            g.Nodes.Add(n);
            return n;
        }

        foreach (var s in df.Body.Body)
        {
            switch (s)
            {
                case Assign { Target: NameExpr pn, Op: "=" } a:
                    {
                        if (pipes.ContainsKey(pn.Name))
                            throw DfErr(file, pn, "AL0246", $"dataflow edge `{pn.Name}` has more than one producer");
                        var n = NodeOf(a.Value, pn.Name);
                        if (n.Kind == DataflowKind.Sink) throw DfErr(file, a, "AL0246", "a `sink` ends a branch: it produces no pipe");
                        pipes[pn.Name] = n;
                        break;
                    }
                case ExprStmt es:
                    {
                        var n = NodeOf(es.Expr, null);
                        if (n.Kind != DataflowKind.Sink) throw DfErr(file, es, "AL0246", "an unnamed dataflow chain must end with `.sink { |x| ... }`");
                        break;
                    }
                default:
                    throw DfErr(file, s, "AL0246", "not a static network: a `dataflow` block holds only `source`, `stage` and `sink` nodes");
            }
        }
        foreach (var n in g.Nodes)
            if (n.Kind != DataflowKind.Sink && !consumed.Contains(n))
                throw DfErr(file, n.Call, "AL0246", $"dataflow edge `{n.Name ?? n.Call.Name}` has no consumer; end every branch with `.sink`");
        if (!g.Nodes.Any(n => n.Kind == DataflowKind.Sink))
            throw DfErr(file, df, "AL0246", "a `dataflow` block needs at least one `source ... sink` chain");
        foreach (var n in g.Nodes) RewriteEmits(n);
        return g;
    }

    int IntArg(Arg a) => a.Value is LitExpr { Kind: "int" } l && int.TryParse(l.Text.Replace("_", ""), out int v) && v > 0 ? v
        : throw DfErr(file, a, "AL0246", $"`{a.Name}:` is a positive integer literal");

    bool BoolArg(Arg a) => a.Value is LitExpr { Kind: "bool" } b ? b.Text == "true"
        : throw DfErr(file, a, "AL0246", $"`{a.Name}:` is `true` or `false`");

    /// <summary>`out << v` is `out.write(v)` (RingWriter.Write): the block's emissions bind as calls.</summary>
    static void RewriteEmits(DataflowNode n)
    {
        int s = n.State != null ? 1 : 0;
        bool hasOut = n.Kind == DataflowKind.Source || n.Kind == DataflowKind.Stage && n.Block.Params.Count == s + 2;
        if (!hasOut || n.Block.Params.Count == 0) return;
        string outName = n.Block.Params[^1].Name;
        AstRewrite.Each(n.Block.Body, x => x is BinaryExpr { Op: "<<", L: NameExpr { Name: var ln } l } b && ln == outName
            ? new CallExpr { Recv = l, Name = "write", Args = { new Arg { Value = b.R, Line = b.R.Line, Col = b.R.Col } }, Parens = true, Line = b.Line, Col = b.Col }
            : null);
    }

    // ---------------------------------------------------------------- binding and typing

    void BindDataflowNode(DataflowNode n)
    {
        var blk = n.Block;
        if (n.State != null) n.StateType = BindExpr(n.State);
        int s = n.State != null ? 1 : 0;
        int want = n.Kind switch { DataflowKind.Source => s + 1, DataflowKind.Sink => s + 1, _ => -1 };
        if (n.Kind == DataflowKind.Stage)
        {
            if (blk.Params.Count == s + 2) n.MapForm = false;
            else if (blk.Params.Count == s + 1) n.MapForm = true;
            else throw DfErr(file, blk, "AL0246", $"a `stage` block takes {(s == 1 ? "|state, x, out| or |state, x|" : "|x, out| or |x|")}");
        }
        else if (blk.Params.Count != want)
            throw DfErr(file, blk, "AL0246", n.Kind == DataflowKind.Source
                ? $"a `source` block takes {(s == 1 ? "|state, out|" : "|out|")}"
                : $"a `sink` block takes {(s == 1 ? "|state, x|" : "|x|")}");
        var expected = new List<AType>();
        if (s == 1) expected.Add(n.StateType);
        if (n.Input != null) expected.Add(n.Input.Out);
        bool hasOut = n.Kind == DataflowKind.Source || n.Kind == DataflowKind.Stage && !n.MapForm;
        if (hasOut) expected.Add(n.Out.IsKnown ? Writer(n.Out) : AType.Unknown);
        n.Depth = lambdas.Count;
        BindLambda(blk, expected);
        n.StateParam = s == 1 ? blk.ParamSyms[0] : null;
        n.ItemParam = n.Input != null ? blk.ParamSyms[s] : null;
        n.OutParam = hasOut ? blk.ParamSyms[^1] : null;

        AType outT;
        if (n.Kind == DataflowKind.Sink) return;
        if (n.MapForm) outT = blk.ReturnsValue ? blk.Result : AType.Unknown;
        else
        {
            var writes = new List<AType>();
            AstWalk.Each(blk.Body, x =>
            {
                if (x is CallExpr { Recv: NameExpr { Sym: LocalSym o }, Name: "write" or "Write", Args: [{ Value: { } v }] } && o == n.OutParam) writes.Add(v.Type);
            });
            if (writes.Count == 0) throw DfErr(file, blk, "AL0246", $"`{n.Call.Name}` never writes to `{blk.Params[^1].Name}`: emit items with `{blk.Params[^1].Name} << value`");
            outT = Unify(writes);
        }
        if (outT.IsKnown && !outT.Same(n.Out)) { n.Out = outT; changed = true; }
        if (finalPass && !n.Out.IsKnown)
            throw DfErr(file, blk, "AL0246", $"cannot infer the item type of `{n.Name ?? n.Call.Name}`: give the emitted values a static type");
    }

    static AType Writer(AType elem) => Builtins.Generic(typeof(Alice.Runtime.RingWriter<>), elem);

    // ---------------------------------------------------------------- isolation (AL0244)

    void CheckDataflowCaptures(DataflowGraph g)
    {
        var mentions = new Dictionary<LocalSym, List<DataflowNode>>();
        foreach (var n in g.Nodes)
        {
            n.Captures = new();
            n.Writes = new();
            var written = new HashSet<LocalSym>();
            Node? selfAt = null;
            AstWalk.Each(n.Block.Body, x =>
            {
                switch (x)
                {
                    case NameExpr { Sym: LocalSym l } when l.LambdaDepth <= n.Depth && !n.Captures.Contains(l):
                        n.Captures.Add(l);
                        break;
                    case Assign { Target: NameExpr { Sym: LocalSym l } } when l.LambdaDepth <= n.Depth:
                        written.Add(l);
                        break;
                    case MultiAssign ma:
                        foreach (var l in ma.Locals) if (l.LambdaDepth <= n.Depth) written.Add(l);
                        break;
                    case SelfExpr or IvarExpr or SuperExpr:
                    case NameExpr { Res: "field" or "selfcall" }:
                    case CallExpr { Recv: null, Sym: MethodSym { IsStatic: false, IsCtor: false } }:
                        selfAt ??= x;
                        break;
                }
            });
            if (selfAt != null)
                throw DfErr(file, selfAt, "AL0244", "dataflow stage captures `self`: nodes run as static methods on the pool; pass what they need with `state:` or capture sendable locals");
            foreach (var l in written)
            {
                if (!n.Captures.Contains(l)) n.Captures.Add(l);
                if (n.Kind != DataflowKind.Sink)
                    throw DfErr(file, n.Block, "AL0244", $"dataflow stage captures `{l.Name}` and writes it: a {n.Call.Name} runs concurrently with the other nodes; keep it in `state:` or write it in the sink");
                n.Writes.Add(l);
            }
            foreach (var l in n.Captures)
            {
                if (!mentions.TryGetValue(l, out var ns)) mentions[l] = ns = new();
                ns.Add(n);
            }
        }
        foreach (var n in g.Nodes)
        {
            if (n.State is NameExpr { Sym: LocalSym sl } && mentions.TryGetValue(sl, out var others) && others.Any(o => o != n))
                throw DfErr(file, n.State, "AL0244", $"dataflow stage captures `{sl.Name}`, the state of another node: each node owns its `state:`");
            foreach (var l in n.Captures)
            {
                bool shared = mentions[l].Count > 1;
                if (n.Writes.Contains(l))
                {
                    if (shared) throw DfErr(file, n.Block, "AL0244", $"dataflow stage captures `{l.Name}`, which the sink writes: a value written by one node is not visible to the others while the network runs");
                    continue;
                }
                if (l.Type.Kind is "unknown" or "dynamic")
                    throw DfErr(file, n.Block, "AL0244", $"dataflow stage captures `{l.Name}`, whose type is not known at compile time");
                if (ThreadSafe(l.Type) || ActorThreadSafe(l.Type) || Sendable(l.Type, new HashSet<object>(), out _)) continue;
                if (n.Kind == DataflowKind.Sink && !shared) continue;   // the sink owns it while the network runs (the method waits)
                Sendable(l.Type, new HashSet<object>(), out string why);
                throw DfErr(file, n.Block, "AL0244", $"dataflow stage captures `{l.Name}` ({l.Type.Cs}), which is neither sendable nor thread-safe: {why}; build it with `{n.Call.Name}(state: ...)` or send it through the stream");
            }
        }
    }

    /// <summary>`stream def`: the body runs as a stage concurrently with its consumer, so it may not reach `self`.</summary>
    void CheckStreamDef(MethodSym m, MethodDecl d)
    {
        if (!d.IsStream || !finalPass) return;
        if (IteratorElement(m.Ret) == null)
            throw DfErr(file, d, "AL0246", $"`stream def {d.Name}` must return an iterator: Iterator(T) or IEnumerable(T)");
        Node? selfAt = null;
        AstWalk.Each(d.Body, x =>
        {
            if (x is SelfExpr or IvarExpr or SuperExpr or NameExpr { Res: "field" or "selfcall" } or CallExpr { Recv: null, Sym: MethodSym { IsStatic: false, IsCtor: false } }) selfAt ??= x;
        });
        if (selfAt != null && !m.IsStatic && !Sendable(curType.Type, new HashSet<object>(), out _))
            throw DfErr(file, selfAt, "AL0244", $"`stream def {d.Name}` captures `self` ({curType.Name}), which is not sendable: its body runs concurrently with the consumer");
    }
}
