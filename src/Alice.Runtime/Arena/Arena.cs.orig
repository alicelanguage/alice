using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    // ------------------------------------------------------------------ raw access (ADR 0040 section 2)
    // The IL backend inlines Alloc/Take/Borrow over this thread's slab (lane cursor fast path, no Locate, the lane
    // slab read once per method); the C# backend calls the value forms below. Not for hand-written code.

    /// <summary>This thread's slab (attaches the lane on first use). Raw access for generated code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ArenaSlab<TSlot> RawSlab
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_slab ?? Attach();
    }

    /// <summary>The term bits compared against <see cref="ArenaSlab{TSlot}.LaneBits"/>: packed flag plus lane.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public const ulong RawLaneMask = LaneMask;

    /// <summary><see cref="Alloc"/> on a raw term word.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AllocValue(int tag, in TSlot value) => Alloc(tag, value).Raw;

    /// <summary><see cref="Take"/> by value on a raw term word (the slot is freed when this lane owns it).</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TSlot TakeValue(ulong term)
    {
        Take(new Term(term), out var slot);
        return slot;
    }

    /// <summary><see cref="Borrow"/> by value on a raw term word.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TSlot BorrowValue(ulong term) => Borrow(new Term(term));

    /// <summary>Frees the slot of a raw term word without reading it (a lane-local free; packed or foreign: no-op).</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Drop(ulong term)
    {
        if ((long)term < 0) return;
        Take(new Term(term), out _);
    }

    /// <summary>
    /// Recursive Drop of a dead subtree (Bend's free of an unconsumed value): frees the node of <paramref name="term"/>
    /// and, through the term words in <paramref name="top"/> (this node) and <paramref name="all"/> (every node below),
    /// its children. Packed and foreign-lane terms stop the walk (a foreign subtree returns at the region reset), so no
    /// free crosses a thread. Iterative on the last child, so a long chain does not grow the stack.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void DropDeep(ulong term, uint top, uint all)
    {
        var s = t_slab;
        if (s is not null) DropRec(s, term, top, all);
    }

    /// <summary><see cref="DropDeep"/> over the lane slab read once: the subtrees of all child words but the last
    /// first (this slot stays intact until it is freed), then this slot, then the last child in the loop.</summary>
    static void DropRec(ArenaSlab<TSlot> s, ulong term, uint mask, uint all)
    {
        int n = Unsafe.SizeOf<TSlot>() / sizeof(ulong);
        while (true)
        {
            if ((term & LaneMask) != s.LaneBits) return;
            uint i = (uint)term;
            if (i >= s.Len) ThrowStale(new Term(term), s.Len);
            uint off = i - s.CurBase;
            ref TSlot r = ref off < (uint)s.Cur.Length
                ? ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s.Cur), (nint)off)
                : ref s.Ref(i);
            ref ulong w = ref Unsafe.As<TSlot, ulong>(ref r);
            int last = -1;
            for (int k = 0; k < n; k++)
                if ((mask & (1u << k)) != 0 && (long)Unsafe.Add(ref w, k) >= 0)
                {
                    if (last >= 0) DropRec(s, Unsafe.Add(ref w, last), all, all);
                    last = k;
                }
            ulong next = last >= 0 ? Unsafe.Add(ref w, last) : 0;
            s.RawFreed(i, term);
            if (i >= s.RegionBase)
            {
                Unsafe.As<TSlot, uint>(ref r) = s.Free;
                s.Free = i;
            }
            else s.FreeOuter(i);
            if (last < 0) return;
            term = next;
            mask = all;
        }
    }

    /// <summary>Slow path of the inlined reads: a foreign lane's node, or a stale term (throws).</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.NoInlining)]
    public static TSlot RawForeign(ulong term) => Borrow(new Term(term));

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
    static ArenaSlab<TSlot> Attach() => t_slab = Kind.Attach(ArenaLane.CurrentInfo);

    /// <summary>Points this thread's cached slab at a lane's (null: attach lazily); see <see cref="ArenaLane"/>.</summary>
    internal static void BindThread(ArenaSlab<TSlot>? slab) => t_slab = slab;

    static void ThrowTag(int tag) => throw new ArgumentOutOfRangeException(nameof(tag), tag, $"variant tag must be 0..{Term.MaxTag}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowFreed(Term term, string what) =>
        throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: {what} of freed {term}: the term was already Taken (use after move)");

    static void ThrowStale(Term term, uint len) =>
        throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: {term} is past its lane's cursor {len} (a term of an ended region or a forged word)");
}

