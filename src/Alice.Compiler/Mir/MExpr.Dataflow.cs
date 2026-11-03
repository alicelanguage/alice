namespace Alice.Compiler.Mir;

/// <summary>How a dataflow node runs (ADR 0038, ADR 0040 section 4).</summary>
public enum AirStageKind
{
    /// <summary>An iterator method feeding its ring through a pump `(ref IEnumerator&lt;T&gt;, ref RingWriter&lt;T&gt;) -> bool`.</summary>
    Source,
    /// <summary>A sequential stage `(ref TState, ReadOnlySpan&lt;TIn&gt;, ref RingWriter&lt;TOut&gt;)`.</summary>
    Stage,
    /// <summary>A pure stage on k pool workers, batches numbered and merged in order unless <see cref="AirStage.Ordered"/> is false.</summary>
    Replicate,
    /// <summary>The end of a branch `(ref TState, ReadOnlySpan&lt;T&gt;)`; its state is read after the run.</summary>
    Sink,
}

/// <summary>An edge of the network: a bounded SPSC ring (`Alice.Runtime.Ring&lt;T&gt;`). Capacity 0 is the graph default.</summary>
public sealed class AirRing
{
    public AirType Elem = AirType.Unknown;
    public int Capacity;
}

/// <summary>One field of a node's state (the explicit `state:` or a captured local), in tuple order.</summary>
public sealed record AirStageField(string Name, AirType Type, MExpr Init);

/// <summary>
/// One node: a static method over a batch. <see cref="Body"/> is the per-item body after the MIR passes; the printer
/// (Backend/Printer.Dataflow.cs) and the IL backend wrap it in the batch loop
/// `for (i &lt; batch.Length) { var item = batch[i]; Body }` with every state field bound by `ref` to the state tuple, so
/// state is mutated in place and no item crosses a delegate.
/// </summary>
public sealed class AirStage
{
    public AirStageKind Kind;
    /// <summary>The static method (`__df_main_1_n0`); a source's iterator method.</summary>
    public string Method = "";
    /// <summary>Source only: the pump that drains the iterator into the ring while it has room.</summary>
    public string? Pump;
    public AirType? In, Out;
    /// <summary>State fields in order: an empty list is a `byte` placeholder, one field is the field's type, more are a tuple.</summary>
    public List<AirStageField> State = new();
    /// <summary>The item parameter's C# name (stage, sink).</summary>
    public string? Item;
    /// <summary>The writer parameter's C# name (source, stage).</summary>
    public string? Writer;
    /// <summary>A map-form stage: the per-item body assigns this temp, which is written after it.</summary>
    public string? MapValue;
    public bool Parallel;
    public bool Ordered = true;
    /// <summary>Replicate: pool work items (0: one per processor) and items per numbered batch (0: the graph's).</summary>
    public int Workers, Batch;
    /// <summary>Indexes into <see cref="AirDataflow.Rings"/>; -1 when absent (a source has no input, a sink no output).</summary>
    public int Input = -1, Output = -1;
    public List<MStmt> Body = new();

    /// <summary>The C# type of the state (`byte`, `T`, `(T1, T2)`).</summary>
    public string StateCs => State.Count switch
    {
        0 => "byte",
        1 => State[0].Type.ToCs(),
        _ => "(" + string.Join(", ", State.Select(f => f.Type.ToCs())) + ")",
    };

    /// <summary>The access path of field <paramref name="k"/> in the state (`s`, `s.Item2`).</summary>
    public string FieldPath(string s, int k) => State.Count == 1 ? s : s + ".Item" + (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A sink state field the run hands back to the enclosing method (a local the sink wrote).</summary>
public sealed record AirDataflowResult(int Stage, int Field, AirType Type);

/// <summary>
/// `dataflow do ... end` (ADR 0038): an acyclic SPSC network, proven by the binder (AL0244-AL0246). It prints as a
/// call of its runner, a static method that builds one `Alice.Runtime.Graph` (each node through the function-pointer
/// overloads: `ldftn`, no delegate), calls `Run()` once and returns the sink fields in <see cref="Results"/>
/// (`total = __df_main_1(text, n)`). <see cref="Args"/> are the values the runner receives: every captured local and
/// `state:` value, evaluated in the enclosing method before the run. The stage bodies are frozen.
/// </summary>
public sealed class AirDataflow : AirParExpr
{
    public string Runner = "";
    /// <summary>`new Graph(capacity, batch)`; 0 keeps the runtime default.</summary>
    public int Capacity, Batch;
    public List<AirParamDecl> Params = new();
    public List<MExpr> Args = new();
    public List<AirStage> Stages = new();
    public List<AirRing> Rings = new();
    public List<AirDataflowResult> Results = new();

    /// <summary>The runner's return type: void, the one result, or a tuple of them.</summary>
    public string ResultCs => Results.Count switch
    {
        0 => "void",
        1 => Results[0].Type.ToCs(),
        _ => "(" + string.Join(", ", Results.Select(r => r.Type.ToCs())) + ")",
    };

    public override IEnumerable<MExpr> Children() => Args;
    public override void Map(Func<MExpr, MExpr> f)
    {
        for (int i = 0; i < Args.Count; i++) Args[i] = f(Args[i]);
    }
}
