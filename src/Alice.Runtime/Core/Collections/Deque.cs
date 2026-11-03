using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Alice.Core.Collections;

/// <summary>
/// Double-ended queue on a growable power-of-two ring: O(1) push and pop at both ends, O(1) indexing (0 = front).
/// Ruby names: <see cref="Push"/>/<see cref="Pop"/> at the back, <see cref="Unshift"/>/<see cref="Shift"/> at the front.
/// No allocation except when growing; struct enumerator.
/// </summary>
public sealed class Deque<T> : IReadOnlyList<T>
{
    T[] buf;
    int head, count;

    /// <summary>An empty deque with room for <paramref name="capacity"/> elements.</summary>
    public Deque(int capacity = 8) => buf = new T[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 4))];

    /// <summary>Number of elements.</summary>
    public int Count => count;

    /// <summary>Ruby `empty?`.</summary>
    public bool IsEmpty => count == 0;

    /// <summary>Element <paramref name="index"/> from the front.</summary>
    public T this[int index]
    {
        get { if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index)); return buf[(head + index) & (buf.Length - 1)]; }
        set { if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index)); buf[(head + index) & (buf.Length - 1)] = value; }
    }

    /// <summary>Adds at the back (Ruby `push` / `&lt;&lt;`).</summary>
    public void Push(T item)
    {
        if (count == buf.Length) Grow();
        buf[(head + count) & (buf.Length - 1)] = item;
        count++;
    }

    /// <summary>Adds at the front (Ruby `unshift`).</summary>
    public void Unshift(T item)
    {
        if (count == buf.Length) Grow();
        head = (head - 1) & (buf.Length - 1);
        buf[head] = item;
        count++;
    }

    /// <summary>Removes from the back (Ruby `pop`); throws when empty.</summary>
    public T Pop()
    {
        if (count == 0) throw new InvalidOperationException("deque is empty");
        int i = (head + --count) & (buf.Length - 1);
        T v = buf[i];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) buf[i] = default!;
        return v;
    }

    /// <summary>Removes from the front (Ruby `shift`); throws when empty.</summary>
    public T Shift()
    {
        if (count == 0) throw new InvalidOperationException("deque is empty");
        T v = buf[head];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) buf[head] = default!;
        head = (head + 1) & (buf.Length - 1);
        count--;
        return v;
    }

    /// <summary>Like <see cref="Pop"/>; false when empty.</summary>
    public bool TryPop(out T item) { if (count == 0) { item = default!; return false; } item = Pop(); return true; }

    /// <summary>Like <see cref="Shift"/>; false when empty.</summary>
    public bool TryShift(out T item) { if (count == 0) { item = default!; return false; } item = Shift(); return true; }

    /// <summary>Ruby `first`.</summary>
    public T First => count > 0 ? buf[head] : throw new InvalidOperationException("deque is empty");

    /// <summary>Ruby `last`.</summary>
    public T Last => count > 0 ? buf[(head + count - 1) & (buf.Length - 1)] : throw new InvalidOperationException("deque is empty");

    /// <summary>Ruby `clear`.</summary>
    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(buf);
        head = count = 0;
    }

    /// <summary>Ruby `to_a`, front first.</summary>
    public T[] ToA()
    {
        var a = new T[count];
        for (int i = 0; i < count; i++) a[i] = buf[(head + i) & (buf.Length - 1)];
        return a;
    }

    void Grow()
    {
        var n = new T[buf.Length * 2];
        for (int i = 0; i < count; i++) n[i] = buf[(head + i) & (buf.Length - 1)];
        buf = n;
        head = 0;
    }

    /// <summary>Struct enumerator, front first.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Struct enumerator of <see cref="Deque{T}"/>.</summary>
    public struct Enumerator(Deque<T> d) : IEnumerator<T>
    {
        int i = -1;
        /// <inheritdoc/>
        public readonly T Current => d[i];
        readonly object? IEnumerator.Current => Current;
        /// <inheritdoc/>
        public bool MoveNext() => ++i < d.count;
        /// <inheritdoc/>
        public void Reset() => i = -1;
        /// <inheritdoc/>
        public readonly void Dispose() { }
    }
}

