using System.Reflection;
using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>The generic tree example of ADR 0037: `enum Tree { Leaf(UInt32), Node(Tree, Tree) }` with Leaf packed
/// in the term and Node in one <see cref="Arena{TSlot}"/> slot.</summary>
static class ArenaTree
{
    public const int Leaf = 0, Node = 1;

    public record struct Pair(Term L, Term R);

    public static Term Gen(int d, uint x) =>
        d == 0 ? Term.Pack(Leaf, x) : Arena<Pair>.Alloc(Node, new Pair(Gen(d - 1, x * 2), Gen(d - 1, x * 2 + 1)));

    /// <summary>Consuming sum: every node of this lane is freed as it is read.</summary>
    public static uint Sum(Term t)
    {
        if (t.IsPacked) return t.Payload;
        Arena<Pair>.Take(t, out var p);
        return Sum(p.L) + Sum(p.R);
    }

    public static uint SumBorrowed(Term t)
    {
        if (t.IsPacked) return t.Payload;
        ref readonly var p = ref Arena<Pair>.Borrow(t);
        return SumBorrowed(p.L) + SumBorrowed(p.R);
    }

    /// <summary>Forks the top levels down to depth <paramref name="cut"/>; each half builds into its own lane.</summary>
    public static Term GenPar(int d, uint x, int cut)
    {
        if (d <= cut) return Gen(d, x);
        var (l, r) = Par.Fork(() => GenPar(d - 1, x * 2, cut), () => GenPar(d - 1, x * 2 + 1, cut));
        return Arena<Pair>.Alloc(Node, new Pair(l, r));
    }

    public static uint SumPar(Term t, int d, int cut)
    {
        if (d <= cut) return Sum(t);
        Arena<Pair>.Take(t, out var p);
        var (l, r) = Par.Fork(() => SumPar(p.L, d - 1, cut), () => SumPar(p.R, d - 1, cut));
        return l + r;
    }

    /// <summary>Sum of the leaves 2^d .. 2^(d+1)-1 mod 2^32.</summary>
    public static uint Expected(int d) => (uint)(((1UL << d) + (1UL << (d + 1)) - 1) * (1UL << d) / 2);
}

public class ArenaTests
{
    record struct Cell(uint V, uint W);
    record struct Big(ulong A, ulong B, ulong C);
    record struct Limited(uint V);
    record struct Tiny(ushort V);

    static Term RunOnOtherThread(Func<Term> f)
    {
        Term t = default;
        var th = new Thread(() => t = f());
        th.Start();
        th.Join();
        return t;
    }

