using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>A parallel loop body over chunks [lo, hi), as a struct: the IL backend's display struct of a Par.Range
/// kernel (ADR 0033 closure ABI). Read-only while the loop runs: every worker reads the same copy.</summary>
public interface IParKernel
{
    void Invoke(int lo, int hi);
}

/// <summary>A Par.Reduce chunk body returning the chunk's partial result, as a struct.</summary>
public interface IParKernel<T>
{
    T Invoke(int lo, int hi);
}

/// <summary>One branch of a fork (ADR 0015), as a struct.</summary>
public interface IForkBranch<T>
{
    T Invoke();
}

/// <summary>
/// Struct-kernel overloads of the data-parallel primitives: what closure conversion (ADR 0033) passes instead of a
/// delegate for a Par kernel or fork branch that never escapes. The kernel is copied once into a job (no ref fields
/// cross threads: the pass gives these kernels value fields only, and demotes a kernel that writes a capture to a
/// display class); jobs are reused per thread, so a steady-state loop or fork allocates nothing. Scheduling, the
/// dispatch threshold, chunk counts and exception behaviour are those of the delegate forms.
/// </summary>
public static partial class Par
{
    static int ChunkCount(int n, int grain)
    {
        int cap = grain <= 1 ? Workers * 64 : Workers * 8;
        return Math.Min(Math.Max(1, n / Math.Max(1, grain)), cap);
    }

    /// <summary><see cref="Range(int, Action{int, int}, int)"/> with a struct kernel.</summary>
    public static void Range<TKernel>(int n, ref TKernel kernel, int grain = 1) where TKernel : struct, IParKernel
    {
        if (n <= 0) return;
        int chunks = ChunkCount(n, grain);
        if (chunks <= 1) { kernel.Invoke(0, n); return; }
        var job = RangeKernelJob<TKernel>.Rent();
        job.Kernel = kernel;
        try { job.Run(n, chunks); }
        finally { job.Kernel = default; RangeKernelJob<TKernel>.Return(job); }
    }

    /// <summary><see cref="Reduce{T}(int, Func{int, int, T}, Func{T, T, T}, int)"/> with a struct chunk kernel;
    /// partials are combined in index order.</summary>
    public static T Reduce<T, TKernel>(int n, ref TKernel kernel, Func<T, T, T> combine, int grain = 8192) where TKernel : struct, IParKernel<T>
    {
        int chunks = ChunkCount(n, grain);
        if (chunks <= 1) return kernel.Invoke(0, n);
        var job = ReduceKernelJob<TKernel, T>.Rent(chunks);
        job.Kernel = kernel;
        try
        {
            job.Run(n, chunks);
            var partial = job.Partial;
            T acc = partial[0];
            for (int i = 1; i < chunks; i++) acc = combine(acc, partial[i]);
            return acc;
        }
        finally
        {
            job.Kernel = default;
            Array.Clear(job.Partial, 0, chunks);
            ReduceKernelJob<TKernel, T>.Return(job);
        }
    }

    /// <summary><see cref="Fork{A, B}(Func{A}, Func{B})"/> with struct branches: the left one is offered to the pool,
    /// the right one runs here, then the left is joined (run inline when no pool thread took it yet).</summary>
    public static (A, B) Fork<A, B, TA, TB>(ref TA left, ref TB right) where TA : struct, IForkBranch<A> where TB : struct, IForkBranch<B>
    {
        int depth = ForkDepth + 1;
        var job = ForkKernelJob<TA, A>.Rent(left, depth);
        ThreadPool.UnsafeQueueUserWorkItem(job, preferLocal: true);
        int outer = ForkDepth;
        ForkDepth = depth;
        B b;
        try { b = right.Invoke(); }
        catch
        {
            ForkDepth = outer;
            try { job.Join(); } catch { }   // the left half may still be running on our data: finish it before unwinding
            ForkKernelJob<TA, A>.Return(job);
            throw;
        }
        ForkDepth = outer;
        A a;
        try { a = job.Join(); }
        finally { ForkKernelJob<TA, A>.Return(job); }
        return (a, b);
    }

