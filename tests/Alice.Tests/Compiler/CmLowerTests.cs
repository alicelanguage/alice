using System.Diagnostics;
using System.Reflection;
using Alice.Compiler;
using Alice.Compiler.Backend;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;

namespace Alice.Tests.Compiler;

/// <summary>
/// Connection-Machine shapes (ADR 0039, Lowering/Lowerer.Cm.cs): each inferred shape lowers to its runtime kernel and
/// gives the serial loop's result (every program checks itself against a `while` twin, which is never a CM shape),
/// look-alikes are declined, `cm for` reports AL0247-AL0249, both backends print the same, the results are
/// bit-identical at 1 and 32 workers, and warm calls allocate nothing.
/// </summary>
[Collection("console")]
public class CmLowerTests
{
    static string Cs(string src) => AliceCompiler.Transpile(src);

    static string Run(string src) => AliceCompiler.Run(src).Replace("\r", "");

    static MethodInfo Lib(string src, string name) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cm", exe: false)).GetType("Program")!.GetMethod(name)!;

    // ------------------------------------------------------------------ programs

    /// <summary>Inputs shared by the shape programs: n = 100003 elements (past the 16K inference threshold, odd).</summary>
    const string Inputs = """
        def inputs(n : Int32) : Array(Int32)
          xs = Array(Int32).new(n)
          i = 0
          while i < n
            xs[i] = (i &* 7919) % 1000 - 300
            i += 1
          end
          xs
        end

        """;

    const string ScanProgram = Inputs + """
        def cm_scan(xs : Array(Int32), o : Array(Int32), s : Int32) : Int32
          acc = s
          for i in 0...xs.size
            acc = acc &+ xs[i] &* 3
            o[i] = acc
          end
          acc
        end
        def ref_scan(xs : Array(Int32), o : Array(Int32), s : Int32) : Int32
          acc = s
          i = 0
          while i < xs.size
            acc = acc &+ xs[i] &* 3
            o[i] = acc
            i += 1
          end
          acc
        end
        def lib_scan(xs : Array(Int64), o : Array(Int64), s : Int64) : Int64
          acc = s
          for i in 0...xs.size
            acc = acc &+ xs[i]
            o[i] = acc
          end
          acc
        end
        def ref_lib(xs : Array(Int64), o : Array(Int64), s : Int64) : Int64
          acc = s
          i = 0
          while i < xs.size
            acc = acc &+ xs[i]
            o[i] = acc
            i += 1
          end
          acc
        end
        def ip_scan(xs : Array(Int64)) : Int64
          acc = 0_i64
          for i in 0...xs.size
            acc = acc &+ xs[i]
            xs[i] = acc
          end
          acc
        end
        def cm_excl(xs : Array(UInt32), o : Array(UInt32), s : UInt32) : UInt32
          acc = s
          for i in 0...xs.size
            o[i] = acc
            acc = acc ^ (xs[i] &* 2654435761_u32)
          end
          acc
        end
        def ref_excl(xs : Array(UInt32), o : Array(UInt32), s : UInt32) : UInt32
          acc = s
          i = 0
          while i < xs.size
            o[i] = acc
            acc = acc ^ (xs[i] &* 2654435761_u32)
            i += 1
          end
          acc
        end
        def same(a : Array(Int32), b : Array(Int32)) : Bool
          return false if a.size != b.size
          i = 0
          while i < a.size
            return false if a[i] != b[i]
            i += 1
          end
          true
        end
        n = 100003
        xs = inputs(n)
        a = Array(Int32).new(n)
        b = Array(Int32).new(n)
        puts cm_scan(xs, a, 5) == ref_scan(xs, b, 5) && same(a, b)
        puts cm_scan(xs, a, 0) == ref_scan(xs, b, 0) && same(a, b)
        l = Array(Int64).new(n)
        l2 = Array(Int64).new(n)
        lo = Array(Int64).new(n)
        lo2 = Array(Int64).new(n)
        i = 0
        while i < n
          l[i] = xs[i].to_i64 * 1000003
          l2[i] = l[i]
          i += 1
        end
        puts lib_scan(l, lo, 0_i64) == ref_lib(l, lo2, 0_i64) && lo[n - 1] == lo2[n - 1] && lo[777] == lo2[777]
        puts lib_scan(l, lo, 9_i64) == ref_lib(l, lo2, 9_i64) && lo[n - 1] == lo2[n - 1] && lo[0] == lo2[0]
        # in place: the source is the destination
        puts ip_scan(l) == ref_lib(l2, lo2, 0_i64) && l[n - 1] == lo2[n - 1] && l[4242] == lo2[4242]
        u = Array(UInt32).new(n)
        i = 0
        while i < n
          u[i] = (i &* 31).to_u32
          i += 1
        end
        uo = Array(UInt32).new(n)
        uo2 = Array(UInt32).new(n)
        puts cm_excl(u, uo, 9_u32) == ref_excl(u, uo2, 9_u32) && uo[0] == uo2[0] && uo[n - 1] == uo2[n - 1] && uo[12345] == uo2[12345]
        # short: the serial path
        s1 = Array(Int32).new(10)
        s2 = Array(Int32).new(10)
        puts cm_scan(xs[0...10], s1, 1) == ref_scan(xs[0...10], s2, 1) && same(s1, s2)
        """;

