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
        "  return (Mul (local x):int (local x):int):int @2\n" +
        "body 2\n" +
        "  return (call unchecked _ (Add unchecked (local a):int (const 1):int)) @5\n" +
        "body 3\n" +
        "  expr (call puts _ (call sq _ (const 3):int)) @7\n";

    /// <summary>Typed leaves (D0.4): receivers, locals, literals, runtime type refs and type operators dump by kind
    /// with the binder's type, where they used to be opaque `text`.</summary>
    [Fact]
    public void Typed_leaves_dump_by_kind()
    {
        const string src =
            "class Counter\n  def initialize(n : Int32)\n    @n = n\n  end\n  def bump : Int32\n    @n = @n + 1\n    self.peek\n  end\n" +
            "  def peek : Int32\n    @n\n  end\nend\n" +
            "def kind(v : Int32) : String\n  case v\n  when 0 then \"zero\"\n  else \"many #{v > 1}\"\n  end\nend\n" +
            "def split(a : Int32, b : Int32) : Int32\n  q, r = a.divmod(b)\n  t = typeof(Int32)\n  s : String? = nil\n  q + r + 7 / b\nend\n" +
            "puts kind(2)\n";
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(src));
        Assert.Equal(TypedExpected, dump);
    }

    const string TypedExpected =
        "body 1\n" +
        "  assign (member n (this)):int = (local n):int @3\n" +
        "body 2\n" +
        "  assign (member n (this)):int = (Add (member n (this)):int (const 1):int):int @6\n" +
        "  return (call peek (this):Counter) @7\n" +
        "body 3\n" +
        "  return (member n (this)):int @10\n" +
        "body 4\n" +
        "  if (Eq (local v):int (const 0):int) @14\n" +
        "    return (const \"zero\"):string @15\n" +
        "  else\n" +
        "    return (interp \"many \" (cond (Gt (local v):int (const 1):int):bool (const \"true\"):string (const \"false\"):string) \"\") @16\n" +
        "body 5\n" +
        "  decl __dv1 var = (local a):int @20\n" +
        "  decl __dv2 var = (local b):int @20\n" +
        "  deconstruct decl (q, r) = (tuple (call floor_div (type Prelude) (local __dv1):int (local __dv2):int) (call floor_mod (type Prelude) (local __dv1):int (local __dv2):int)) @20\n" +
        "  decl t var = (typeof int):global::System.Type @21\n" +
        "  decl s string? = (null):object @22\n" +
        "  return (Add (Add (local q):int (local r):int):int (call floor_div (type Prelude) (const 7):int (local b):int):int):int @23\n" +
        "body 6\n" +
        "  expr (call puts _ (call kind _ (const 2):int)) @25\n";

    /// <summary>Each typed leaf prints the exact C# its MCode predecessor spelled (byte-identical output, D0.4).</summary>
    [Fact]
    public void Typed_leaves_print_the_old_text()
    {
        var i32 = new AirType.Prim(PrimKind.Int);
        (MExpr Node, string Cs, string Dump)[] cases =
        [
            (new AirConst(AirConstKind.Int, "-5L"), "-5L", "(const -5L)"),
            (AirConst.Str("a\\\"b"), "\"a\\\"b\"", "(const \"a\\\"b\"):string"),
            (AirConst.Bool(false), "false", "(const false):bool"),
            (new AirNull(), "null", "(null):object"),
            (new AirThis(), "this", "(this)"),
            (new AirThis(extension: true), "self", "(self)"),
            (new AirBase(), "base", "(base)"),
            (new AirLocalRef("n", i32, narrowed: true), "n.Value", "(local n narrowed):int"),
            (new AirParamRef("blk"), "blk", "(param blk)"),
            (new AirFieldRef("count"), "count", "(field count)"),
            (AirArrayLength.Of("xs.Length"), "xs.Length", "(length xs):int"),
            (AirTypeRef.Named("global::System.Math"), "global::System.Math", "(type global::System.Math)"),
            (new AirMethodGroup(AirTypeRef.Named("Geo").Ref, "area"), "Geo.area", "(methodgroup Geo area)"),
            (new AirMethodGroup(null, "sq"), "sq", "(methodgroup _ sq)"),
            (new AirDefault(null), "default", "(default)"),
            (new AirDefault(null, forgiving: true), "default!", "(default!)"),
            (new AirDefault(i32), "default(int)", "(default):int"),
            (new AirTypeOf(i32), "typeof(int)", "(typeof int):global::System.Type"),
            (new AirSizeOf(i32), "sizeof(int)", "(sizeof int):int"),
            (new AirDiscard(), "_", "(discard)"),
            (new AirYieldBreak(), "yield break", "(yieldbreak)"),
            (new AirName("System.Console"), "System.Console", "(name System.Console)"),
            (new AirRawCs("a => a"), "a => a", "(cs `a => a`)"),
        ];
        foreach (var (node, cs, dump) in cases)
        {
            Assert.Equal(cs, Alice.Compiler.Backend.Printer.E(node));
            Assert.Equal("expr " + dump + "\n", MirDump.Of([new MExprStmt { E = node }]));
        }
        // a negative literal stays non-atomic: `-5.abs()` must print as `(-5).abs()`
        Assert.Equal("(-5).abs()", Alice.Compiler.Backend.Printer.E(new MCall { Target = new AirConst(AirConstKind.Int, "-5"), Name = "abs" }));
    }

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