    /// <summary><see cref="Fork{A, B, C}(Func{A}, Func{B}, Func{C})"/> with struct branches.</summary>
    public static (A, B, C) Fork<A, B, C, TA, TB, TC>(ref TA first, ref TB second, ref TC third)
        where TA : struct, IForkBranch<A> where TB : struct, IForkBranch<B> where TC : struct, IForkBranch<C>
    {
        int depth = ForkDepth + 1;
        var j1 = ForkKernelJob<TA, A>.Rent(first, depth);
        var j2 = ForkKernelJob<TB, B>.Rent(second, depth);
        ThreadPool.UnsafeQueueUserWorkItem(j1, preferLocal: true);
        ThreadPool.UnsafeQueueUserWorkItem(j2, preferLocal: true);
        int outer = ForkDepth;
        ForkDepth = depth;
        C c;
        try { c = third.Invoke(); }
        catch
        {
            ForkDepth = outer;
            try { j1.Join(); } catch { }
            try { j2.Join(); } catch { }
            ForkKernelJob<TA, A>.Return(j1);
            ForkKernelJob<TB, B>.Return(j2);
            throw;
        }
        ForkDepth = outer;
        A a;
        B b;
        try
        {
            try { a = j1.Join(); }
            catch { try { j2.Join(); } catch { } throw; }
            b = j2.Join();
        }
        finally
        {
            ForkKernelJob<TA, A>.Return(j1);
            ForkKernelJob<TB, B>.Return(j2);
        }
        return (a, b, c);
    }
}

/// <summary>
/// The chunk scheduler of <see cref="ParJob{T}"/> (atomic chunk counter, the caller works too, a timed second chunk
/// decides whether helpers are woken) for struct kernels. The thread that owns a job reuses it on its next call even
/// while helpers it queued have not run yet (a pool that wakes late, as on 3-core macOS runners: the owner runs every
/// chunk itself). The chunk count and the claim counter share one word (<see cref="work"/>), replaced atomically after
/// the run's fields are set, so a late helper either finds its old run closed or joins the new one, never a mix.
/// </summary>
abstract class KernelJob : IThreadPoolWorkItem
{
    int n, pending, helpers;
    long work;   // (chunks << 32) | chunks claimed so far
    ExceptionDispatchInfo? error;
    RegionFrame? region;

    protected abstract void Chunk(int c, int lo, int hi);

    /// <summary>Helpers queued and not yet returned (late ones are harmless, see the class summary).</summary>
    internal int QueuedHelpers => Volatile.Read(ref helpers);

    internal void Run(int n, int chunks)
    {
        this.n = n;
        error = null;
        region = ArenaRegion.Context;
        Volatile.Write(ref pending, chunks);
        Interlocked.Exchange(ref work, (long)chunks << 32);   // publishes the run: everything above is visible to a claim
        RunOne();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RunOne();
        long sample = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (error == null && ParJob<byte>.WorthDispatch(sample * (chunks - 2)))
        {
            int h = Math.Min(chunks - 1, Par.Workers - 1);
            Interlocked.Add(ref helpers, h);
            for (int i = 0; i < h; i++) ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
        while (RunOne()) { }
        var spin = new SpinWait();
        while (Volatile.Read(ref pending) != 0) spin.SpinOnce(sleep1Threshold: -1);
        error?.Throw();
    }

    void IThreadPoolWorkItem.Execute()
    {
        try { while (RunOne()) { } }
        finally { Interlocked.Decrement(ref helpers); }
    }

    bool RunOne()
    {
        long w = Interlocked.Increment(ref work);
        int chunks = (int)(w >> 32), c = (int)(uint)w - 1;
        if (c >= chunks) return false;
        int lo = (int)((long)n * c / chunks), hi = (int)((long)n * (c + 1) / chunks);
        ChildLane lane = default;
        try
        {
            lane = ArenaRegion.EnterChild(region);   // per claimed chunk: a late helper never touches an ended region
            if (error == null) Chunk(c, lo, hi);
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref error, ExceptionDispatchInfo.Capture(e), null);
        }
        finally { ArenaRegion.ExitChild(lane); }
        Interlocked.Decrement(ref pending);
        return true;
    }
}

