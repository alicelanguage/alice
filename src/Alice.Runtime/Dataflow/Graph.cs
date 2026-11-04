using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// An acyclic single-producer single-consumer dataflow network (ADR 0038): <c>Source -> Stage* -> Sink</c>, every
/// edge a bounded <see cref="Ring{T}"/>, every node a thread-pool work item. Built forward only, so it cannot have a
/// cycle; each <see cref="Pipe{T}"/> may be consumed once, so every edge has one producer and one consumer.
/// Acyclic plus SPSC plus bounded rings is deadlock-free, and each stage sees its input in source order, so the
/// output is the same for every thread count (a Kahn network).
/// <para>
/// <see cref="Run"/> starts every node, waits until all of them finished, and rethrows the first exception a body
/// threw (once, with its original stack). Completion propagates downstream: a source that returns false completes
/// its ring, a stage completes its output after draining a completed input, and the run ends when every node did.
/// After a failure every node stops at its next turn and the remaining items are dropped.
/// </para>
/// </summary>
public sealed class Graph
{
    /// <summary>Ring capacity used when a builder call passes 0.</summary>
    public int DefaultCapacity { get; }
    /// <summary>Most items a body receives per call, and the batch of a replicated stage.</summary>
    public int Batch { get; }

    readonly List<DataflowNode> nodes = new();
    int open;               // pipes not consumed yet
    int started;
    int remaining;
    internal volatile bool failed;
    ExceptionDispatchInfo? error;
    // Run waits on this monitor until the last node exits. A ManualResetEventSlim allocated its lock object (24 B)
    // only when the wait outlasted its spin, so a long run allocated more than a short one (DataflowLowerAllocationTests)
    readonly object gate = new();

    /// <summary>Creates an empty graph.</summary>
    /// <param name="capacity">Default ring capacity per edge (rounded up to a power of two).</param>
    /// <param name="batch">Most items handed to a body per call.</param>
    public Graph(int capacity = 8192, int batch = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        DefaultCapacity = capacity;
        Batch = batch;
    }

    internal Ring<T> NewRing<T>(int capacity) => new(capacity > 0 ? capacity : DefaultCapacity);

    internal Pipe<T> Add<T>(DataflowNode node, Ring<T> output)
    {
        Add(node);
        open++;
        return new Pipe<T>(this, output);
    }

    internal void Add(DataflowNode node)
    {
        if (started != 0) throw new InvalidOperationException("dataflow graph already started");
        node.graph = this;
        nodes.Add(node);
    }

    internal void Consume<T>(Pipe<T> pipe)
    {
        if (pipe.graph != this) throw new InvalidOperationException("pipe belongs to another dataflow graph");
        if (pipe.consumed) throw new InvalidOperationException("a dataflow pipe has exactly one consumer (single-producer single-consumer)");
        pipe.consumed = true;
        open--;
    }

    /// <summary>A source driven by <paramref name="body"/>, called until it returns false.</summary>
    public Pipe<T> Source<TState, T>(TState state, SourceBody<TState, T> body, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        unsafe { return Source(state, body, null, capacity); }
    }