    [Fact]
    public void Term_packs_fields_in_the_documented_bits()
    {
        var p = Term.Pack(5, 0xDEADBEEF);
        Assert.True(p.IsPacked);
        Assert.Equal(5, p.Tag);
        Assert.Equal(0xDEADBEEFu, p.Payload);
        Assert.Equal(1UL << 63 | 5UL << 56 | 0xDEADBEEF, p.Raw);
        Term.Unpack(p, out int tag, out uint payload);
        Assert.Equal((5, 0xDEADBEEFu), (tag, payload));

        var n = Term.Node(127, Term.MaxLane, uint.MaxValue);
        Assert.False(n.IsPacked);
        Assert.Equal((127, Term.MaxLane, uint.MaxValue), (n.Tag, n.Lane, n.Slot));
        Assert.Equal(127UL << 56 | (ulong)Term.MaxLane << 32 | uint.MaxValue, n.Raw);

        Assert.Throws<ArgumentOutOfRangeException>(() => Term.Pack(128, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Term.Node(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Term.Node(0, Term.MaxLane + 1, 0));
        Assert.Throws<ArgumentException>(() => Term.Unpack(n, out _, out _));
    }

    [Fact]
    public void Alloc_records_tag_lane_and_value()
    {
        var t = Arena<Cell>.Alloc(9, new Cell(1, 2));
        Assert.False(t.IsPacked);
        Assert.Equal(9, t.Tag);
        Assert.Equal(ArenaLane.Current, t.Lane);
        Assert.Equal(new Cell(1, 2), Arena<Cell>.Borrow(t));
        Assert.Throws<ArgumentOutOfRangeException>(() => Arena<Cell>.Alloc(128, default));
    }

    [Fact]
    public void Take_frees_the_slot_for_the_next_alloc_and_borrow_does_not()
    {
        var a = Arena<Big>.Alloc(1, new Big(1, 2, 3));
        Assert.Equal(new Big(1, 2, 3), Arena<Big>.Borrow(a));
        var b = Arena<Big>.Alloc(1, new Big(4, 5, 6));
        Assert.NotEqual(a.Slot, b.Slot);   // borrow kept a alive

        Assert.True(Arena<Big>.Take(a, out var va));
        Assert.Equal(new Big(1, 2, 3), va);
        var c = Arena<Big>.Alloc(2, new Big(7, 8, 9));
        Assert.Equal(a.Slot, c.Slot);      // reused through the free list
        Assert.Equal(new Big(7, 8, 9), Arena<Big>.Borrow(c));
        Assert.Equal(new Big(4, 5, 6), Arena<Big>.Borrow(b));
    }

    [Fact]
    public void Foreign_lane_take_reads_without_freeing()
    {
        Term t = RunOnOtherThread(() => Arena<Cell>.Alloc(3, new Cell(42, 43)));
        Assert.NotEqual(ArenaLane.Current, t.Lane);
        Assert.False(Arena<Cell>.Take(t, out var v));
        Assert.Equal(new Cell(42, 43), v);
        Assert.Equal(new Cell(42, 43), Arena<Cell>.Borrow(t));   // still intact: no cross-lane free
    }

    [Fact]
    public void Bad_terms_fail_with_clear_exceptions()
    {
        using var region = ArenaRegion.Enter();
        Assert.Throws<ArgumentException>(() => Arena<Cell>.Borrow(Term.Pack(0, 1)));
        Assert.Throws<ArgumentException>(() => Arena<Cell>.Take(Term.Pack(0, 1), out _));
        var t = Arena<Cell>.Alloc(0, new Cell(1, 1));
        var forged = Term.Node(0, t.Lane, uint.MaxValue - 1);
        var e = Assert.Throws<InvalidOperationException>(() => Arena<Cell>.Borrow(forged));
        Assert.Contains("cursor", e.Message);
        Assert.Throws<InvalidOperationException>(() => Arena<Cell>.Take(forged, out _));
        Assert.Throws<InvalidOperationException>(() => Arena<Cell>.Borrow(Term.Node(0, Term.MaxLane, 0)));
    }

    [Fact]
    public void Slots_smaller_than_the_free_link_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() => Arena<Tiny>.Alloc(0, default));
    }

    [Fact]
    public void Region_resets_every_lane_and_nests()
    {
        var keep = Arena<Cell>.Alloc(0, new Cell(7, 7));
        uint before = Arena<Cell>.Used;
        using (var outer = ArenaRegion.Enter())
        {
            Assert.Equal(ArenaRegion.CurrentDepth, outer.Depth);
            for (uint i = 0; i < 10; i++) Arena<Cell>.Alloc(0, new Cell(i, i));
            uint mid = Arena<Cell>.Used;
            Assert.Equal(before + 10, mid);
            Term inner;
            using (ArenaRegion.Enter())
            {
                inner = Arena<Cell>.Alloc(0, new Cell(1, 2));
                Arena<Cell>.Take(inner, out _);
                Arena<Cell>.Alloc(0, new Cell(3, 4));   // reuses inner's slot
                Assert.Equal(mid + 1, Arena<Cell>.Used);
            }
            Assert.Equal(mid, Arena<Cell>.Used);
            Assert.Throws<InvalidOperationException>(() => Arena<Cell>.Borrow(inner));   // ended with its region
        }
        Assert.Equal(before, Arena<Cell>.Used);
        Assert.Equal(new Cell(7, 7), Arena<Cell>.Borrow(keep));
    }

    [Fact]
    public void Region_keeps_the_outer_free_list_intact()
    {
        var a = Arena<Big>.Alloc(0, new Big(1, 1, 1));
        Arena<Big>.Take(a, out _);                       // a's slot heads the free list
        using (ArenaRegion.Enter())
        {
            var b = Arena<Big>.Alloc(0, new Big(2, 2, 2));
            Assert.NotEqual(a.Slot, b.Slot);              // the region starts with an empty free list
        }
        Assert.Equal(a.Slot, Arena<Big>.Alloc(0, default).Slot);
    }

    [Fact]
    public void Region_reclaims_foreign_lanes_and_lanes_born_inside_it()
    {
        uint used = 0;
        Term t;
        using (ArenaRegion.Enter())
        {
            t = RunOnOtherThread(() => { var x = Arena<Limited>.Alloc(1, new Limited(5)); used = Arena<Limited>.Used; return x; });
            Assert.Equal(new Limited(5), Arena<Limited>.Borrow(t));
            Assert.True(used >= 1);
        }
        Assert.Throws<InvalidOperationException>(() => Arena<Limited>.Borrow(t));
    }

