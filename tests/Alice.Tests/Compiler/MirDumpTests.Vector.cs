using Alice.Compiler;
using Alice.Compiler.Backend;
using Alice.Compiler.Mir;

namespace Alice.Tests.Compiler;

/// <summary>
/// The SIMD/stack AIR nodes (Mir/MExpr.Vector.cs, backend plan D0.6): the C# printer spells them as the text the
/// lowerings used to build, and the dump shows them as typed nodes instead of opaque C#.
/// </summary>
public class MirDumpVectorTests
{
    static readonly AirType Dbl = new AirType.Prim(PrimKind.Double);
    static readonly AirType I32 = new AirType.Prim(PrimKind.Int);

    static AirVectorOp Op(AirVecOp op, AirType vt, params MExpr[] args) => new() { Op = op, Vec = vt, Args = args.ToList() };

    [Fact]
    public void Vector_types_spell_the_CSharp_vector_types()
    {
        Assert.Equal("global::System.Numerics.Vector<double>", AirVector.Of(Dbl, 0).ToCs());
        Assert.Equal("global::System.Runtime.Intrinsics.Vector256<int>", AirVector.Of(I32, 256).ToCs());
        Assert.Equal(512, AirVector.Width(AirVector.Of(I32, AirVector.Api(512))));
        Assert.Same(typeof(System.Numerics.Vector<>), AirVector.Of(Dbl, 0).Clr);
    }

    [Fact]
    public void Printer_reproduces_the_vector_text()
    {
        var v = AirVector.Of(Dbl, 0);
        var w = AirVector.Of(I32, 256);
        Assert.Equal("new global::System.Numerics.Vector<double>((double)px)", Printer.E(Op(AirVecOp.Splat, v, new AirLocalRef("px"))));
        Assert.Equal("global::System.Runtime.Intrinsics.Vector256.Create((int)(3))", Printer.E(Op(AirVecOp.Splat, w, new AirConst(AirConstKind.Int, "3"))));
        Assert.Equal("(a * b)", Printer.E(Op(AirVecOp.Mul, w, new AirLocalRef("a"), new AirLocalRef("b"))));
        Assert.Equal("-(a)", Printer.E(Op(AirVecOp.Neg, w, new AirLocalRef("a"))));
        Assert.Equal("global::System.Numerics.Vector.SquareRoot(a)", Printer.E(Op(AirVecOp.Sqrt, v, new AirLocalRef("a"))));
        Assert.Equal("global::System.Runtime.Intrinsics.Vector256.Sqrt(a)", Printer.E(Op(AirVecOp.Sqrt, w, new AirLocalRef("a"))));
        Assert.Equal("global::System.Runtime.Intrinsics.Vector256<int>.Count", Printer.E(Op(AirVecOp.Count, w)));
        Assert.Equal("(int)global::System.Runtime.Intrinsics.Vector256.Sum(acc)",
            Printer.E(new AirVectorReduce { Vec = w, Arg = new AirLocalRef("acc"), CastTo = I32 }));
        Assert.Equal("global::System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref global::System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(xs), (nuint)i)",
            Printer.E(new AirVectorLoad { Vec = w, Array = new AirLocalRef("xs"), Index = new AirLocalRef("i") }));
        Assert.Equal("i <= (n) + 1 - global::System.Numerics.Vector<double>.Count",
            Printer.E(new AirVectorFits { Form = AirFitsForm.Fits, Vec = v, Index = new AirLocalRef("i"), End = new AirLocalRef("n"), EndInclusive = true }));
        Assert.Equal("global::System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated && n - i >= global::System.Runtime.Intrinsics.Vector256<int>.Count",
            Printer.E(new AirVectorFits { Form = AirFitsForm.Guard, Vec = w, Index = new AirLocalRef("i"), End = new AirLocalRef("n") }));
        Assert.Equal("stackalloc double[4]", Printer.E(new AirStackAlloc { Elem = Dbl, Count = AirConst.Int(4) }));
        // a vector operand of an operator needs no parentheses, a negation does
        Assert.Equal("new global::System.Numerics.Vector<double>((double)px) + global::System.Numerics.Vector<double>.Indices",
            Printer.E(new MBinary { Op = AirBinOp.Add, L = Op(AirVecOp.Splat, v, new AirLocalRef("px")), R = Op(AirVecOp.Indices, v) }));
        Assert.Equal("(-(a)) + b", Printer.E(new MBinary { Op = AirBinOp.Add, L = Op(AirVecOp.Neg, w, new AirLocalRef("a")), R = new AirLocalRef("b") }));
    }

    [Fact]
    public void Dump_shows_vector_nodes()
    {
        var w = AirVector.Of(I32, 256);
        var e = new AirVectorReduce { Vec = w, Arg = Op(AirVecOp.Add, w, Op(AirVecOp.One, w), Op(AirVecOp.Zero, w)), CastTo = I32 };
        Assert.Equal("expr (v.reduce Sum global::System.Runtime.Intrinsics.Vector256<int> (v.Add global::System.Runtime.Intrinsics.Vector256<int> (v.One global::System.Runtime.Intrinsics.Vector256<int>) (v.Zero global::System.Runtime.Intrinsics.Vector256<int>)) as int)\n",
            MirDump.Of([new MExprStmt { E = e }]));
    }

    [Fact]
    public void Vectorized_sum_lowers_to_typed_vector_nodes()
    {
        string dump = MirDump.Collect(() => AliceCompiler.Transpile("def f(xs : Array(Int32))\n  xs.map { |x| x * 3 + 1 }.select { |x| x > 10 }.sum\nend\nputs f([1, 2, 3])\n"));
        Assert.Contains("(v.load global::System.Runtime.Intrinsics.Vector512<int>", dump);
        Assert.Contains("(v.fits Guard global::System.Runtime.Intrinsics.Vector256<int>", dump);
        Assert.Contains("(v.GreaterThan global::System.Runtime.Intrinsics.Vector128<int>", dump);
        Assert.Contains("(v.reduce Sum", dump);
        Assert.DoesNotContain("LoadUnsafe", dump);   // no C# text left in the vector prologue
    }

    [Fact]
    public void Simt_loop_lowers_to_typed_vector_nodes()
    {
        const string src = "def f(n : Int32) : Int64\n  s : Int64 = 0\n  for i in 0...n\n    x = 0.0\n    while x < 10.0\n      x = x * 2.0 + 1.0\n    end\n    s += 1 if x > 11.0\n  end\n  s\nend\nputs f(4)\n";
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(src));
        Assert.Contains("(v.EqualsAll global::System.Numerics.Vector<long> (v.AsVectorInt64", dump);
        Assert.Contains("(v.Indices global::System.Numerics.Vector<double>)", dump);
        Assert.Contains("(v.reduce Sum global::System.Numerics.Vector<double> ", dump);
        Assert.DoesNotContain("ConditionalSelect(", dump);
    }

    [Fact]
    public void Stack_array_is_a_stackalloc_node()
    {
        const string src = "def f(n : Int32) : Int32\n  out = Array(Int32).new(1000, 0)\n  for i in 0...1000\n    p = [i, i * 2]\n    out[i] = p[0] + p[1]\n  end\n  out[999] + n\nend\nputs f(1)\n";
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(src));
        // leaf forms (locals, constants) belong to the common nodes; assert only the stack nodes' shape
        Assert.Matches(@" global::System\.Span<int> = \(stackalloc int \S+\):global::System\.Span<int>", dump);
        Assert.Matches(@"assign \(index \S+:global::System\.Span<int> \S+\) = \(Mul", dump);
    }
}
