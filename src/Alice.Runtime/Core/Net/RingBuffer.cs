using System.Buffers;
using System.Numerics;

namespace Alice.Core.Net;

/// <summary>
/// An owned byte ring (power-of-two capacity, pooled array) for socket IO without copies: the socket receives into
/// <see cref="GetWriteSpan"/> and the parser reads <see cref="GetReadSpan"/> in place, then <see cref="Consume"/>s.
/// Single owner (one connection); not thread-safe. Dispose returns the array to the pool.
/// </summary>
public sealed class RingBuffer : IDisposable
{
    byte[] buf;
    long head, tail;   // read and write positions (monotonic; index = pos & mask)

    /// <summary>A ring of at least <paramref name="capacity"/> bytes.</summary>
    public RingBuffer(int capacity = 16 * 1024)
    {
        int n = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 64));
        buf = ArrayPool<byte>.Shared.Rent(n);
        if (!BitOperations.IsPow2(buf.Length)) { ArrayPool<byte>.Shared.Return(buf); buf = new byte[n]; }
    }

    int Mask => buf.Length - 1;

    /// <summary>Capacity in bytes.</summary>
    public int Capacity => buf.Length;

    /// <summary>Bytes ready to read.</summary>
    public int Available => (int)(tail - head);

    /// <summary>Bytes that can be written.</summary>
    public int FreeSpace => buf.Length - Available;

    /// <summary>The contiguous readable bytes at the head (may be less than <see cref="Available"/> at the wrap).</summary>
    public ReadOnlySpan<byte> GetReadSpan()
    {
        int h = (int)(head & Mask);
        return buf.AsSpan(h, Math.Min(Available, buf.Length - h));
    }

    /// <summary>Marks <paramref name="count"/> bytes read.</summary>
    public void Consume(int count)
    {
        if ((uint)count > (uint)Available) throw new ArgumentOutOfRangeException(nameof(count));
        head += count;
        if (head == tail) head = tail = 0;   // empty: realign so the next read span is contiguous
    }

    /// <summary>The contiguous writable span at the tail (may be less than <see cref="FreeSpace"/> at the wrap).</summary>
    public Span<byte> GetWriteSpan()
    {
        int t = (int)(tail & Mask);
        return buf.AsSpan(t, Math.Min(FreeSpace, buf.Length - t));
    }

    /// <summary>The contiguous writable memory at the tail (for async receives).</summary>
    public Memory<byte> GetWriteMemory()
    {
        int t = (int)(tail & Mask);
        return buf.AsMemory(t, Math.Min(FreeSpace, buf.Length - t));
    }

    /// <summary>Marks <paramref name="count"/> bytes written.</summary>
    public void Commit(int count)
    {
        if ((uint)count > (uint)FreeSpace) throw new ArgumentOutOfRangeException(nameof(count));
        tail += count;
    }

    /// <summary>Copies as much of <paramref name="data"/> as fits; returns the bytes written.</summary>
    public int Write(ReadOnlySpan<byte> data)
    {
        int total = 0;
        while (!data.IsEmpty && FreeSpace > 0)
        {
            var w = GetWriteSpan();
            int n = Math.Min(w.Length, data.Length);
            data[..n].CopyTo(w);
            Commit(n);
            data = data[n..];
            total += n;
        }
        return total;
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length bytes out; returns the count.</summary>
    public int Read(Span<byte> destination)
    {
        int total = 0;
        while (!destination.IsEmpty && Available > 0)
        {
            var r = GetReadSpan();
            int n = Math.Min(r.Length, destination.Length);
            r[..n].CopyTo(destination);
            Consume(n);
            destination = destination[n..];
            total += n;
        }
        return total;
    }

    /// <summary>Offset of <paramref name="value"/> in the readable bytes, or -1 (searches across the wrap).</summary>
    public int IndexOf(byte value)
    {
        var first = GetReadSpan();
        int k = first.IndexOf(value);
        if (k >= 0 || first.Length == Available) return k;
        k = buf.AsSpan(0, Available - first.Length).IndexOf(value);
        return k < 0 ? -1 : first.Length + k;
    }

    /// <summary>Empties the ring.</summary>
    public void Clear() => head = tail = 0;

    /// <summary>Returns the array to the pool.</summary>
    public void Dispose()
    {
        if (buf.Length > 0) ArrayPool<byte>.Shared.Return(buf);
        buf = [];
        head = tail = 0;
    }
}