    [Fact]
    public void Regions_end_innermost_first()
    {
        var outer = ArenaRegion.Enter();
        var inner = ArenaRegion.Enter();
        Assert.Throws<InvalidOperationException>(() => outer.Dispose());
        inner.Dispose();
        Assert.Throws<InvalidOperationException>(() => inner.Dispose());
        outer.Dispose();
        default(ArenaRegion).Dispose();   // no-op
    }

    record struct Capped(uint V);

    [Fact]
    public void Lane_slot_limit_is_fail_stop()
    {
        typeof(Arena<Capped>).GetField("SlotCap", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 5u);
        RunOnOtherThread(() =>
        {
            for (int i = 0; i < 5; i++) Arena<Capped>.Alloc(0, default);
            var e = Assert.Throws<InvalidOperationException>(() => Arena<Capped>.Alloc(0, default));
            Assert.Contains("2^32", e.Message);
            Assert.Equal(5u, Arena<Capped>.Used);
            return default;
        });
    }

    [Fact]
    public void Lane_count_is_fail_stop_at_two_to_the_24()
    {
        var check = typeof(ArenaLane).GetMethod("Check", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(ArenaLane.MaxLanes - 1, check.Invoke(null, [ArenaLane.MaxLanes - 1]));
        var e = Assert.Throws<TargetInvocationException>(() => check.Invoke(null, [ArenaLane.MaxLanes]));
        Assert.Contains("2^24", Assert.IsType<InvalidOperationException>(e.InnerException).Message);
        Assert.Throws<TargetInvocationException>(() => check.Invoke(null, [int.MinValue]));
    }

    [Fact]
    public void Every_build_detects_double_free_and_use_after_free()
    {
        // review F4: Release builds too; a double Take used to corrupt the free list so that two Allocs aliased
        var t = Arena<Big>.Alloc(0, new Big(1, 1, 1));
        Assert.True(Arena<Big>.Take(t, out _));
        Assert.Contains("double free", Assert.Throws<InvalidOperationException>(() => Arena<Big>.Take(t, out _)).Message);
        Assert.Contains("freed", Assert.Throws<InvalidOperationException>(() => Arena<Big>.Borrow(t)).Message);
        var x = Arena<Big>.Alloc(0, new Big(2, 2, 2));   // pops t's slot: live again
        var y = Arena<Big>.Alloc(0, new Big(3, 3, 3));
        Assert.Equal(t.Slot, x.Slot);
        Assert.NotEqual(x.Slot, y.Slot);                  // the rejected second Take left the free list intact
        Assert.Equal(new Big(2, 2, 2), Arena<Big>.Borrow(x));
        Assert.Equal(new Big(3, 3, 3), Arena<Big>.Borrow(y));
    }

    [Fact]
    public void A_foreign_read_of_a_freed_node_throws()
    {
        Term t = RunOnOtherThread(() =>
        {
            var a = Arena<Cell>.Alloc(1, new Cell(1, 2));
            Arena<Cell>.Take(a, out _);
            return a;
        });
        Assert.Throws<InvalidOperationException>(() => Arena<Cell>.Borrow(t));
        Assert.Throws<InvalidOperationException>(() => Arena<Cell>.Take(t, out _));
    }

    [Fact]
    public void Bits_are_fresh_for_slots_reused_after_a_region_reset()
    {
        Term inner;
        using (ArenaRegion.Enter())
        {
            inner = Arena<Big>.Alloc(0, new Big(4, 4, 4));
            Arena<Big>.Take(inner, out _);                  // freed bit set, then the region rewinds past it
        }
        using (ArenaRegion.Enter())
        {
            var again = Arena<Big>.Alloc(0, new Big(5, 5, 5));   // bumps over the same index
            Assert.Equal(inner.Slot, again.Slot);
            Assert.Equal(new Big(5, 5, 5), Arena<Big>.Borrow(again));
            Assert.True(Arena<Big>.Take(again, out _));
        }
    }

    record struct Outer(uint V, uint W);

    [Fact]
    public void A_node_from_outside_a_region_taken_inside_it_is_not_leaked()
    {
        // review F16: the slot goes back to the free list of the level that allocated it, not the region's
        var o = Arena<Outer>.Alloc(0, new Outer(1, 1));
        Term mid;
        using (ArenaRegion.Enter())
        {
            mid = Arena<Outer>.Alloc(0, new Outer(2, 2));
            using (ArenaRegion.Enter())
            {
                Arena<Outer>.Take(o, out _);
                Arena<Outer>.Take(mid, out _);
                var n = Arena<Outer>.Alloc(0, new Outer(3, 3));
                Assert.NotEqual(o.Slot, n.Slot);       // the inner region does not reuse outer slots
                Assert.NotEqual(mid.Slot, n.Slot);
            }
            Assert.Equal(mid.Slot, Arena<Outer>.Alloc(0, default).Slot);   // mid's level got it back
        }
        Assert.Equal(o.Slot, Arena<Outer>.Alloc(0, default).Slot);         // and o's level got o back
    }

    record struct S16(ulong A, ulong B);

    [Fact]
    public void A_lane_commits_two_to_the_12_slots_first()
    {
        // review F10: the first chunk was 2^20 slots (16 MiB at 16 B) per thread and arena type
        int len = 0;
        RunOnOtherThread(() =>
        {
            var t = Arena<S16>.Alloc(0, default);
            var slab = typeof(Arena<S16>).GetField("t_slab", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var chunks = (Array)slab.GetType().GetField("Chunks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(slab)!;
            len = ((Array)chunks.GetValue(0)!).Length;
            Assert.Null(chunks.GetValue(1));
            return t;
        });
        Assert.Equal(1 << 12, len);
    }

    record struct Lanes(uint V, uint W);

    [Fact]
    public void An_exited_threads_lane_is_reused_with_its_nodes_intact()
    {
        // review F10: lane ids were never reused, so every short-lived thread kept its slabs for the process' life
        var lanes = new HashSet<int>();
        var first = new List<(Term T, uint V)>();
        for (uint k = 0; k < 24; k++)
        {
            uint v = k;
            var t = RunOnOtherThread(() => Arena<Lanes>.Alloc(1, new Lanes(v, v + 1)));
            lanes.Add(t.Lane);
            first.Add((t, v));
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.True(lanes.Count < 24, $"{lanes.Count} distinct lanes for 24 sequential threads");
        foreach (var (t, v) in first) Assert.Equal(new Lanes(v, v + 1), Arena<Lanes>.Borrow(t));   // nothing freed early
    }

    [Fact]
    public void Tree_23_consuming_sum_has_the_bend_checksum()
    {
        using var region = ArenaRegion.Enter();
        Assert.Equal(4290772992u, ArenaTree.Expected(23));
        Assert.Equal(4290772992u, ArenaTree.Sum(ArenaTree.Gen(23, 1)));
        uint used = Arena<ArenaTree.Pair>.Used;
        Assert.Equal(4290772992u, ArenaTree.SumBorrowed(ArenaTree.Gen(23, 1)));
        Assert.Equal(used, Arena<ArenaTree.Pair>.Used);   // the second tree reused every freed slot
    }

    [Fact]
    public void Parallel_tree_on_lane_arenas_has_the_checksum()
    {
        using var region = ArenaRegion.Enter();
        for (int i = 0; i < 2; i++)
            Assert.Equal(4290772992u, ArenaTree.SumPar(ArenaTree.GenPar(23, 1, 18), 23, 18));
    }

    static long Allocated(Action f)
    {
        for (int i = 0; i < 5; i++) f();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) f();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Steady_state_allocates_zero_bytes()
    {
        Assert.Equal(0, Allocated(() => ArenaTree.Sum(ArenaTree.Gen(14, 1))));
        Assert.Equal(0, Allocated(() =>
        {
            var t = Arena<Big>.Alloc(1, new Big(1, 2, 3));
            _ = Arena<Big>.Borrow(t).B;
            Arena<Big>.Take(t, out _);
        }));
        Assert.Equal(0, Allocated(() =>
        {
            using var r = ArenaRegion.Enter();
            using (ArenaRegion.Enter()) ArenaTree.SumBorrowed(ArenaTree.Gen(10, 1));
        }));
    }

    [Fact]
    public void Arena_types_have_no_static_constructors()
    {
        foreach (var t in new[] { typeof(Term), typeof(ArenaLane), typeof(ArenaRegion), typeof(Arena<ArenaTree.Pair>) })
            Assert.Null(t.TypeInitializer);
    }
}
