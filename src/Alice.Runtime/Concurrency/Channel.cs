namespace Alice.Runtime;

/// <summary>Raised by `send` on a closed channel and by `receive` on a closed, drained one.</summary>
public sealed class ClosedChannelError : InvalidOperationException
{
    public ClosedChannelError() : base("channel closed") { }
}

/// <summary>
/// Crystal-style typed channel. `new` is a rendezvous: `send` returns once a receiver has taken the value.
/// `new(n)` buffers up to n values. One lock guards a fixed ring (BendRT's channel rows: a ring per channel,
/// parked waiters, no allocation per message); blocking callers wait on the lock's monitor, so a send or
/// receive allocates nothing and never blocks on a Task. Async callers allocate one waiter only when they
/// actually wait.
/// </summary>
public sealed class Channel<T>
{
    readonly object gate = new();
    readonly T[] ring;
    readonly bool rendezvous;
    int head, count;
    long sent, received;   // sequence numbers: a rendezvous sender waits until received passes its own
    bool closed;
    List<TaskCompletionSource>? asyncWaiters;

    public Channel() : this(0) { }

    public Channel(int capacity)
    {
        rendezvous = capacity <= 0;
        ring = new T[Math.Max(1, capacity)];   // a rendezvous holds the one value being handed over
    }

    public void send(T v)
    {
        lock (gate)
        {
            while (true)
            {
                if (closed) throw new ClosedChannelError();
                if (count < ring.Length) break;
                Monitor.Wait(gate);
            }
            long seq = Enqueue(v);
            if (!rendezvous) return;
            while (received <= seq)
            {
                if (closed) throw new ClosedChannelError();   // closed before anyone took the value
                Monitor.Wait(gate);
            }
        }
    }

    /// <summary>Blocks until a value arrives. Raises <see cref="ClosedChannelError"/> when closed and drained.</summary>
    public T receive()
    {
        lock (gate)
        {
            while (count == 0)
            {
                if (closed) throw new ClosedChannelError();
                Monitor.Wait(gate);
            }
            return Dequeue();
        }
    }

    public bool try_receive(out T v)
    {
        lock (gate)
        {
            if (count == 0) { v = default!; return false; }
            v = Dequeue();
            return true;
        }
    }

    public void close()
    {
        lock (gate)
        {
            closed = true;
            Changed();
        }
    }

    public void each(Action<T> f)
    {
        while (true)
        {
            T v;
            lock (gate)
            {
                while (count == 0)
                {
                    if (closed) return;
                    Monitor.Wait(gate);
                }
                v = Dequeue();
            }
            f(v);
        }
    }

    // ---- async variants for `async def` code: no thread is parked while waiting ----

    public async ValueTask send_async(T v)
    {
        long seq;
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (closed) throw new ClosedChannelError();
                if (count < ring.Length) { seq = Enqueue(v); break; }
                wait = Waiter();
            }
            await wait.ConfigureAwait(false);
        }
        while (rendezvous)
        {
            Task wait;
            lock (gate)
            {
                if (received > seq) return;
                if (closed) throw new ClosedChannelError();
                wait = Waiter();
            }
            await wait.ConfigureAwait(false);
        }
    }

    public async ValueTask<T> receive_async()
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (count > 0) return Dequeue();
                if (closed) throw new ClosedChannelError();
                wait = Waiter();
            }
            await wait.ConfigureAwait(false);
        }
    }

    /// <summary>True when a value is ready, false once closed and drained.</summary>
    public async ValueTask<bool> wait_async()
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (count > 0) return true;
                if (closed) return false;
                wait = Waiter();
            }
            await wait.ConfigureAwait(false);
        }
    }

    // ---- ring (callers hold the gate) ----

    long Enqueue(T v)
    {
        int tail = head + count;
        if (tail >= ring.Length) tail -= ring.Length;
        ring[tail] = v;
        count++;
        Changed();
        return sent++;
    }

    T Dequeue()
    {
        T v = ring[head];
        ring[head] = default!;   // do not keep the value alive for the GC
        if (++head == ring.Length) head = 0;
        count--;
        received++;
        Changed();
        return v;
    }

    /// <summary>State changed: wake blocked threads and async waiters; each re-checks its condition.</summary>
    void Changed()
    {
        Monitor.PulseAll(gate);
        if (asyncWaiters is { Count: > 0 } ws)
        {
            foreach (var w in ws) w.TrySetResult();
            ws.Clear();
        }
    }

    Task Waiter()
    {
        var w = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (asyncWaiters ??= new()).Add(w);
        return w.Task;
    }
}
