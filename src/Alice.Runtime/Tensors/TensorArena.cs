using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>Bump allocator over one aligned native block: scratch spans for a step, freed together by <see cref="Reset"/>. 0 allocations.</summary>
public sealed unsafe class TensorArena : IDisposable
{
    byte* _base;
    readonly long _capacity;
    long _used;

    public TensorArena(long bytes)
    {
        _capacity = bytes;
        _base = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, bytes), Tensor<byte>.Alignment);
    }

    public long Capacity => _capacity;
    public long Used => _used;

    /// <summary>Zero-initialized, 64-byte aligned span valid until <see cref="Reset"/>.</summary>
    public Span<T> Rent<T>(int count) where T : unmanaged
    {
        if (_base == null) throw new ObjectDisposedException(nameof(TensorArena));
        long bytes = ((long)count * sizeof(T) + 63) & ~63L;
        if (_used + bytes > _capacity) throw new InsufficientMemoryException($"arena of {_capacity} bytes exhausted");
        var p = _base + _used;
        _used += bytes;
        NativeMemory.Clear(p, (nuint)bytes);
        return new Span<T>(p, count);
    }

    public void Reset() => _used = 0;

    public void Dispose()
    {
        if (_base != null) { NativeMemory.AlignedFree(_base); _base = null; }
        GC.SuppressFinalize(this);
    }

    ~TensorArena() { if (_base != null) NativeMemory.AlignedFree(_base); }
}
