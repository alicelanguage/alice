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

    /// <summary>Lowering builds typed nodes only: no `new MCode(` text leaf anywhere under Lowering/ (MCode.Empty, the
    /// statement sentinel, and MCode as the typed leaves' base class are not constructions).</summary>
    [Fact]
    public void Lowering_constructs_no_text_leaves()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        var dir = Path.Combine(d!.FullName, "src", "Alice.Compiler", "Lowering");
        var hits = Directory.GetFiles(dir, "*.cs").SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => x.l.Contains("new MCode(")).Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1)).ToList();
        Assert.Empty(hits);
    }

    /// <summary>Every helper the lowering adds is a typed declaration: no C# text in Lowerer.Extra.</summary>
    [Fact]
    public void Word_count_and_gpu_helpers_are_typed()
    {
        var l = Lower("def f(text : String) : Int32\n  counts = Dictionary(String, Int32).new\n  for w in text.Split(\" \")\n"
            + "    counts[w] = counts.GetValueOrDefault(w) + 1\n  end\n  counts.Count\nend\n");
        var d = Typed(l, "__parwords_");
        Assert.Equal(["__text", "__dict"], d.Method!.Params.Select(p => p.Name));
        Assert.All(l.Extra, x => Assert.True(l.TypedExtra.ContainsKey(x), x));
    }

    /// <summary>The typed `__parwords` helper counts like the serial loop on a text above the parallel threshold.</summary>
    [Fact]
    public void Parallel_word_count_matches_the_serial_count()
    {
        const string src = "def f(text : String) : Int32\n  counts = Dictionary(String, Int32).new\n  for w in text.Split(' ')\n"
            + "    counts[w] = counts.GetValueOrDefault(w) + 1\n  end\n  counts[\"w7\"] * 100000 + counts.Count\nend\n";
        string cs = AliceCompiler.Transpile(src);
        Assert.Contains("__parwords_", cs);
        var f = System.Reflection.Assembly.Load(AliceCompiler.Emit([cs], "wc" + Guid.NewGuid().ToString("N"), exe: false)).GetType("Program")!.GetMethod("f")!;
        var words = Enumerable.Range(0, 200_000).Select(k => "w" + (k * 7919 % 1000)).ToArray();
        string text = string.Join(' ', words);
        int expected = words.Count(w => w == "w7") * 100000 + words.Distinct().Count();
        Assert.Equal(expected, f.Invoke(null, [text]));
    }

    /// <summary>`MFor.Step` is typed (AirForStep) and prints the C# the step string spelled.</summary>
    [Fact]
    public void For_steps_are_typed_and_print_the_old_text()
    {
        Assert.Equal("++", Alice.Compiler.Backend.Printer.Step(AirForStep.Inc()));
        Assert.Equal("--", Alice.Compiler.Backend.Printer.Step(AirForStep.Dec()));
        Assert.Equal(" += 2", Alice.Compiler.Backend.Printer.Step(AirForStep.By("+=", AirConst.Int(2))));
        var u = AirForStep.UncheckedAdd("i", AirConst.Int(1));
        Assert.Equal(" = unchecked(i + 1)", Alice.Compiler.Backend.Printer.Step(u));
        Assert.True(u.IsIncrement("i"));
        Assert.False(u.IsIncrement("j"));
        var grid = AirForStep.By("=", new AirGridStep { Index = new AirLocalRef("t"), Count = new AirLocalRef("n"), Stride = new AirLocalRef("s") });
        Assert.Equal(" = n - t > s ? t + s : n", Alice.Compiler.Backend.Printer.Step(grid));
        Assert.Equal("for i int = `0`; < `n`; = (call unchecked _ (Add unchecked (local i) (const 1):int))\n",
            MirDump.Of([new MFor { Var = "i", VarType = "int", Start = new MCode("0"), End = new MCode("n"), Step = u }]));
    }

    [Fact]
    public void Ir_has_no_text_escape_hatch()
    {
        var names = typeof(MExpr).Assembly.GetTypes().Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("AirRawCs", names);
        Assert.DoesNotContain("AirName", names);
    }
}
