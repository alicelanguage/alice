namespace Alice.Runtime;

/// <summary>
/// Rust-style mutex: the guarded value is reachable only inside `lock { |v| ... }`, so it cannot be
/// touched without holding the lock (the compiler rejects unguarded shared mutation).
/// </summary>
public sealed class Mutex<T>
{
    readonly T _value;
    readonly Lock _gate = new();

    public Mutex(T value) => _value = value;

    public void @lock(Action<T> body)
    {
        lock (_gate) body(_value);
    }

    public R @lock<R>(Func<T, R> body)
    {
        lock (_gate) return body(_value);
    }
}

/// <summary>Lock-free 64-bit counter/cell (Interlocked).</summary>
public sealed class Atomic
{
    long _value;

    public Atomic(long value = 0) => _value = value;

    public long get() => Interlocked.Read(ref _value);
    public void set(long v) => Interlocked.Exchange(ref _value, v);
    public long add(long d) => Interlocked.Add(ref _value, d);
    public long increment() => Interlocked.Increment(ref _value);
    public long decrement() => Interlocked.Decrement(ref _value);
    public bool compare_and_set(long expected, long value) => Interlocked.CompareExchange(ref _value, value, expected) == expected;
    public override string ToString() => get().ToString();
}
