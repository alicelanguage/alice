using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// The left half of a fork (ADR 0015), as one thread-pool work item. Whoever claims it first runs it: a pool
/// thread, or the forking thread at the join when no thread got to it yet (lazy task creation, as in BendRT:
/// an unclaimed fork costs one allocation and a compare-exchange). A joiner whose fork is already running
/// spins briefly, then blocks on the job itself; the worker only takes the lock when someone blocked.
/// </summary>
sealed class ForkJob<A>(Func<A> body, int depth) : IThreadPoolWorkItem
{
    const int Pending = 0, Running = 1, Done = 2;
    int state, waiting;
    A? result;
    ExceptionDispatchInfo? error;

    public void Execute()
    {
        if (Interlocked.CompareExchange(ref state, Running, Pending) == Pending) Run();
    }

    void Run()
    {
        int saved = Par.ForkDepth;
        Par.ForkDepth = depth;
        try { result = body(); }
        catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
        finally { Par.ForkDepth = saved; }
        Interlocked.Exchange(ref state, Done);
        if (Volatile.Read(ref waiting) != 0)
            lock (this) Monitor.PulseAll(this);
    }

    public A Join()
    {
        if (Interlocked.CompareExchange(ref state, Running, Pending) == Pending) Run();
        else if (Volatile.Read(ref state) != Done)
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref state) != Done && !spin.NextSpinWillYield) spin.SpinOnce(sleep1Threshold: -1);
            if (Volatile.Read(ref state) != Done)
            {
                Interlocked.Exchange(ref waiting, 1);
                lock (this)
                    while (Volatile.Read(ref state) != Done) Monitor.Wait(this);
            }
        }
        error?.Throw();
        return result!;
    }
}
