using Alice.Compiler;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

/// <summary>
/// D0.7b: the SoA-fusion, specialization and native-callback helpers are typed declarations (AirHelperDecl) whose
/// C# in Lowerer.Extra is printed from them, and the text escape hatches are gone from the IR.
/// </summary>
public class MirDumpHelpersTests
{
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

    static AirHelperDecl Typed(Lowerer l, string prefix)
    {
        var text = Assert.Single(l.Extra, x => x.Contains(prefix));
        Assert.True(l.TypedExtra.TryGetValue(text, out var d), prefix + " is still text");
        Assert.Equal(text, Alice.Compiler.Backend.Printer.Helper(d!));
        return d!;
    }

    [Fact]
    public void Native_callback_and_its_address_helper_are_typed()
    {
        var l = Lower("@[Link(\"libc.so.6\")]\nlib LibC\n  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void\nend\n"
            + "def sort(xs : Array(Int32))\n  LibC.qsort(xs, 5_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })\nend\n");
        var cb = l.Extra.Where(x => x.Contains("__cb_")).ToList();
        Assert.Equal(2, cb.Count);
        Assert.All(cb, x => Assert.True(l.TypedExtra.ContainsKey(x)));
        var body = l.TypedExtra[cb[0]];
        Assert.True(body.Unsafe);
        Assert.Equal("global::System.Runtime.InteropServices.UnmanagedCallersOnly", Assert.Single(body.Attributes).Type.ToCs());
        Assert.IsType<MTry>(Assert.Single(body.Method!.Body!));
        var addr = l.TypedExtra[cb[1]];
        var mnew = Assert.IsType<MNew>(addr.ExprBody);
        Assert.Equal("", mnew.TypeCs);   // target-typed new(...)
        Assert.Contains("=> new((nint)(delegate* unmanaged[Cdecl]<int*, int*, int>)&__cb_", cb[1]);
    }

    [Fact]
    public void Specialized_lambda_is_typed()
    {
        var l = Lower("def twice(&b : Int32 ->)\n  yield 1\n  yield 2\nend\ndef run : Int32\n  t = 0\n  twice { |x| t += x }\n  t\nend\n");
        var d = Typed(l, "__lam_");
        Assert.Equal("global::System.Runtime.CompilerServices.MethodImpl", Assert.Single(d.Attributes).Type.ToCs());
        Assert.Contains(d.Method!.Params, p => p.Type is AirType.ByRef);   // the captured `t`, by reference
    }

    [Fact]
    public void Fused_soa_helper_is_typed()
    {
        var l = Lower("@[SoA]\nrecord struct P(x : Float64, v : Float64)\ndef f(n : Int32) : Float64\n  ps = SoA(P).new\n"
            + "  n.times { |i| ps.add(P.new(i.to_f, 2.0)) }\n  ps.each { |p| p.x += p.v * Math.Sqrt(p.v) }\n  ps.sum { |p| p.x }\nend\n");
        var m = Typed(l, "__fused_").Method!;
        Assert.Equal("double", m.Ret.ToCs());
        Assert.IsType<MReturn>(Assert.Single(m.Body!));
    }

    [Fact]
    public void Ir_has_no_text_escape_hatch()
    {
        var names = typeof(MExpr).Assembly.GetTypes().Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("AirRawCs", names);
        Assert.DoesNotContain("AirName", names);
    }
}
