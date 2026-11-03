using System.Reflection;
using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Backend.Il.Opt;
using Xunit.Abstractions;

namespace Alice.Tests.Backends;

/// <summary>
/// The IL-side optimizer (il-opt: Backend/Il/Opt, Air/Passes/IlOpt). Shape tests on the emitted image (method flags,
/// instruction listing) plus differential runs against the C# backend: every rewrite keeps the output identical.
/// </summary>
[Collection("console")]
public class IlOptTests(ITestOutputHelper output)
{
    static IlCompileResult Il(string src, string name = "ilopt")
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], name, exe: true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        return res;
    }

    static string RunCs(string src, params string[] args) =>
        AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), args);

    static MethodInfo Method(Assembly a, string name) =>
        a.GetType("Program")!.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    void Same(string src, byte[] pe, params string[] args)
    {
        string il = AliceCompiler.Invoke(AliceCompiler.Load(pe), args);
        Assert.Equal(RunCs(src, args), il);
        output.WriteLine(il);
    }

    const string Kernel = """
        def sq(x : Float32) : Float32
          x * x + 1.0_f32
        end

        def hit(sp : Array(Float32), i : Int32, ox : Float32) : Float32
          k = i * 2
          d = ox - sp[k]
          return 1000.0_f32 if d < 0.0_f32
          sq(d) - sp[k + 1]
        end

        def scan(sp : Array(Float32), ox : Float32) : Float32
          best = 1000.0_f32
          i = 0
          while i < 3
            t = hit(sp, i, ox)
            best = t if t < best
            i += 1
          end
          best
        end

        sp = [0.5_f32, 1.0_f32, 2.0_f32, 0.25_f32, -1.0_f32, 3.0_f32]
        acc = 0.0_f32
        j = 0
        while j < 1000
          acc = acc + scan(sp, j.to_f32 * 0.01_f32)
          j += 1
        end
        puts acc
        """;

    [Fact]
    public void Small_loop_free_methods_get_AggressiveInlining_and_loops_do_not()
    {
        var res = Il(Kernel);
        var asm = AliceCompiler.Load(res.Pe!);
        Assert.True(Method(asm, "sq").MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining));
        Assert.True(Method(asm, "hit").MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining));
        Assert.False(Method(asm, "scan").MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining));
        Same(Kernel, res.Pe!);
    }

    static HashSet<string> Flagged(Assembly a, MethodImplAttributes f) =>
        [.. a.GetType("Program")!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.MethodImplementationFlags.HasFlag(f)).Select(m => m.Name)];

    [Fact]
    public void AggressiveOptimization_follows_the_hot_policy_unless_noao()
    {
        var saved = IlOpt.Enabled;
        try
        {
            IlOpt.Enabled = IlOptPass.None;
            var plain = Flagged(AliceCompiler.Load(Il(Kernel, "ilopt_ao0").Pe!), MethodImplAttributes.AggressiveOptimization);
            IlOpt.Enabled = IlOptPass.All;
            Assert.Equal(plain, Flagged(AliceCompiler.Load(Il(Kernel, "ilopt_ao1").Pe!), MethodImplAttributes.AggressiveOptimization));
            IlOpt.Enabled = IlOptPass.All | IlOptPass.NoAo;
            Assert.Empty(Flagged(AliceCompiler.Load(Il(Kernel, "ilopt_ao2").Pe!), MethodImplAttributes.AggressiveOptimization));
            // leaves only: loop-free, non-recursive bodies lose it, loops keep it
            IlOpt.Enabled = IlOptPass.All | IlOptPass.NoAoLeaves;
            var leaves = Flagged(AliceCompiler.Load(Il(Kernel, "ilopt_ao3").Pe!), MethodImplAttributes.AggressiveOptimization);
            Assert.DoesNotContain("sq", leaves);
            Assert.DoesNotContain("hit", leaves);
            Assert.True(leaves.IsSubsetOf(plain));
        }
        finally { IlOpt.Enabled = saved; }
    }

    const string HelperLoop = """
        def step(x : UInt32) : UInt32
          x ^ (x << 13)
        end

        def spin(n : UInt32) : UInt32
          acc = 1_u32
          while n != 0_u32
            acc = step(acc)
            n = n &- 1_u32
          end
          acc
        end

        puts spin(1000_u32)
        """;

    [Fact]
    public void AoLoops_adds_AggressiveOptimization_to_loop_bodies_only()
    {
        var saved = IlOpt.Enabled;
        try
        {
            IlOpt.Enabled = IlOptPass.All;
            var plain = Flagged(AliceCompiler.Load(Il(HelperLoop, "ilopt_aol0").Pe!), MethodImplAttributes.AggressiveOptimization);
            Assert.DoesNotContain("spin", plain);   // a primitive loop that only calls helpers: tiered (perf2 policy)
            IlOpt.Enabled = IlOptPass.All | IlOptPass.AoLoops;
            var res = Il(HelperLoop, "ilopt_aol1");
            var ao = Flagged(AliceCompiler.Load(res.Pe!), MethodImplAttributes.AggressiveOptimization);
            Assert.Contains("spin", ao);
            Assert.DoesNotContain("step", ao.Except(plain));
            Assert.DoesNotContain("Main", ao);
            Same(HelperLoop, res.Pe!);
        }
        finally { IlOpt.Enabled = saved; }
    }

    const string Forks = """
        def leaf(x : UInt32) : UInt32
          x ^ (x << 13)
        end

        def tree(d : Int32, i : UInt32) : UInt32
          return leaf(i) if d == 0
          a, b = tree(d - 1, i), tree(d - 1, i &+ (1_u32 << (d - 1)))
          a &+ b
        end

        def chain(d : Int32, acc : UInt32) : UInt32
          return acc if d == 0
          chain(d - 1, leaf(acc))
        end

        puts tree(6, 1_u32) &+ chain(10, 3_u32)
        """;

    [Fact]
    public void Fork_kernels_take_the_parallel_form_and_are_not_forced_inline()
    {
        // tree recursion lowers to a fork dispatcher plus a tiered `__serial` clone, as in the C# backend, so the
        // kernel rule's [AggressiveOptimization] stays on the single-self-call `chain` only
        var res = Il(Forks, "ilopt_fk");
        var asm = AliceCompiler.Load(res.Pe!);
        var ao = Flagged(asm, MethodImplAttributes.AggressiveOptimization);
        Assert.Contains("chain", ao);
        Assert.DoesNotContain("tree", ao);
        Assert.DoesNotContain("tree__serial", ao);
        var inl = Flagged(asm, MethodImplAttributes.AggressiveInlining);
        Assert.Contains("leaf", inl);
        Assert.DoesNotContain("tree", inl);
        Assert.DoesNotContain(inl, n => n.StartsWith("__fork_", StringComparison.Ordinal));
        Same(Forks, res.Pe!);
    }

    [Fact]
    public void Methods_called_from_many_sites_are_not_forced_inline()
    {
        const string src = """
            def mixw(x : UInt32) : UInt32
              b = x ^ (x << 13)
              b ^ (b >> 17)
            end
            def two(x : UInt32) : UInt32
              b = x &* 3_u32
              b ^ (b >> 7)
            end
            a = mixw(1_u32) &+ mixw(2_u32) &+ mixw(3_u32) &+ mixw(4_u32)
            puts a &+ two(5_u32) &+ two(6_u32)
            """;
        var res = Il(src, "ilopt_sites");
        var ai = Flagged(AliceCompiler.Load(res.Pe!), MethodImplAttributes.AggressiveInlining);
        Assert.DoesNotContain("mixw", ai);
        Assert.Contains("two", ai);
        Same(src, res.Pe!);
    }

    [Fact]
    public void Recursive_and_switched_off_methods_are_not_inlined()
    {
        const string src = "def fib(n : Int32) : Int32\n  n < 2 ? n : fib(n - 1) + fib(n - 2)\nend\nputs fib(15)\n";
        var asm = AliceCompiler.Load(Il(src).Pe!);
        Assert.False(Method(asm, "fib").MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining));
        var saved = IlOpt.Enabled;
        try
        {
            IlOpt.Enabled = IlOptPass.None;
            var off = AliceCompiler.Load(Il(Kernel, "ilopt_off").Pe!);
            Assert.False(Method(off, "sq").MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining));
        }
        finally { IlOpt.Enabled = saved; }
    }

    [Fact]
    public void Body_facts_see_back_edges_regions_and_calls()
    {
        var f = BodyFacts.Parse("    .locals ([0] int32 i)\n  L0:\n    ldloc 0\n    call Program::f\n    brtrue L0\n    ret\n");
        Assert.True(f.Loop);
        Assert.Equal(4, f.Instructions);
        Assert.Contains("Program::f", f.Calls);
        var g = BodyFacts.Parse("    // .try L0 to L1 finally handler L1 to L2\n    br L3\n  L3:\n    ret\n");
        Assert.True(g.Eh);
        Assert.False(g.Loop);
    }

    const string Sum = """
        enum E
          V
          L(v : String)
          A(a : E, b : E)
        end

        def ev(e : E, x : Int32) : Int32
          case e
          when E::V then x
          when E::L(v) then v.size
          when E::A(a, b) then ev(a, x) + ev(b, x)
          end
        end

        t = E::A.new(E::L.new("abc"), E::A.new(E::V.new, E::L.new("ab")))
        puts ev(t, 5)
        n = 0
        q = 0
        while n < 10
          q += 1 if n > 3
          q += 2 if n == 0
          n += 1
        end
        puts q
        """;

    static string Listing(IlCompileResult r) => IlDisassembler.Disassemble(r.Module!);

    static List<string> Ops(string listing) => listing.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//") && !l.EndsWith(':'))
        .Select(l => l.Split(' ')[0]).ToList();

    /// <summary>The listing of the first method whose header contains <paramref name="sig"/>.</summary>
    static string MethodListing(string listing, string sig) =>
        listing.Split(".method ").First(m => m.Split('\n')[0].Contains(sig, StringComparison.Ordinal));

    static bool HasRun(List<string> ops, params string[] run)
    {
        for (int i = 0; i + run.Length <= ops.Count; i++)
            if (run.Select((r, k) => ops[i + k] == r).All(x => x)) return true;
        return false;
    }

    [Fact]
    public void Type_tests_branch_on_the_reference_and_constant_arms_vanish()
    {
        var res = Il(Sum);
        // the case arms of `ev` (record members such as `__With` keep their needed Clone downcast)
        var ops = Ops(MethodListing(Listing(res), " ev("));
        // Roslyn's `e is T p`: `isinst T; stloc p; ldloc p; brfalse` (one method-table compare in RyuJIT), no castclass
        // re-check after it, and not the `dup; stloc` form (measured 24-50 % slower, IlBuilder.Opt docs)
        Assert.True(HasRun(ops, "isinst", "stloc", "ldloc", "brfalse"), Listing(res));
        Assert.DoesNotContain("castclass", ops);
        Assert.False(HasRun(ops, "isinst", "ldnull", "cgt.un"));
        Assert.False(HasRun(ops, "isinst", "dup", "stloc"));
        Assert.False(HasRun(ops, "ldc.i4", "brfalse"));
        Same(Sum, res.Pe!);
        AirToIlVerify(res.Pe!);
    }

    [Fact]
    public void Peepholes_switch_off_with_the_flag()
    {
        var saved = IlOpt.Enabled;
        try
        {
            IlOpt.Enabled = IlOptPass.All & ~IlOptPass.Peephole;
            var ops = Ops(MethodListing(Listing(Il(Sum, "ilopt_pp_off")), " ev("));
            Assert.True(HasRun(ops, "isinst", "ldnull", "cgt.un"));
            Assert.Contains("castclass", ops);
        }
        finally { IlOpt.Enabled = saved; }
    }

    static void AirToIlVerify(byte[] pe)
    {
        var decode = AirToIlTests.DecodeCheck(pe);
        Assert.True(decode.Count == 0, "IL decode check: " + string.Join(", ", decode));
        var errors = AirToIlTests.IlVerify(pe);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
    }

    // ------------------------------------------------------------------ proven-in-bounds indexing (R2)

    const string Rows = """
        def dp(p : UInt32) : UInt32
          b = Array(UInt32).new(64)
          rows = Array(UInt32).new(130)
          k = 0
          while k < 64
            b[k] = (p &+ k.to_u32) & 3_u32
            k += 1
          end
          k = 0
          while k <= 64
            rows[k] = k.to_u32
            k += 1
          end
          po = 0
          co = 65
          i = 0
          while i < 64
            rows[co] = (i + 1).to_u32
            j = 0
            while j < 64
              cost = (i.to_u32 & 3_u32) != b[j] ? 1_u32 : 0_u32
              up = rows[po + j + 1] &+ 1_u32
              left = rows[co + j] &+ 1_u32
              m = up < left ? up : left
              dg = rows[po + j] &+ cost
              rows[co + j + 1] = m < dg ? m : dg
              j += 1
            end
            t2 = po
            po = co
            co = t2
            i += 1
          end
          rows[po + 64]
        end

        def grid(e : UInt32) : UInt32
          h = Array(UInt32).new(256)
          i = 0
          while i < 256
            h[i] = i.to_u32 &* 2654435761_u32
            i += 1
          end
          for zc in 0...16
            up = zc > 0 ? -16 : 0
            for xc in 0...16
              i = zc * 16 + xc
              hl = h[xc > 0 ? i - 1 : i]
              h[i] = (h[i] &+ hl &+ h[i + up]) &+ e
            end
          end
          h[255] &+ h[17]
        end

        def risky(n : Int32) : UInt32
          a = Array(UInt32).new(8)
          j = n
          a[j & 7] &+ a[j % 8 < 0 ? 0 : j % 8]
        end

        s = 0_u32
        q = 0_u32
        while q < 20_u32
          s = s &+ dp(q) &+ grid(q)
          q = q &+ 1_u32
        end
        puts s
        puts risky(13)
        begin
          puts risky(-3)
        rescue e
          puts "caught"
        end
        """;

    static string Body(string listing, string method)
    {
        int i = listing.IndexOf(" " + method + "(", StringComparison.Ordinal);
        Assert.True(i >= 0, method + " not found");
        int end = listing.IndexOf("\n  }", i, StringComparison.Ordinal);
        return listing[i..end];
    }

    [Fact]
    public void Proven_indices_lose_their_bounds_checks()
    {
        var res = Il(Rows, "ilopt_rows");
        var text = Listing(res);
        // dp: rows[po + j + 1], rows[co + j], rows[po + j], rows[co + j + 1], rows[co], rows[po + 64] are proven
        // (po, co in [0, 65], j in [0, 63], rows has 130 elements); b[j], rows[k] stay the JIT's own shape
        Assert.Equal(6, CountCalls(Body(text, "dp"), "GetArrayDataReference"));
        Assert.Equal(6, CountCalls(Body(text, "dp"), "Unsafe::Add"));
        // grid: the select-split proofs (xc > 0 ? i - 1 : i, i + (zc > 0 ? -16 : 0)), h[i] with i = zc * 16 + xc
        // (read and store; the optimizer may unroll the xc loop), the constant h[255], h[17]: only the fill loop's
        // bare h[i] keeps stelem
        var grid = Ops(Body(text, "grid"));
        Assert.True(CountCalls(Body(text, "grid"), "GetArrayDataReference") >= 6);
        Assert.Equal(1, grid.Count(o => o.StartsWith("stelem", StringComparison.Ordinal) || o.StartsWith("ldelem", StringComparison.Ordinal)));
        // risky: an 8-element array becomes a stackalloc Span (not an array): nothing to prove, output unchanged
        Same(Rows, res.Pe!);
        AirToIlVerify(res.Pe!);
    }

    static int CountCalls(string body, string what) =>
        body.Split('\n').Count(l => l.TrimStart().StartsWith("call ", StringComparison.Ordinal) && l.Contains(what, StringComparison.Ordinal));

    [Fact]
    public void Unproven_or_rewritten_arrays_keep_their_checks()
    {
        const string src = """
            def f(n : Int32) : UInt32
              a = Array(UInt32).new(16)
              b = Array(UInt32).new(16)
              b = Array(UInt32).new(4) if n > 3
              i = 0
              s = 0_u32
              while i < n
                s = s &+ a[i + 1] &+ b[i & 15]
                i += 1
              end
              s
            end
            puts f(3)
            begin
              puts f(40)
            rescue e
              puts "caught"
            end
            """;
        var res = Il(src, "ilopt_unproven");
        Assert.Equal(0, CountCalls(Body(Listing(res), "f"), "GetArrayDataReference"));
        Same(src, res.Pe!);
    }

    [Fact]
    public void Overflow_checks_go_only_where_the_interval_fits()
    {
        const string src = """
            def count(n : Int32) : Int32
              s = 0
              i = 0
              while i < 1000
                s += i
                i += 1
              end
              s + n
            end
            puts count(5)
            begin
              puts count(2147483647)
            rescue e : OverflowException
              puts "overflow"
            end
            """;
        var res = Il(src, "ilopt_ovf");
        var body = Body(Listing(res), "count");
        var ops = Ops(body);
        // i += 1 under i < 1000 and s += i (s <= 0 + ... is bounded only by widening: stays checked), s + n checked
        Assert.Equal(2, ops.Count(o => o == "add.ovf"));
        Assert.True(ops.Count(o => o == "add") >= 1, body);
        Same(src, res.Pe!);
    }

    [Theory]
    [InlineData("", IlOptPass.All)]
    [InlineData("0", IlOptPass.None)]
    [InlineData("-inline", IlOptPass.All & ~IlOptPass.Inline)]
    [InlineData("-inline,+noao,+inline", IlOptPass.All | IlOptPass.NoAo)]
    [InlineData("-peephole", IlOptPass.All & ~IlOptPass.Peephole)]
    [InlineData("0,+peephole", IlOptPass.Peephole)]
    [InlineData("0,+inbounds", IlOptPass.InBounds)]
    [InlineData("+noaoleaves", IlOptPass.All | IlOptPass.NoAoLeaves)]
    public void Switches_parse(string spec, IlOptPass expected) => Assert.Equal(expected, IlOpt.Parse(spec));
}
