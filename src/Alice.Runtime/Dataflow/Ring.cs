using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>
/// Head and tail of a <see cref="Ring{T}"/>, each on its own pair of cache lines (128 bytes: the adjacent-line
/// prefetcher pulls lines in pairs) so the producer and the consumer never write the same line. Each side keeps a
/// private copy of the other side's index and reads the shared one only when its copy says full or empty.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 384)]
struct RingIndices
{
    [FieldOffset(128)] public long Head;        // written by the consumer
    [FieldOffset(136)] public long CachedTail;  // consumer-private
    [FieldOffset(256)] public long Tail;        // written by the producer
    [FieldOffset(264)] public long CachedHead;  // producer-private
}

/// <summary>
/// Bounded single-producer single-consumer ring over a <c>T[]</c> (ADR 0038): power-of-two capacity, monotonic
/// 64-bit head and tail, cached opposite index, no locks and no compare-and-swap per item. One thread may write and
/// one thread may read at a time. Nothing is allocated after construction.
/// <para>
/// In a <see cref="Graph"/> the ring is an edge: a publish wakes an idle consumer stage, and a release wakes a
/// producer stage that stopped on a full ring (backpressure).
/// </para>
/// </summary>
public sealed class Ring<T>
{
    internal readonly T[] buf;
    internal readonly int mask;
    internal RingIndices idx;
    int completed;
    internal DataflowNode? consumer, producer;

