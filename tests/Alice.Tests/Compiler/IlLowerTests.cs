using System.Globalization;
using System.Reflection;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Backend;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;
using Alice.Compiler.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Alice.Tests.Compiler;

/// <summary>
/// IL-only lowering passes (Air/Passes, backend plan D1.7): before/after AIR dumps for every shape, and behaviour
/// checked against Roslyn as the oracle (the original body printed as C#, against the lowered body printed as C#,
/// which Roslyn must no longer lower). Il_*: the lowered bodies through the IL emitter (AirToIl.CompileBody).
/// </summary>
public class IlLowerTests
{
    // ---- types and builders ----

    static readonly AirType I = new AirType.Prim(PrimKind.Int);
    static readonly AirType Ch = new AirType.Prim(PrimKind.Char);
    static readonly AirType D = new AirType.Prim(PrimKind.Double);
    static readonly AirType B = new AirType.Prim(PrimKind.Bool);
    static readonly AirType S = new AirType.Prim(PrimKind.String);
    static readonly AirType O = new AirType.Prim(PrimKind.Object);
    static readonly AirType NI = I with { Nullable = true };
    static readonly AirType NS = S with { Nullable = true };
    static AirType ListOf(AirType e, Type clr) => new AirType.ClrRef("global::System.Collections.Generic.List", clr, [e]);
    static readonly AirType IEnumInt = new AirType.ClrRef("global::System.Collections.Generic.IEnumerable", typeof(IEnumerable<int>), [I]);
    static readonly AirType Dict = new AirType.ClrRef("global::System.Collections.Generic.Dictionary", typeof(Dictionary<string, int>), [S, I]);
    static readonly AirType KV = new AirType.ClrRef("global::System.Collections.Generic.KeyValuePair", typeof(KeyValuePair<string, int>), [S, I]);

    static AirType Alice(string name, string kind) => new AirType.AliceType(new TypeSym { Name = name, Kind = kind, Type = new AType(kind, name) }, []);

    static AirLocalRef L(string n, AirType t) => new(n, t);
    static MExpr K(object? v) => v == null ? new AirNull() : AirConst.Of(v);
    static MCode C(string text, AirType? t = null) => new(text) { Type = t };
    static MBinary Bin(AirBinOp op, MExpr l, MExpr r, AirType? t = null) => new() { Op = op, L = l, R = r, Type = t };
    static MDecl Decl(string n, string t, MExpr? init = null) => new() { Name = n, TypeCs = t, Init = init };
    static MExprStmt Out(MExpr e) => new() { E = new MCall { Target = new MCode("__o"), Name = "Append", Args = { e } } };
    static MExprStmt Out(string text) => Out(new MCode("\"" + text + "\""));

    static MIf Chain(Func<MExpr> subj, List<(object[] Labels, List<MStmt> Body)> arms, List<MStmt>? dflt)
    {
        MIf? head = null, last = null;
        foreach (var (labels, body) in arms)
        {
            MExpr? cond = null;
            foreach (var l in labels)
            {
                var eq = Bin(AirBinOp.Eq, subj(), K(l), B);
                cond = cond == null ? eq : Bin(AirBinOp.Or, cond, eq, B);
            }
            var arm = new MIf { Cond = cond!, Then = body };
            if (last == null) head = arm; else last.Else = [arm];
            last = arm;
        }
        last!.Else = dflt;
        return head!;
    }

    static string Lowered(List<MStmt> body, IMirPass pass)
    {
        pass.Run(body);
        return MirDump.Of(body);
    }

    static string Lowered(List<MStmt> body, out IlLowerContext ctx)
    {
        ctx = IlLowering.Run(body);
        return MirDump.Of(body);
    }

    // ================= ForeachLower =================

    [Fact]
    public void Foreach_over_an_array_is_a_counted_loop()
    {
        var body = new List<MStmt> { new MForeach { Vars = { "x" }, Coll = L("xs", new AirType.Array(I)), Body = { new MBreak() } } };
        Assert.Equal("foreach (x) in (local xs):int[]\n  break\n", MirDump.Of(body));
        Assert.Equal(
            "decl __ilc1 int[] = (local xs):int[]\n" +
            "for __ili2 int = (const 0):int; < (member Length (local __ilc1):int[]):int; ++\n" +
            "  decl x int = (index (local __ilc1):int[] (local __ili2):int):int\n" +
            "  break\n", Lowered(body, new ForeachLower()));

        // a nil array throws at `.Length`, as foreach does: the nullable spelling lowers the same way
        var maybe = new List<MStmt> { new MForeach { Vars = { "x" }, Coll = L("xs", new AirType.Array(I) { Nullable = true }) } };
        Assert.StartsWith("decl __ilc1 int[]? = (local xs):int[]?\nfor __ili2 int", Lowered(maybe, new ForeachLower()));
    }

    [Fact]
    public void Foreach_over_a_span_and_a_string_index_locals()
    {
        var span = new AirType.ClrRef("global::System.ReadOnlySpan", null, [I]);
        var body = new List<MStmt>
        {
            new MForeach { Vars = { "v" }, Coll = L("sp", span) },
            new MForeach { Vars = { "ch" }, Coll = L("str", S) },
        };
        Assert.Equal(
            "decl __ilc1 global::System.ReadOnlySpan<int> = (local sp):global::System.ReadOnlySpan<int>\n" +
            "for __ili2 int = (const 0):int; < (member Length (local __ilc1):global::System.ReadOnlySpan<int>):int; ++\n" +
            "  decl v int = (index (local __ilc1):global::System.ReadOnlySpan<int> (local __ili2):int):int\n" +
            "decl __ilc3 string = (local str):string\n" +
            "for __ili4 int = (const 0):int; < (member Length (local __ilc3):string):int; ++\n" +
            "  decl ch char = (index (local __ilc3):string (local __ili4):int):char\n", Lowered(body, new ForeachLower()));
    }

    [Fact]
    public void Foreach_over_a_list_keeps_its_struct_enumerator_in_a_local()
    {
        var body = new List<MStmt> { new MForeach { Vars = { "x" }, Coll = L("xs", ListOf(I, typeof(List<int>))), Body = { Out(L("x", I)) } } };
        Assert.Equal(
            "decl __ile1 global::System.Collections.Generic.List<int>.Enumerator = (call GetEnumerator (local xs):global::System.Collections.Generic.List<int>):global::System.Collections.Generic.List<int>.Enumerator\n" +
            "try\n" +
            "  while (call MoveNext (local __ile1):global::System.Collections.Generic.List<int>.Enumerator):bool\n" +
            "    decl x int = (member Current (local __ile1):global::System.Collections.Generic.List<int>.Enumerator):int\n" +
            "    expr (call Append `__o` (local x):int)\n" +
            "finally\n" +
            "  expr (call Dispose (local __ile1):global::System.Collections.Generic.List<int>.Enumerator):void\n", Lowered(body, new ForeachLower()));
    }