sealed class RangeKernelJob<TKernel> : KernelJob where TKernel : struct, IParKernel
{
    [ThreadStatic] static RangeKernelJob<TKernel>? free;
    internal TKernel Kernel;

    protected override void Chunk(int c, int lo, int hi) => Kernel.Invoke(lo, hi);

    internal static RangeKernelJob<TKernel> Rent()
    {
        var j = free;
        if (j != null) { free = null; return j; }
        return new RangeKernelJob<TKernel>();
    }

    internal static void Return(RangeKernelJob<TKernel> j) => free = j;
}

sealed class ReduceKernelJob<TKernel, T> : KernelJob where TKernel : struct, IParKernel<T>
{
    [ThreadStatic] static ReduceKernelJob<TKernel, T>? free;
    internal TKernel Kernel;
    internal T[] Partial = [];

    protected override void Chunk(int c, int lo, int hi) => Partial[c] = Kernel.Invoke(lo, hi);

    internal static ReduceKernelJob<TKernel, T> Rent(int chunks)
    {
        var j = free;
        if (j != null) free = null;
        else j = new ReduceKernelJob<TKernel, T>();
        if (j.Partial.Length < chunks) j.Partial = new T[chunks];
        return j;
    }

    internal static void Return(ReduceKernelJob<TKernel, T> j) => free = j;
}

/// <summary>
/// The left half of a struct fork (as <see cref="ForkJob{A}"/>). Reused through a small per-thread pool: a queue entry
/// left behind by a fork joined inline may run against a reused job, but every run is claimed by the Pending -> Running
/// compare-exchange, and the fields are published before the state turns Pending.
/// </summary>
sealed class ForkKernelJob<TBranch, A> : IThreadPoolWorkItem where TBranch : struct, IForkBranch<A>
{
    const int Pending = 0, Running = 1, Done = 2;
    const int PoolSize = 8;
    [ThreadStatic] static ForkKernelJob<TBranch, A>?[]? pool;
    [ThreadStatic] static int pooled;

    int state = Done, waiting, depth;
    RegionFrame? region;
    TBranch branch;
    A? result;
    ExceptionDispatchInfo? error;

    internal static ForkKernelJob<TBranch, A> Rent(TBranch branch, int depth)
    {
        var p = pool;
        var j = p != null && pooled > 0 ? p[--pooled]! : new ForkKernelJob<TBranch, A>();
        if (p != null) p[pooled] = null;
        j.branch = branch;
        j.depth = depth;
        j.region = ArenaRegion.Context;
        j.result = default;
        j.error = null;
        j.waiting = 0;
        Volatile.Write(ref j.state, Pending);
        return j;
    }

    internal static void Return(ForkKernelJob<TBranch, A> j)
    {
        j.branch = default;
        j.result = default;
        j.region = null;
        var p = pool ??= new ForkKernelJob<TBranch, A>?[PoolSize];
        if (pooled < PoolSize) p[pooled++] = j;
    }

    public void Execute()
    {
        if (Interlocked.CompareExchange(ref state, Running, Pending) == Pending) Run();
    }

    void Run()
    {
        int saved = Par.ForkDepth;
        Par.ForkDepth = depth;
        ChildLane lane = default;
        try
        {
            lane = ArenaRegion.EnterChild(region);
            result = branch.Invoke();
        }
        catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
        finally
        {
            ArenaRegion.ExitChild(lane);
            Par.ForkDepth = saved;
        }
        Interlocked.Exchange(ref state, Done);
        if (Volatile.Read(ref waiting) != 0)
            lock (this) Monitor.PulseAll(this);
    }

    internal A Join()
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
