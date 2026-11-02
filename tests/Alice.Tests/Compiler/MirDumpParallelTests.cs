using Alice.Compiler;
using Alice.Compiler.Backend;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;

namespace Alice.Tests.Compiler;

/// <summary>
/// Parallel and ownership AIR nodes (ADR 0032 step 2, Mir/MExpr.Parallel.cs): each prints the C# text the lowerer
/// used to build, has a stable dump form, and is visible to MIR analyses.
/// </summary>
public class MirDumpParallelTests
{
    static MCode N(string s) => AirLeaf.Name(s);

    [Fact]
    public void Nodes_print_the_former_csharp_text()
    {
        var guard = new AirParGuard { Count = N("n"), Min = 65536, Distinct = { new AirReferenceEqualsGuard { W = N("a"), R = N("b") } } };
        Assert.Equal("n >= 65536 && global::Alice.Runtime.Par.Workers > 1 && !global::System.Object.ReferenceEquals(a, b)", Printer.E(guard));
        var reduce = new AirPar
        {
            Kind = AirParKind.Reduce, ResultCs = "int", Count = new AirLength { Target = N("xs") },
            Kernel = new MLambda { Params = { "lo", "hi" }, ExprBody = N("0") },
            Combine = new MLambda { Static = true, Params = { "a", "b" }, ExprBody = new MBinary { Op = AirBinOp.Add, L = N("a"), R = N("b") } },
            Grain = 8192,
        };
        Assert.Equal("global::Alice.Runtime.Par.Reduce<int>(xs.Length, (lo, hi) => 0, static (a, b) => a + b, 8192)", Printer.E(reduce));
        var probe = new AirParProbe { Kind = AirProbeKind.Offload, I = N("i"), Start = N("s"), T0 = N("t"), Count = N("n") };
        Assert.Equal("i - s >= 2 && ((i - s) & (i - s - 1)) == 0 && global::Alice.Runtime.Par.Offload(t, i - s - 1, n - (i - s))", Printer.E(probe));
        Assert.Equal("i == s + 1", Printer.E(new AirParProbe { Kind = AirProbeKind.Second, I = N("i"), Start = N("s") }));
        Assert.Equal("global::Alice.Runtime.Par.Fork(() => f(1), () => f(2))",
            Printer.E(new AirForkJoin { Branches = { new MLambda { ExprBody = N("f(1)") }, new MLambda { ExprBody = N("f(2)") } } }));
        Assert.Equal("global::Alice.Runtime.Par.Forkable", Printer.E(new AirForkable()));
        Assert.Equal("Own<int>.Rent(n)", Printer.E(new AirRent { PoolCs = "Own<int>", Args = { N("n") } }));
        Assert.Equal("Own<int>.Release(a)", Printer.E(new AirRelease { PoolCs = "Own<int>", Target = N("a") }));
        Assert.Equal("m - 1 - a + 1", Printer.E(new AirTripCount { End = new MBinary { Op = AirBinOp.Sub, L = N("m"), R = N("1") }, Start = N("a"), Inclusive = true }));
        Assert.Equal("a + lo", Printer.E(new AirOffset { Base = N("a"), Offset = N("lo") }));
        Assert.Equal("global::System.Math.Max(0, (b) - (a))", Printer.E(new AirPresizeCount { From = N("a"), To = N("b") }));
        // atomic operands, like the MCode text they replace: no parentheses as a receiver
        Assert.Equal("xs.Length.ToString()", Printer.E(new MCall { Target = new AirLength { Target = N("xs") }, Name = "ToString" }));
    }

    [Fact]
    public void Nodes_have_a_dump_form()
    {
        var guard = new AirParGuard { Count = AirLeaf.Name("n", AirLeaf.Int), Min = 2, Type = AirLeaf.Bool };
        Assert.Equal("if (par-guard `n`:int 2):bool\n", MirDump.Of([new MIf { Cond = guard }]));
        Assert.Equal("expr (release Own<int> `a`)\n", MirDump.Of([new MExprStmt { E = new AirRelease { PoolCs = "Own<int>", Target = N("a") } }]));
        Assert.Equal("expr (fork (lambda () `x`))\n", MirDump.Of([new MExprStmt { E = new AirForkJoin { Branches = { new MLambda { ExprBody = N("x") } } } }]));
    }

    [Fact]
    public void Analyses_see_the_children()
    {
        var rent = new AirRent { PoolCs = "Own<int>", Args = { N("n") } };
        Assert.Equal([rent.Args[0]], MirFacts.Children(rent));
        // a write inside the kernel lambda of a parallel call is a write
        var par = new AirPar { Kind = AirParKind.Range, Count = N("n"), Kernel = new MLambda { Body = [new MAssign { Target = N("k"), Value = N("1") }] } };
        Assert.True(MirFacts.Writes([new MExprStmt { E = par }], "k"));
        Assert.False(MirFacts.Writes([new MExprStmt { E = par }], "n"));
    }

    [Fact]
    public void Lowered_programs_dump_the_typed_nodes()
    {
        string sum = MirDump.Collect(() => AliceCompiler.Transpile("def f(xs : Array(Float64))\n  xs.sum { |x| Math.Sqrt(x) }\nend\n"));
        Assert.Contains("(par reduce double (length Length (local xs)", sum);
        Assert.Contains("(par-guard (length Length (local xs)", sum);
        string fib = MirDump.Collect(() => AliceCompiler.Transpile(
            "def fib(n : Int32) : Int64\n  if n < 2\n    n.to_i64\n  else\n    a = fib(n - 1)\n    b = fib(n - 2)\n    a + b\n  end\nend\n"));
        Assert.Contains("(fork (lambda ()", fib);
        Assert.Contains("if (forkable):bool", fib);
        string own = MirDump.Collect(() => AliceCompiler.Transpile(
            "def fill(xs : Array(Float64), v : Float64)\n  for i in 0...xs.size\n    xs[i] = v\n  end\nend\ndef f(n : Int32) : Int32\n  t = Array(Float64).new(n)\n  fill(t, 2.0)\n  t[n - 1].to_i32\nend\n"));
        Assert.Contains("(rent global::Alice.Runtime.Own<double>", own);
        Assert.Contains("(release global::Alice.Runtime.Own<double> `t`:double[])", own);
    }

    [Fact]
    public void Trip_count_parenthesizes_a_compound_range_end()
    {
        Assert.Equal("(c ? n : m) - a + 1", Printer.E(new AirTripCount { End = new MCond { C = N("c"), A = N("n"), B = N("m") }, Start = N("a"), Inclusive = true }));
        var cs = AliceCompiler.Transpile("def f(xs : Array(Float64), c : Bool, n : Int32, m : Int32)\n  for i in 1...(c ? n : m)\n    xs[i] = Math.sqrt(xs[i])\n  end\nend\n");
        Assert.Matches(@"int __n\d+ = \(c \? n : m\) - __a\d+;", cs);
    }
}