    [Fact]
    public void Foreach_over_a_list_of_an_Alice_type_maps_the_open_enumerator()
    {
        var point = Alice("Point", "struct");
        var body = new List<MStmt> { new MForeach { Vars = { "p" }, Coll = L("ps", ListOf(point, typeof(List<>))) } };
        string dump = Lowered(body, new ForeachLower());
        Assert.Contains("decl __ile1 global::System.Collections.Generic.List<Point>.Enumerator = (call GetEnumerator", dump);
        Assert.Contains("decl p Point = (member Current (local __ile1):global::System.Collections.Generic.List<Point>.Enumerator):Point", dump);
    }

    [Fact]
    public void Foreach_over_an_interface_disposes_with_a_null_check()
    {
        var body = new List<MStmt> { new MForeach { Vars = { "x" }, Coll = L("seq", IEnumInt) } };
        Assert.Equal(
            "decl __ile1 global::System.Collections.Generic.IEnumerator<int> = (call GetEnumerator (local seq):global::System.Collections.Generic.IEnumerable<int>):global::System.Collections.Generic.IEnumerator<int>\n" +
            "try\n" +
            "  while (call MoveNext (local __ile1):global::System.Collections.Generic.IEnumerator<int>):bool\n" +
            "    decl x int = (member Current (local __ile1):global::System.Collections.Generic.IEnumerator<int>):int\n" +
            "finally\n" +
            "  if (Ne (local __ile1):global::System.Collections.Generic.IEnumerator<int> (null):object):bool\n" +
            "    expr (call Dispose (local __ile1):global::System.Collections.Generic.IEnumerator<int>):void\n", Lowered(body, new ForeachLower()));
    }

    [Fact]
    public void Foreach_with_two_names_deconstructs_the_element()
    {
        var body = new List<MStmt> { new MForeach { Vars = { "k", "v" }, Coll = L("d", Dict) } };
        string dump = Lowered(body, out var ctx);
        Assert.Empty(ctx.Unsupported);
        Assert.Contains("decl __ilx2 global::System.Collections.Generic.KeyValuePair<string, int> = (member Current", dump);
        Assert.Contains("    decl k string\n    decl v int\n    expr (call Deconstruct (local __ilt3):global::System.Collections.Generic.KeyValuePair<string, int> (arg \"out \" (local k):string) (arg \"out \" (local v):int)):void\n", dump);
        Assert.DoesNotContain("foreach", dump);
        Assert.DoesNotContain("deconstruct", dump);
    }

    [Fact]
    public void Await_foreach_and_untyped_collections_stay_and_are_reported()
    {
        var body = new List<MStmt>
        {
            new MForeach { Await = true, Vars = { "x" }, Coll = L("stream", new AirType.ClrRef("global::System.Collections.Generic.IAsyncEnumerable", null, [I])), Line = 4 },
            new MForeach { Vars = { "y" }, Coll = C("things") },
        };
        var ctx = new IlLowerContext();
        string dump = Lowered(body, new ForeachLower(ctx));
        Assert.Equal("foreach await (x) in (local stream):global::System.Collections.Generic.IAsyncEnumerable<int> @4\nforeach (y) in `things`\n", dump);
        Assert.Equal(["line 4: `await foreach` (IAsyncEnumerable) is not lowered for the IL backend yet", "foreach over a collection of unknown type"], ctx.Unsupported);
    }

    static List<MStmt> ForeachProgram() =>
    [
        new MForeach
        {
            Vars = { "x" }, Coll = C("new[] { 1, 2, 3, 4, 5 }", new AirType.Array(I)),
            Body = { new MIf { Cond = Bin(AirBinOp.Eq, L("x", I), K(2), B), Then = { new MContinue() } }, new MIf { Cond = Bin(AirBinOp.Eq, L("x", I), K(5), B), Then = { new MBreak() } }, Out(L("x", I)) },
        },
        Out(";"),
        new MForeach { Vars = { "x" }, Coll = C("new global::System.Collections.Generic.List<int> { 7, 8 }", ListOf(I, typeof(List<int>))), Body = { Out(L("x", I)) } },
        Out(";"),
        new MForeach { Vars = { "x" }, Coll = C("global::System.Linq.Enumerable.Range(1, 3)", IEnumInt), Body = { Out(L("x", I)) } },
        Out(";"),
        new MForeach { Vars = { "c" }, Coll = C("\"hey\"", S), Body = { Out(L("c", Ch)), Out("|") } },
        Out(";"),
        Decl("d", "global::System.Collections.Generic.Dictionary<string, int>", C("new global::System.Collections.Generic.Dictionary<string, int> { [\"a\"] = 1, [\"b\"] = 2 }", Dict)),
        new MForeach { Vars = { "k", "v" }, Coll = L("d", Dict), Body = { Out(L("k", S)), Out(L("v", I)) } },
        Out(";"),
        Decl("arr", "int[]", C("new[] { 4, 5, 6 }", new AirType.Array(I))),
        new MForeach { Vars = { "x" }, Coll = C("new global::System.ReadOnlySpan<int>(arr)", new AirType.ClrRef("global::System.ReadOnlySpan", null, [I])), Body = { Out(L("x", I)) } },
    ];

    [Fact]
    public void Foreach_lowering_behaves_like_Roslyn() => Same(ForeachProgram, expect: "134;78;123;h|e|y|;a1b2;456");

    // ================= InterpLower =================

    static MInterp Interp(string[] chunks, params MExpr[] holes) => new() { Chunks = [.. chunks], Holes = [.. holes] };

    [Fact]
    public void Interpolation_of_few_strings_is_a_concat()
    {
        var body = new List<MStmt>
        {
            Out(Interp(["<", ">"], L("s", S))),
            Out(Interp(["", ""], L("s", NS))),
            Out(Interp(["a", "b"], L("s", S))),
        };
        Assert.Equal(
            "expr (call Append `__o` (call Concat (type string) `\"<\"`:string (local s):string `\">\"`:string):string)\n" +
            "expr (call Append `__o` (Coalesce (local s):string? (const \"\"):string):string)\n" +
            "expr (call Append `__o` (call Concat (type string) `\"a\"`:string (local s):string `\"b\"`:string):string)\n",
            Lowered(body, new InterpLower()));
    }

