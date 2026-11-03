using System.Numerics;
using System.Runtime.InteropServices;
using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>Connection-Machine primitives (ADR 0039): exact vs serial folds for integers, bit-identical floats for any
/// worker count, zero steady-state allocation, edge sizes around the chunk length.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public unsafe class CmTests
{
    static readonly int[] Sizes = [0, 1, 2, 3, 7, 15, 16, 17, 31, 63, 64, 65, 100, 4095, 4096, 4097, 8191, 12_345, 65_536 * 3 + 5, (1 << 20) + 7];
    static readonly CmOp[] AllOps = [CmOp.Add, CmOp.Mul, CmOp.And, CmOp.Or, CmOp.Xor, CmOp.Min, CmOp.Max];

    static T Apply<T>(CmOp op, T a, T b) where T : IBinaryNumber<T> => op switch
    {
        CmOp.Add => a + b,
        CmOp.Mul => a * b,
        CmOp.And => a & b,
        CmOp.Or => a | b,
        CmOp.Xor => a ^ b,
        CmOp.Min => T.Min(a, b),
        _ => T.Max(a, b),
    };

    static T[] Random<T>(int n, int seed) where T : INumber<T>
    {
        var r = new System.Random(seed);
        var a = new T[n];
        for (int i = 0; i < n; i++)
            a[i] = typeof(T) == typeof(float) || typeof(T) == typeof(double)
                ? T.CreateTruncating(r.NextDouble() * 2 - 1)
                : T.CreateTruncating(r.NextInt64(-1000, 1000) | 1);   // odd: Mul never collapses to 0
        return a;
    }

    static T[] SerialScan<T>(T[] a, CmOp op, bool exclusive, T id) where T : IBinaryNumber<T>
    {
        var r = new T[a.Length];
        T acc = id;
        for (int i = 0; i < a.Length; i++)
        {
            T next = i == 0 ? a[0] : Apply(op, acc, a[i]);
            r[i] = exclusive ? acc : next;
            acc = next;
        }
        return r;
    }

    static T Identity<T>(CmOp op) where T : IBinaryNumber<T> => op switch
    {
        CmOp.Mul => T.One,
        CmOp.And => T.AllBitsSet,
        CmOp.Min => T.CreateSaturating(double.PositiveInfinity),
        CmOp.Max => T.CreateSaturating(double.NegativeInfinity),
        _ => T.Zero,
    };

    static void IntegerScans<T>() where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        foreach (int n in Sizes)
        {
            var a = Random<T>(n, n + 1);
            foreach (var op in AllOps)
            {
                var inc = new T[n];
                Cm.InclusiveScan<T>(a, inc, op);
                Assert.True(SerialScan(a, op, false, Identity<T>(op)).AsSpan().SequenceEqual(inc), $"{typeof(T).Name} {op} inclusive n={n}");
                var exc = new T[n];
                Cm.ExclusiveScan<T>(a, exc, op);
                Assert.True(SerialScan(a, op, true, Identity<T>(op)).AsSpan().SequenceEqual(exc), $"{typeof(T).Name} {op} exclusive n={n}");
                T fold = Identity<T>(op);
                foreach (var x in a) fold = Apply(op, fold, x);
                Assert.Equal(fold, Cm.Reduce<T>(a, op));
                var inPlace = (T[])a.Clone();
                Cm.InclusiveScan<T>(inPlace, inPlace, op);
                Assert.True(inc.AsSpan().SequenceEqual(inPlace), $"{typeof(T).Name} {op} in place n={n}");
            }
        }
    }

    [Fact] public void Int32_scans_match_serial_folds() => IntegerScans<int>();
    [Fact] public void Int64_scans_match_serial_folds() => IntegerScans<long>();
    [Fact] public void UInt32_scans_match_serial_folds() => IntegerScans<uint>();
    [Fact] public void UInt64_scans_match_serial_folds() => IntegerScans<ulong>();

    static void FloatScans<T>() where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>, IFloatingPointIeee754<T>
    {
        foreach (int n in Sizes)
        {
            var a = Random<T>(n, n + 7);
            foreach (var op in new[] { CmOp.Add, CmOp.Min, CmOp.Max, CmOp.Mul })
            {
                var r = new T[n];
                Cm.InclusiveScan<T>(a, r, op);
                var s = SerialScan(a, op, false, Identity<T>(op));
                if (op is CmOp.Min or CmOp.Max) Assert.True(s.AsSpan().SequenceEqual(r), $"{op} n={n}");   // exactly associative
                else if (op == CmOp.Add)
                {
                    double exact = 0, mag = 0, eps = sizeof(T) == 4 ? 1e-4 : 1e-12;
                    for (int i = 0; i < n; i++)
                    {
                        exact += double.CreateTruncating(a[i]); mag += Math.Abs(double.CreateTruncating(a[i]));
                        Assert.True(Math.Abs(double.CreateTruncating(r[i]) - exact) <= eps * (1 + mag), $"{typeof(T).Name} add n={n} i={i}");
                    }
                }
                Assert.Equal(Bits(r), Bits(Forced1(() => { var q = new T[n]; Cm.InclusiveScan<T>(a, q, op); return q; })));
            }
            var e = new T[n];
            Cm.ExclusiveScan<T>(a, e, CmOp.Add);
            if (n > 0) Assert.True(T.IsPositive(e[0]) && T.IsZero(e[0]), "exclusive sum starts at +0");
            Assert.Equal(Bits(e), Bits(Forced1(() => { var q = new T[n]; Cm.ExclusiveScan<T>(a, q, CmOp.Add); return q; })));
            Assert.Equal(Bits([Cm.Reduce<T>(a)]), Bits(Forced1(() => new[] { Cm.Reduce<T>(a) })));
        }
    }

    static void IntegerValuedFloatSums<T>() where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        foreach (int n in Sizes)
        {
            var r = new System.Random(n);
            var a = new T[n];
            for (int i = 0; i < n; i++) a[i] = T.CreateTruncating(r.Next(-8, 9));   // every partial sum is exact
            var got = new T[n];
            Cm.InclusiveScan<T>(a, got);
            Assert.True(SerialScan(a, CmOp.Add, false, T.Zero).AsSpan().SequenceEqual(got), $"{typeof(T).Name} n={n}");
            Cm.ExclusiveScan<T>(a, got);
            Assert.True(SerialScan(a, CmOp.Add, true, T.Zero).AsSpan().SequenceEqual(got), $"{typeof(T).Name} exclusive n={n}");
        }
    }

    [Fact] public void Integer_valued_float_sums_are_exact() { IntegerValuedFloatSums<float>(); IntegerValuedFloatSums<double>(); }

    [Fact] public void Float32_scans_are_close_to_serial_and_bit_identical_at_one_worker() => FloatScans<float>();
    [Fact] public void Float64_scans_are_close_to_serial_and_bit_identical_at_one_worker() => FloatScans<double>();

    [Fact]
    public void Bitwise_ops_on_floats_are_rejected() =>
        Assert.Throws<ArgumentException>(() => Cm.InclusiveScan<float>(new float[4], new float[4], CmOp.Xor));

    /// <summary>Runs <paramref name="f"/> as the single-worker path does: from inside a pool job, every KernelPool.Run
    /// is serial on the calling thread (the same code path as DOTNET_PROCESSOR_COUNT=1).</summary>
    static T Forced1<T>(Func<T> f)
    {
        Assert.True(KernelPool.Threads > 1, "needs a multi-core host to compare against the serial path");
        var box = new ForcedBox<T> { F = f };
        var h = GCHandle.Alloc(box);
        try
        {
            KernelPool.Run(2, 1, &ForcedRun<T>, (void*)GCHandle.ToIntPtr(h));
            if (box.Error != null) throw box.Error;
            return box.Result!;
        }
        finally { h.Free(); }
    }

    sealed class ForcedBox<T> { public Func<T> F = null!; public T? Result; public Exception? Error; }

    static void ForcedRun<T>(void* p, int lo, int hi)
    {
        if (lo != 0) return;
        var box = (ForcedBox<T>)GCHandle.FromIntPtr((nint)p).Target!;
        try { box.Result = box.F(); } catch (Exception e) { box.Error = e; }
    }

    static ulong[] Bits<T>(T[] a) where T : unmanaged
    {
        var r = new ulong[a.Length];
        for (int i = 0; i < a.Length; i++)
            r[i] = sizeof(T) == 4 ? Unsafe32(a[i]) : MemoryMarshal.Read<ulong>(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a[i])));
        return r;
    }

    static ulong Unsafe32<T>(T x) where T : unmanaged => MemoryMarshal.Read<uint>(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in x)));

    // ---------------- segmented ----------------

    static bool[] Flags(int n, int seed, int every)
    {
        var r = new System.Random(seed);
        var f = new bool[n];
        for (int i = 0; i < n; i++) f[i] = every switch { 0 => false, 1 => true, _ => r.Next(every) == 0 };
        return f;
    }

    [Fact]
    public void Segmented_scan_and_reduce_match_serial()
    {
        foreach (int n in Sizes)
            foreach (int every in new[] { 0, 1, 3, 1000, 200_000 })
                foreach (var op in new[] { CmOp.Add, CmOp.Max, CmOp.Xor })
                {
                    var a = Random<long>(n, n);
                    var f = Flags(n, n + every, every);
                    var scan = new long[n];
                    var segs = new List<long>();
                    long acc = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (i == 0 || f[i]) { if (i > 0) segs.Add(acc); acc = a[i]; }
                        else acc = Apply(op, acc, a[i]);
                        scan[i] = acc;
                    }
                    if (n > 0) segs.Add(acc);
                    var got = new long[n];
                    Cm.SegmentedScan<long>(a, f, got, op);
                    Assert.Equal(scan, got);
                    Assert.Equal(segs.Count, Cm.SegmentCount(f));
                    var res = new long[segs.Count];
                    Assert.Equal(segs.Count, Cm.SegmentedReduce<long>(a, f, res, op));
                    Assert.Equal(segs.ToArray(), res);
                }
    }

    [Fact]
    public void Segmented_float_sums_are_bit_identical_at_one_worker()
    {
        int n = 3_000_017;
        var a = Random<double>(n, 5);
        var f = Flags(n, 6, 50_000);
        double[] Run() { var r = new double[Cm.SegmentCount(f)]; Cm.SegmentedReduce<double>(a, f, r); return r; }
        Assert.Equal(Bits(Run()), Bits(Forced1(Run)));
        double[] RunScan() { var r = new double[n]; Cm.SegmentedScan<double>(a, f, r); return r; }
        Assert.Equal(Bits(RunScan()), Bits(Forced1(RunScan)));
    }

    [Fact]
    public void Segmented_reduce_rejects_short_results() =>
        Assert.Throws<ArgumentException>(() => Cm.SegmentedReduce<int>(new int[4], new[] { false, true, true, false }, new int[2]));

    // ---------------- combine ----------------

    static int[] Keys(int n, int k, int seed)
    {
        var r = new System.Random(seed);
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = r.Next(k);
        return a;
    }

    [Fact]
    public void Histogram_matches_serial_on_every_path()
    {
        foreach (int k in new[] { 1, 7, 256, 65_536, 1 << 20 })   // 1 << 20 Int32 bins = 4 MiB: atomic path
            foreach (int n in new[] { 0, 1, 1000, 40_000, 2_000_003 })
            {
                var keys = Keys(n, k, n ^ k);
                var want = new int[k];
                foreach (var x in keys) want[x]++;
                var got = new int[k];
                Cm.Histogram<int>(keys, got);
                Assert.Equal(want, got);
                var wide = new long[k];
                Cm.Histogram<long>(MemoryMarshal.Cast<int, uint>(keys), wide);
                Assert.Equal(want.Select(x => (long)x), wide);
            }
    }

    [Fact]
    public void Histogram_accumulates_and_rejects_out_of_range_keys()
    {
        var bins = new int[4] { 1, 1, 1, 1 };
        Cm.Histogram<int>(new[] { 0, 3 }, bins);
        Assert.Equal(new[] { 2, 1, 1, 2 }, bins);
        var big = Keys(1_000_000, 100, 1);
        big[777_777] = 100;
        var b2 = new int[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => Cm.Histogram<int>(big, b2));
        Assert.All(b2, x => Assert.Equal(0, x));   // private-table path: untouched on error
        big[777_777] = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => Cm.Histogram<int>(big, b2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cm.Histogram<int>(new[] { 5 }, new int[2]));
    }

    [Fact]
    public void Scatter_add_is_exact_for_integers_and_bit_identical_for_floats()
    {
        int n = 3_000_000;
        foreach (int k in new[] { 10, 5000, 1 << 18 })   // 1 << 18 doubles = 2 MiB: ordered serial path for floats
        {
            var idx = Keys(n, k, k);
            var lv = Random<long>(n, 2);
            var want = new long[k];
            for (int i = 0; i < n; i++) want[idx[i]] += lv[i];
            var got = new long[k];
            Cm.ScatterAdd<long>(got, idx, lv);
            Assert.Equal(want, got);

            var dv = Random<double>(n, 3);
            double[] Run() { var t = new double[k]; Cm.ScatterAdd<double>(t, idx, dv); return t; }
            var d1 = Run();
            Assert.Equal(Bits(d1), Bits(Forced1(Run)));
            var serial = new double[k];
            for (int i = 0; i < n; i++) serial[idx[i]] += dv[i];
            for (int j = 0; j < k; j++) Assert.True(Math.Abs(serial[j] - d1[j]) < 1e-6, $"k={k} j={j}");
            if (k == 1 << 18) Assert.Equal(Bits(serial), Bits(d1));
        }
    }

    // ---------------- compact / permute / sort ----------------

    [Fact]
    public void Compact_matches_where()
    {
        foreach (int n in Sizes)
        {
            var a = Random<int>(n, n + 3);
            var f = a.Select(x => x % 3 == 0).ToArray();
            var want = a.Where(x => x % 3 == 0).ToArray();
            var dst = new int[n];
            int m = Cm.Compact(a, f, dst);
            Assert.Equal(want, dst[..m]);
            Assert.Equal(want, Cm.Compact(a, x => x % 3 == 0));
            var s = a.Select(x => x.ToString()).ToArray();
            Assert.Equal(s.Where(x => x.Length > 3), Cm.Compact(s, x => x.Length > 3));
        }
        Assert.Throws<ArgumentException>(() => Cm.Compact(new[] { 1, 2 }, new[] { true, true }, new int[1]));
        Assert.Throws<InvalidOperationException>(() => Cm.Compact(Enumerable.Range(0, 100_000).ToArray(), x => x == 50_000 ? throw new InvalidOperationException() : true));
    }

    [Fact]
    public void Permute_scatters_and_checks()
    {
        foreach (int n in Sizes)
        {
            var perm = Enumerable.Range(0, n).ToArray();
            new System.Random(n).Shuffle(perm);
            var a = Enumerable.Range(0, n).Select(i => "v" + i).ToArray();
            var dst = new string[n];
            Cm.Permute(a, perm, dst);
            for (int i = 0; i < n; i++) Assert.Equal(a[i], dst[perm[i]]);
        }
        var p = Enumerable.Range(0, 100_000).ToArray();
        p[99_999] = 5;
        Assert.Throws<ArgumentException>(() => Cm.Permute(new int[100_000], p, new int[100_000]));
        p[99_999] = 100_000;
        Assert.Throws<ArgumentOutOfRangeException>(() => Cm.Permute(new int[100_000], p, new int[100_000]));
    }

    [Fact]
    public void Sort_by_key_is_a_stable_radix_sort()
    {
        foreach (int n in Sizes.Append(3_000_001))
            foreach (uint mask in new[] { 0xFFFF_FFFFu, 0xFFu, 0xFF00u, 0u })
            {
                var r = new System.Random(n);
                var keys = new uint[n];
                for (int i = 0; i < n; i++) keys[i] = (uint)r.NextInt64() & mask;
                var vals = Enumerable.Range(0, n).ToArray();
                var want = Enumerable.Range(0, n).OrderBy(i => keys[i]).ToArray();   // stable
                var k = (uint[])keys.Clone();
                Cm.SortByKey<int>(k, vals);
                Assert.Equal(want, vals);
                Assert.Equal(want.Select(i => keys[i]), k);
            }
    }

    // ---------------- Prelude surface ----------------

    [Fact]
    public void Prelude_surface()
    {
        Assert.Equal(new[] { 1, 3, 6 }, new[] { 1, 2, 3 }.prefix_sum());
        Assert.Empty(Array.Empty<long>().prefix_sum());
        Assert.Equal(new[] { 3, 3, 4, 4 }, new[] { 3, 1, 4, 1 }.scan((a, b) => Math.Max(a, b)));
        Assert.Equal(Enumerable.Range(1, 200_000).Select(i => (long)i * (i + 1) / 2), Enumerable.Range(1, 200_000).Select(i => (long)i).ToArray().scan((a, b) => a + b));
        Assert.Equal(new[] { 3, 7 }, new[] { 1, 2, 3, 4 }.segmented_sum(new[] { true, false, true, false }));
        Assert.Equal(new[] { 1, 3, 3, 7 }, new[] { 1, 2, 3, 4 }.segmented_prefix_sum(new[] { true, false, true, false }));
        Assert.Equal(new[] { 1, 0, 2 }, new[] { 0, 2, 2 }.histogram(3));
        Assert.Equal(new[] { 1, 0, 2 }, new[] { 0u, 2u, 2u }.histogram(3));
        Assert.Equal(new[] { 2, 4 }, new[] { 1, 2, 3, 4 }.compact(x => x % 2 == 0));
        var t = new double[3];
        Assert.Same(t, Prelude.scatter_add(t, new[] { 0, 2, 2 }, new[] { 1.5, 2.0, 3.0 }));
        Assert.Equal(new[] { 1.5, 0, 5.0 }, t);
        Assert.Equal(new[] { "c", "a", "b" }, new[] { "a", "b", "c" }.permute(new[] { 1, 2, 0 }));
        var (k, v) = new[] { 3u, 1u, 2u, 1u }.sort_by_key(new[] { "d", "a", "c", "b" });
        Assert.Equal(new[] { 1u, 1u, 2u, 3u }, k);
        Assert.Equal(new[] { "a", "b", "c", "d" }, v);
        var (ks, vs) = new[] { 5, -3, 0, -3, int.MinValue }.sort_by_key(new[] { 0, 1, 2, 3, 4 });
        Assert.Equal(new[] { int.MinValue, -3, -3, 0, 5 }, ks);
        Assert.Equal(new[] { 4, 1, 3, 2, 0 }, vs);
    }

    // ---------------- allocation ----------------

    static long Allocated(Action f)
    {
        for (int i = 0; i < 5; i++) f();
        return AllocationProbe.Least(() => { for (int i = 0; i < 20; i++) f(); });
    }

    [Fact]
    public void Kernels_allocate_zero_bytes()
    {
        int n = 1 << 20;
        var a = Random<int>(n, 1);
        var d = new int[n];
        var fl = Random<double>(n, 2);
        var fd = new double[n];
        var flags = Flags(n, 3, 100);
        var segs = new long[Cm.SegmentCount(flags)];
        var la = Random<long>(n, 4);
        var keys = Keys(n, 65_536, 5);
        var bins = new int[65_536];
        var bigBins = new int[1 << 20];
        var dv = new double[1000];
        var idx = Keys(n, 1000, 6);
        var perm = Enumerable.Range(0, n).ToArray();
        new System.Random(7).Shuffle(perm);
        var uk = new uint[n];
        var vals = new int[n];
        var strs = Enumerable.Range(0, 10_000).Select(i => i.ToString()).ToArray();
        var sdst = new string[strs.Length];
        Func<string, bool> keep = s => s.Length > 3;
        Func<int, int, int> max = Math.Max;

        Assert.Equal(0, Allocated(() => Cm.InclusiveScan<int>(a, d)));
        Assert.Equal(0, Allocated(() => Cm.ExclusiveScan<double>(fl, fd, CmOp.Max)));
        Assert.Equal(0, Allocated(() => Cm.Reduce<double>(fl)));
        Assert.Equal(0, Allocated(() => Cm.SegmentedScan<double>(fl, flags, fd)));
        Assert.Equal(0, Allocated(() => Cm.SegmentedReduce<long>(la, flags, segs)));
        Assert.Equal(0, Allocated(() => Cm.Histogram<int>(keys, bins)));
        Assert.Equal(0, Allocated(() => Cm.Histogram<int>(keys, bigBins)));
        Assert.Equal(0, Allocated(() => Cm.ScatterAdd<double>(dv, idx, fl)));
        Assert.Equal(0, Allocated(() => Cm.Compact(a, flags, d)));
        Assert.Equal(0, Allocated(() => Cm.Compact(strs, keep, sdst)));
        Assert.Equal(0, Allocated(() => Cm.Permute(a, perm, d)));
        Assert.Equal(0, Allocated(() => { for (int i = 0; i < 1000; i++) uk[i] = (uint)(i * 2654435761u); Cm.SortByKey<int>(uk, vals); }));
        Assert.True(Allocated(() => Cm.InclusiveScan(d, max, d)) <= 20 * (256 * 4 + 64));   // block form: only its chunk-carry array
    }
}
