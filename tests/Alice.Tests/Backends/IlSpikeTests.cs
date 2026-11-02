using Alice.Compiler;
using Alice.Compiler.Backend.IlSpike;

namespace Alice.Tests.Backends;

/// <summary>
/// D1.1 throwaway spike (src/Alice.Compiler/Backend/IlSpike/README.md): MIR -> IL through PersistedAssemblyBuilder
/// must print exactly what the C# backend prints. Sequential lowering on both sides (forks need closures, D1.5).
/// </summary>
[Collection("console")]
[Trait("Category", "Spike")]
public class IlSpikeTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static (string Il, string Cs) Both(IReadOnlyList<SourceFile> files, SpikeOptions? opt = null, params string[] args)
    {
        var res = IlSpikeCompiler.Compile(files, "spike", opt ?? new SpikeOptions());
        Assert.True(res.Image != null, res.Error);
        string il = AliceCompiler.Invoke(AliceCompiler.Load(res.Image!), args);
        var cs = AliceCompiler.TranspileFull(files, null, "alice_native", sequential: true).CSharp;
        string reference = AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit(cs, "spikecs", exe: true)), args);
        return (il, reference);
    }

    static (string Il, string Cs) Both(string src, SpikeOptions? opt = null, params string[] args) => Both([new SourceFile("main.al", src)], opt, args);

    [Theory]
    [InlineData("queens", "6")]
    [InlineData("editdist", "3")]
    [InlineData("nbody", "3")]
    [InlineData("mandelbrot", "3")]
    public void Bend2_workload_prints_the_csharp_checksum(string name, string arg)
    {
        var file = Path.Combine(Root(), "benchmarks", "bend2", "alice", name + ".al");
        var (il, cs) = Both([new SourceFile(file, File.ReadAllText(file))], null, arg);
        Assert.Equal(cs, il);
        Assert.NotEmpty(il.Trim());
    }

    [Fact]
    public void Editdist_with_unchecked_indexing_and_stack_buffers_keeps_the_checksum()
    {
        var file = Path.Combine(Root(), "benchmarks", "bend2", "alice", "editdist.al");
        var opt = new SpikeOptions { NoBounds = ["pair"], StackArrays = true, StackZero = false };
        var (il, cs) = Both([new SourceFile(file, File.ReadAllText(file))], opt, "3");
        Assert.Equal(cs, il);
    }

    [Fact]
    public void Checked_arithmetic_traps_and_wrapping_operators_wrap()
    {
        const string wrap = """
            def g(a : UInt32) : UInt32
              a &+ 1_u32
            end
            puts g(4294967295_u32)
            puts 3_u32 &- 5_u32
            """;
        var (il, cs) = Both(wrap);
        Assert.Equal(cs, il);
        const string trap = """
            def f(a : Int32) : Int32
              a + 1
            end
            puts f(2147483647)
            """;
        var res = IlSpikeCompiler.Compile([new SourceFile("main.al", trap)], "spike", new SpikeOptions());
        Assert.True(res.Image != null, res.Error);
        Assert.Throws<OverflowException>(() => AliceCompiler.Invoke(AliceCompiler.Load(res.Image!), []));
        Assert.Throws<OverflowException>(() => AliceCompiler.Run(trap));
    }

    [Fact]
    public void Float_comparisons_with_nan_and_shifts_by_large_counts_match_csharp()
    {
        const string src = """
            def ge(a : Float32, b : Float32) : Bool
              a >= b
            end
            def lt(a : Float64, b : Float64) : Bool
              !(a < b)
            end
            def sh(x : UInt32, n : Int32) : UInt32
              (x << n) | (x >> (n + 1))
            end
            nan = 0.0_f32 / 0.0_f32
            puts ge(nan, 1.0_f32)
            puts ge(2.0_f32, 1.0_f32)
            puts lt(0.0 / 0.0, 1.0)
            puts sh(3_u32, 33)
            puts (7_u32).to_f32 / 2.0_f32
            puts 4000000000_u32.to_f64
            """;
        var (il, cs) = Both(src);
        Assert.Equal(cs, il);
    }

    [Fact]
    public void Tail_prefix_runs_deep_self_recursion_at_tier0()
    {
        // without AggressiveOptimization the method starts at tier 0, where only an explicit `tail.` avoids a frame per call
        var file = Path.Combine(Root(), "src", "Alice.Compiler", "Backend", "IlSpike", "bench", "tailrec.al");
        var res = IlSpikeCompiler.Compile([new SourceFile(file, File.ReadAllText(file))], "spike", new SpikeOptions { TailCalls = true });
        Assert.True(res.Image != null, res.Error);
        Assert.Equal("61\n50000005000000\n", AliceCompiler.Invoke(AliceCompiler.Load(res.Image!), ["10", "10000000"]).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Unsupported_nodes_are_reported_by_kind()
    {
        const string src = "class Box\n  getter v : Int32\n  def initialize(@v : Int32)\n  end\nend\nputs Box.new(1).v\n";
        var res = IlSpikeCompiler.Compile([new SourceFile("main.al", src)], "spike", new SpikeOptions());
        Assert.Null(res.Image);
        Assert.Contains("TypeDecl:class", res.Blockers.Keys);
    }
}