    /// <summary>Creates a ring holding at least <paramref name="capacity"/> items (rounded up to a power of two, minimum 2).</summary>
    public Ring(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1 << 30);
        int c = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, capacity));
        buf = new T[c];
        mask = c - 1;
    }

    /// <summary>Slots in the ring (a power of two).</summary>
    public int Capacity => buf.Length;

    /// <summary>Items published and not yet read (a snapshot; exact only when both sides are quiet).</summary>
    public int Count => (int)(Volatile.Read(ref idx.Tail) - Volatile.Read(ref idx.Head));

    /// <summary>True when no published item is waiting.</summary>
    public bool IsEmpty => Volatile.Read(ref idx.Head) == Volatile.Read(ref idx.Tail);

    // ---- producer side ----

    /// <summary>Producer: appends one item and publishes it; false when the ring is full.</summary>
    public bool TryWrite(T item)
    {
        long t = idx.Tail;
        if (t - idx.CachedHead >= buf.Length)
        {
            idx.CachedHead = Volatile.Read(ref idx.Head);
            if (t - idx.CachedHead >= buf.Length) return false;
        }
        buf[(int)t & mask] = item;
        Publish(t + 1);
        return true;
    }

    /// <summary>Producer: appends as many items of <paramref name="items"/> as fit and publishes them once; returns the count written.</summary>
    public int TryWrite(ReadOnlySpan<T> items)
    {
        long t = idx.Tail;
        long free = buf.Length - (t - idx.CachedHead);
        if (free < items.Length)
        {
            idx.CachedHead = Volatile.Read(ref idx.Head);
            free = buf.Length - (t - idx.CachedHead);
        }
        int n = (int)Math.Min(free, items.Length);
        if (n == 0) return 0;
        int start = (int)t & mask;
        int first = Math.Min(n, buf.Length - start);
        items[..first].CopyTo(buf.AsSpan(start));
        if (first < n) items[first..n].CopyTo(buf);
        Publish(t + n);
        return n;
    }

    /// <summary>Producer: no more items will be written. The consumer sees <see cref="IsCompleted"/> once it drained the ring.</summary>
    public void Complete()
    {
        Interlocked.Exchange(ref completed, 1);
        consumer?.Wake();
    }

    /// <summary>Makes every item below <paramref name="tail"/> visible to the consumer and wakes it if idle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Publish(long tail)
    {
        // Release store, no fence: a wake missed because the store was still buffered is repaired when either side
        // goes idle (DataflowNode.Nudge runs after a full fence), so the steady state pays no interlocked operation.
        Volatile.Write(ref idx.Tail, tail);
        consumer?.Wake();
    }

    /// <summary>Producer: true when at least one slot is free (refreshes the cached head).</summary>
    internal bool HasRoom()
    {
        idx.CachedHead = Volatile.Read(ref idx.Head);
        return idx.Tail - idx.CachedHead < buf.Length;
    }

    // ---- consumer side ----

    /// <summary>Consumer: true when the producer completed and every item was read.</summary>
    public bool IsCompleted => Volatile.Read(ref completed) != 0 && Volatile.Read(ref idx.Tail) == idx.Head;

    /// <summary>Consumer: the producer called <see cref="Complete"/> (items may still be waiting).</summary>
    internal bool ProducerCompleted => Volatile.Read(ref completed) != 0;

    /// <summary>Consumer: published items not yet read (refreshes the cached tail).</summary>
    internal long Available()
    {
        idx.CachedTail = Volatile.Read(ref idx.Tail);
        return idx.CachedTail - idx.Head;
    }

    /// <summary>Consumer: removes and returns one item; false when empty.</summary>
    public bool TryRead(out T item)
    {
        long h = idx.Head;
        if (h == idx.CachedTail && Available() == 0)
        {
            item = default!;
            return false;
        }
        int i = (int)h & mask;
        item = buf[i];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) buf[i] = default!;
        Release(h + 1);
        return true;
    }

    /// <summary>Consumer: moves up to <c>dest.Length</c> items into <paramref name="dest"/>; returns the count read.</summary>
    public int TryRead(Span<T> dest)
    {
        long h = idx.Head;
        long avail = idx.CachedTail - h;
        if (avail < dest.Length) avail = Available();
        int n = (int)Math.Min(avail, dest.Length);
        if (n == 0) return 0;
        int start = (int)h & mask;
        int first = Math.Min(n, buf.Length - start);
        buf.AsSpan(start, first).CopyTo(dest);
        if (first < n) buf.AsSpan(0, n - first).CopyTo(dest[first..]);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(buf, start, first);
            if (first < n) Array.Clear(buf, 0, n - first);
        }
        Release(h + n);
        return n;
    }

    /// <summary>
    /// Consumer, zero-copy: up to <paramref name="max"/> contiguous published items starting at the head (empty when
    /// none). The items stay owned by the ring until <see cref="Release(int)"/>.
    /// </summary>
    public ReadOnlySpan<T> Peek(int max)
    {
        long h = idx.Head;
        long avail = idx.CachedTail - h;
        if (avail < max) avail = Available();
        int start = (int)h & mask;
        int n = (int)Math.Min(Math.Min(avail, max), buf.Length - start);
        return new ReadOnlySpan<T>(buf, start, n);
    }

    /// <summary>Consumer: frees the first <paramref name="n"/> items returned by <see cref="Peek"/>. Releasing more
    /// than is published (more than Peek could have returned) throws instead of corrupting the ring's count.</summary>
    public void Release(int n)
    {
        long h = idx.Head;
        if ((ulong)n > (ulong)(idx.CachedTail - h) && (ulong)n > (ulong)Available())
            throw new ArgumentOutOfRangeException(nameof(n), n, $"Ring.Release: only {idx.CachedTail - h} published items to release");
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(buf, (int)h & mask, n);
        Release(h + n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Release(long head)
    {
        Volatile.Write(ref idx.Head, head);   // no fence: see Publish
        var p = producer;
        if (p != null && Volatile.Read(ref p.blocked) != 0) p.Wake();
    }
}

/// <summary>Growable overflow buffer of a stage: holds what a body wrote past a full output ring. It grows only when
/// a larger burst than ever before arrives, so the steady state allocates nothing.</summary>
sealed class Spill<T>(int capacity)
{
    public T[] items = new T[Math.Max(16, capacity)];
    public int start, count;

    public void Add(T item)
    {
        if (start + count == items.Length) Grow(1);
        items[start + count++] = item;
    }

    /// <summary>Compacts to start 0 and makes room for <paramref name="more"/> items after the pending ones.</summary>
    public void Grow(int more)
    {
        if (start > 0)
        {
            Array.Copy(items, start, items, 0, count);
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(items, count, start);
            start = 0;
        }
        if (count + more > items.Length) Array.Resize(ref items, Math.Max(items.Length * 2, count + more));
    }

    public ReadOnlySpan<T> Pending => new(items, start, count);

    public void Consume(int n)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(items, start, n);
        start += n;
        count -= n;
        if (count == 0) start = 0;
    }

    /// <summary>Moves as much as fits into <paramref name="ring"/>; true when the spill is empty afterwards.</summary>
    public bool FlushTo(Ring<T> ring)
    {
        if (count > 0) Consume(ring.TryWrite(Pending));
        return count == 0;
    }
}

