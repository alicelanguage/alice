namespace Alice.Runtime;

/// <summary>
/// A lightweight BEAM-style process (ADR 0028): private mailbox, one logical consumer, isolated state.
/// The process is its own thread-pool work item: a send enqueues on a lock-free queue and, when the
/// process is idle, flips one flag and queues the process itself. An idle process holds no thread and the
/// steady-state send/handle path allocates nothing.
///
/// "Let it crash": an exception in a handler kills only this process. Its exit propagates to linked
/// processes (which die too, unless they are supervisors), to `on_exit` monitors, and to its supervisor,
/// which may restart it in place (the same object, so references stay valid, with fresh state).
/// </summary>
public abstract class ProcessCore : IThreadPoolWorkItem
{
    static long ids;

    const int Alive = 0, Dead = 1;
    const int Batch = 128;   // messages per turn before yielding the pool thread (fairness)

    int status;
    int scheduled;           // 1 while queued or running on the pool
    int stopping;            // `stop`: drain, then exit normally
    long dropped;
    ExitReason? last;        // most recent exit, kept across restarts
    int exits;
    bool propagating;        // dead, but links/monitors/supervisor not yet told               // bumped on every exit, so a join survives an immediate restart
    readonly object gate = new();
    List<ProcessCore>? links;
    List<Action<ExitReason>>? monitors;
    internal Supervisor? parent;

    public long id { get; } = Interlocked.Increment(ref ids);
    public ExitReason? exit_reason { get; private set; }
    /// <summary>Times a supervisor restarted this process.</summary>
    public int restarts { get; private set; }
    /// <summary>Messages refused because the process was dead or stopping.</summary>
    public long dropped_messages => Interlocked.Read(ref dropped);
    public bool IsAlive => Volatile.Read(ref status) == Alive;

    /// <summary>Handles one queued message; false when the mailbox is empty.</summary>
    protected abstract bool HandleOne();
    protected abstract bool HasMessages { get; }
    protected abstract void ClearMailbox();
    /// <summary>Fresh state for a restart.</summary>
    protected virtual void Reinit() { }
    /// <summary>Runs once when the process dies, before anyone is notified.</summary>
    protected virtual void OnTerminated(ExitReason reason) { }

    /// <summary>For senders: true when the message may be queued; counts a refusal.</summary>
    protected bool Admit()
    {
        if (Volatile.Read(ref status) == Alive && Volatile.Read(ref stopping) == 0) return true;
        Interlocked.Increment(ref dropped);
        return false;
    }

