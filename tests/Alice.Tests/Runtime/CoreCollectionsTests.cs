using Alice.Core.Collections;
using Alice.Core.Env;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Collections (Set, Deque, Ring, BitSet) and Alice.Core.Env (Env, Argv, Platform).</summary>
public class CoreCollectionsTests
{
    [Fact]
    public void Set_has_ruby_operators()
    {
        var a = Set<int>.Of(1, 2, 3);
        var b = new Set<int>([3, 4]);
        Assert.Equal(Set<int>.Of(1, 2, 3, 4), a | b);
        Assert.Equal(Set<int>.Of(3), a & b);
        Assert.Equal(Set<int>.Of(1, 2), a - b);
        Assert.Equal(Set<int>.Of(1, 2, 4), a ^ b);
        Assert.True(Set<int>.Of(1, 2) <= a);
        Assert.True(Set<int>.Of(1, 2) < a);
        Assert.False(a < a.Dup());
        Assert.True(a.Include(2));
        Assert.True(a.Add(9));
        Assert.False(a.Add(9));
        _ = a << 10;
        Assert.Equal(5, a.Size);
        Assert.True(a.Delete(10));
        Assert.True(a.IsDisjoint([7, 8]));
        Assert.Equal("#<Set: {5}>", Set<int>.Of(5).ToString());
        int sum = 0;
        foreach (var x in Set<int>.Of(1, 2, 3)) sum += x;
        Assert.Equal(6, sum);
        Assert.Equal(Set<int>.Of(1, 2).GetHashCode(), Set<int>.Of(2, 1).GetHashCode());
    }

    [Fact]
    public void Deque_pushes_and_pops_at_both_ends_across_growth()
    {
        var d = new Deque<int>(4);
        for (int i = 0; i < 10; i++) { d.Push(i); d.Unshift(-i - 1); }
        Assert.Equal(20, d.Count);
        Assert.Equal(-10, d.First);
        Assert.Equal(9, d.Last);
        Assert.Equal(-10, d.Shift());
        Assert.Equal(9, d.Pop());
        Assert.Equal(-9, d[0]);
        Assert.Equal(8, d[d.Count - 1]);
        Assert.Equal(d.ToA(), d.ToList());
        Assert.False(new Deque<string>().TryPop(out _));
        Assert.Throws<InvalidOperationException>(() => new Deque<int>().Shift());
        var q = new Deque<int>();
        for (int round = 0; round < 1000; round++) { q.Push(round); if (round % 3 == 0) q.Shift(); }
        Assert.Equal(1000 - 334, q.Count);
    }

    [Fact]
    public void Ring_overwrites_the_oldest()
    {
        var r = new Ring<int>(3);
        for (int i = 1; i <= 5; i++) r.Push(i);
        Assert.Equal([3, 4, 5], r.ToA());
        Assert.True(r.IsFull);
        Assert.Equal(3, r.Oldest);
        Assert.Equal(5, r.Newest);
        Assert.True(r.Push(6, out int evicted));
        Assert.Equal(3, evicted);
        Assert.Equal([4, 5, 6], r.ToList());
    }

    [Fact]
    public void BitSet_sets_counts_and_combines()
    {
        var a = new BitSet(1000);
        foreach (int i in new[] { 0, 63, 64, 500, 999 }) a.Set(i);
        Assert.Equal(5, a.PopCount());
        Assert.True(a[500]);
        Assert.Equal(63, a.NextSetBit(1));
        Assert.Equal(-1, a.NextSetBit(1000));
        var seen = new List<int>();
        foreach (int i in a) seen.Add(i);
        Assert.Equal([0, 63, 64, 500, 999], seen);
        var b = new BitSet(1000);
        b.SetAll();
        Assert.Equal(1000, b.PopCount());
        b.AndNot(a);
        Assert.Equal(995, b.PopCount());
        b.Or(a);
        Assert.Equal(1000, b.PopCount());
        b.Xor(a);
        Assert.False(b[999]);
        b.And(a);
        Assert.True(b.IsEmpty);
        a.Flip(0);
        Assert.False(a[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => a[1000]);
    }

    [Fact]
    public void Env_reads_and_writes_the_live_environment()
    {
        string name = "ALICE_CORE_ENV_TEST_" + Environment.ProcessId;
        Assert.Null(Env.Get(name));
        Assert.Equal("d", Env.Fetch(name, "d"));
        Assert.Throws<KeyNotFoundException>(() => Env.Fetch(name));
        Env.Set(name, "42");
        Assert.True(Env.HasKey(name));
        Assert.Equal(42, Env.FetchInt(name, 0));
        Assert.Equal("42", Env.ToH()[name]);
        Assert.Equal("42", Env.Delete(name));
        Assert.False(Env.HasKey(name));
        Env.Set(name, "Yes");
        Assert.True(Env.FetchBool(name));
        Env.Set(name, null);
    }

    [Fact]
    public void Platform_reports_the_host()
    {
        Assert.Contains(Platform.Os, new[] { "linux", "windows", "macos", "freebsd", "wasi", "browser", "unknown" });
        Assert.Equal(Environment.ProcessorCount, Platform.Cores);
        Assert.Equal(Environment.ProcessId, Platform.Pid);
        Assert.Equal(Platform.Arch + "-" + Platform.Os, Platform.Triple);
        Assert.Equal(Alice.Runtime.Prelude.ARGV.Length, Argv.Count);
        Assert.Null(Argv.At(10_000));
    }
}

/// <summary>Steady-state collection operations allocate nothing.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class CoreCollectionsAllocationTests
{
    [Fact]
    public void Deque_ring_and_bitset_steady_state_allocate_nothing()
    {
        var d = new Deque<int>(64);
        var r = new Ring<long>(16);
        var a = new BitSet(4096);
        var b = new BitSet(4096);
        long sink = 0;
        void Body()
        {
            for (int i = 0; i < 1000; i++)
            {
                d.Push(i); d.Unshift(i);
                sink += d.Pop() + d.Shift();
                r.Push(i);
                a.Set(i);
                b.Or(a);
                sink += b.NextSetBit(i);
            }
            foreach (var x in r) sink += x;
            foreach (int i in a) sink += i;
            sink += a.PopCount();
        }
        Body();
        Assert.Equal(0, AllocationProbe.Least(Body));
        Assert.NotEqual(0, sink);
    }
}