    /// <summary>A source whose body is a static function pointer (what the compiler emits; no delegate).</summary>
    public unsafe Pipe<T> Source<TState, T>(TState state, delegate*<ref TState, ref RingWriter<T>, bool> body, int capacity = 0)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        return Source(state, null, body, capacity);
    }

    unsafe Pipe<T> Source<TState, T>(TState state, SourceBody<TState, T>? body, delegate*<ref TState, ref RingWriter<T>, bool> fn, int capacity)
    {
        var ring = NewRing<T>(capacity);
        return Add(new SourceNode<TState, T>(state, body, fn, ring, Batch), ring);
    }

    /// <summary>A source over the items of an array, in order.</summary>
    public Pipe<T> Source<T>(T[] items, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Source(new ArraySource<T>(items), static (ref ArraySource<T> s, ref RingWriter<T> w) =>
        {
            while (s.next < s.items.Length && w.HasRoom) w.Write(s.items[s.next++]);
            return s.next < s.items.Length;
        }, capacity);
    }

    struct ArraySource<T>(T[] items)
    {
        public readonly T[] items = items;
        public int next;
    }

    /// <summary>
    /// Runs the graph to completion on the thread pool and blocks until every node finished. Rethrows the first
    /// exception thrown by any body. A graph runs once.
    /// </summary>
    public void Run()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("dataflow graph already ran");
        if (open != 0) throw new InvalidOperationException($"{open} dataflow pipe(s) have no consumer; end every branch with Sink");
        if (nodes.Count > 0)
        {
            Volatile.Write(ref remaining, nodes.Count);
            foreach (var n in nodes) n.Wake();
            // a short graph ends while we spin; otherwise sleep on the monitor (Exited pulses it under the lock, so
            // the check below cannot miss the last exit)
            var spin = new SpinWait();
            while (Volatile.Read(ref remaining) != 0 && !spin.NextSpinWillYield) spin.SpinOnce();
            if (Volatile.Read(ref remaining) != 0)
                lock (gate)
                    while (Volatile.Read(ref remaining) != 0) Monitor.Wait(gate);
        }
        error?.Throw();
    }

    internal void Fail(Exception e)
    {
        if (Interlocked.CompareExchange(ref error, ExceptionDispatchInfo.Capture(e), null) != null) return;
        failed = true;
        foreach (var n in nodes) n.Wake();   // idle nodes see the failure and exit
    }

    internal void Exited()
    {
        if (Interlocked.Decrement(ref remaining) == 0)
            lock (gate) Monitor.PulseAll(gate);
    }
}

/// <summary>
/// The output edge of a node while the graph is being built. Each pipe gets exactly one consumer: a stage, a
/// replicated stage, or a sink.
/// </summary>
public sealed class Pipe<T>
{
    internal readonly Graph graph;
    internal readonly Ring<T> ring;
    internal bool consumed;

    internal Pipe(Graph graph, Ring<T> ring)
    {
        this.graph = graph;
        this.ring = ring;
    }

    /// <summary>A sequential stage with private <paramref name="state"/>; <paramref name="finish"/> runs once after the last input.</summary>
    public Pipe<TOut> Stage<TState, TOut>(TState state, StageBody<TState, T, TOut> body, StageFinish<TState, TOut>? finish = null, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        unsafe { return Stage(state, body, null, finish, capacity); }
    }