    [Fact]
    public void Interpolation_of_a_value_fills_an_invariant_handler()
    {
        var body = new List<MStmt> { Out(Interp(["n=", "!"], L("n", I))) };
        Assert.Equal(
            "expr (call Append `__o` (seq\n" +
            "  decl __ilh1 global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler = (new global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler (const 3):int (const 1):int (member InvariantCulture (type global::System.Globalization.CultureInfo)):global::System.Globalization.CultureInfo):global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler\n" +
            "  expr (call AppendLiteral (local __ilh1):global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler `\"n=\"`:string):void\n" +
            "  expr (call AppendFormatted (local __ilh1):global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler (local n):int):void\n" +
            "  expr (call AppendLiteral (local __ilh1):global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler `\"!\"`:string):void\n" +
            ") (call ToStringAndClear (local __ilh1):global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler):string):string)\n",
            Lowered(body, new InterpLower()));
    }

    [Fact]
    public void Interpolation_of_five_string_parts_uses_the_handler()
    {
        var body = new List<MStmt> { Out(Interp(["a", "b", "c"], L("x", S), L("y", S))) };
        string dump = Lowered(body, new InterpLower());
        Assert.Contains("DefaultInterpolatedStringHandler (const 3):int (const 2):int", dump);
        Assert.Equal(2, dump.Split("AppendFormatted").Length - 1);
        Assert.Equal(3, dump.Split("AppendLiteral").Length - 1);
    }

    [Fact]
    public void Interpolation_with_an_untyped_hole_stays_and_is_reported()
    {
        var body = new List<MStmt> { Out(Interp(["a", ""], C("x"))) };
        var ctx = new IlLowerContext();
        Assert.Equal("expr (call Append `__o` (interp \"a\" `x` \"\"))\n", Lowered(body, new InterpLower(ctx)));
        Assert.Equal(["interpolated string with a hole of unknown type"], ctx.Unsupported);
    }

    static List<MStmt> InterpProgram() =>
    [
        Decl("d", "double", C("1.5", D)), Decl("n", "int", C("42", I)), Decl("s", "string", C("\"x\"", S)), Decl("ns", "string?", C("null", NS)),
        Out(Interp(["d=", " n=", " s=", "!"], L("d", D), L("n", I), L("s", S))),
        Out(Interp(["<", ">"], L("s", S))),
        Out(Interp(["[", "]"], L("ns", NS))),
        Out(Interp(["", ""], L("ns", NS))),
        Out(Interp(["", " ", ""], L("n", I), L("d", D))),
    ];

    [Fact]
    public void Interpolation_lowering_behaves_like_Roslyn_under_a_comma_culture()
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try { Same(InterpProgram, expect: "d=1.5 n=42 s=x!<x>[]42 1.5"); }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    // ================= PatternTemps =================

    [Fact]
    public void Declaration_pattern_of_a_reference_type_is_an_as_into_a_typed_local()
    {
        var body = new List<MStmt> { new MIf { Cond = new MIs { E = L("o", O), TypeCs = "string s" }, Then = { Out(L("s", S)) } } };
        Assert.Equal("if (is (local o):object string s)\n  expr (call Append `__o` (local s):string)\n", MirDump.Of(body));
        Assert.Equal(
            "decl s string\n" +
            "if (Ne (seq\n" +
            "  assign (local s):string = (as string (local o):object):string\n" +
            ") (local s):string):string (null):object):bool\n" +
            "  expr (call Append `__o` (local s):string)\n", Lowered(body, new PatternTemps()));
    }

    [Fact]
    public void Declaration_pattern_of_another_type_is_isinst_then_a_cast()
    {
        var body = new List<MStmt> { new MWhile { Cond = new MIs { E = L("o", O), TypeCs = "int n" }, Body = { new MBreak() } } };
        Assert.Equal(
            "block\n" +
            "  decl n int\n" +
            "  while (And (is (local o):object int):bool (seq\n" +
            "    assign (local n):int = (cast int (local o):object):int\n" +
            "  ) (const true):bool):bool):bool\n" +
            "    break\n", Lowered(body, new PatternTemps()));
    }

    [Fact]
    public void Declaration_pattern_reads_a_computed_subject_once_through_a_temp()
    {
        var body = new List<MStmt> { Decl("ok", "bool", new MIs { E = new MCall { Name = "Next", Target = C("Src"), Type = O }, TypeCs = "Point p" }) };
        Assert.Equal(
            "decl __ilp1 object\n" +
            "decl p Point\n" +
            "decl ok bool = (And (is (seq\n" +
            "  assign (local __ilp1):object = (call Next `Src`):object\n" +
            ") (local __ilp1):object):object Point):bool (seq\n" +
            "  assign (local p):Point = (cast Point (local __ilp1):object):Point\n" +
            ") (const true):bool):bool):bool\n", Lowered(body, new PatternTemps()));
    }

    [Fact]
    public void Var_pattern_is_an_assignment_and_true()
    {
        var body = new List<MStmt> { new MExprStmt { E = Bin(AirBinOp.And, new MIs { E = L("a", I), TypeCs = "var x" }, Bin(AirBinOp.Gt, L("x", I), K(1), B), B) } };
        Assert.Equal(
            "decl x int\n" +
            "expr (And (seq\n" +
            "  assign (local x):int = (local a):int\n" +
            ") (const true):bool):bool (Gt (local x):int (const 1):int):bool):bool\n", Lowered(body, new PatternTemps()));
    }

    [Fact]
    public void Pattern_in_an_expression_bodied_lambda_gives_the_lambda_a_block_body()
    {
        var f = new MLambda { Params = { "x" }, ExprBody = Bin(AirBinOp.And, new MIs { E = L("x", O), TypeCs = "int n" }, Bin(AirBinOp.Gt, L("n", I), K(3), B), B), Type = new AirType.ClrRef("global::System.Func", typeof(Func<,>), [O, B]) };
        var body = new List<MStmt> { Decl("f", "global::System.Func<object, bool>", f) };
        string dump = Lowered(body, new PatternTemps());
        Assert.StartsWith("decl f global::System.Func<object, bool> = (lambda (x)\n  decl n int\n  return (And (And (is (local x):object int):bool", dump);
        Assert.EndsWith("):global::System.Func<object, bool>\n", dump);
    }