/// <summary>
/// The output of a stage body, passed by <c>ref</c>. Writes go straight into the downstream ring and are published
/// in batches of up to <see cref="PublishBatch"/> items (one fence and at most one wake per batch). Items that do not
/// fit while the ring is full go to the stage's overflow buffer and are flushed before the stage reads more input,
/// so a body never blocks and order is kept.
/// </summary>
public ref struct RingWriter<T>
{
    /// <summary>Items written between two publishes.</summary>
    public const int PublishBatch = 64;

    readonly Ring<T>? ring;      // null: buffer mode (a replicated worker writing into its batch slot)
    readonly Spill<T> spill;
    T[] buf;
    readonly int mask;
    long pos, limit, publishAt;

    internal RingWriter(Ring<T> ring, Spill<T> spill)
    {
        this.ring = ring;
        this.spill = spill;
        buf = ring.buf;
        mask = ring.mask;
        pos = ring.idx.Tail;
        limit = spill.count > 0 ? pos : ring.idx.CachedHead + buf.Length;
        publishAt = pos + PublishBatch;
    }

    internal RingWriter(Spill<T> spill)
    {
        ring = null;
        this.spill = spill;
        spill.Grow(0);   // compact so that pending items start at 0
        buf = spill.items;
        mask = -1;
        pos = spill.count;
        limit = buf.Length;
        publishAt = long.MaxValue;
    }

    /// <summary>Appends one item.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(T item)
    {
        if (pos < limit)
        {
            buf[(int)pos & mask] = item;
            if (++pos == publishAt) Publish();
        }
        else WriteSlow(item);
    }

    /// <summary>Appends every item of <paramref name="items"/>.</summary>
    public void Write(ReadOnlySpan<T> items)
    {
        foreach (var x in items) Write(x);
    }

    /// <summary>
    /// True while the downstream ring has a free slot (refreshing the consumer's position when needed). A source
    /// checks it to end its turn instead of filling the overflow buffer.
    /// </summary>
    public bool HasRoom
    {
        get
        {
            if (pos < limit || ring == null) return true;
            if (spill.count > 0) return false;
            ring.idx.CachedHead = Volatile.Read(ref ring.idx.Head);
            limit = ring.idx.CachedHead + buf.Length;
            return pos < limit;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void WriteSlow(T item)
    {
        if (ring == null)
        {
            spill.count = (int)pos;   // the buffer is full: grow it, keeping what was written
            spill.Grow(buf.Length);
            buf = spill.items;
            limit = buf.Length;
            buf[(int)pos++] = item;
            return;
        }
        if (spill.count == 0)
        {
            ring.idx.CachedHead = Volatile.Read(ref ring.idx.Head);
            limit = ring.idx.CachedHead + buf.Length;
            if (pos < limit)
            {
                buf[(int)pos & mask] = item;
                if (++pos == publishAt) Publish();
                return;
            }
            Publish();   // the consumer drains what is there while we overflow
        }
        limit = pos;     // keep order: once spilling, everything spills until the stage flushes
        spill.Add(item);
    }

    void Publish()
    {
        publishAt = pos + PublishBatch;
        if (pos != ring!.idx.Tail) ring.Publish(pos);
    }

    /// <summary>Publishes what is still unpublished (the stage calls it after the body returns).</summary>
    internal void Flush()
    {
        if (ring == null) spill.count = (int)pos;
        else if (pos != ring.idx.Tail) ring.Publish(pos);
    }
}
