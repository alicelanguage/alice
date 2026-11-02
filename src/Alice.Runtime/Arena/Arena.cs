using System.Numerics;
using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Affine arena for one node type (ADR 0037): the slab behind an `affine enum` whose slot variants store their
/// fields in <typeparamref name="TSlot"/>. Every thread allocates in its own lane (<see cref="ArenaLane"/>): chunked
/// slabs of 2^20 slots doubling up to 2^30 per chunk, at most 2^32 - 1 slots per lane, and a free list threaded
/// through the first 4 bytes of each freed slot. <see cref="Alloc"/> pops the free list or bumps;
/// <see cref="Take"/> reads a node and frees it when this thread's lane owns it (a node of a foreign lane is only
/// read and is reclaimed when the enclosing <see cref="ArenaRegion"/> ends, so no free ever crosses a thread);
/// <see cref="Borrow"/> reads without freeing. Every slot access is bounds-checked against the lane's cursor; Debug
/// builds also detect double frees and reads of freed slots. 0 allocations after warm-up (a new chunk, lane or
/// region depth allocates once).
/// </summary>
public static class Arena<TSlot> where TSlot : unmanaged
{
    internal const uint None = uint.MaxValue;

    /// <summary>Lowered slot limit per lane for tests; 0 = the real limit, 2^32 - 1 (uint.MaxValue ends the free list).</summary>
#pragma warning disable CS0649 // set through reflection by ArenaTests
    internal static uint SlotCap;
#pragma warning restore CS0649

    internal static uint SlotLimit => SlotCap == 0 ? None : SlotCap;

    [ThreadStatic] static ArenaSlab<TSlot>? t_slab;
    static ArenaTypeOf<TSlot>? s_kind;

    /// <summary>Stores <paramref name="value"/> in a slot of this thread's lane and returns its slot term.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Term Alloc(int tag, in TSlot value)
    {
        if ((uint)tag > Term.MaxTag) ThrowTag(tag);
        var s = t_slab ?? Attach();
        uint i = s.Free;
        if (i != None)
        {
            ref TSlot r = ref s.Ref(i);
            s.Free = Unsafe.As<TSlot, uint>(ref r);
            r = value;
        }
        else
        {
            i = s.Len;
            if (i >= s.CurEnd) s.Grow();
            s.Cur[i - s.CurBase] = value;
            s.Len = i + 1;
        }
#if DEBUG
        s.MarkLive(i);
#endif
        return new Term((ulong)(uint)tag << 56 | s.LaneBits | i);
    }

    /// <summary>Reads the node of <paramref name="term"/> and frees its slot if this thread's lane owns it.
    /// Returns whether the slot was freed (false: a foreign lane's node, left for the region reset).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Take(Term term, out TSlot slot)
    {
        var s = t_slab;
        // the packed bit is part of the compared bits, so a packed term never matches a lane
        if (s is not null && (term.Raw & LaneMask) == s.LaneBits)
        {
            uint i = (uint)term.Raw;
            if (i >= s.Len) ThrowStale(term, s.Len);
            ref TSlot r = ref s.Ref(i);
#if DEBUG
            s.MarkFreed(i, term);
#endif
            slot = r;
            Unsafe.As<TSlot, uint>(ref r) = s.Free;
            s.Free = i;
            return true;
        }
        slot = Foreign(term);
        return false;
    }

    /// <summary>Reads the node of <paramref name="term"/> without freeing it. The reference stays valid until the slot
    /// is freed and reused or its region ends.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly TSlot Borrow(Term term)
    {
        var s = t_slab;
        if (s is null || (term.Raw & LaneMask) != s.LaneBits) s = Kind.SlabOf(term);
        uint i = (uint)term.Raw;
        if (i >= s.Len) ThrowStale(term, s.Len);
#if DEBUG
        s.CheckLive(i, term);
#endif
        return ref s.Ref(i);
    }

    /// <summary>Slots this thread's lane has bumped so far (its cursor; freed slots included).</summary>
    public static uint Used => t_slab?.Len ?? 0;

    const ulong LaneMask = 0x80FF_FFFF_0000_0000UL;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static TSlot Foreign(Term term)
    {
        var s = Kind.SlabOf(term);
        uint i = (uint)term.Raw;
        if (i >= s.Len) ThrowStale(term, s.Len);
        return s.Ref(i);
    }