    [Fact]
    public void Plain_type_and_null_tests_stay()
    {
        var body = new List<MStmt> { Out(new MIs { E = L("o", O), TypeCs = "string" }), Out(new MIs { E = L("o", O), TypeCs = "null" }) };
        string before = MirDump.Of(body);
        Assert.Equal(before, Lowered(body, new PatternTemps()));
    }

    static List<MStmt> PatternProgram()
    {
        var o = new List<MStmt>();
        foreach (var v in new[] { "42", "\"hi\"", "2.5" })
        {
            // one scope per value: C# scopes `if` pattern variables to the enclosing block
            var scope = new MBlock();
            o.Add(scope);
            string name = "o" + o.Count;
            scope.Body.Add(Decl(name, "object", C(v, O)));
            scope.Body.Add(new MIf { Cond = new MIs { E = L(name, O), TypeCs = "int n" }, Then = { Out(Bin(AirBinOp.Add, L("n", I), K(1), I)) }, Else = [new MIf { Cond = new MIs { E = L(name, O), TypeCs = "string s" }, Then = { Out(L("s", S)) }, Else = [Out("?")] }] });
            scope.Body.Add(new MIf { Cond = Bin(AirBinOp.And, new MIs { E = L(name, O), TypeCs = "var any" }, new MIs { E = L("any", O), TypeCs = "double" }, B), Then = { Out("d") } });
        }
        o.Add(new MIf { Cond = new MIs { E = new MCall { Target = C("Src"), Name = "Num", Type = O }, TypeCs = "int m" }, Then = { Out(L("m", I)) } });
        o.Add(Out(C("Src.Calls", I)));
        o.Add(Decl("f", "global::System.Func<object, bool>", new MLambda { Params = { "x" }, ExprBody = Bin(AirBinOp.And, new MIs { E = L("x", O), TypeCs = "int q" }, Bin(AirBinOp.Gt, L("q", I), K(3), B), B), Type = new AirType.ClrRef("global::System.Func", typeof(Func<,>), [O, B]) }));
        o.Add(Out(new MInvoke { Target = L("f", O), Args = { C("(object)5") } }));
        o.Add(Out(new MInvoke { Target = L("f", O), Args = { C("(object)2") } }));
        return o;
    }

    const string SrcClass = "public static class Src { public static int Calls; public static object Num() { Calls++; return 7; } }";

    [Fact]
    public void Pattern_lowering_behaves_like_Roslyn() => Same(PatternProgram, SrcClass, "43hi?d71TrueFalse");

    // ================= UsingLockLower =================

    [Fact]
    public void Using_a_class_disposes_after_a_null_check()
    {
        var body = new List<MStmt> { new MUsing { Name = "r", Value = new MNew { TypeCs = "CRes", Type = Alice("CRes", "class") }, Body = { Out("in") } } };
        Assert.Equal(
            "block\n" +
            "  decl r CRes = (new CRes):CRes\n" +
            "  try\n" +
            "    expr (call Append `__o` `\"in\"`)\n" +
            "  finally\n" +
            "    if (Ne (local r):CRes (null):object):bool\n" +
            "      expr (call Dispose (local r):CRes):void\n", Lowered(body, new UsingLockLower()));
    }

    [Fact]
    public void Using_a_struct_calls_Dispose_directly()
    {
        var body = new List<MStmt> { new MUsing { Name = "r", Value = new MNew { TypeCs = "SRes", Type = Alice("SRes", "struct") } } };
        Assert.Equal(
            "block\n" +
            "  decl r SRes = (new SRes):SRes\n" +
            "  try\n" +
            "  finally\n" +
            "    expr (call Dispose (local r):SRes):void\n", Lowered(body, new UsingLockLower()));
    }

    [Fact]
    public void Lock_enters_the_monitor_with_a_taken_flag()
    {
        var body = new List<MStmt> { new MBlock { Lock = L("gate", O), Body = { Out("x") } } };
        Assert.Equal(
            "block\n" +
            "  decl __illock1 object = (local gate):object\n" +
            "  decl __iltaken2 bool = (const false):bool\n" +
            "  try\n" +
            "    expr (call Enter (type global::System.Threading.Monitor) (local __illock1):object (arg \"ref \" (local __iltaken2):bool)):void\n" +
            "    expr (call Append `__o` `\"x\"`)\n" +
            "  finally\n" +
            "    if (local __iltaken2):bool\n" +
            "      expr (call Exit (type global::System.Threading.Monitor) (local __illock1):object):void\n", Lowered(body, new UsingLockLower()));
    }

    [Fact]
    public void Lock_on_a_System_Threading_Lock_uses_its_scope()
    {
        var lockT = new AirType.ClrRef("global::System.Threading.Lock", typeof(Lock), []);
        var body = new List<MStmt> { new MBlock { Lock = L("l", lockT) } };
        Assert.Equal(
            "block\n" +
            "  decl __ilscope1 global::System.Threading.Lock.Scope = (call EnterScope (local l):global::System.Threading.Lock):global::System.Threading.Lock.Scope\n" +
            "  try\n" +
            "  finally\n" +
            "    expr (call Dispose (local __ilscope1):global::System.Threading.Lock.Scope):void\n", Lowered(body, new UsingLockLower()));
    }

    [Fact]
    public void Await_using_and_untyped_resources_stay_and_are_reported()
    {
        var body = new List<MStmt>
        {
            new MUsing { Await = true, Name = "a", Value = C("x", O) },
            new MUsing { Name = "b", Value = C("y") },
            new MUsing { Name = "c", Value = C("z", Alice("SRes", "struct") with { Nullable = true }) },
        };
        var ctx = new IlLowerContext();
        Assert.Equal("using await a = `x`:object\nusing b = `y`\nusing c = `z`:SRes?\n", Lowered(body, new UsingLockLower(ctx)));
        Assert.Equal(3, ctx.Unsupported.Count);
    }

    static List<MStmt> UsingProgram() =>
    [
        new MUsing { Name = "c", Value = new MNew { TypeCs = "CRes", Type = Alice("CRes", "class") }, Body = { Out("c;") } },
        new MUsing { Name = "n", Value = C("(CRes?)null", Alice("CRes", "class")), Body = { Out("n;") } },
        new MUsing { Name = "s", Value = new MNew { TypeCs = "SRes", Type = Alice("SRes", "struct") }, Body = { Out("s;") } },
        Out(C("Log.Text")),
        Decl("gate", "object", C("new object()", O)),
        new MBlock { Lock = L("gate", O), Body = { Out(C("global::System.Threading.Monitor.IsEntered(gate)")) } },
        Out(C("global::System.Threading.Monitor.IsEntered(gate)")),
        Decl("l", "global::System.Threading.Lock", C("new global::System.Threading.Lock()", new AirType.ClrRef("global::System.Threading.Lock", typeof(Lock), []))),
        new MBlock { Lock = L("l", new AirType.ClrRef("global::System.Threading.Lock", typeof(Lock), [])), Body = { Out(C("l.IsHeldByCurrentThread")) } },
        Out(C("l.IsHeldByCurrentThread")),
    ];