    const string SegmentedProgram = Inputs + """
        def cm_seg(xs : Array(Int64), f : Array(Bool), o : Array(Int64), s : Int64) : Int64
          acc = s
          for i in 0...xs.size
            if f[i]
              acc = 0_i64
            end
            acc = acc &+ xs[i] &* 2_i64
            o[i] = acc
          end
          acc
        end
        def ref_seg(xs : Array(Int64), f : Array(Bool), o : Array(Int64), s : Int64) : Int64
          acc = s
          i = 0
          while i < xs.size
            acc = 0_i64 if f[i]
            acc = acc &+ xs[i] &* 2_i64
            o[i] = acc
            i += 1
          end
          acc
        end
        n = 100003
        xs = inputs(n)
        l = Array(Int64).new(n)
        f = Array(Bool).new(n)
        i = 0
        while i < n
          l[i] = xs[i].to_i64
          f[i] = i % 977 == 0 || i % 1013 == 5
          i += 1
        end
        a = Array(Int64).new(n)
        b = Array(Int64).new(n)
        puts cm_seg(l, f, a, 100_i64) == ref_seg(l, f, b, 100_i64) && a[0] == b[0] && a[n - 1] == b[n - 1] && a[5000] == b[5000] && a[1018] == b[1018]
        f[0] = false
        puts cm_seg(l, f, a, 7_i64) == ref_seg(l, f, b, 7_i64) && a[0] == b[0] && a[3] == b[3]
        """;