/// <summary>
/// Fixed-capacity ring buffer that overwrites its oldest element when full (recent-history windows, moving averages).
/// Index 0 is the oldest element. No allocation after construction; struct enumerator oldest first.
/// </summary>
public sealed class Ring<T> : IReadOnlyList<T>
{
    readonly T[] buf;
    int head, count;

    /// <summary>A ring of <paramref name="capacity"/> elements.</summary>
    public Ring(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        buf = new T[capacity];
    }

    /// <summary>Capacity.</summary>
    public int Capacity => buf.Length;

    /// <summary>Number of elements (at most <see cref="Capacity"/>).</summary>
    public int Count => count;

    /// <summary>Ruby `empty?`.</summary>
    public bool IsEmpty => count == 0;

    /// <summary>Ruby `full?`.</summary>
    public bool IsFull => count == buf.Length;

    /// <summary>Element <paramref name="index"/>, 0 = oldest.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
            int i = head + index;
            return buf[i >= buf.Length ? i - buf.Length : i];
        }
    }

    /// <summary>Appends; when full, overwrites and returns true with the evicted oldest element.</summary>
    public bool Push(T item, out T evicted)
    {
        if (count < buf.Length)
        {
            int i = head + count;
            buf[i >= buf.Length ? i - buf.Length : i] = item;
            count++;
            evicted = default!;
            return false;
        }
        evicted = buf[head];
        buf[head] = item;
        head = head + 1 == buf.Length ? 0 : head + 1;
        return true;
    }

    /// <summary>Appends (Ruby `&lt;&lt;`), overwriting the oldest element when full.</summary>
    public void Push(T item) => Push(item, out _);

    /// <summary>The oldest element.</summary>
    public T Oldest => count > 0 ? buf[head] : throw new InvalidOperationException("ring is empty");

    /// <summary>The newest element.</summary>
    public T Newest => count > 0 ? this[count - 1] : throw new InvalidOperationException("ring is empty");

    /// <summary>Removes every element.</summary>
    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(buf);
        head = count = 0;
    }

    /// <summary>Ruby `to_a`, oldest first.</summary>
    public T[] ToA()
    {
        var a = new T[count];
        for (int i = 0; i < count; i++) a[i] = this[i];
        return a;
    }

    /// <summary>Struct enumerator, oldest first.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Struct enumerator of <see cref="Ring{T}"/>.</summary>
    public struct Enumerator(Ring<T> r) : IEnumerator<T>
    {
        int i = -1;
        /// <inheritdoc/>
        public readonly T Current => r[i];
        readonly object? IEnumerator.Current => Current;
        /// <inheritdoc/>
        public bool MoveNext() => ++i < r.count;
        /// <inheritdoc/>
        public void Reset() => i = -1;
        /// <inheritdoc/>
        public readonly void Dispose() { }
    }
}

/// <summary>
/// A fixed-length set of bits over <c>ulong</c> words: get/set/flip, population count, in-place and/or/xor/and-not
/// with SIMD (<see cref="Vector{T}"/>), and enumeration of set bits (struct enumerator). 0 bytes allocated after
/// construction.
/// </summary>
public sealed class BitSet : IEquatable<BitSet>
{
    readonly ulong[] words;

    /// <summary>A set of <paramref name="length"/> bits, all clear.</summary>
    public BitSet(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        words = new ulong[(length + 63) >> 6];
    }

    /// <summary>Number of bits.</summary>
    public int Length { get; }

    /// <summary>The words (bit i is bit i &amp; 63 of word i &gt;&gt; 6).</summary>
    public Span<ulong> Words => words;

    /// <summary>Bit <paramref name="index"/>.</summary>
    public bool this[int index]
    {
        get { Check(index); return (words[index >> 6] >> (index & 63) & 1) != 0; }
        set { Check(index); if (value) words[index >> 6] |= 1UL << (index & 63); else words[index >> 6] &= ~(1UL << (index & 63)); }
    }

