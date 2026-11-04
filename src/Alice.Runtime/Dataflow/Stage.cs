namespace Alice.Runtime;

/// <summary>
/// A source body: writes the next chunk of items into <paramref name="output"/> and returns false once the stream
/// is exhausted. It should stop when <see cref="RingWriter{T}.HasRoom"/> turns false (backpressure); what it writes
/// past that point is buffered, not lost.
/// </summary>
public delegate bool SourceBody<TState, T>(ref TState state, ref RingWriter<T> output);

/// <summary>
/// A stage body: consumes one batch of input and writes any number of outputs (1:1, 1:n, filter). The output must
/// be a function of the item sequence, not of where batch boundaries fall (they depend on timing).
/// </summary>
public delegate void StageBody<TState, TIn, TOut>(ref TState state, ReadOnlySpan<TIn> input, ref RingWriter<TOut> output);

/// <summary>Runs once when a stage's input is complete, before its output completes (flush buffered state).</summary>
public delegate void StageFinish<TState, TOut>(ref TState state, ref RingWriter<TOut> output);

/// <summary>A sink body: consumes one batch of input.</summary>
public delegate void SinkBody<TState, T>(ref TState state, ReadOnlySpan<T> input);

/// <summary>
/// One node of a dataflow <see cref="Graph"/>, and its own thread-pool work item (the <see cref="ProcessCore"/>
/// trick of ADR 0028): it is queued only when it is idle and has something to do, with one compare-and-swap per
/// wake. A turn runs until the input is empty, the output ring is full (backpressure: the node goes idle and the
/// consumer's release wakes it) or the turn budget is spent (it requeues itself, so the pool can run other stages).
/// Nothing blocks a pool thread and there is no custom scheduler (ADR 0005).
/// </summary>
internal abstract class DataflowNode : IThreadPoolWorkItem
{
    /// <summary>Items per turn before the node requeues itself (fairness when stages outnumber pool threads).</summary>
    const int TurnBudget = 1 << 16;
    /// <summary>
    /// Re-checks before an idle node gives its thread back: a wake through the pool costs microseconds. Only while no
    /// other work item waits for a pool thread, and yielding the core between checks: a spinning node must never
    /// hold the thread or the core that the node it waits for needs. With fewer cores than busy nodes (a 2-4 core CI
    /// runner under the parallel test suite) plain spinning made every hop of a tiny-ring chain cost a full spin
    /// window: 50k items through 15 nodes went from 0.4 s to 8-15 s per run on 1-2 cores and past the 30 s test bound
    /// under load. No wake was lost; the graph crawled.
    /// </summary>
    const int IdleSpins = 40;

    protected enum Turn { Idle, Blocked, Yield, Done }

    internal Graph graph = null!;
    int scheduled;          // 1 while queued or running, and forever once the node exited
    internal int blocked;   // 1 while stopped on a full output ring; read by the consumer's release

