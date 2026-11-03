using Alice.Compiler;
using Alice.Compiler.Backend.Il;

namespace Alice.Tests.Fuzz.Regressions;

/// <summary>
/// Binder, lowering, backend and tooling bugs found by the fuzzers (tools/fuzz, /work/reports/fuzz-findings.md),
/// minimized. Skipped until fixed; each asserts the correct behavior.
/// </summary>
[Collection("console")]
public class CompilerRegressionTests
{
    static string Run(string src) => AliceCompiler.Run(src);

    [Fact]
    public void F1_strict_accepts_a_range_pattern()
    {
        const string src = "def k(n : Int32) : Int32\n  case n\n  when 3..15\n    1\n  else\n    0\n  end\nend\nputs k(5)\n";
        AliceCompiler.TranspileStrict([new SourceFile("main.al", src)]);
    }

    [Fact]
    public void F2_subclass_inherits_initialize()
    {
        const string src = "class C\n  def initialize(@start : Int32)\n  end\n  def get : Int32\n    @start\n  end\nend\nclass D < C\nend\nputs D.new(3).get\n";
        Assert.Equal("3\n", Run(src).Replace("\r\n", "\n"));
    }

    [Fact]
    public void F3_subclass_assigns_an_inherited_getter_ivar()
    {
        const string src = "class C\n  getter count : Int32\n  def initialize\n    @count = 1\n  end\nend\nclass D < C\n  def bump : Int32\n    @count = @count + 1\n    @count\n  end\nend\nputs D.new.bump\n";
        Assert.Equal("2\n", Run(src).Replace("\r\n", "\n"));
    }

    [Fact]
    public void F4_range_pattern_is_pure()
    {
        const string src = "pure def k(n : Int32) : Int32\n  v = 0\n  case n\n  when 1..44\n    v = 2\n  end\n  v\nend\nputs k(5)\n";
        Assert.Equal("2\n", Run(src).Replace("\r\n", "\n"));
    }

    [Fact]
    public void F5_local_reassigned_in_a_sibling_block()
    {
        const string src = "def k(n : Int32) : Int32\n  t = 0\n  j = 0\n  while j < 2\n    i = 0\n    while i < 2\n      t += 1\n      i += 1\n    end\n    j += 1\n  end\n  if n > 0\n    i = 0\n    while i < 2\n      t += 1\n      i += 1\n    end\n  end\n  t\nend\nputs k(3)\n";
        Assert.Equal("6\n", Run(src).Replace("\r\n", "\n"));
    }

    [Fact]   // fuzz I1, fixed on main by fix-il
    public void I1_il_backend_folds_float64_constants_in_float64()
    {
        const string src = "puts 1.0 / 3.0\nputs -3.25 / 7.0\n";
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "i1", exe: true);
        Assert.True(res.Ok);
        Assert.Equal(Run(src), AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []));
    }

    [Fact]
    public void T1_run_cache_key_covers_disabled_passes()
    {
        var files = new List<SourceFile> { new("main.al", "puts 1\n") };
        var before = Alice.Cli.Commands.RunCache.Key(files, "main", []);
        var old = Environment.GetEnvironmentVariable("ALICE_DISABLE_PASSES");
        try
        {
            Environment.SetEnvironmentVariable("ALICE_DISABLE_PASSES", "const-fold");
            Assert.NotEqual(before, Alice.Cli.Commands.RunCache.Key(files, "main", []));
        }
        finally { Environment.SetEnvironmentVariable("ALICE_DISABLE_PASSES", old); }
    }
}
