using Alice.Compiler;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

/// <summary>
/// D0.7c: the parallel helpers (`__fork_*`, `__par_*`, `__parfor_*`) and shared constants are typed declarations with
/// MIR bodies (Mir/AirDecls.Helpers.cs); the C# text in Lowerer.Extra is printed from them.
/// </summary>
public class HelperDeclTests
{
    /// <summary>Lowers every top-level method as Backend/Emitter.cs does and returns the lowerer.</summary>
    static Lowerer Lower(string src)
    {
        var ast = Parser.ParseFile(src, "main.al");
        var binder = new Binder([ast], new ClrTypes(null));
        binder.Bind();
        var lowerer = new Lowerer(binder) { File = ast.Path };
        foreach (var it in ast.Items)
        {
            if (it is not MethodDecl d || d.Sym is not MethodSym m) continue;
            lowerer.Type = binder.Program;
            lowerer.Method = m;
            lowerer.CloneOk = true;
            bool returns = !m.Ret.IsVoid && !m.IsCtor && !m.IsIterator;
            lowerer.LowerBody(d.Body, returns ? Tail.Return : Tail.None, d.HoistedLocals, d.Line);
        }
        return lowerer;
    }

    static AirHelperDecl Only(Lowerer l, string prefix)
    {
        var text = Assert.Single(l.Extra, x => x.Contains(prefix));
        Assert.True(l.TypedExtra.TryGetValue(text, out var d));
        Assert.Equal(text, Alice.Compiler.Backend.Printer.Helper(d!));   // the C# is printed from the declaration
        return d!;
    }

    [Fact]
    public void Fork_helper_is_a_typed_method()
    {
        var l = Lower("def fib(n : Int32) : Int64\n  if n < 2\n    n.to_i64\n  else\n    a = fib(n - 1)\n    b = fib(n - 2)\n    a + b\n  end\nend\n");
        var m = Only(l, "__fork_").Method!;
        Assert.StartsWith("__fork_fib_", m.Name);
        Assert.True(m.Is(AirMethodFlags.Static));
        Assert.Equal("(long, long)", m.Ret.ToCs());
        Assert.Equal(["n:int"], m.Params.Select(p => p.Name + ":" + p.Type.ToCs()));
        var ret = Assert.IsType<MReturn>(Assert.Single(m.Body!));
        var fork = Assert.IsType<AirForkJoin>(ret.Value);
        Assert.All(fork.Branches, b => Assert.IsType<MLambda>(b));
    }

    [Fact]
    public void Parallel_reduce_helper_is_a_typed_method()
    {
        var l = Lower("def f(xs : Array(Float64))\n  xs.sum { |x| Math.Sqrt(x) }\nend\n");
        var m = Only(l, "__par_").Method!;
        Assert.Equal("double", m.Ret.ToCs());
        Assert.Equal(["xs:double[]"], m.Params.Select(p => p.Name + ":" + p.Type.ToCs()));
        var par = Assert.IsType<AirPar>(Assert.IsType<MReturn>(Assert.Single(m.Body!)).Value);
        Assert.Equal(AirParKind.Reduce, par.Kind);
    }

    [Fact]
    public void Parallel_loop_helper_is_a_typed_method()
    {
        var l = Lower("def f(xs : Array(Float64), n : Int32)\n  for i in 0...n\n    xs[i] = Math.sqrt(i.to_f)\n  end\nend\n");
        var m = Only(l, "__parfor_").Method!;
        Assert.Equal("void", m.Ret.ToCs());
        Assert.Equal("int", m.Params[^1].Type.ToCs());
        Assert.IsType<AirPar>(Assert.IsType<MExprStmt>(Assert.Single(m.Body!)).E);
    }

    [Fact]
    public void Shared_constant_is_a_typed_field()
    {
        var l = Lower("class Node\n  getter left : Node?\n  getter right : Node?\n  def initialize(@left : Node?, @right : Node?)\n  end\nend\n"
            + "def build(d : Int32) : Node\n  d > 0 ? Node.new(build(d - 1), build(d - 1)) : Node.new(nil, nil)\nend\n");
        var d = Only(l, "__shared_");
        Assert.True(d.Field!.Static && d.Field.ReadOnly);
        Assert.IsType<MNew>(d.FieldInit);
    }
}
