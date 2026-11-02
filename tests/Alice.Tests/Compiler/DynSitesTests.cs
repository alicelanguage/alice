using Alice.Compiler;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Alice.Runtime;

namespace Alice.Tests.Compiler;

/// <summary>
/// The IL backend's dynamic-site pass (Air/Passes/DynSites.cs, backend plan D1.9, ADR 0033): every operation on an
/// `Any` value becomes an <see cref="AirDynCall"/> on its own <see cref="DynSite"/> field, and AL0232 rejects dynamic
/// sites under `native = true` and on wasm. Bodies come from the real binder + lowerer (as the IL backend gets them)
/// and are compared as AIR dumps; AirDynCall prints as `(dyn.Kind#site name target args)` here.
/// </summary>
public class DynSitesTests
{
    const string Program =
        "class P\n" +
        "  property x : Int32\n" +
        "  def initialize(@x : Int32)\n" +
        "  end\n" +
        "  def scaled(k : Int32) : Int32\n" +
        "    @x * k\n" +
        "  end\n" +
        "end\n" +
        "v : Any = P.new(2)\n" +
        "puts v.scaled(3)\n" +
        "v.x = 5\n" +
        "v.x += 1\n" +
        "w : Int32 = v.x\n" +
        "n : Any = 4\n" +
        "puts n + 1\n" +
        "puts -n\n" +
        "if v.x > 3\n" +
        "  puts \"big\"\n" +
        "end\n" +
        "xs : Any = [1, 2, 3]\n" +
        "xs[0] = 9\n" +
        "puts xs[0]\n" +
        "f : Any = ->(a : Int32) { a * 2 }\n" +
        "puts f.(4)\n" +
        "puts w\n";

    /// <summary>The Main body as the IL backend lowers it: binder, lowerer, MIR passes.</summary>
    static List<MStmt> LowerMain(string src)
    {
        var ast = Parser.ParseFile(src, "main.al");
        var binder = new Binder([ast], new ClrTypes());
        binder.Bind();
        var lowerer = new Lowerer(binder) { Sequential = true, File = "main.al", Type = binder.Program, Method = null };
        var stmts = ast.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList();
        var body = lowerer.LowerBody(stmts, Tail.None, ast.HoistedLocals, stmts[0].Line);
        Pipeline.Run(body);
        return body;
    }

    static string Dump(List<MStmt> body) => MirDump.Of(body);

    [Fact]
    public void Dynamic_operations_become_sites()
    {
        var body = LowerMain(Program);
        var pass = new DynSites("main.al");
        pass.Run(body);
        Assert.Empty(pass.Unsupported);
        Assert.Equal(Expected, Dump(body));
    }

    const string Expected =
        "decl v dynamic = (new P (const 2):int) @9\n" +
        "expr (call puts _ (dyn.Call#0 scaled (local v):dynamic (const 3):int):dynamic) @10\n" +
        "expr (dyn.SetMember#1 x (local v):dynamic (const 5):int):void @11\n" +
        "expr (dyn.SetMember#14 x (local v):dynamic (dyn.BinaryOp#13 Add (dyn.GetMember#2 x (local v):dynamic):dynamic (const 1):int):dynamic):void @12\n" +
        "decl w int = (dyn.Convert#4 int (dyn.GetMember#3 x (local v):dynamic):dynamic):int @13\n" +
        "decl n dynamic = (const 4):int @14\n" +
        "expr (call puts _ (dyn.BinaryOp#5 Add (local n):dynamic (const 1):int):dynamic) @15\n" +
        "expr (call puts _ (dyn.UnaryOp#6 Neg (local n):dynamic):dynamic) @16\n" +
        "if (dyn.Convert#9 bool (dyn.BinaryOp#8 Gt (dyn.GetMember#7 x (local v):dynamic):dynamic (const 3):int):dynamic):bool @17\n" +
        "  expr (call puts _ (const \"big\"):string) @18\n" +
        "decl xs dynamic = (newarray int (items (const 1):int (const 2):int (const 3):int)) @20\n" +
        "expr (dyn.SetIndex#10 (local xs):dynamic (const 0):int (const 9):int):void @21\n" +
        "expr (call puts _ (dyn.GetIndex#11 (local xs):dynamic (const 0):int):dynamic) @22\n" +
        "decl f dynamic = (lambda static (int a_1) (Mul (local a_1):int (const 2):int):int) @23\n" +
        "expr (call puts _ (dyn.Invoke#12 (local f):dynamic (const 4):int):dynamic) @24\n" +
        "expr (call puts _ (local w):int) @25\n";