    const string ResClasses =
        "public static class Log { public static System.Text.StringBuilder Text = new(); }\n" +
        "public sealed class CRes : IDisposable { public void Dispose() => Log.Text.Append(\"C\"); }\n" +
        "public struct SRes : IDisposable { public void Dispose() => Log.Text.Append(\"S\"); }\n";

    [Fact]
    public void Using_and_lock_lowering_behave_like_Roslyn() => Same(UsingProgram, ResClasses, "c;n;s;CSTrueFalseTrueFalse");

    // ================= SwitchLower =================

    static List<MStmt> Arm(string text) => [Out(text)];

    [Fact]
    public void Dense_int_case_is_a_jump_table()
    {
        var body = new List<MStmt> { Chain(() => L("i", I), [([0, 1], Arm("a")), ([2], Arm("b")), ([4], Arm("c")), ([5, 1], Arm("d"))], Arm("-")) };
        Assert.Equal(
            "switch (local i):int min 0 table [0, 0, 1, -1, 2, 3]\n" +
            "case 0\n" +
            "  expr (call Append `__o` `\"a\"`)\n" +
            "case 1\n" +
            "  expr (call Append `__o` `\"b\"`)\n" +
            "case 2\n" +
            "  expr (call Append `__o` `\"c\"`)\n" +
            "case 3\n" +
            "  expr (call Append `__o` `\"d\"`)\n" +
            "default\n" +
            "  expr (call Append `__o` `\"-\"`)\n", Lowered(body, new SwitchLower()));
    }

    [Fact]
    public void Sparse_or_small_int_case_stays_a_compare_chain()
    {
        var sparse = new List<MStmt> { Chain(() => L("i", I), [([1], Arm("a")), ([100], Arm("b")), ([1000], Arm("c"))], null) };
        var small = new List<MStmt> { Chain(() => L("i", I), [([1], Arm("a")), ([2], Arm("b"))], null) };
        Assert.Equal(MirDump.Of(sparse), Lowered(sparse, new SwitchLower()));
        Assert.Equal(MirDump.Of(small), Lowered(small, new SwitchLower()));
    }

    [Fact]
    public void Char_case_with_a_guard_arm_switches_the_prefix()
    {
        var guard = new MIf { Cond = Bin(AirBinOp.Gt, L("c", Ch), K('x'), B), Then = Arm("big") };
        var head = Chain(() => L("c", Ch), [(['a'], Arm("a")), (['b'], Arm("b")), (['c'], Arm("c"))], [guard]);
        var body = new List<MStmt> { head };
        string dump = Lowered(body, new SwitchLower());
        Assert.Equal(
            "switch (local c):char min 97 table [0, 1, 2]\n" +
            "case 0\n" +
            "  expr (call Append `__o` `\"a\"`)\n" +
            "case 1\n" +
            "  expr (call Append `__o` `\"b\"`)\n" +
            "case 2\n" +
            "  expr (call Append `__o` `\"c\"`)\n" +
            "default\n" +
            "  if (Gt (local c):char (const 'x'):char):bool\n" +
            "    expr (call Append `__o` `\"big\"`)\n", dump);
    }

    [Fact]
    public void String_case_switches_on_length_then_compares_ordinally()
    {
        var body = new List<MStmt> { Chain(() => L("s", NS), [(["apple"], Arm("1")), (["kiwi"], Arm("2")), (["fig"], Arm("3")), (["pear", "plum"], Arm("4"))], Arm("-")) };
        string dump = Lowered(body, new SwitchLower());
        Assert.Equal(
            "decl __ilarm1 int = (const -1):int\n" +
            "if (Ne (local s):string? (null):object):bool\n" +
            "  switch (member Length (local s):string?):int min 3 table [0, 1, 2]\n" +
            "  case 0\n" +
            "    if (call Equals (type string) (local s):string? (const \"fig\"):string):bool\n" +
            "      assign (local __ilarm1):int = (const 2):int\n" +
            "  case 1\n" +
            "    if (call Equals (type string) (local s):string? (const \"kiwi\"):string):bool\n" +
            "      assign (local __ilarm1):int = (const 1):int\n" +
            "    else\n" +
            "      if (call Equals (type string) (local s):string? (const \"pear\"):string):bool\n" +
            "        assign (local __ilarm1):int = (const 3):int\n" +
            "      else\n" +
            "        if (call Equals (type string) (local s):string? (const \"plum\"):string):bool\n" +
            "          assign (local __ilarm1):int = (const 3):int\n" +
            "  case 2\n" +
            "    if (call Equals (type string) (local s):string? (const \"apple\"):string):bool\n" +
            "      assign (local __ilarm1):int = (const 0):int\n" +
            "switch (local __ilarm1):int min 0 table [0, 1, 2, 3]\n" +
            "case 0\n" +
            "  expr (call Append `__o` `\"1\"`)\n" +
            "case 1\n" +
            "  expr (call Append `__o` `\"2\"`)\n" +
            "case 2\n" +
            "  expr (call Append `__o` `\"3\"`)\n" +
            "case 3\n" +
            "  expr (call Append `__o` `\"4\"`)\n" +
            "default\n" +
            "  expr (call Append `__o` `\"-\"`)\n", dump);
        Assert.DoesNotContain("GetHashCode", dump);
    }

    [Fact]
    public void Type_case_becomes_an_isinst_chain()
    {
        var body = new List<MStmt>
        {
            new MIf
            {
                Cond = new MIs { E = L("o", O), TypeCs = "int n" }, Then = Arm("i"),
                Else = [new MIf { Cond = new MIs { E = L("o", O), TypeCs = "string s" }, Then = Arm("s"), Else = Arm("-") }],
            },
        };
        string dump = Lowered(body, out var ctx);
        Assert.Empty(ctx.Unsupported);
        Assert.Contains("assign (local s):string = (as string (local o):object)", dump);
        Assert.StartsWith("decl n int\nif (And (is (local o):object int):bool", dump);
        Assert.Contains("  decl s string\n", dump);
        Assert.DoesNotContain("object int n", dump);
    }

