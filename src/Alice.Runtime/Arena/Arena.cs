using System.Numerics;
using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Affine arena for one node type (ADR 0037): the slab behind an `affine enum` whose slot variants store their
/// fields in <typeparamref name="TSlot"/>. Every thread allocates in its own lane (<see cref="ArenaLane"/>): chunked
/// slabs of 2^12 slots doubling up to 2^30 per chunk, at most 2^32 - 1 slots per lane, and a free list threaded
/// through the first 4 bytes of each freed slot. <see cref="Alloc"/> pops the free list or bumps;
/// <see cref="Take"/> reads a node and frees it when this thread's lane owns it (a node of a foreign lane is only
/// read and is reclaimed when the enclosing <see cref="ArenaRegion"/> ends, so no free ever crosses a thread);
/// <see cref="Borrow"/> reads without freeing. Every slot access is bounds-checked against the lane's cursor, and
/// every build (Release included) keeps one freed bit per slot: a second Take of a term, or a Take/Borrow of a term
/// whose slot is free, throws (fail-stop: the free list is never corrupted, two Allocs never alias). A stale term
/// whose slot a later Alloc already reused names that live node and is NOT detected: the term word has no room for
/// a generation, so that case stays the affine checker's (ADR 0037). A node allocated before a region and Taken
/// inside it returns to the free list of the level that allocated it, so the region's reset does not leak it.
/// 0 allocations after warm-up (a new chunk, lane or region depth allocates once).
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
            ArenaSlab<TSlot>.Locate(i, out int c, out uint off);
            ref TSlot r = ref s.Chunks[c]![off];
            s.Free = Unsafe.As<TSlot, uint>(ref r);
            r = value;
            s.Freed[c]![off >> 6] &= ~(1UL << (int)off);
        }
        else
        {
            i = s.Len;
            if (i >= s.CurEnd) s.Grow();
            uint off = i - s.CurBase;
            s.Cur[off] = value;
            if (i < s.Dirty) s.CurFreed[off >> 6] &= ~(1UL << (int)off);   // a slot reused after a region reset
            s.Len = i + 1;
        }
        return new Term((ulong)(uint)tag << 56 | s.LaneBits | i);
    }

    /// <summary>Reads the node of <paramref name="term"/> and frees its slot if this thread's lane owns it.
    /// Returns whether the slot was freed (false: a foreign lane's node, left for the region reset). Throws
    /// <see cref="InvalidOperationException"/> if the slot is already free (double Take).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Take(Term term, out TSlot slot)
    {
        var s = t_slab;
        // the packed bit is part of the compared bits, so a packed term never matches a lane
        if (s is not null && (term.Raw & LaneMask) == s.LaneBits)
        {
            uint i = (uint)term.Raw;
            if (i >= s.Len) ThrowStale(term, s.Len);
            ArenaSlab<TSlot>.Locate(i, out int c, out uint off);
            ref ulong w = ref s.Freed[c]![off >> 6];
            ulong bit = 1UL << (int)off;
            if ((w & bit) != 0) ThrowFreed(term, "double free (Take)");
            w |= bit;
            ref TSlot r = ref s.Chunks[c]![off];
            slot = r;
            if (i >= s.RegionBase)
            {
                Unsafe.As<TSlot, uint>(ref r) = s.Free;
                s.Free = i;
            }
            else s.FreeOuter(i);
            return true;
        }
        slot = Foreign(term);
        return false;
    }

    /// <summary>Reads the node of <paramref name="term"/> without freeing it. The reference stays valid until the slot
    /// is freed and reused or its region ends. Throws <see cref="InvalidOperationException"/> if the slot is free.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly TSlot Borrow(Term term)
    {
        var s = t_slab;
        if (s is null || (term.Raw & LaneMask) != s.LaneBits) s = Kind.SlabOf(term);
        uint i = (uint)term.Raw;
        if (i >= s.Len) ThrowStale(term, s.Len);
        ArenaSlab<TSlot>.Locate(i, out int c, out uint off);
        if ((s.Freed[c]![off >> 6] & (1UL << (int)off)) != 0) ThrowFreed(term, "read (Borrow)");
        return ref s.Chunks[c]![off];
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
        ArenaSlab<TSlot>.Locate(i, out int c, out uint off);
        if ((s.Freed[c]![off >> 6] & (1UL << (int)off)) != 0) ThrowFreed(term, "read (foreign Take)");
        return s.Chunks[c]![off];
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowFreed(Term term, string what) =>
        throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: {what} of freed {term}: the term was already Taken (use after move)");

    static void ThrowStale(Term term, uint len) =>
        throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: {term} is past its lane's cursor {len} (a term of an ended region or a forged word)");
}