    [Fact]
    public void Each_site_is_declared_once_with_its_position()
    {
        var body = LowerMain(Program);
        var pass = new DynSites("main.al");
        pass.Run(body);
        Assert.Equal(Enumerable.Range(0, pass.Sites.Count), pass.Sites.Select(s => s.Id));
        Assert.All(pass.Sites, s => Assert.Equal("site" + s.Id, s.Field));
        Assert.All(pass.Sites, s => Assert.True(s.Span is { File: "main.al", Line: > 0 }, s + " has no position"));
        var call = Assert.Single(pass.Sites, s => s.Kind == DynKind.Call);
        Assert.Equal(("scaled", 1, 10), (call.Name, call.Arity, call.Span!.Value.Line));
        Assert.Contains(pass.Sites, s => s is { Kind: DynKind.SetMember, Name: "x", Span.Line: 11 });
        Assert.Contains(pass.Sites, s => s is { Kind: DynKind.SetIndex, Arity: 1, Span.Line: 21 });
        Assert.Contains(pass.Sites, s => s is { Kind: DynKind.Convert, To: AirType.Prim { Kind: PrimKind.Int }, Span.Line: 13 });
    }

    [Fact]
    public void Site_initializers_match_a_DynSite_constructor()
    {
        var body = LowerMain(Program);
        var pass = new DynSites("main.al");
        pass.Run(body);
        foreach (var s in pass.Sites)
        {
            var init = s.Init();
            Assert.Equal("global::Alice.Runtime.DynSite", init.TypeCs);
            var values = init.Args.Select(Value).ToArray();
            var site = (DynSite)Activator.CreateInstance(typeof(DynSite), values)!;
            Assert.Equal(s.Kind, site.Kind);
            if (s.Kind is DynKind.BinaryOp or DynKind.UnaryOp) Assert.Equal(s.Op, site.Op);
            else if (s.Kind is DynKind.Convert or DynKind.Cast) Assert.NotNull(site.To);
            else Assert.Equal((s.Name, s.Arity, s.Safe), (site.Name, site.Arity, site.Safe));
        }
    }

    static object Value(MExpr e) => e switch
    {
        MCast { TypeCs: "global::Alice.Runtime.DynKind", E: AirConst k } => (DynKind)(int)k.Value!,
        MCast { TypeCs: "global::Alice.Runtime.DynOp", E: AirConst k } => (DynOp)(int)k.Value!,
        AirTypeOf t => t.Of switch
        {
            AirType.Prim { Kind: PrimKind.Int } => typeof(int),
            AirType.Prim { Kind: PrimKind.Bool } => typeof(bool),
            _ => typeof(object),
        },
        AirConst c => c.Value!,
        _ => throw new InvalidOperationException("unexpected initializer argument " + e.GetType().Name),
    };

    [Fact]
    public void Sites_are_declared_as_a_static_holder_type()
    {
        var pass = new DynSites("main.al");
        pass.Run(LowerMain("n : Any = 4\nputs n.abs\nputs n + 1\n"));
        var t = pass.Declaration()!;
        Assert.Equal(DeclExpected, AirDeclDump.Of(t));
        Assert.Same(t, t.Sym.Synth);
        Assert.Null(new DynSites().Declaration());
    }

    const string DeclExpected =
        "type module abstract sealed beforefieldinit <>DynSites\n" +
        "  field internal static global::Alice.Runtime.DynSite site0\n" +
        "  field internal static global::Alice.Runtime.DynSite site1\n" +
        "  method private static specialname void .cctor() [TypeInitializer]\n" +
        "    assign (member site0 (type <>DynSites)):global::Alice.Runtime.DynSite = (new global::Alice.Runtime.DynSite (cast global::Alice.Runtime.DynKind (const 1):int):global::Alice.Runtime.DynKind (const \"abs\"):string (const 0):int (const false):bool):global::Alice.Runtime.DynSite @main.al:2:6+0\n" +
        "    assign (member site1 (type <>DynSites)):global::Alice.Runtime.DynSite = (new global::Alice.Runtime.DynSite (cast global::Alice.Runtime.DynKind (const 5):int):global::Alice.Runtime.DynKind (cast global::Alice.Runtime.DynOp (const 1):int):global::Alice.Runtime.DynOp):global::Alice.Runtime.DynSite @main.al:3:6+0\n" +
        "    return\n";