    static List<MStmt> SwitchProgram() =>
    [
        new MFor
        {
            Var = "i", VarType = "int", Start = K(-2), Cmp = "<=", End = K(12),
            Body = { Chain(() => L("i", I), [([0, 1], Arm("a")), ([2], Arm("b")), ([4], Arm("c")), ([5, 1], Arm("d")), ([7], [new MContinue()]), ([11], [new MBreak()])], Arm("-")), Out(".") },
        },
        Out(";"),
        new MForeach
        {
            Vars = { "s" }, Coll = C("new string?[] { \"apple\", \"kiwi\", \"fig\", \"banana\", \"pear\", null, \"plum\", \"\", \"Fig\" }", new AirType.Array(NS)),
            Body = { Chain(() => L("s", NS), [(["apple"], Arm("1")), (["kiwi"], Arm("2")), (["fig"], Arm("3")), (["pear", "plum"], Arm("4"))], Arm("-")) },
        },
        Out(";"),
        new MForeach
        {
            Vars = { "c" }, Coll = C("\"abcz\"", S),
            Body = { Chain(() => L("c", Ch), [(['a'], Arm("A")), (['b'], Arm("B")), (['c'], Arm("C"))], [new MIf { Cond = Bin(AirBinOp.Gt, L("c", Ch), K('x'), B), Then = Arm("big") }]) },
        },
    ];

    [Fact]
    public void Switch_lowering_behaves_like_Roslyn() => Same(SwitchProgram, expect: "-.-.a.a.b.-.c.d.-.-.-.-.;123-4-4--;ABCbig");

    // ================= TupleLower =================

    [Fact]
    public void Tuple_literal_is_a_ValueTuple_constructor_that_keeps_its_names()
    {
        var t = new MTuple { Items = { new MArg { Prefix = "x: ", E = K(1) }, new MArg { Prefix = "y: ", E = L("s", S) } } };
        var body = new List<MStmt> { new MExprStmt { E = t } };
        Assert.Equal("expr (new global::System.ValueTuple<int, string> (const 1):int (local s):string):(int x, string y)\n", Lowered(body, new TupleLower()));
    }

    [Fact]
    public void Named_element_reads_are_ItemN_and_Rest_past_seven()
    {
        var nine = new AirType.Tuple(Enumerable.Repeat(I, 9).ToList(), Enumerable.Range(1, 9).Select(k => "n" + k).ToList());
        var pair = new AirType.Tuple([I, S], ["count", "name"]);
        var body = new List<MStmt> { Out(new MMember { Target = L("t", nine), Name = "n9" }), Out(new MMember { Target = L("p", pair), Name = "name" }) };
        Assert.Equal(
            "expr (call Append `__o` (member Item2 (member Rest (local t):(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9)):(int, int)):int)\n" +
            "expr (call Append `__o` (member Item2 (local p):(int count, string name)):string)\n", Lowered(body, new TupleLower()));
    }

    [Fact]
    public void Deconstruction_reads_items_from_a_temp_or_calls_Deconstruct()
    {
        var pair = new AirType.Tuple([I, I], null);
        var body = new List<MStmt>
        {
            new MDeconstruct { Names = { "a", "b" }, Value = new MTuple { Items = { L("b", I), L("a", I) }, Type = pair } },
            new MDeconstruct { Names = { "k", "_" }, Declare = true, Value = L("kv", KV) },
        };
        Assert.Equal(
            "decl __ilt1 global::System.ValueTuple<int, int> = (new global::System.ValueTuple<int, int> (local b):int (local a):int):(int, int)\n" +
            "assign (local a):int = (member Item1 (local __ilt1):(int, int)):int\n" +
            "assign (local b):int = (member Item2 (local __ilt1):(int, int)):int\n" +
            "decl __ilt2 global::System.Collections.Generic.KeyValuePair<string, int> = (local kv):global::System.Collections.Generic.KeyValuePair<string, int>\n" +
            "decl k string\n" +
            "decl __ildiscard3 int\n" +
            "expr (call Deconstruct (local __ilt2):global::System.Collections.Generic.KeyValuePair<string, int> (arg \"out \" (local k):string) (arg \"out \" (local __ildiscard3):int)):void\n",
            Lowered(body, new TupleLower()));
    }

    [Fact]
    public void Tuple_local_types_are_spelled_as_ValueTuple_and_record_names()
    {
        var body = new List<MStmt> { Decl("t", "(int a, (int, string c) d)?"), Decl("xs", "global::System.Collections.Generic.List<(int, int)>[]") };
        var ctx = new IlLowerContext();
        Assert.Equal(
            "decl t global::System.ValueTuple<int, global::System.ValueTuple<int, string>>?\n" +
            "decl xs global::System.Collections.Generic.List<global::System.ValueTuple<int, int>>[]\n", Lowered(body, new TupleLower(ctx)));
        Assert.Equal(["a", "d", null, "c"], ctx.TupleLocalNames["t"]);
        Assert.False(ctx.TupleLocalNames.ContainsKey("xs"));
        Assert.Null(TupleLower.Spell("(int", null));

        var cast = new List<MStmt> { Out(new MCast { TypeCs = "(int, int)", E = L("o", O) }) };
        Assert.Equal("expr (call Append `__o` (cast global::System.ValueTuple<int, int> (local o):object))\n", Lowered(cast, new TupleLower()));
    }

    static List<MStmt> TupleProgram()
    {
        var pair = new AirType.Tuple([I, I], null);
        var nine = new AirType.Tuple(Enumerable.Repeat(I, 9).ToList(), Enumerable.Range(1, 9).Select(k => "n" + k).ToList());
        return
        [
            Decl("a", "int", K(1)), Decl("b", "int", K(2)),
            new MDeconstruct { Names = { "a", "b" }, Value = new MTuple { Items = { L("b", I), L("a", I) }, Type = pair } },
            Out(L("a", I)), Out(L("b", I)),
            Decl("t", "(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9)", new MTuple { Items = Enumerable.Range(1, 9).Select(k => (MExpr)K(k * 10)).ToList(), Type = nine }),
            Out(new MMember { Target = L("t", nine), Name = "n9" }), Out(new MMember { Target = L("t", nine), Name = "n3" }),
            Decl("kv", "global::System.Collections.Generic.KeyValuePair<string, int>", C("new global::System.Collections.Generic.KeyValuePair<string, int>(\"k\", 5)", KV)),
            new MDeconstruct { Names = { "k", "v" }, Declare = true, Value = L("kv", KV) },
            Out(L("k", S)), Out(L("v", I)),
        ];
    }

    [Fact]
    public void Tuple_lowering_behaves_like_Roslyn() => Same(TupleProgram, expect: "219030k5");

