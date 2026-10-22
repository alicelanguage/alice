using Alice.Cli.Commands;
using Alice.Compiler;

namespace Alice.Tests.Tooling;

/// <summary>`alice explain`: which optimizations each method received, read back from the generated C#.</summary>
public class ExplainTests
{
    [Fact]
    public void Reports_forks_owned_arrays_and_shared_objects()
    {
        const string src = """
            def fib(n : Int32) : Int64
              return n.to_i64 if n < 2
              fib(n - 1) + fib(n - 2)
            end
            def rows(n : Int32) : Int32
              xs = Array(Int32).new(n, 1)
              xs = xs.map { |v| v + 1 }
              xs[0]
            end
            """;
        var report = Explain.Report(AliceCompiler.Transpile(src));
        Assert.Contains(report["fib"], f => f.Contains("fork"));
        Assert.Contains(report["rows"], f => f.Contains("owned"));
        Assert.Contains(report["rows"], f => f.Contains("in place"));
        Assert.DoesNotContain(report.Keys, k => k.StartsWith("__"));   // generated helpers are folded into their method
    }

    [Fact]
    public void Shared_object_is_credited_to_the_method_that_uses_it()
    {
        const string src = "class Leaf\n  getter v : Int32\n  def initialize(@v : Int32)\n  end\n  def get : Int32\n    v\n  end\nend\ndef make : Leaf\n  Leaf.new(1)\nend\n";
        var report = Explain.Report(AliceCompiler.Transpile(src));
        Assert.Contains(report["make"], f => f.Contains("shared"));
        Assert.DoesNotContain(report["get"], f => f.Contains("shared"));
    }
}
