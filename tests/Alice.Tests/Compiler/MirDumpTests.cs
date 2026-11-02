using Alice.Compiler;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;

namespace Alice.Tests.Compiler;

/// <summary>
/// The AIR foundation (ADR 0032): the stable MIR text dump behind `alice emit --air`, typed operators with a
/// checked flag, and AType -> AirType spelling the same C#.
/// </summary>
public class MirDumpTests
{
    [Fact]
    public void Dump_of_a_small_program_is_stable()
    {
        const string src = "def sq(x : Int32) : Int32\n  x * x\nend\ndef wrap(a : Int32) : Int32\n  a &+ 1\nend\nputs sq(3)\n";
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(src));
        Assert.Equal(Expected, dump);
    }

    const string Expected =
        "body 1\n" +
        "  return (Mul `x` `x`) @2\n" +
        "body 2\n" +
        "  return (call unchecked _ (Add unchecked `a` `1`)) @5\n" +
        "body 3\n" +
        "  expr (call puts _ (call sq _ `3`)) @7\n";

    [Fact]
    public void Await_is_an_explicit_operator()
    {
        var u = new MUnary { Op = AirUnOp.Await, E = new MCode("t") };
        Assert.Equal("await t", Alice.Compiler.Backend.Printer.E(u));
        Assert.Equal("expr (Await `t`)\n", MirDump.Of([new MExprStmt { E = u }]));
    }

    [Fact]
    public void Statement_line_follows_its_span()
    {
        var s = new MBreak { Line = 3 };
        Assert.Equal(3, s.Line);
        s.Span = new Span("a.al", 7, 2, 5);
        Assert.Equal(7, s.Line);
        s.Line = 9;
        Assert.Equal(new Span("a.al", 9, 2, 5), s.Span);
        Assert.Equal("break @a.al:9:2+5\n", MirDump.Of([s]));
    }

    public static TheoryData<string> Spellings() =>
    [
        "Int32", "String?", "Array(Float64)", "Array(Int32?)", "Hash(String, Array(Int32))", "Tuple(Int32, String)",
        "Proc(Int32, String)", "Proc(Int32, Nil)", "Pointer(UInt8)", "List(String?)", "Any", "Object",
    ];

    [Theory]
    [MemberData(nameof(Spellings))]
    public void AirType_spells_the_binder_type(string alice)
    {
        var t = TypeOf(alice);
        var air = t.ToAir();
        Assert.Equal(t.Cs, air.ToCs());
        Assert.False(air is AirType.ClrRef { Clr: null, Args.Count: 0 }, $"{t.Cs} fell back to a by-name type");
    }

    [Fact]
    public void AirType_structure()
    {
        Assert.Equal(new AirType.Prim(PrimKind.Double), TypeOf("Float64").ToAir());
        Assert.Equal(new AirType.Prim(PrimKind.String) { Nullable = true }, TypeOf("String?").ToAir());
        Assert.IsType<AirType.Pointer>(TypeOf("Pointer(UInt8)").ToAir());
        var tuple = Assert.IsType<AirType.Tuple>(TypeOf("Tuple(Int32, String)").ToAir());
        Assert.Equal(2, tuple.Items.Count);
        var list = Assert.IsType<AirType.ClrRef>(TypeOf("List(String?)").ToAir());
        Assert.Equal("global::System.Collections.Generic.List", list.Name);
        Assert.Equal("string?", list.Args[0].ToCs());
        Assert.Equal("var", AType.Unknown.ToAir().ToCs());
        var fp = Assert.IsType<AirType.FnPtr>(TypeOf("Callback(Int32, Nil)").ToAir());
        Assert.Equal("delegate* unmanaged[Cdecl]<int, void>", fp.ToCs());
    }

    /// <summary>Every type the binder gives a declared field, parameter or return in the samples spells the same.</summary>
    [Fact]
    public void AirType_round_trips_over_the_samples()
    {
        int n = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "samples"), "*.al", SearchOption.AllDirectories))
        {
            Binder b;
            try
            {
                b = new Binder([Alice.Compiler.Syntax.Parser.ParseFile(File.ReadAllText(file), file)], new ClrTypes(null));
                b.Bind();
            }
            catch (CompileException) { continue; }
            foreach (var sym in b.Types.Values.Distinct())
            {
                var types = sym.Fields.Values.Select(f => f.Type)
                    .Concat(sym.Methods.Values.SelectMany(ms => ms).SelectMany(m => m.Params.Select(p => p.Type).Append(m.Ret)))
                    .Append(sym.Type);
                foreach (var t in types) { Assert.Equal(t.Cs, t.ToAir().ToCs()); n++; }
            }
        }
        Assert.True(n > 100, $"only {n} types checked");
    }

    static AType TypeOf(string alice)
    {
        var b = new Binder([Alice.Compiler.Syntax.Parser.ParseFile("x = 1\n", "t.al")], new ClrTypes(null));
        b.Bind();
        var r = Alice.Compiler.Syntax.Parser.ParseFile($"def f(v : {alice})\nend\n", "t.al").Items.OfType<Alice.Compiler.Syntax.MethodDecl>().Single().Params[0].Type!;
        return b.ResolveType(r);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