    // ================= NullableLift =================

    [Fact]
    public void Lifted_addition_checks_both_HasValue_flags()
    {
        var body = new List<MStmt> { Out(Bin(AirBinOp.Add, L("a", NI), L("b", NI), NI)) };
        Assert.Equal(
            "expr (call Append `__o` (cond (BitAnd (member HasValue (local a):int?):bool (member HasValue (local b):int?):bool):bool (new global::System.Nullable<int> (Add (call GetValueOrDefault (local a):int?):int (call GetValueOrDefault (local b):int?):int):int):int? (default):int?):int?)\n", Lowered(body, new NullableLift()));
    }

    [Fact]
    public void Lifted_comparison_with_a_computed_operand_uses_a_temp()
    {
        var body = new List<MStmt> { Out(Bin(AirBinOp.Lt, L("a", NI), new MCall { Target = C("N"), Name = "Three", Type = I }, B)) };
        Assert.Equal(
            "expr (call Append `__o` (seq\n" +
            "  decl __iln1 int = (call Three `N`):int\n" +
            ") (BitAnd (Lt (call GetValueOrDefault (local a):int?):int (local __iln1):int):bool (member HasValue (local a):int?):bool):bool):bool)\n",
            Lowered(body, new NullableLift()));
    }

    [Fact]
    public void Null_tests_and_coalesce_read_HasValue_and_GetValueOrDefault()
    {
        var body = new List<MStmt>
        {
            Out(Bin(AirBinOp.Eq, L("a", NI), K(null), B)),
            Out(Bin(AirBinOp.Ne, C("null"), L("a", NI), B)),
            Out(Bin(AirBinOp.Coalesce, L("a", NI), K(7), I)),
            Out(new MUnary { Op = AirUnOp.Neg, E = L("a", NI), Type = NI }),
        };
        Assert.Equal(
            "expr (call Append `__o` (Not (member HasValue (local a):int?):bool):bool)\n" +
            "expr (call Append `__o` (member HasValue (local a):int?):bool)\n" +
            "expr (call Append `__o` (call GetValueOrDefault (local a):int? (const 7):int):int)\n" +
            "expr (call Append `__o` (cond (member HasValue (local a):int?):bool (new global::System.Nullable<int> (Neg (call GetValueOrDefault (local a):int?):int):int):int? (default):int?):int?)\n",
            Lowered(body, new NullableLift()));
    }

    [Fact]
    public void Three_valued_bool_operators_stay_and_are_reported()
    {
        var nb = B with { Nullable = true };
        var body = new List<MStmt> { Out(Bin(AirBinOp.BitAnd, L("p", nb), L("q", nb), nb)) };
        var ctx = new IlLowerContext();
        Assert.Equal(MirDump.Of(body), Lowered(body, new NullableLift(ctx)));
        Assert.Equal(["three-valued `bool?` operator &"], ctx.Unsupported);
    }

    static List<MStmt> NullableProgram()
    {
        var arr = new AirType.Array(NI);
        var row = new List<MStmt>
        {
            Out(Bin(AirBinOp.Add, L("a", NI), L("b", NI), NI)), Out(","),
            Out(Bin(AirBinOp.Mul, L("a", NI), new MCall { Target = C("N"), Name = "Three", Type = NI }, NI)), Out(","),
            Out(Bin(AirBinOp.Lt, L("a", NI), L("b", NI), B)), Out(","),
            Out(Bin(AirBinOp.Ge, L("a", NI), new MCall { Target = C("N"), Name = "Two", Type = I }, B)), Out(","),
            Out(Bin(AirBinOp.Eq, L("a", NI), L("b", NI), B)), Out(","),
            Out(Bin(AirBinOp.Ne, L("a", NI), L("b", NI), B)), Out(","),
            Out(Bin(AirBinOp.Eq, L("a", NI), K(3), B)), Out(","),
            Out(Bin(AirBinOp.Eq, L("a", NI), K(null), B)), Out(","),
            Out(Bin(AirBinOp.Coalesce, L("a", NI), K(7), I)), Out(","),
            Out(Bin(AirBinOp.Coalesce, L("a", NI), new MCall { Target = C("N"), Name = "Two", Type = I }, I)), Out(","),
            Out(Bin(AirBinOp.Coalesce, L("a", NI), L("b", NI), NI)), Out(","),
            Out(new MUnary { Op = AirUnOp.Neg, E = L("a", NI), Type = NI }), Out(";"),
        };
        return
        [
            new MForeach
            {
                Vars = { "a" }, Coll = C("new int?[] { null, 3, -2 }", arr),
                Body = { new MForeach { Vars = { "b" }, Coll = C("new int?[] { null, 3, -2 }", arr), Body = row } },
            },
            Out(C("N.Calls")),
        ];
    }

    const string NClass = "public static class N { public static int Calls; public static int? Three() { Calls++; return 3; } public static int Two() { Calls++; return 2; } }";

    [Fact]
    public void Nullable_lifting_behaves_like_Roslyn() => Same(NullableProgram, NClass);

    // ================= the pipeline and the IL emitter =================

    [Fact]
    public void Lowering_reaches_lambda_bodies_and_leaves_the_csharp_pipeline_alone()
    {
        var body = new List<MStmt>
        {
            Decl("f", "global::System.Action", new MLambda { Body = [new MForeach { Vars = { "x" }, Coll = L("xs", new AirType.Array(I)), Body = { Out(Interp(["", "!"], L("x", I))) } }] }),
        };
        string dump = Lowered(body, out var ctx);
        Assert.Empty(ctx.Unsupported);
        Assert.DoesNotContain("foreach", dump);
        Assert.DoesNotContain("(interp", dump);
        Assert.Contains("for __ili2 int", dump);
        Assert.DoesNotContain(Pipeline.Default, p => p().Name.StartsWith("il-"));
    }

    [Fact(Skip = "pending: the body spells collections as C# text (`new[] { 1, 2, 3, 4, 5 }`), which AirToIl.CsText does not parse (il-emit)"), Trait("Category", "Pending")]
    public void Il_foreach() => SameOnIl(ForeachProgram);

    [Fact]
    public void Il_interp() => SameOnIl(InterpProgram);

    [Fact(Skip = "pending: the lowered body keeps an MLambda, which AirToIl emits after closure conversion (il-emit, D1.5)"), Trait("Category", "Pending")]
    public void Il_patterns() => SameOnIl(PatternProgram, SrcClass);

    [Fact]
    public void Il_using_lock() => SameOnIl(UsingProgram, ResClasses);

