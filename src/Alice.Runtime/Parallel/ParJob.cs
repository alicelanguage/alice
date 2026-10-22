using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// One parallel loop: chunks of [0, n) claimed through an atomic counter. The calling thread works through the chunks
/// too, so a loop completes even when every pool thread is busy (nested parallel loops cannot starve the pool), and
/// helpers that start after the last chunk was claimed return at once. One allocation per loop, no delegates per
/// chunk, no TPL machinery to JIT at startup.
/// </summary>
sealed class ParJob<T> : IThreadPoolWorkItem
{
    readonly Func<int, int, T>? chunk;
    readonly Action<int, int>? action;
    readonly T[]? partial;
    readonly int n, chunks;
    int next, pending;
    ExceptionDispatchInfo? error;

    ParJob(int n, int chunks, Func<int, int, T>? chunk, Action<int, int>? action)
    {
        this.n = n;
        this.chunks = chunks;
        this.chunk = chunk;
        this.action = action;
        pending = chunks;
        if (chunk != null) partial = new T[chunks];
    }

    public static void Run(int n, int chunks, Action<int, int> body) => new ParJob<T>(n, chunks, null, body).Start();

    public static T[] Run(int n, int chunks, Func<int, int, T> body)
    {
        var job = new ParJob<T>(n, chunks, body, null);
        job.Start();
        return job.partial!;
    }

    void Start()
    {
        int helpers = Math.Min(chunks, Par.Workers) - 1;
        for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        Execute();
        var spin = new SpinWait();
        while (Volatile.Read(ref pending) != 0) spin.SpinOnce(sleep1Threshold: -1);   // only chunks already running remain
        error?.Throw();
    }

    public void Execute()
    {
        int c;
        while ((c = Interlocked.Increment(ref next) - 1) < chunks)
        {
            int lo = (int)((long)n * c / chunks), hi = (int)((long)n * (c + 1) / chunks);
            try
            {
                if (error == null)
                {
                    if (action != null) action(lo, hi);
                    else partial![c] = chunk!(lo, hi);
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, ExceptionDispatchInfo.Capture(e), null);
            }
            Interlocked.Decrement(ref pending);
        }
    }
}