    void Check(int index) { if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index)); }

    /// <summary>Sets bit <paramref name="index"/>.</summary>
    public void Set(int index) => this[index] = true;

    /// <summary>Clears bit <paramref name="index"/>.</summary>
    public void Clear(int index) => this[index] = false;

    /// <summary>Flips bit <paramref name="index"/>.</summary>
    public void Flip(int index) { Check(index); words[index >> 6] ^= 1UL << (index & 63); }

    /// <summary>Clears every bit.</summary>
    public void ClearAll() => Array.Clear(words);

    /// <summary>Sets every bit.</summary>
    public void SetAll()
    {
        words.AsSpan().Fill(ulong.MaxValue);
        TrimTail();
    }

    void TrimTail() { if ((Length & 63) != 0) words[^1] &= (1UL << (Length & 63)) - 1; }

    /// <summary>Number of set bits.</summary>
    public int PopCount()
    {
        int n = 0;
        foreach (ulong w in words) n += BitOperations.PopCount(w);
        return n;
    }

    /// <summary>True when no bit is set.</summary>
    public bool IsEmpty => words.AsSpan().IndexOfAnyExcept(0UL) < 0;

    /// <summary>Index of the first set bit at or after <paramref name="from"/>, or -1.</summary>
    public int NextSetBit(int from)
    {
        if (from >= Length) return -1;
        if (from < 0) from = 0;
        int wi = from >> 6;
        ulong w = words[wi] & (ulong.MaxValue << (from & 63));
        while (true)
        {
            if (w != 0) { int i = (wi << 6) + BitOperations.TrailingZeroCount(w); return i < Length ? i : -1; }
            if (++wi >= words.Length) return -1;
            w = words[wi];
        }
    }

    /// <summary>this &amp;= other.</summary>
    public BitSet And(BitSet other) { Op(other, 0); return this; }

    /// <summary>this |= other.</summary>
    public BitSet Or(BitSet other) { Op(other, 1); return this; }

    /// <summary>this ^= other.</summary>
    public BitSet Xor(BitSet other) { Op(other, 2); return this; }

    /// <summary>this &amp;= ~other.</summary>
    public BitSet AndNot(BitSet other) { Op(other, 3); return this; }

    void Op(BitSet o, int op)
    {
        if (o.Length != Length) throw new ArgumentException("bit sets differ in length", nameof(o));
        Span<ulong> a = words;
        ReadOnlySpan<ulong> b = o.words;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
            for (; i + Vector<ulong>.Count <= a.Length; i += Vector<ulong>.Count)
            {
                var x = new Vector<ulong>(a[i..]);
                var y = new Vector<ulong>(b[i..]);
                (op switch { 0 => x & y, 1 => x | y, 2 => x ^ y, _ => Vector.AndNot(x, y) }).CopyTo(a[i..]);
            }
        for (; i < a.Length; i++) a[i] = op switch { 0 => a[i] & b[i], 1 => a[i] | b[i], 2 => a[i] ^ b[i], _ => a[i] & ~b[i] };
    }

    /// <summary>Struct enumerator over the indexes of set bits, ascending.</summary>
    public SetBitEnumerator GetEnumerator() => new(this);

    /// <summary>Enumerates the indexes of set bits.</summary>
    public struct SetBitEnumerator(BitSet s)
    {
        int next = -1;
        /// <summary>The current index.</summary>
        public int Current { get; private set; }
        /// <summary>Advances to the next set bit.</summary>
        public bool MoveNext()
        {
            next = s.NextSetBit(next + 1);
            if (next < 0) { next = s.Length; return false; }
            Current = next;
            return true;
        }
    }

    /// <inheritdoc/>
    public bool Equals(BitSet? other) => other is not null && other.Length == Length && words.AsSpan().SequenceEqual(other.words);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BitSet b && Equals(b);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
        return h.ToHashCode();
    }
}