/// <summary>All lanes of one arena type. A lane id freed by an exited thread is handed to a new thread together
/// with its slab (<see cref="ArenaLane"/>), so the slab's nodes stay valid and its memory is reused.</summary>
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

/// <summary>One lane of one arena type: chunk c holds 2^(12+c) slots for c up to 18, then 2^30 each; each chunk has
/// a bitmap with one freed bit per slot (zeroed at allocation; slots past <see cref="Dirty"/> were never freed).</summary>
sealed class ArenaSlab<TSlot> where TSlot : unmanaged
{
    const int FirstBits = 12, Doublings = 19, MaxBits = 30, ChunkCount = 22;
    const uint Big = ((1u << Doublings) - 1) << FirstBits;   // first index of the fixed-size chunks

    internal readonly int Lane;
    internal readonly ulong LaneBits;
    internal uint Len, Free = Arena<TSlot>.None, CurBase, CurEnd;
    /// <summary>Len at the entry of the innermost open region (0 outside regions): slots below it belong to an
    /// outer level and are freed into that level's list.</summary>
    internal uint RegionBase;
    /// <summary>Highest cursor before a region reset: freed bits at or past it are still 0, so a bump there skips
    /// clearing its bit.</summary>
    internal uint Dirty;
    internal TSlot[] Cur = [];
    internal ulong[] CurFreed = [];
    internal readonly TSlot[]?[] Chunks = new TSlot[ChunkCount][];
    internal readonly ulong[]?[] Freed = new ulong[ChunkCount][];
    uint[] _markLen, _markFree;
    int _depth;

    internal ArenaSlab(int lane, int depth)
    {
        Lane = lane;
        LaneBits = (ulong)(uint)lane << 32;
        int n = Math.Max(4, depth);
        _markLen = new uint[n];
        _markFree = new uint[n];
        Array.Fill(_markFree, Arena<TSlot>.None);
        _depth = depth;   // a lane born inside regions is empty at each of their entries (RegionBase 0)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Locate(uint i, out int chunk, out uint offset)
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
        var chunk = Chunks[c];
        if (chunk is null)
        {
            if (!allocate) { Cur = []; CurFreed = []; CurBase = CurEnd = i; return; }
            chunk = Chunks[c] = GC.AllocateUninitializedArray<TSlot>(SizeOf(c));
            Freed[c] = new ulong[chunk.Length / 64];
        }
        Cur = chunk;
        CurFreed = Freed[c]!;
        CurBase = BaseOf(c);
        CurEnd = (uint)Math.Min((ulong)CurBase + (ulong)chunk.Length, Arena<TSlot>.SlotLimit);
    }

    /// <summary>Slot <paramref name="i"/>, below <see cref="RegionBase"/>, was Taken inside a region: link it into
    /// the free list of the level that allocated it (saved in the mark stack), so the region's reset keeps it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void FreeOuter(uint i)
    {
        int j = _depth - 1;
        while (j > 0 && i < _markLen[j - 1]) j--;
        Locate(i, out int c, out uint off);
        Unsafe.As<TSlot, uint>(ref Chunks[c]![off]) = _markFree[j];
        _markFree[j] = i;
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
        RegionBase = Len;
        Free = Arena<TSlot>.None;   // the region starts with an empty free list, so the outer one stays intact
    }

    internal void PopMark()
    {
        _depth--;
        if (Len > Dirty) Dirty = Len;
        Len = _markLen[_depth];
        Free = _markFree[_depth];
        RegionBase = _depth > 0 ? _markLen[_depth - 1] : 0;
        SetCursor(Len, allocate: false);
    }
}