    static ArenaTypeOf<TSlot> Kind
    {
        get
        {
            var k = Volatile.Read(ref s_kind);
            if (k is not null) return k;
            lock (ArenaRegistry.Gate)
            {
                if (s_kind is null)
                {
                    if (Unsafe.SizeOf<TSlot>() < sizeof(uint))
                        throw new NotSupportedException($"Arena<{typeof(TSlot).Name}>: a slot must be at least 4 bytes (the free list lives in its first 4 bytes)");
                    k = new ArenaTypeOf<TSlot>();
                    ArenaRegistry.Add(k);
                    Volatile.Write(ref s_kind, k);
                }
                return s_kind!;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ArenaSlab<TSlot> Attach() => t_slab = Kind.Attach(ArenaLane.Current);

    static void ThrowTag(int tag) => throw new ArgumentOutOfRangeException(nameof(tag), tag, $"variant tag must be 0..{Term.MaxTag}");

    static void ThrowStale(Term term, uint len) =>
        throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: {term} is past its lane's cursor {len} (a term of an ended region or a forged word)");
}

/// <summary>All lanes of one arena type.</summary>
sealed class ArenaTypeOf<TSlot> : ArenaKind where TSlot : unmanaged
{
    ArenaSlab<TSlot>?[] _lanes = new ArenaSlab<TSlot>?[8];

    internal ArenaSlab<TSlot> Attach(int lane)
    {
        lock (ArenaRegistry.Gate)
        {
            var a = _lanes;
            if (lane >= a.Length)
            {
                Array.Resize(ref a, Math.Max(a.Length * 2, lane + 1));
                Volatile.Write(ref _lanes, a);
            }
            return a[lane] ??= new ArenaSlab<TSlot>(lane, ArenaRegistry.Depth);
        }
    }

    internal ArenaSlab<TSlot> SlabOf(Term term)
    {
        if (term.IsPacked) throw new ArgumentException($"Arena<{typeof(TSlot).Name}>: {term} is packed and has no slot", nameof(term));
        var a = Volatile.Read(ref _lanes);
        int lane = term.Lane;
        var s = lane < a.Length ? a[lane] : null;
        return s ?? throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: {term} names lane {lane}, which never allocated in this arena");
    }

    internal override void PushMarks()
    {
        foreach (var s in _lanes) s?.PushMark();
    }

    internal override void PopMarks()
    {
        foreach (var s in _lanes) s?.PopMark();
    }
}

/// <summary>One lane of one arena type: chunk c holds 2^(20+c) slots for c up to 10, then 2^30 each.</summary>
sealed class ArenaSlab<TSlot> where TSlot : unmanaged
{
    const int FirstBits = 20, Doublings = 11, MaxBits = 30, ChunkCount = 14;
    const uint Big = ((1u << Doublings) - 1) << FirstBits;   // first index of the fixed-size chunks

    internal readonly int Lane;
    internal readonly ulong LaneBits;
    internal uint Len, Free = Arena<TSlot>.None, CurBase, CurEnd;
    internal TSlot[] Cur = [];
    readonly TSlot[]?[] _chunks = new TSlot[ChunkCount][];
    uint[] _markLen, _markFree;
    int _depth;
#if DEBUG
    readonly ulong[]?[] _freed = new ulong[ChunkCount][];
#endif

    internal ArenaSlab(int lane, int depth)
    {
        Lane = lane;
        LaneBits = (ulong)(uint)lane << 32;
        int n = Math.Max(4, depth);
        _markLen = new uint[n];
        _markFree = new uint[n];
        Array.Fill(_markFree, Arena<TSlot>.None);
        _depth = depth;   // a lane born inside regions is empty at each of their entries
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Locate(uint i, out int chunk, out uint offset)
    {
        uint q = (i >> FirstBits) + 1;
        if (q < 1u << Doublings)
        {
            chunk = BitOperations.Log2(q);
            offset = i - (((1u << chunk) - 1) << FirstBits);
        }
        else
        {
            chunk = Doublings + (int)((i - Big) >> MaxBits);
            offset = (i - Big) & ((1u << MaxBits) - 1);
        }
    }

    static uint BaseOf(int chunk) =>
        chunk < Doublings ? ((1u << chunk) - 1) << FirstBits : Big + ((uint)(chunk - Doublings) << MaxBits);

    static int SizeOf(int chunk) => 1 << (FirstBits + Math.Min(chunk, Doublings - 1));

    /// <summary>Slot i, i below Len (callers check); array accesses stay bounds-checked.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TSlot Ref(uint i)
    {
        Locate(i, out int c, out uint off);
        return ref _chunks[c]![off];
    }

    /// <summary>Len reached CurEnd: fail-stop at the slot limit, else move the cursor into the next chunk.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Grow()
    {
        uint limit = Arena<TSlot>.SlotLimit;
        if (Len >= limit)
            throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: lane {Lane} exhausted its {limit} slots (the 2^32 per-lane limit); end a region to reclaim them or fork the work across lanes");
        SetCursor(Len, allocate: true);
    }

    void SetCursor(uint i, bool allocate)
    {
        Locate(i, out int c, out _);
        var chunk = _chunks[c];
        if (chunk is null)
        {
            if (!allocate) { Cur = []; CurBase = CurEnd = i; return; }
            chunk = _chunks[c] = GC.AllocateUninitializedArray<TSlot>(SizeOf(c));
#if DEBUG
            _freed[c] = new ulong[(chunk.Length + 63) / 64];
#endif
        }
        Cur = chunk;
        CurBase = BaseOf(c);
        CurEnd = (uint)Math.Min((ulong)CurBase + (ulong)chunk.Length, Arena<TSlot>.SlotLimit);
    }

    internal void PushMark()
    {
        if (_depth == _markLen.Length)
        {
            Array.Resize(ref _markLen, _depth * 2);
            Array.Resize(ref _markFree, _depth * 2);
        }
        _markLen[_depth] = Len;
        _markFree[_depth] = Free;
        _depth++;
        Free = Arena<TSlot>.None;   // the region starts with an empty free list, so the outer one stays intact
    }

    internal void PopMark()
    {
        _depth--;
        Len = _markLen[_depth];
        Free = _markFree[_depth];
        SetCursor(Len, allocate: false);
    }

#if DEBUG
    internal void MarkLive(uint i)
    {
        Locate(i, out int c, out uint off);
        _freed[c]![off >> 6] &= ~(1UL << (int)(off & 63));
    }

    internal void MarkFreed(uint i, Term term)
    {
        Locate(i, out int c, out uint off);
        ref ulong w = ref _freed[c]![off >> 6];
        ulong bit = 1UL << (int)(off & 63);
        if ((w & bit) != 0) throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: double free of {term}");
        w |= bit;
    }

    internal void CheckLive(uint i, Term term)
    {
        Locate(i, out int c, out uint off);
        if ((_freed[c]![off >> 6] & (1UL << (int)(off & 63))) != 0)
            throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: read of freed {term}");
    }
#endif
}