    const string CombineProgram = Inputs + """
        def cm_hist(xs : Array(Int32), h : Array(UInt32), w : Array(Int64))
          for i in 0...xs.size
            b = (xs[i] &* 7) & 63
            h[b] = h[b] &+ 1_u32
            w[b] &+= xs[i].to_i64
          end
        end
        def ref_hist(xs : Array(Int32), h : Array(UInt32), w : Array(Int64))
          i = 0
          while i < xs.size
            b = (xs[i] &* 7) & 63
            h[b] = h[b] &+ 1_u32
            w[b] &+= xs[i].to_i64
            i += 1
          end
        end
        def lib_hist(ks : Array(Int32), h : Array(Int32), n : Int32)
          for i in 0...n
            h[ks[i]] &+= 1
          end
        end
        def scatter(ks : Array(Int32), vs : Array(Int64), h : Array(Int64))
          for i in 0...ks.size
            h[ks[i]] = h[ks[i]] &+ vs[i]
          end
        end
        n = 100003
        xs = inputs(n)
        h1 = Array(UInt32).new(64)
        w1 = Array(Int64).new(64)
        h2 = Array(UInt32).new(64)
        w2 = Array(Int64).new(64)
        h1[3] = 11_u32
        h2[3] = 11_u32
        cm_hist(xs, h1, w1)
        ref_hist(xs, h2, w2)
        ok = true
        k = 0
        while k < 64
          ok = false if h1[k] != h2[k] || w1[k] != w2[k]
          k += 1
        end
        puts ok
        ks = Array(Int32).new(n)
        vs = Array(Int64).new(n)
        i = 0
        while i < n
          ks[i] = (xs[i] + 300) % 1000
          vs[i] = i.to_i64 * 3
          i += 1
        end
        g = Array(Int32).new(1000)
        lib_hist(ks, g, n)
        tot = 0
        k = 0
        while k < 1000
          tot += g[k]
          k += 1
        end
        puts tot == n && g[ks[5]] > 0
        s = Array(Int64).new(1000)
        scatter(ks, vs, s)
        st = 0_i64
        k = 0
        while k < 1000
          st += s[k]
          k += 1
        end
        puts st == (n.to_i64 - 1) * n.to_i64 / 2 * 3
        """;

    const string CompactProgram = Inputs + """
        def cm_pack(xs : Array(Int32), o : Array(Int32)) : Int32
          m = 0
          for i in 0...xs.size
            if xs[i] % 3 == 0 && xs[i] > -100
              o[m] = xs[i]
              m += 1
            end
          end
          m
        end
        def ref_pack(xs : Array(Int32), o : Array(Int32)) : Int32
          m = 0
          i = 0
          while i < xs.size
            if xs[i] % 3 == 0 && xs[i] > -100
              o[m] = xs[i]
              m += 1
            end
            i += 1
          end
          m
        end
        def cm_perm(xs : Array(Float64), p : Array(Int32), o : Array(Float64))
          for i in 0...xs.size
            o[p[i]] = xs[i]
          end
        end
        n = 100003
        xs = inputs(n)
        a = Array(Int32).new(n)
        b = Array(Int32).new(n)
        ma = cm_pack(xs, a)
        mb = ref_pack(xs, b)
        same = ma == mb
        i = 0
        while i < n
          same = false if a[i] != b[i]
          i += 1
        end
        puts same && ma > 0
        # out too short: the loop raises where the serial loop raises
        short = Array(Int32).new(10)
        begin
          cm_pack(xs, short)
          puts false
        rescue e : IndexOutOfRangeException
          puts short[9] == b[9]
        end
        f = Array(Float64).new(n)
        p = Array(Int32).new(n)
        i = 0
        while i < n
          f[i] = i.to_f * 0.5
          p[i] = (i &* 7) % n
          i += 1
        end
        o = Array(Float64).new(n)
        cm_perm(f, p, o)
        puts o[p[12]] == f[12] && o[p[n - 1]] == f[n - 1]
        # a duplicate target: the serial loop's result (the last write wins), no new exception
        p[3] = p[4]
        o2 = Array(Float64).new(n)
        cm_perm(f, p, o2)
        puts o2[p[4]] == f[4] && o2[p[3]] == f[4]
        """;

    // ------------------------------------------------------------------ dumps and printing