    [Fact]
    public void Statically_typed_code_has_no_sites()
    {
        var body = LowerMain("class P\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\nend\np = P.new(2)\nputs p.x + 1\n");
        string before = MirDump.Of(body);
        var pass = new DynSites("main.al");
        pass.Run(body);
        Assert.Empty(pass.Sites);
        Assert.Equal(before, MirDump.Of(body));
    }

    [Fact]
    public void Compound_store_through_a_computed_receiver_evaluates_it_once()
    {
        var body = LowerMain("a : Any = [[1]]\na[0][0] += 2\nputs a[0][0]\n");
        var pass = new DynSites("main.al");
        pass.Run(body);
        string dump = Dump(body);
        Assert.Contains("decl __dyn1 dynamic = (dyn.GetIndex#", dump);
        Assert.Equal(1, dump.Split("decl __dyn").Length - 1);
    }

    [Fact]
    public void Dynamic_sites_are_AL0232_under_native_and_wasm()
    {
        var body = LowerMain("n : Any = 4\nputs n.abs\n");
        var pass = new DynSites("main.al");
        pass.Run(body);
        Assert.Empty(DynSites.Check(pass.Sites, native: false, wasm: false));
        var d = Assert.Single(DynSites.Check(pass.Sites, native: true, wasm: false));
        Assert.Equal(("error", "AL0232", "main.al", 2), (d.Severity, d.Code, d.File, d.Line));
        Assert.Equal("`.abs` on an `Any` value needs dynamic dispatch, which `native = true` (NativeAOT) does not support; give the value a static type (`x : T = ...`, `def f(x : T)`) or convert it (`x.as(T)`)", d.Message);
        var w = Assert.Single(DynSites.Check(pass.Sites, native: false, wasm: true));
        Assert.Contains("the wasm target", w.Message);
        Assert.StartsWith("main.al:2:", w.ToString());
    }

    [Fact]
    public void The_CSharp_backend_does_not_see_the_pass()
    {
        // the pass is IL-only: the C# backend's dynamic code is the same before and after it ran on the same source
        string before = AliceCompiler.Transpile(Program);
        new DynSites().Run(LowerMain(Program));
        Assert.Equal(before, AliceCompiler.Transpile(Program));
        Assert.Contains("dynamic v = new P(2);", before);
    }

    /// <summary>Dynamic operators follow Alice semantics (floored `/` and `%`, checked arithmetic), the same as statically
    /// typed code; the C# backend's `dynamic` truncates (a known divergence, docs/production.md).</summary>
    const string SemanticsProgram = "a : Any = -7\nb : Any = 2\nputs a / b\nputs a % 3\nputs -7.5 % b\nputs a * b\n";
    const string SemanticsExpected = "-4\n2\n0.5\n-14\n";

    [Fact]
    public void Dynamic_operators_have_Alice_semantics_on_the_IL_path()
    {
        // the IL path evaluates each BinaryOp site with Dyn.BinaryOp; this checks what those sites compute
        var pass = new DynSites("main.al");
        pass.Run(LowerMain(SemanticsProgram));
        var ops = pass.Sites.Where(s => s.Kind == DynKind.BinaryOp).Select(s => s.Op).ToList();
        Assert.Equal(new[] { DynOp.Div, DynOp.Mod, DynOp.Mod, DynOp.Mul }, ops);
        object?[] lefts = [-7, -7, -7.5, -7];
        object?[] rights = [2, 3, 2, 2];
        var got = ops.Select((op, i) =>
        {
            var site = new DynSite(DynKind.BinaryOp, op);
            return Prelude.str(Dyn.BinaryOp(ref site, lefts[i], rights[i])) + "\n";
        });
        Assert.Equal(SemanticsExpected, string.Concat(got));
    }

    [Fact(Skip = "Pending: AirToIl (D1.4, il-emit) does not emit AirDynCall yet; then run SemanticsProgram under --backend il and assert SemanticsExpected (Alice semantics; the C# backend prints -3, -1, -1.5 for the first three)")]
    public void Dynamic_program_runs_with_Alice_semantics_under_the_IL_backend()
    {
    }
}