    [Fact]
    public void Il_switch() => SameOnIl(SwitchProgram);

    [Fact]
    public void Il_tuples() => SameOnIl(TupleProgram);

    [Fact]
    public void Il_nullable() => SameOnIl(NullableProgram, NClass);

    static void SameOnIl(Func<List<MStmt>> make, string extra = "")
    {
        string expected = RunCs(make(), extra);
        var lowered = make();
        Assert.Empty(IlLowering.Run(lowered).Unsupported);
        Assert.Equal(expected, RunIl(lowered, extra));
    }

    /// <summary>The lowered body through the IL emitter (AirToIl.CompileBody) as `Program.Run()`; the C# helper
    /// classes of <paramref name="extra"/> are compiled by Roslyn into a referenced assembly.</summary>
    static string RunIl(List<MStmt> body, string extra)
    {
        var refs = new List<string>();
        if (extra.Length > 0)
        {
            string src = "using System;\nusing System.Collections.Generic;\n#nullable enable\n" + extra;
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(f => MetadataReference.CreateFromFile(f));
            string name = "ilextra" + Guid.NewGuid().ToString("N");
            var comp = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(src)], trusted,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            string path = Path.Combine(Path.GetTempPath(), name + ".dll");
            var r = comp.Emit(path);
            Assert.True(r.Success, string.Join("\n", r.Diagnostics));
            refs.Add(path);
        }
        var res = global::Alice.Compiler.Backend.Il.AirToIl.CompileBody(body, "lowered", refs);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        Assert.Empty(global::Alice.Tests.Backends.AirToIlTests.DecodeCheck(res.Pe!));
        // ILVerify resolves framework assemblies only: images that reference the helper assembly are decoded, not verified
        if (refs.Count == 0)
        {
            var verify = global::Alice.Tests.Backends.AirToIlTests.IlVerify(res.Pe!);
            Assert.True(verify.Count == 0, "ILVerify:\n" + string.Join("\n", verify));
        }
        var asm = Assembly.Load(res.Pe!);
        return (string)asm.GetType("Program")!.GetMethod("Run")!.Invoke(null, null)!;
    }

    // ---- oracle: Roslyn compiles the original and the lowered body ----

    /// <summary>The original body and the lowered one print the same output; Roslyn lowers only the original.</summary>
    static void Same(Func<List<MStmt>> make, string extra = "", string? expect = null)
    {
        string before = RunCs(make(), extra);
        if (expect != null) Assert.Equal(expect, before);
        var lowered = make();
        var ctx = IlLowering.Run(lowered);
        Assert.Empty(ctx.Unsupported);
        string dump = MirDump.Of(lowered);
        foreach (var gone in new[] { "foreach", "using", "(interp", "(tuple", "deconstruct", " lock " })
            Assert.DoesNotContain(gone, dump);
        new CSharpView().Go(lowered);
        Assert.Equal(before, RunCs(lowered, extra));
    }

    static string RunCs(List<MStmt> body, string extra)
    {
        var p = new Printer("", 2);
        p.Stmts(body);
        string src = "using System;\nusing System.Collections.Generic;\n#nullable enable\n" + extra +
            "\npublic static class Program\n{\n    public static string Run()\n    {\n        var __o = new global::System.Text.StringBuilder();\n" +
            p + "        return __o.ToString();\n    }\n}\n";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(f => MetadataReference.CreateFromFile(f));
        var comp = CSharpCompilation.Create("lower" + Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(src)], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var ms = new MemoryStream();
        var r = comp.Emit(ms);
        if (!r.Success)
            Assert.Fail(string.Join("\n", r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + src);
        var asm = Assembly.Load(ms.ToArray());
        return (string)asm.GetType("Program")!.GetMethod("Run")!.Invoke(null, null)!;
    }

    /// <summary>
    /// The IL-only helper nodes as C# the Printer prints with the same meaning: a block expression is an
    /// immediately invoked lambda, a jump table an if chain over its slots, declarations without a value start at default.
    /// </summary>
    sealed class CSharpView : MirRewriter
    {
        public void Go(List<MStmt> body)
        {
            Stmts(body);
            Fix(body);
        }

        protected override void Visit(MStmt s)
        {
            foreach (var b in Lists(s)) Fix(b);
            if (s is MDecl { Init: null, TypeCs: { } t } d) d.Init = new MCode("default(" + t + ")");
        }

        static IEnumerable<List<MStmt>> Lists(MStmt s) => s switch
        {
            MIf i => i.Else != null ? [i.Then, i.Else] : [i.Then],
            MWhile w => [w.Body],
            MFor f => [f.Body],
            MForeach fe => [fe.Body],
            MTry t => [t.Body, .. t.Catches.Select(c => c.Body), .. t.Finally != null ? [t.Finally] : Array.Empty<List<MStmt>>()],
            MBlock b => [b.Body],
            MUsing u => [u.Body],
            _ => [],
        };

        void Fix(List<MStmt> list)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] is AirSwitch sw) list[i] = Switch(sw);
        }

        MStmt Switch(AirSwitch sw)
        {
            sw.Value = Expr(sw.Value);
            foreach (var c in sw.Cases) Go(c);
            if (sw.Default != null) Go(sw.Default);
            string v = Printer.E(sw.Value);
            MIf? head = null, last = null;
            for (int c = 0; c < sw.Cases.Count; c++)
            {
                var slots = Enumerable.Range(0, sw.Table.Count).Where(k => sw.Table[k] == c).Select(k => $"(long)({v}) - {sw.Min}L == {k}L");
                var arm = new MIf { Cond = new MCode(string.Join(" || ", slots)), Then = sw.Cases[c] };
                if (last == null) head = arm; else last.Else = [arm];
                last = arm;
            }
            if (last == null) return new MBlock { Body = sw.Default ?? [] };
            last.Else = sw.Default;
            return head!;
        }

        protected override MExpr Rewrite(MExpr e)
        {
            switch (e)
            {
                case AirSeq q:
                    Go(q.Pre);
                    q.Value = Expr(q.Value);
                    return new MInvoke { Target = new MCast { TypeCs = $"global::System.Func<{q.Type!.ToCs()}>", E = new MLambda { Body = [.. q.Pre, new MReturn { Value = q.Value }] } } };
                case AirAs a:
                    a.E = Expr(a.E);
                    return new MCode("(" + Printer.E(a.E) + " as " + a.TypeCs + ")");
                case MLambda { Body: { } b }:
                    Fix(b);
                    break;
            }
            return e;
        }
    }
}