    [Fact]
    public void Nodes_print_runtime_calls_and_dump()
    {
        static MCode N(string s) => AirLeaf.Name(s);
        var ui = new AirType.Prim(PrimKind.UInt);
        Assert.Equal("global::Alice.Runtime.Cm.InclusiveScan<int>(new global::System.ReadOnlySpan<int>(xs, 0, n), new global::System.Span<int>(o, 0, n), global::Alice.Runtime.CmOp.Add)",
            Printer.E(new AirScan { Op = AirCmOp.Add, Src = N("xs"), Dst = N("o"), Count = N("n") }));
        Assert.Equal("global::Alice.Runtime.Cm.ExclusiveScan<uint>(new global::System.ReadOnlySpan<uint>(o, 0, n), new global::System.Span<uint>(o, 0, n), global::Alice.Runtime.CmOp.Xor)",
            Printer.E(new AirScan { Op = AirCmOp.Xor, Inclusive = false, Src = N("o"), Dst = N("o"), Count = N("n"), Elem = ui }));
        Assert.Equal("global::Alice.Runtime.Cm.Histogram<uint>(new global::System.ReadOnlySpan<int>(k, 0, n), new global::System.Span<uint>(h))",
            Printer.E(new AirCombine { Target = N("h"), Idx = N("k"), Count = N("n"), Elem = ui }));
        Assert.Equal("global::Alice.Runtime.Cm.ScatterAdd<uint>(new global::System.Span<uint>(h), global::System.Runtime.InteropServices.MemoryMarshal.Cast<uint, int>(new global::System.ReadOnlySpan<uint>(k, 0, n)), new global::System.ReadOnlySpan<uint>(v, 0, n))",
            Printer.E(new AirCombine { Target = N("h"), Idx = N("k"), Vals = N("v"), IdxElem = ui, Count = N("n"), Elem = ui }));
        Assert.Equal("global::Alice.Runtime.Cm.SegmentedScan<int>(new global::System.ReadOnlySpan<int>(o, 0, n), new global::System.ReadOnlySpan<bool>(f, 0, n), new global::System.Span<int>(o, 0, n), global::Alice.Runtime.CmOp.Add)",
            Printer.E(new AirSegmented { Src = N("o"), Flags = N("f"), Dst = N("o"), Count = N("n") }));
        Assert.Equal("global::Alice.Runtime.Cm.Compact<int>(xs, new global::System.ReadOnlySpan<bool>(f, 0, n), o)", Printer.E(new AirCompact { Src = N("xs"), Flags = N("f"), Dst = N("o"), Count = N("n") }));
        Assert.Equal("global::Alice.Runtime.Cm.Permute<int>(xs, new global::System.ReadOnlySpan<int>(p, 0, n), o)", Printer.E(new AirPermute { Src = N("xs"), Perm = N("p"), Dst = N("o"), Count = N("n") }));
        Assert.Equal("!global::System.Object.ReferenceEquals(a, b)", Printer.E(new AirCmDistinct { W = N("a"), R = N("b") }));
        Assert.Equal("expr (cm-scan add inclusive int chunked fused (local o) (local o) (local n))\n",
            MirDump.Of([new MExprStmt { E = new AirScan { Op = AirCmOp.Add, Src = N("o"), Dst = N("o"), Count = N("n"), Ordered = false, Fused = true } }]));
        Assert.Equal("expr (cm-combine histogram uint idx int (local h) (local k) (local n))\n", MirDump.Of([new MExprStmt { E = new AirCombine { Target = N("h"), Idx = N("k"), Count = N("n"), Elem = ui } }]));
        Assert.Equal("expr (cm-permute int proof runtime (local xs) (local p) (local o) (local n))\n", MirDump.Of([new MExprStmt { E = new AirPermute { Src = N("xs"), Perm = N("p"), Dst = N("o"), Count = N("n") } }]));
        // analyses see the operands
        var comb = new AirCombine { Target = N("h"), Idx = N("k"), Vals = N("v"), Count = N("n") };
        Assert.Equal(4, MirFacts.Children(comb).Count());
    }