    /// <summary>Queues the node unless it is already queued, running or finished.</summary>
    internal void Wake()
    {
        if (Volatile.Read(ref scheduled) == 0 && Interlocked.CompareExchange(ref scheduled, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    /// <summary>Does work until it cannot; never blocks.</summary>
    protected abstract Turn Pump(ref int budget);
    /// <summary>Idle: true when input arrived or the input completed.</summary>
    protected abstract bool CanProgress();
    /// <summary>Blocked: true when the output ring has a free slot.</summary>
    protected abstract bool HasRoom();
    /// <summary>
    /// Runs after the full fence of going idle: wakes the consumer of a non-empty output ring and a producer stopped
    /// on our input ring. Publishes and releases do not fence, so a wake they missed is caught here.
    /// </summary>
    protected abstract void Nudge();

    protected static void NudgeConsumer<T>(Ring<T> output)
    {
        if (output.consumer is { } c && !output.IsEmpty) c.Wake();
    }

    protected static void NudgeProducer<T>(Ring<T> input)
    {
        if (input.producer is { } p && Volatile.Read(ref p.blocked) != 0) p.Wake();
    }

    /// <summary>After a failure: true once no work of this node is still running elsewhere.</summary>
    protected virtual bool CanExitOnFailure => true;

    bool Ready(bool isBlocked) =>
        graph.failed ? CanExitOnFailure : isBlocked ? HasRoom() : CanProgress();

    // Wake protocol, per edge P -> ring -> C (ADR 0038):
    //   publish (P):  Tail = t (release store); C.Wake(): if C.scheduled == 0, CAS 0 -> 1 and queue C
    //   release (C):  Head = h (release store); if P.blocked == 1, P.Wake()
    //   idle (node):  blocked = 1 if stopped on a full ring; spin on Ready(); scheduled = 0 (Interlocked: full
    //                 fence); Nudge(); if Ready(), CAS scheduled 0 -> 1 and take the turn back
    // A publish or release does not fence, so its index store can be passed by its flag load (store -> load
    // reordering, allowed even by x86): the waker reads scheduled == 1 (or blocked == 0) while the fenced re-check
    // of the sleeper still reads the old index, and that wake is missed. It is repaired, not lost: every path of the
    // waker reaches a full fence and a re-check later (going idle runs Nudge after the Exchange; finishing runs
    // Ring.Complete, an interlocked write, then wakes the consumer; a yield only defers that), and a fence plus a
    // read on both sides is the Dekker pattern: one of the two sees the other. A node never finishes while a
    // neighbour still waits on it (a stage finishes only after its input completed). Stress: DataflowStressTests.
    void IThreadPoolWorkItem.Execute()
    {
        int budget = TurnBudget;
        while (true)
        {
            if (blocked != 0) Volatile.Write(ref blocked, 0);
            Turn t;
            if (graph.failed) t = CanExitOnFailure ? Turn.Done : Turn.Idle;
            else
            {
                try { t = Pump(ref budget); }
                catch (Exception e)
                {
                    graph.Fail(e);
                    t = CanExitOnFailure ? Turn.Done : Turn.Idle;
                }
            }
            if (t == Turn.Done)
            {
                graph.Exited();   // `scheduled` stays 1: the node is never queued again
                return;
            }
            if (t == Turn.Yield)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);   // still scheduled: requeue
                return;
            }
            bool isBlocked = t == Turn.Blocked;
            if (isBlocked) Volatile.Write(ref blocked, 1);
            bool again = false;
            for (int i = 0; i < IdleSpins && !again; i++)
            {
                if (ThreadPool.PendingWorkItemCount > 0) break;
                Thread.Yield();
                again = Ready(isBlocked);
            }
            if (again) { budget = Math.Max(budget, 1); continue; }
            Interlocked.Exchange(ref scheduled, 0);   // full fence before re-reading the rings (Dekker with the waker)
            Nudge();
            // a publish or release may have raced the flag reset; take the turn back if so
            if (Ready(isBlocked) && Interlocked.CompareExchange(ref scheduled, 1, 0) == 0) continue;
            return;
        }
    }
}

sealed unsafe class SourceNode<TS, T> : DataflowNode
{
    TS state;
    readonly SourceBody<TS, T>? body;
    readonly delegate*<ref TS, ref RingWriter<T>, bool> fn;
    readonly Ring<T> output;
    readonly Spill<T> spill;
    bool exhausted;

    public SourceNode(TS state, SourceBody<TS, T>? body, delegate*<ref TS, ref RingWriter<T>, bool> fn, Ring<T> output, int batch)
    {
        this.state = state;
        this.body = body;
        this.fn = fn;
        this.output = output;
        spill = new Spill<T>(batch);
        output.producer = this;
    }

    protected override Turn Pump(ref int budget)
    {
        while (true)
        {
            if (spill.count > 0 && !spill.FlushTo(output)) return Turn.Blocked;
            if (exhausted)
            {
                output.Complete();
                return Turn.Done;
            }
            if (budget <= 0) return Turn.Yield;
            long before = output.idx.Tail;
            var w = new RingWriter<T>(output, spill);
            bool more = fn != null ? fn(ref state, ref w) : body!(ref state, ref w);
            bool room = w.HasRoom;
            w.Flush();
            budget -= (int)(output.idx.Tail - before) + 1;
            if (!more) exhausted = true;
            else if (!room) return Turn.Blocked;
        }
    }

