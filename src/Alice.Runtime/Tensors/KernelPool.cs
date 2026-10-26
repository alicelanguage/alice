using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Persistent worker threads for tensor kernels. Jobs are a function pointer plus a context pointer on the caller's
/// stack, so dispatch allocates nothing. The caller works too and returns only after every woken worker has left the
/// job, so no worker ever sees a stale job. Nested or concurrent calls fall back to running serially.
/// </summary>
public static unsafe class KernelPool
{
    static readonly int Workers = Math.Max(0, Environment.ProcessorCount - 1);
    static readonly SemaphoreSlim Wake = new(0);
    static int _busy, _started;
    static delegate*<void*, int, int, void> _fn;
    static void* _ctx;
    static int _n, _chunk, _chunks, _next, _active;

    public static int Threads => Workers + 1;

    /// <summary>Runs <c>fn(ctx, lo, hi)</c> over [0, n) in chunks of at least <paramref name="grain"/>.</summary>
    public static void Run(int n, int grain, delegate*<void*, int, int, void> fn, void* ctx)
    {
        if (n <= 0) return;
        int chunks = Math.Min(n / Math.Max(1, grain), Threads * 4);
        if (chunks < 2 || Workers == 0 || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            fn(ctx, 0, n);
            return;
        }
        try
        {
            Start();
            _fn = fn; _ctx = ctx; _n = n; _chunks = chunks; _chunk = (n + chunks - 1) / chunks; _next = 0;
            int wake = Math.Min(Workers, chunks - 1);
            Volatile.Write(ref _active, wake);
            Wake.Release(wake);
            Work();
            var spin = new SpinWait();
            while (Volatile.Read(ref _active) != 0) spin.SpinOnce(-1);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Work()
    {
        while (true)
        {
            int c = Interlocked.Increment(ref _next) - 1;
            if (c >= _chunks) return;
            int lo = c * _chunk, hi = Math.Min(_n, lo + _chunk);
            if (lo < hi) _fn(_ctx, lo, hi);
        }
    }

    static void Start()
    {
        if (Volatile.Read(ref _started) != 0 || Interlocked.Exchange(ref _started, 1) != 0) return;
        for (int i = 0; i < Workers; i++)
            new Thread(Loop) { IsBackground = true, Name = $"alice-kernel-{i}" }.Start();
    }

    static void Loop()
    {
        while (true)
        {
            Wake.Wait();
            Work();
            Interlocked.Decrement(ref _active);
        }
    }
}