    [Fact]
    public void Each_shape_lowers_to_its_kernel()
    {
        var scan = Cs(ScanProgram);
        Assert.Contains("global::Alice.Runtime.Cm.InclusiveScan<int>(new global::System.ReadOnlySpan<int>(o, 0, ", Fn(scan, "cm_scan"));   // fused: in place
        Assert.Contains("global::Alice.Runtime.Cm.InclusiveScan<long>(new global::System.ReadOnlySpan<long>(xs, 0, ", scan);   // library: straight from xs
        Assert.Contains("global::Alice.Runtime.Cm.ExclusiveScan<uint>(", scan);
        Assert.DoesNotContain("Cm.", Fn(scan, "ref_scan") + Fn(scan, "ref_lib") + Fn(scan, "ref_excl"));
        Assert.Contains("global::Alice.Runtime.Cm.SegmentedScan<long>(", Cs(SegmentedProgram));
        var comb = Cs(CombineProgram);
        Assert.Contains("global::Alice.Runtime.Cm.Histogram<uint>(", Fn(comb, "cm_hist"));
        Assert.Contains("global::Alice.Runtime.Cm.ScatterAdd<long>(", Fn(comb, "cm_hist"));
        Assert.Contains("global::Alice.Runtime.Own<int>.Rent(", Fn(comb, "cm_hist"));   // the shared key, materialized once
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Fn(comb, "cm_hist"), @"Own<int>\.Rent\("));
        Assert.Contains("global::Alice.Runtime.Cm.Histogram<int>(new global::System.ReadOnlySpan<int>(ks, 0, ", Fn(comb, "lib_hist"));
        Assert.Contains("global::Alice.Runtime.Cm.ScatterAdd<long>(new global::System.Span<long>(h), new global::System.ReadOnlySpan<int>(ks, 0, ", Fn(comb, "scatter"));
        var pack = Cs(CompactProgram);
        Assert.Contains("global::Alice.Runtime.Cm.Compact<int>(xs, ", Fn(pack, "cm_pack"));
        Assert.Contains("global::Alice.Runtime.Cm.Permute<double>(xs, ", Fn(pack, "cm_perm"));
        Assert.Contains("catch (global::System.ArgumentException)", Fn(pack, "cm_perm"));
    }

    /// <summary>The C# of one method (from its head to the next method head).</summary>
    static string Fn(string cs, string name)
    {
        int a = cs.IndexOf(" " + name + "(", StringComparison.Ordinal);
        Assert.True(a >= 0, name);
        int b = cs.IndexOf("\npublic static ", a + 1, StringComparison.Ordinal);
        int c = cs.IndexOf("\nprivate static ", a + 1, StringComparison.Ordinal);
        int end = new[] { b, c, cs.Length }.Where(x => x > 0).Min();
        return cs[a..end];
    }

    [Theory]
    [InlineData(nameof(ScanProgram), 7)]
    [InlineData(nameof(SegmentedProgram), 2)]
    [InlineData(nameof(CombineProgram), 3)]
    [InlineData(nameof(CompactProgram), 4)]
    public void Shapes_equal_the_serial_loop(string program, int lines)
    {
        string src = (string)typeof(CmLowerTests).GetField(program, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(string.Concat(Enumerable.Repeat("true\n", lines)), Run(src));
    }

    [Fact]
    public void Explain_lists_the_kernels()
    {
        var comb = Alice.Cli.Commands.Explain.Report(Cs(CombineProgram));
        Assert.Contains("data-parallel histogram/combine: private tables up to 256 KiB merged in order, atomics above", comb["cm_hist"]);
        Assert.DoesNotContain(comb.GetValueOrDefault("ref_hist") ?? [], f => f.StartsWith("data-parallel"));
        var scan = Alice.Cli.Commands.Explain.Report(Cs(ScanProgram));
        Assert.Contains("data-parallel scan (exact)", scan["cm_scan"]);
        var fl = Alice.Cli.Commands.Explain.Report(Cs("def f(xs : Array(Float64), o : Array(Float64)) : Float64\n  acc = 0.0\n  cm for i in 0...xs.size\n    acc += xs[i]\n    o[i] = acc\n  end\n  acc\nend\n"));
        Assert.Contains("data-parallel scan over floats: fixed chunking by n, bit-identical for any core count, not the serial fold", fl["f"]);
        var pack = Alice.Cli.Commands.Explain.Report(Cs(CompactProgram));
        Assert.Contains("data-parallel compaction", pack["cm_pack"]);
        Assert.Contains("data-parallel permutation (checked at run time, the loop on a repeated target)", pack["cm_perm"]);
    }

    // ------------------------------------------------------------------ declined look-alikes

    [Theory]
    [InlineData("checked + may trap", "def f(xs : Array(Int32), o : Array(Int32)) : Int32\n  acc = 0\n  for i in 0...xs.size\n    acc += xs[i]\n    o[i] = acc\n  end\n  acc\nend\n")]
    [InlineData("float scan keeps the serial fold", "def f(xs : Array(Float64), o : Array(Float64)) : Float64\n  acc = 0.0\n  for i in 0...xs.size\n    acc = acc + xs[i]\n    o[i] = acc\n  end\n  acc\nend\n")]
    [InlineData("element reads the destination", "def f(xs : Array(Int32), o : Array(Int32)) : Int32\n  acc = 0\n  for i in 0...xs.size\n    acc = acc &+ o[i &- 1 < 0 ? 0 : i &- 1]\n    o[i] = acc\n  end\n  acc\nend\n")]
    [InlineData("another effect", "def f(xs : Array(Int32), o : Array(Int32)) : Int32\n  acc = 0\n  for i in 0...xs.size\n    acc = acc &+ xs[i]\n    o[i] = acc\n    puts acc\n  end\n  acc\nend\n")]
    [InlineData("combine target read", "def f(ks : Array(Int32), h : Array(Int32))\n  for i in 0...ks.size\n    h[ks[i]] = h[ks[i]] &+ h[0]\n  end\nend\n")]
    [InlineData("checked combine", "def f(ks : Array(Int32), h : Array(Int32))\n  for i in 0...ks.size\n    h[ks[i]] += 1\n  end\nend\n")]
    [InlineData("float combine", "def f(ks : Array(Int32), h : Array(Float64), v : Array(Float64))\n  for i in 0...ks.size\n    h[ks[i]] += v[i]\n  end\nend\n")]
    [InlineData("compaction counter read", "def f(xs : Array(Int32), o : Array(Int32)) : Int32\n  m = 0\n  for i in 0...xs.size\n    if xs[i] > m\n      o[m] = xs[i]\n      m += 1\n    end\n  end\n  m\nend\n")]
    [InlineData("range from 1", "def f(xs : Array(Int32), o : Array(Int32)) : Int32\n  acc = 0\n  for i in 1...xs.size\n    acc = acc &+ xs[i]\n    o[i] = acc\n  end\n  acc\nend\n")]
    public void Look_alikes_stay_serial(string why, string src)
    {
        Assert.False(Cs(src).Contains("Alice.Runtime.Cm."), why);
    }

    // ------------------------------------------------------------------ cm for

    static Diagnostic Error(string src)
    {
        var ex = Assert.Throws<CompileException>(() => Cs(src));
        return ex.Diagnostics[0];
    }

    [Fact]
    public void Cm_for_lowers_floats_and_checked_ints_with_the_chunked_contract()
    {
        var src = "def f(xs : Array(Float64), o : Array(Float64)) : Float64\n  acc = 0.0\n  cm for i in 0...xs.size\n    acc += xs[i]\n    o[i] = acc\n  end\n  acc\nend\n"
            + "def g(ks : Array(Int32), h : Array(UInt32))\n  cm for j in 0...ks.size\n    h[ks[j].clamp(0, 255)] += 1_u32\n  end\nend\n";
        var cs = Cs(src);
        Assert.Contains("global::Alice.Runtime.Cm.InclusiveScan<double>(", cs);
        Assert.Contains("global::Alice.Runtime.Cm.Histogram<uint>(", cs);
        var f = Lib(src, "f");
        var xs = Enumerable.Range(0, 50_000).Select(i => (i % 7) * 0.25).ToArray();   // exact partial sums
        var o = new double[xs.Length];
        Assert.Equal(xs.Sum(), (double)f.Invoke(null, [xs, o])!);
        Assert.Equal(xs.Take(1001).Sum(), o[1000]);
        // small n: `cm for` has no threshold
        var h = new uint[256];
        Lib(src, "g").Invoke(null, [new[] { 1, 300, -4, 1 }, h]);
        Assert.Equal([1u, 2u], h[..2]);
        Assert.Equal(1u, h[255]);
    }

    [Fact]
    public void Cm_ordered_for_runs_a_float_combine_in_index_order()
    {
        var src = "def f(ks : Array(Int32), h : Array(Float64), v : Array(Float64))\n  cm ordered for i in 0...ks.size\n    h[ks[i]] += v[i]\n  end\nend\n";
        Assert.DoesNotContain("Alice.Runtime.Cm.", Cs(src));
        var h = new double[2];
        Lib(src, "f").Invoke(null, [new[] { 0, 1, 0 }, h, new[] { 0.1, 0.2, 0.3 }]);
        Assert.Equal(0.1 + 0.3, h[0]);
    }

    [Fact]
    public void Cm_for_errors()
    {
        var a = Error("def f(a : Array(Int32), n : Int32)\n  cm for i in 0...n\n    a[i] = a[i - 1] + 1\n  end\nend\n");
        Assert.Equal("AL0247", a.Code);
        Assert.Contains("cannot lower `cm for`", a.Message);
        Assert.Equal(3, a.Line);
        var b = Error("def f(ks : Array(Int32), h : Array(Float64), v : Array(Float64))\n  cm for i in 0...ks.size\n    h[ks[i]] += v[i]\n  end\nend\n");
        Assert.Equal("AL0248", b.Code);
        Assert.Contains("ordered", b.Message);
        var c = Error("def f(xs : Array(Int32), p : Array(Int32), o : Array(Int32))\n  cm for i in 0...xs.size\n    o[p[i]] = xs[i]\n  end\nend\n");
        Assert.Equal("AL0249", c.Code);
        Assert.Contains("`p`", c.Message);
        var d = Error("def f(xs : Array(Int32), o : Array(Int32))\n  acc = 0\n  cm for i in 1...xs.size\n    acc = acc &+ xs[i]\n    o[i] = acc\n  end\nend\n");
        Assert.Equal("AL0247", d.Code);
        Assert.Contains("start at 0", d.Message);
    }

    // ------------------------------------------------------------------ backends, workers, allocation

    [Theory]
    [InlineData(nameof(ScanProgram))]
    [InlineData(nameof(SegmentedProgram))]
    [InlineData(nameof(CombineProgram))]
    [InlineData(nameof(CompactProgram))]
    public void Il_backend_prints_what_the_csharp_backend_prints(string program)
    {
        string src = (string)typeof(CmLowerTests).GetField(program, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "cmil", exe: true);
        Assert.True(res.Errors.Count == 0 && res.Pe != null, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        var errors = Alice.Tests.Backends.AirToIlTests.IlVerify(res.Pe!);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
        Assert.Equal(Run(src), AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []).Replace("\r", ""));
    }

    const string WorkersProgram = """
        def fscan(xs : Array(Float64), o : Array(Float64)) : Float64
          acc = 0.0
          cm for i in 0...xs.size
            acc = acc + xs[i] * 1.0000001
            o[i] = acc
          end
          acc
        end
        def hist(xs : Array(Float64), h : Array(Int64))
          cm for i in 0...xs.size
            b = (xs[i] * 1000.0).to_i32 & 255
            h[b] = h[b] &+ (b.to_i64 &* 3)
          end
        end
        n = 3_000_017
        xs = Array(Float64).new(n)
        i = 0
        while i < n
          xs[i] = Math.Sin(i.to_f) * 1.0e3 + 1.0 / (i + 1).to_f
          i += 1
        end
        o = Array(Float64).new(n)
        puts fscan(xs, o)
        puts o[n / 2]
        puts o[n - 7]
        h = Array(Int64).new(256)
        hist(xs, h)
        puts h.sum
        puts h[17]
        """;

    [Fact]
    public void Results_are_bit_identical_at_one_and_thirty_two_workers()
    {
        var image = AliceCompiler.Emit([AliceCompiler.Transpile(WorkersProgram)], "cmw", exe: true);
        var dir = Path.Combine(Path.GetTempPath(), "alice-cm-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "cmw.dll"), image);
            var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
            File.Copy(rt, Path.Combine(dir, Path.GetFileName(rt)), true);
            File.WriteAllText(Path.Combine(dir, "cmw.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + Environment.Version.Major + ".0.0\"}}}");
            string one = Proc(Path.Combine(dir, "cmw.dll"), "1"), many = Proc(Path.Combine(dir, "cmw.dll"), "32");
            Assert.Equal(5, one.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(one, many);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    static string Proc(string dll, string cpus)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        psi.Environment["DOTNET_PROCESSOR_COUNT"] = cpus;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "timeout");
        Assert.True(p.ExitCode == 0, e.Result);
        return o.Result.Replace("\r", "");
    }

    [Fact]
    public void Warm_calls_allocate_nothing()
    {
        // n = 20000: past the CM threshold, below the parallel map's, so the map passes run inline
        var src = """
            def scan(xs : Array(Int32), o : Array(Int32)) : Int32
              acc = 0
              for i in 0...xs.size
                acc = acc &+ xs[i]
                o[i] = acc
              end
              acc
            end
            def fused(xs : Array(Int32), o : Array(Int32)) : Int32
              acc = 3
              for i in 0...xs.size
                acc = acc &+ xs[i] &* xs[i]
                o[i] = acc
              end
              acc
            end
            def hist(xs : Array(Int32), h : Array(Int32)) : Int32
              for i in 0...xs.size
                b = xs[i] & 255
                h[b] = h[b] &+ 1
              end
              h[0]
            end
            def pack(xs : Array(Int32), o : Array(Int32)) : Int32
              m = 0
              for i in 0...xs.size
                if xs[i] % 3 == 0
                  o[m] = xs[i]
                  m += 1
                end
              end
              m
            end
            """;
        var asm = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cma", exe: false));
        var xs = Enumerable.Range(0, 20_000).Select(i => i * 7 % 1000).ToArray();
        var o = new int[xs.Length];
        var h = new int[256];
        foreach (var (name, arr) in new[] { ("scan", o), ("fused", o), ("hist", h), ("pack", o) })
        {
            var f = asm.GetType("Program")!.GetMethod(name)!.CreateDelegate<Func<int[], int[], int>>();
            for (int i = 0; i < 20; i++) f(xs, arr);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) f(xs, arr);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before == 0, name + " allocates");
        }
    }

    [Fact]
    public void Fmt_indents_cm_for_bodies()
    {
        var src = """
            def f(xs : Array(Int32), o : Array(Int32)) : Int32
            acc = 0
            cm for i in 0...xs.size
            acc = acc &+ xs[i]
            o[i] = acc
            end
            cm ordered for i in 0...xs.size
            o[i] = acc
            end
            acc
            end

            """;
        var want = """
            def f(xs : Array(Int32), o : Array(Int32)) : Int32
              acc = 0
              cm for i in 0...xs.size
                acc = acc &+ xs[i]
                o[i] = acc
              end
              cm ordered for i in 0...xs.size
                o[i] = acc
              end
              acc
            end

            """;
        var once = Alice.Compiler.Syntax.Formatter.Format(src.ReplaceLineEndings("\n"));
        Assert.Equal(want.ReplaceLineEndings("\n"), once.ReplaceLineEndings("\n"));
        Assert.Equal(once, Alice.Compiler.Syntax.Formatter.Format(once));
    }
}
