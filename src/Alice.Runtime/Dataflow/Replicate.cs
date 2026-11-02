namespace Alice.Runtime;

/// <summary>
/// A stateless stage on k workers (ADR 0038). The node is the single consumer of its input ring and the single
/// producer of its output ring; between the two sits a window of <c>2k</c> batch slots indexed by sequence number.
/// Dispatch cuts the input into batches of exactly <c>batch</c> items (the last may be shorter, so the cut does not
/// depend on timing), numbers them, and queues each slot as its own pool work item; at most k run at once. Each
/// worker writes into its slot's private buffer, so the slots are the many producers and the merge is the single
/// consumer: ordered, slot <c>seq % 2k</c> is merged in sequence order (the reorder window); unordered, any finished
/// slot is merged. A full window stops dispatch and a full output ring stops the merge (backpressure). Slots and
/// buffers are allocated once; a buffer grows only for a larger burst than any before.
/// </summary>
sealed unsafe class ReplicateNode<TS, TIn, TOut> : DataflowNode
{
    const int Free = 0, Running = 1, Finished = 2;

    sealed class Slot(ReplicateNode<TS, TIn, TOut> owner, int batch) : IThreadPoolWorkItem
    {
        public readonly TIn[] input = new TIn[batch];
        public int count;
        public readonly Spill<TOut> output = new(batch);
        public int state;

        void IThreadPoolWorkItem.Execute()
        {
            if (!owner.graph.failed)
            {
                try { owner.Work(this); }
                catch (Exception e) { owner.graph.Fail(e); }
            }
            Volatile.Write(ref state, Finished);
            Interlocked.Decrement(ref owner.inflight);   // full fence before reading the owner's flag
            owner.Wake();
        }
    }

    readonly TS init;
    readonly StageBody<TS, TIn, TOut>? body;
    readonly delegate*<ref TS, ReadOnlySpan<TIn>, ref RingWriter<TOut>, void> fn;
    readonly Ring<TIn> input;
    readonly Ring<TOut> output;
    readonly int workers, batch;
    readonly bool ordered;
    readonly Slot[] slots;
    int inflight;            // slots queued or running
    long nextIn, nextOut;    // ordered: sequence numbers dispatched / merged; unordered: counts
    int scan;                // unordered: where the next merge scan starts

    public ReplicateNode(TS state, StageBody<TS, TIn, TOut>? body, delegate*<ref TS, ReadOnlySpan<TIn>, ref RingWriter<TOut>, void> fn,
        Ring<TIn> input, Ring<TOut> output, int workers, int batch, bool ordered)
    {
        init = state;
        this.body = body;
        this.fn = fn;
        this.input = input;
        this.output = output;
        this.workers = workers;
        this.batch = batch;
        this.ordered = ordered;
        slots = new Slot[2 * workers];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Slot(this, batch);
        input.consumer = this;
        output.producer = this;
    }

    void Work(Slot s)
    {
        var st = init;   // a fresh copy per batch: the result cannot depend on which worker ran which batch
        var w = new RingWriter<TOut>(s.output);
        var span = new ReadOnlySpan<TIn>(s.input, 0, s.count);
        if (fn != null) fn(ref st, span, ref w);
        else body!(ref st, span, ref w);
        w.Flush();
    }

    protected override Turn Pump(ref int budget)
    {
        while (true)
        {
            bool progressed = false;
            if (!Merge(ref progressed, ref budget)) return Turn.Blocked;
            Dispatch(ref progressed, ref budget);
            if (progressed)
            {
                if (budget <= 0) return Turn.Yield;
                continue;
            }
            if (nextIn == nextOut && input.IsCompleted)
            {
                output.Complete();
                return Turn.Done;
            }
            return Turn.Idle;
        }
    }

    /// <summary>Moves finished slots to the output ring; false when the ring filled up with a slot half merged.</summary>
    bool Merge(ref bool progressed, ref int budget)
    {
        while (nextOut < nextIn)
        {
            Slot? s = null;
            if (ordered)
            {
                var c = slots[(int)(nextOut % slots.Length)];
                if (Volatile.Read(ref c.state) == Finished) s = c;
            }
            else
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    var c = slots[(scan + i) % slots.Length];
                    if (Volatile.Read(ref c.state) == Finished) { s = c; scan = (scan + i) % slots.Length; break; }
                }
            }
            if (s == null) return true;
            int n = s.output.count;
            bool all = s.output.FlushTo(output);
            budget -= n - s.output.count;
            if (!all) return false;
            s.state = Free;
            if (!ordered) scan = (scan + 1) % slots.Length;
            nextOut++;
            progressed = true;
        }
        return true;
    }

    void Dispatch(ref bool progressed, ref int budget)
    {
        while (Volatile.Read(ref inflight) < workers && nextIn - nextOut < slots.Length)
        {
            bool last = input.ProducerCompleted;   // read before the tail: a completed ring's tail is final
            long avail = input.Available();
            if (avail == 0 || (avail < batch && !last)) return;
            Slot s;
            if (ordered) s = slots[(int)(nextIn % slots.Length)];
            else
            {
                int i = 0;
                while (slots[i].state != Free) i++;   // one is free: fewer than slots.Length are outstanding
                s = slots[i];
            }
            s.count = input.TryRead(s.input.AsSpan());
            s.state = Running;
            Interlocked.Increment(ref inflight);
            nextIn++;
            budget -= s.count;
            progressed = true;
            ThreadPool.UnsafeQueueUserWorkItem(s, preferLocal: false);
        }
    }

    bool AnyFinished()
    {
        if (nextOut == nextIn) return false;
        if (ordered) return Volatile.Read(ref slots[(int)(nextOut % slots.Length)].state) == Finished;
        foreach (var s in slots)
            if (Volatile.Read(ref s.state) == Finished) return true;
        return false;
    }

    protected override bool CanProgress()
    {
        if (AnyFinished()) return true;
        bool last = input.ProducerCompleted;
        long avail = input.Available();
        if (nextIn == nextOut && last && avail == 0) return true;   // completion
        return Volatile.Read(ref inflight) < workers && nextIn - nextOut < slots.Length && (avail >= batch || (last && avail > 0));
    }

    protected override bool HasRoom() => output.HasRoom();

    protected override void Nudge()
    {
        NudgeConsumer(output);
        NudgeProducer(input);
    }

    protected override bool CanExitOnFailure => Volatile.Read(ref inflight) == 0;
}