/// <summary>All lanes of one arena type. A lane id freed by an exited thread (or an ended region lease) is handed to
/// a new thread together with its slab (<see cref="ArenaLane"/>), so the slab's nodes stay valid and its memory is
/// reused. A region marks and resets only the lanes it owns (<see cref="ArenaRegion"/>).</summary>
sealed class ArenaTypeOf<TSlot> : ArenaKind where TSlot : unmanaged
{
    ArenaSlab<TSlot>?[] _lanes = new ArenaSlab<TSlot>?[8];

    internal ArenaSlab<TSlot> Attach(LaneInfo info)
    {
        int lane = info.Id;
        lock (ArenaRegistry.Gate)
        {
            var a = _lanes;
            if (lane >= a.Length)
            {
                Array.Resize(ref a, Math.Max(a.Length * 2, lane + 1));
                Volatile.Write(ref _lanes, a);
            }
            return a[lane] ??= new ArenaSlab<TSlot>(lane, info.Depth);   // born inside the lane's open regions
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

    ArenaSlab<TSlot>? Find(int lane)
    {
        var a = Volatile.Read(ref _lanes);
        return (uint)lane < (uint)a.Length ? a[lane] : null;
    }

    internal override void PushMark(int lane) => Find(lane)?.PushMark();

    internal override void PopMark(int lane) => Find(lane)?.PopMark();

    internal override void Bind(int lane) => Arena<TSlot>.BindThread(Find(lane));
}

/// <summary>One lane of one arena type: chunk c holds 2^(12+c) slots for c up to 18, then 2^30 each; each chunk has
/// a bitmap with one freed bit per slot (zeroed at allocation; slots past <see cref="Dirty"/> were never freed).
/// Public only for generated code (ADR 0040 raw access): <see cref="Cur"/> is the chunk that holds slots
/// [<see cref="CurBase"/>, <see cref="CurEnd"/>), <see cref="Len"/> the bump cursor, <see cref="Free"/> the free-list
/// head.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ArenaSlab<TSlot> where TSlot : unmanaged
{
    const int FirstBits = 12, Doublings = 19, MaxBits = 30, ChunkCount = 22;
    const uint Big = ((1u << Doublings) - 1) << FirstBits;   // first index of the fixed-size chunks

    public readonly int Lane;
    public readonly ulong LaneBits;
    public uint Len, Free = Arena<TSlot>.None, CurBase, CurEnd;
    /// <summary>Len at the entry of the innermost open region (0 outside regions): slots below it belong to an
    /// outer level and are freed into that level's list.</summary>
    internal uint RegionBase;
    /// <summary>Highest cursor before a region reset: freed bits at or past it are still 0, so a bump there skips
    /// clearing its bit.</summary>
    internal uint Dirty;
    public TSlot[] Cur = [];
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
    public void Grow()
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

    /// <summary>Slot i, i below Len (callers check); array accesses stay bounds-checked.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TSlot Ref(uint i)
    {
        Locate(i, out int c, out uint off);
        return ref Chunks[c]![off];
    }

    /// <summary>Generated code allocated slot i (free-list pop or bump): clears its freed bit.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RawLive(uint i)
    {
        Locate(i, out int c, out uint off);
        Freed[c]![off >> 6] &= ~(1UL << (int)off);
    }

    /// <summary>Generated code frees slot i of <paramref name="term"/>: throws on a double free (every build).</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RawFreed(uint i, ulong term)
    {
        Locate(i, out int c, out uint off);
        ref ulong w = ref Freed[c]![off >> 6];
        ulong bit = 1UL << (int)off;
        if ((w & bit) != 0) ThrowFreed(term, "double free (Take)");
        w |= bit;
    }

    /// <summary>Generated code reads slot i of <paramref name="term"/>: throws if it is free (every build).</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RawCheck(uint i, ulong term)
    {
        Locate(i, out int c, out uint off);
        if ((Freed[c]![off >> 6] & (1UL << (int)off)) != 0) ThrowFreed(term, "read (Borrow)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowFreed(ulong term, string what) =>
        throw new InvalidOperationException($"Arena<{typeof(TSlot).Name}>: {what} of freed {new Term(term)}: the term was already Taken (use after move)");

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