    protected void Schedule()
    {
        if (Volatile.Read(ref scheduled) == 0 && Interlocked.CompareExchange(ref scheduled, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    void IThreadPoolWorkItem.Execute()
    {
        int n = 0;
        while (true)
        {
            if (Volatile.Read(ref status) != Alive) { Volatile.Write(ref scheduled, 0); return; }
            bool got;
            try { got = HandleOne(); }
            catch (Exception e)
            {
                bool died = Kill(ExitReason.Crash(e));
                Volatile.Write(ref scheduled, 0);   // before notifying: a supervisor may restart us on this thread
                if (died) Propagate(exit_reason!);
                return;
            }
            if (!got)
            {
                if (Volatile.Read(ref stopping) == 1)
                {
                    bool died = Kill(ExitReason.Normal);
                    Volatile.Write(ref scheduled, 0);
                    if (died) Propagate(ExitReason.Normal);
                    return;
                }
                Volatile.Write(ref scheduled, 0);
                // a send may have raced the flag reset; take the turn back if so
                if (HasMessages && Interlocked.CompareExchange(ref scheduled, 1, 0) == 0) continue;
                return;
            }
            if (++n == Batch)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);   // still scheduled: requeue
                return;
            }
        }
    }

    /// <summary>Alive -> Dead exactly once; the winner records the reason and drops the mailbox.</summary>
    bool Kill(ExitReason reason)
    {
        lock (gate)
        {
            if (status != Alive) return false;
            Volatile.Write(ref status, Dead);
            propagating = true;
        }
        exit_reason = reason;
        ClearMailbox();
        return true;
    }

    void Propagate(ExitReason reason)
    {
        try { OnTerminated(reason); } catch { }
        List<Action<ExitReason>>? ms;
        List<ProcessCore>? ls;
        lock (gate)
        {
            ms = monitors?.ToList();
            ls = links;
            links = null;
        }
        if (ms != null)
            foreach (var m in ms)
                try { m(reason); } catch { }   // a failing monitor must not take the notifier down
        if (ls != null)
            foreach (var p in ls)
            {
                p.RemoveLink(this);
                if (!reason.IsNormal) p.ExitSignal(this, reason);
            }
        parent?.ChildExited(this, reason);
        lock (gate)
        {
            exits++;
            last = reason;
            propagating = false;
            Monitor.PulseAll(gate);   // joiners wake after links, monitors and the supervisor have seen the exit
        }
    }

    /// <summary>A linked process exited abnormally. Plain processes die with it; supervisors trap it.</summary>
    internal virtual void ExitSignal(ProcessCore from, ExitReason reason) => Terminate(ExitReason.Linked(from.id, reason));

    internal void Terminate(ExitReason reason)
    {
        if (Kill(reason)) Propagate(reason);
    }

    /// <summary>Back to life with an empty mailbox and fresh state (supervisor restart).</summary>
    internal void Restart()
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref scheduled) != 0) sw.SpinOnce();   // the old turn must be over: one consumer only
        ClearMailbox();
        Reinit();
        exit_reason = null;
        Volatile.Write(ref stopping, 0);
        restarts++;
        Volatile.Write(ref status, Alive);
        if (HasMessages) Schedule();
    }

    void RemoveLink(ProcessCore p)
    {
        lock (gate) links?.Remove(p);
    }

    // ---- public API ----

    /// <summary>Bidirectional link: when either exits abnormally, the other receives the exit (and dies).</summary>
    public void link(ProcessCore other)
    {
        if (other == this) return;
        if (!other.IsAlive) { ExitSignal(other, other.exit_reason ?? ExitReason.Killed); return; }
        if (!IsAlive) { other.ExitSignal(this, exit_reason ?? ExitReason.Killed); return; }
        lock (gate) (links ??= new()).Add(other);
        lock (other.gate) (other.links ??= new()).Add(this);
    }

    public void unlink(ProcessCore other)
    {
        RemoveLink(other);
        other.RemoveLink(this);
    }

    /// <summary>Monitor: the block runs (on the exiting process's thread) each time this process exits.</summary>
    public void on_exit(Action<ExitReason> f)
    {
        lock (gate) (monitors ??= new()).Add(f);
        if (!IsAlive && exit_reason is { } r) try { f(r); } catch { }
    }

    /// <summary>Kills the process now, without draining (reason killed).</summary>
    public void kill() => Terminate(ExitReason.Killed);

    /// <summary>Stops accepting messages, drains the mailbox, exits normally, and waits for the exit.</summary>
    public void stop()
    {
        Volatile.Write(ref stopping, 1);
        if (Interlocked.CompareExchange(ref scheduled, 1, 0) == 0) ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        join();
    }

    public Task stop_async() => Task.Run(stop);

    /// <summary>Waits until the process is dead and returns why.</summary>
    public ExitReason join()
    {
        lock (gate)
        {
            int seen = exits;
            while (exits == seen && (IsAlive || propagating)) Monitor.Wait(gate);
            return last ?? ExitReason.Normal;
        }
    }

    /// <summary>Waits up to `ms` milliseconds; nil when still alive.</summary>
    public ExitReason? join(int ms)
    {
        long end = Environment.TickCount64 + ms;
        lock (gate)
        {
            int seen = exits;
            while (exits == seen && (IsAlive || propagating))
            {
                long left = end - Environment.TickCount64;
                if (left <= 0) return null;
                Monitor.Wait(gate, (int)left);
            }
            return last ?? ExitReason.Normal;
        }
    }
}