    /// <summary>A sequential stage whose body is a static function pointer (what the compiler emits).</summary>
    public unsafe Pipe<TOut> Stage<TState, TOut>(TState state, delegate*<ref TState, ReadOnlySpan<T>, ref RingWriter<TOut>, void> body, int capacity = 0)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        return Stage<TState, TOut>(state, null, body, null, capacity);
    }

    unsafe Pipe<TOut> Stage<TState, TOut>(TState state, StageBody<TState, T, TOut>? body,
        delegate*<ref TState, ReadOnlySpan<T>, ref RingWriter<TOut>, void> fn, StageFinish<TState, TOut>? finish, int capacity)
    {
        graph.Consume(this);
        var output = graph.NewRing<TOut>(capacity);
        return graph.Add(new StageNode<TState, T, TOut>(state, body, fn, finish, ring, output, graph.Batch), output);
    }

    /// <summary>
    /// A stateless stage run by <paramref name="workers"/> pool work items at once (0: one per processor). Input is
    /// cut into batches of <paramref name="batch"/> items (0: <see cref="Graph.Batch"/>) numbered in order; each batch
    /// runs on a fresh copy of <paramref name="state"/>. With <paramref name="ordered"/> the outputs are merged in
    /// batch order through a window of <c>2 * workers</c> batches, so the result equals the serial stage; with
    /// <c>ordered: false</c> they are merged as batches finish. A copy of a state that is or holds a reference
    /// would share the referenced object between the k workers (a data race once the body writes it), so such a
    /// <typeparamref name="TState"/> is rejected here: pass a factory (<c>() => new State()</c>), which runs once
    /// per batch.
    /// </summary>
    public Pipe<TOut> Replicate<TState, TOut>(TState state, StageBody<TState, T, TOut> body, int workers = 0, bool ordered = true, int batch = 0, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TState>())
            throw new ArgumentException($"Replicate: state type {typeof(TState).Name} is or holds a reference, which every worker would share (a data race if the body writes it); pass a factory instead: Replicate(() => <new state>, body, ...)", nameof(state));
        unsafe { return Replicate(state, null, body, null, workers, ordered, batch, capacity); }
    }

    /// <summary>A replicated stage whose state comes from <paramref name="state"/>, called once per batch: each batch
    /// gets its own object, so nothing is shared between the workers unless the factory returns a shared one.</summary>
    public Pipe<TOut> Replicate<TState, TOut>(Func<TState> state, StageBody<TState, T, TOut> body, int workers = 0, bool ordered = true, int batch = 0, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(body);
        unsafe { return Replicate(default!, state, body, null, workers, ordered, batch, capacity); }
    }

    /// <summary>A replicated stage whose body is a static function pointer: the compiler's `stage(par: true)`. The
    /// state is copied per batch; a state that is or holds a reference is shared by the workers, which is sound only
    /// because the body never writes it (the compiler proves it, AL0245). C# callers with a mutable reference state use
    /// the factory overload.</summary>
    public unsafe Pipe<TOut> Replicate<TState, TOut>(TState state, delegate*<ref TState, ReadOnlySpan<T>, ref RingWriter<TOut>, void> body,
        int workers = 0, bool ordered = true, int batch = 0, int capacity = 0)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        return Replicate<TState, TOut>(state, null, null, body, workers, ordered, batch, capacity);
    }

    /// <summary>A function-pointer replicated stage with a per-batch state factory.</summary>
    public unsafe Pipe<TOut> Replicate<TState, TOut>(Func<TState> state, delegate*<ref TState, ReadOnlySpan<T>, ref RingWriter<TOut>, void> body,
        int workers = 0, bool ordered = true, int batch = 0, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (body == null) throw new ArgumentNullException(nameof(body));
        return Replicate<TState, TOut>(default!, state, null, body, workers, ordered, batch, capacity);
    }

    unsafe Pipe<TOut> Replicate<TState, TOut>(TState state, Func<TState>? factory, StageBody<TState, T, TOut>? body,
        delegate*<ref TState, ReadOnlySpan<T>, ref RingWriter<TOut>, void> fn, int workers, bool ordered, int batch, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workers);
        ArgumentOutOfRangeException.ThrowIfNegative(batch);
        graph.Consume(this);
        int k = workers > 0 ? workers : Environment.ProcessorCount;
        int b = Math.Min(batch > 0 ? batch : graph.Batch, ring.Capacity);   // a full batch must fit in the input ring
        var output = graph.NewRing<TOut>(capacity);
        return graph.Add(new ReplicateNode<TState, T, TOut>(state, factory, body, fn, ring, output, k, b, ordered), output);
    }

    /// <summary>The end of a branch; read <see cref="SinkResult{TState}.State"/> after <see cref="Graph.Run"/>.</summary>
    public SinkResult<TState> Sink<TState>(TState state, SinkBody<TState, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        unsafe { return Sink(state, body, null); }
    }

    /// <summary>A sink whose body is a static function pointer.</summary>
    public unsafe SinkResult<TState> Sink<TState>(TState state, delegate*<ref TState, ReadOnlySpan<T>, void> body)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        return Sink(state, null, body);
    }

    unsafe SinkResult<TState> Sink<TState>(TState state, SinkBody<TState, T>? body, delegate*<ref TState, ReadOnlySpan<T>, void> fn)
    {
        graph.Consume(this);
        var result = new SinkResult<TState>(state);
        graph.Add(new SinkNode<TState, T>(result, body, fn, ring, graph.Batch));
        return result;
    }
}