    protected override bool CanProgress() => true;   // a source is idle only when blocked
    protected override bool HasRoom() => output.HasRoom();
    protected override void Nudge() => NudgeConsumer(output);
}

sealed unsafe class StageNode<TS, TIn, TOut> : DataflowNode
{
    TS state;
    readonly StageBody<TS, TIn, TOut>? body;
    readonly delegate*<ref TS, ReadOnlySpan<TIn>, ref RingWriter<TOut>, void> fn;
    readonly StageFinish<TS, TOut>? finish;
    readonly Ring<TIn> input;
    readonly Ring<TOut> output;
    readonly Spill<TOut> spill;
    readonly int batch;
    bool finished;

    public StageNode(TS state, StageBody<TS, TIn, TOut>? body, delegate*<ref TS, ReadOnlySpan<TIn>, ref RingWriter<TOut>, void> fn,
        StageFinish<TS, TOut>? finish, Ring<TIn> input, Ring<TOut> output, int batch)
    {
        this.state = state;
        this.body = body;
        this.fn = fn;
        this.finish = finish;
        this.input = input;
        this.output = output;
        this.batch = batch;
        spill = new Spill<TOut>(batch);
        input.consumer = this;
        output.producer = this;
    }

    protected override Turn Pump(ref int budget)
    {
        while (true)
        {
            if (spill.count > 0 && !spill.FlushTo(output)) return Turn.Blocked;
            if (finished)
            {
                output.Complete();
                return Turn.Done;
            }
            if (budget <= 0) return Turn.Yield;
            var span = input.Peek(batch);
            if (span.IsEmpty)
            {
                if (!input.IsCompleted) return Turn.Idle;
                finished = true;
                if (finish != null)
                {
                    var fw = new RingWriter<TOut>(output, spill);
                    finish(ref state, ref fw);
                    fw.Flush();
                }
                continue;
            }
            var w = new RingWriter<TOut>(output, spill);
            if (fn != null) fn(ref state, span, ref w);
            else body!(ref state, span, ref w);
            w.Flush();
            input.Release(span.Length);
            budget -= span.Length;
        }
    }

    protected override bool CanProgress() => input.Available() > 0 || input.IsCompleted;
    protected override bool HasRoom() => output.HasRoom();

    protected override void Nudge()
    {
        NudgeConsumer(output);
        NudgeProducer(input);
    }
}

sealed unsafe class SinkNode<TS, T> : DataflowNode
{
    readonly SinkResult<TS> result;
    readonly SinkBody<TS, T>? body;
    readonly delegate*<ref TS, ReadOnlySpan<T>, void> fn;
    readonly Ring<T> input;
    readonly int batch;

    public SinkNode(SinkResult<TS> result, SinkBody<TS, T>? body, delegate*<ref TS, ReadOnlySpan<T>, void> fn, Ring<T> input, int batch)
    {
        this.result = result;
        this.body = body;
        this.fn = fn;
        this.input = input;
        this.batch = batch;
        input.consumer = this;
    }

    protected override Turn Pump(ref int budget)
    {
        while (true)
        {
            if (budget <= 0) return Turn.Yield;
            var span = input.Peek(batch);
            if (span.IsEmpty) return input.IsCompleted ? Turn.Done : Turn.Idle;
            if (fn != null) fn(ref result.state, span);
            else body!(ref result.state, span);
            input.Release(span.Length);
            budget -= span.Length;
        }
    }

    protected override bool CanProgress() => input.Available() > 0 || input.IsCompleted;
    protected override bool HasRoom() => true;
    protected override void Nudge() => NudgeProducer(input);
}

/// <summary>The state of a sink, readable after <see cref="Graph.Run"/> returned.</summary>
public sealed class SinkResult<TState>
{
    internal TState state;
    internal SinkResult(TState state) => this.state = state;
    /// <summary>The sink's final state.</summary>
    public TState State => state;
}
