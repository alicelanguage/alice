using Alice.Compiler;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Backend;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;

namespace Alice.Tests.Compiler;

/// <summary>
/// Closure conversion (backend plan D1.5, Air/Passes/ClosureConvert.cs): AIR dumps before and after the pass on the
/// lambda shapes the lowerer produces, the escape and write decisions, and that the C# backend never runs it.
/// </summary>
public class ClosureConvertTests
{
    static AirLocalRef L(string n, AirType? t = null) => new(n, t);
    static readonly AirType Int = AirLeaf.Int;
    static MBinary Add(MExpr a, MExpr b) => new() { Op = AirBinOp.Add, L = a, R = b, Type = Int };
    static MBinary Mul(MExpr a, MExpr b) => new() { Op = AirBinOp.Mul, L = a, R = b, Type = Int };
    static ClosureContext Ctx(params (string, string?)[] ps) => new() { Method = "M", Params = ps.ToList() };
    static ClosureContext Instance => new() { Method = "M", Instance = true, ThisTypeCs = "C" };

    static (string Before, string After, ClosureConvert Pass) Convert(List<MStmt> body, ClosureContext? ctx)
    {
        string before = MirDump.Of(body);
        var pass = new ClosureConvert(ctx ?? new ClosureContext { Method = "M" });
        pass.Run(body);
        return (before, MirDump.Of(body), pass);
    }

    /// <summary>The ten shapes (plus one the pass refuses), by name; each call builds a fresh body.</summary>
    static (List<MStmt> Body, ClosureContext? Ctx) Shape(string name) => name switch
    {
        // capture-free: a static method on <>c and a lazily created cached delegate
        "none" => ([
            new MDecl { Name = "f", Init = new MLambda { Params = { "int x" }, ExprBody = Add(L("x", Int), AirConst.Int(1)) } },
            new MReturn { Value = L("f") },
        ], null),
        // read-only capture, escaping: display class, the value copied into a field
        "read" => ([
            new MDecl { Name = "k", TypeCs = "int", Init = AirConst.Int(3) },
            new MDecl { Name = "f", Init = new MLambda { Params = { "x" }, ExprBody = Mul(L("x", Int), L("k", Int)) } },
            new MReturn { Value = L("f") },
        ], null),
        // written capture, escaping: the local is hoisted into a block display; every access goes through its field
        "written" => ([
            new MDecl { Name = "n", TypeCs = "int", Init = AirConst.Int(0) },
            new MDecl { Name = "inc", Init = new MLambda { Body = [new MAssign { Target = L("n", Int), Op = "+=", Value = AirConst.Int(1) }] } },
            new MExprStmt { E = new MInvoke { Target = L("inc") } },
            new MReturn { Value = L("n", Int) },
        ], null),
        // nested: the outer closure captures `a` for the inner one, which reads it from the outer display
        "nested" => ([
            new MDecl { Name = "a", TypeCs = "int", Init = AirConst.Int(1) },
            new MDecl { Name = "f", Init = new MLambda { Params = { "x" }, Body = [
                new MDecl { Name = "b", TypeCs = "int", Init = L("x", Int) },
                new MReturn { Value = new MLambda { Params = { "y" }, ExprBody = Add(Add(L("a", Int), L("b", Int)), L("y", Int)) } },
            ] } },
            new MReturn { Value = L("f") },
        ], null),
        // in a loop: a fresh hoisted display per iteration (the loop body is its block), `w` copied per closure
        "loop" => ([
            new MDecl { Name = "fs", Init = new MNew { TypeCs = "List<Func<int>>" } },
            new MForeach { Vars = { "v" }, Coll = L("xs"), Body = [
                new MDecl { Name = "w", TypeCs = "int", Init = Mul(L("v", Int), AirConst.Int(2)) },
                new MDecl { Name = "c", TypeCs = "int", Init = AirConst.Int(0) },
                new MExprStmt { E = new MCall { Target = L("fs"), Name = "Add", Args = { new MLambda { Body = [
                    new MAssign { Target = L("c", Int), Op = "+=", Value = L("w", Int) },
                    new MReturn { Value = L("c", Int) },
                ] } } } },
            ] },
            new MReturn { Value = L("fs") },
        ], Ctx(("xs", "int[]"))),
        // fork branches never escape the join: one display struct each, passed by ref
        "fork" => ([
            new MReturn { Value = new AirForkJoin { Branches = {
                new MLambda { ExprBody = new MCall { Name = "fib", Args = { L("a", Int) } } },
                new MLambda { ExprBody = new MCall { Name = "fib", Args = { L("b", Int) } } },
            } } },
        ], Ctx(("a", "int"), ("b", "int"))),
        // a Par kernel (struct, value fields) and an immediately invoked lambda writing `hits` (struct, ref field)
        "par" => ([
            new MDecl { Name = "s", TypeCs = "int", Init = AirConst.Int(2) },
            new MDecl { Name = "hits", TypeCs = "int", Init = AirConst.Int(0) },
            new MExprStmt { E = new AirPar { Kind = AirParKind.Range, Count = new AirLength { Target = L("xs") }, Type = AirLeaf.Void,
                Kernel = new MLambda { Params = { "lo", "hi" }, Hot = true, Body = [
                    new MFor { Var = "i", Start = L("lo", Int), End = L("hi", Int), Body = [
                        new MAssign { Target = new MIndex { Target = L("xs"), Args = { L("i", Int) } }, Value = Mul(new MIndex { Target = L("xs"), Args = { L("i", Int) } }, L("s", Int)) },
                    ] },
                ] } } },
            new MExprStmt { E = new MInvoke { Target = new MLambda { Body = [new MAssign { Target = L("hits", Int), Op = "+=", Value = AirConst.Int(1) }] } } },
            new MReturn { Value = L("hits", Int) },
        ], Ctx(("xs", "int[]"))),
        // stored in a field, then the captured local is written: hoisted, so the closure sees 5
        "field" => ([
            new MDecl { Name = "k", TypeCs = "int", Init = AirConst.Int(1) },
            new MAssign { Target = new MMember { Target = new AirThis(), Name = "handler" }, Value = new MLambda { ExprBody = L("k", Int) } },
            new MAssign { Target = L("k", Int), Value = AirConst.Int(5) },
        ], Instance),
        // `this`, explicit and implicit (a bare member name, an unqualified call)
        "this" => ([
            new MDecl { Name = "f", Init = new MLambda { ExprBody = Add(new MMember { Target = new AirThis(), Name = "count" }, new MCode("bonus")) } },
            new MDecl { Name = "g", Init = new MLambda { ExprBody = new MCall { Name = "Step", Args = { new MCode("1") } } } },
        ], Instance),
        // static lambdas hoist even in an instance method (a lowerer-`static` one may not reach `this`)
        "static" => ([
            new MDecl { Name = "abs", Init = new MLambda { Static = true, Params = { "int x" }, ExprBody = new MCode("global::System.Math.Abs(x)") } },
            new MDecl { Name = "sq", Init = new MLambda { Params = { "int x" }, ExprBody = Mul(L("x", Int), L("x", Int)) } },
        ], Instance),
        // opaque text: captured names inside MCode are rewritten in the text, string literals are not
        "opaque" => ([
            new MDecl { Name = "n", TypeCs = "int", Init = AirConst.Int(0) },
            new MDecl { Name = "f", Init = new MLambda { Body = [new MExprStmt { E = new MCode("n++") }, new MReturn { Value = new MCode("n * 2 + \"n\".Length") }] } },
            new MReturn { Value = L("f") },
        ], null),
        // a Par kernel that writes a capture: kernels are copied into the runtime's job (no ref fields across threads),
        // so it is demoted to a display class and the written local is hoisted
        "parwrite" => ([
            new MDecl { Name = "seen", TypeCs = "int", Init = AirConst.Int(0) },
            new MExprStmt { E = new AirPar { Kind = AirParKind.Range, Count = L("n", Int), Type = AirLeaf.Void,
                Kernel = new MLambda { Params = { "lo", "hi" }, Body = [new MAssign { Target = L("seen", Int), Value = L("hi", Int) }] } } },
            new MReturn { Value = L("seen", Int) },
        ], Ctx(("n", "int"))),
        // a `for` variable captured by an escaping closure would need hoisting out of the loop header: refused
        "forvar" => ([
            new MDecl { Name = "fs", Init = new MNew { TypeCs = "List<Func<int>>" } },
            new MFor { Var = "i", Start = AirConst.Int(0), End = AirConst.Int(3), Body = [
                new MExprStmt { E = new MCall { Target = L("fs"), Name = "Add", Args = { new MLambda { ExprBody = L("i", Int) } } } },
            ] },
        ], null),
        _ => throw new ArgumentException(name),
    };

    static readonly Dictionary<string, (string Before, string After)> Expected = new()
    {
        ["none"] = ("""
            decl f var = (lambda (int x) (Add (local x):int (const 1):int):int)
            return (local f)
            """ + "\n",
            """
            decl f var = (delegate-create <>c.<M>b__0 cached <>9__M_0 (method static (int x) (Add (local x):int (const 1):int):int))
            return (local f)
            """ + "\n"),
        ["read"] = ("""
            decl k int = (const 3):int
            decl f var = (lambda (x) (Mul (local x):int (local k):int):int)
            return (local f)
            """ + "\n",
            """
            decl k int = (const 3):int
            decl f var = (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (k (local k):int)) (method (x) (Mul (local x):int (display-field (this) k):int):int))
            return (local f)
            """ + "\n"),
        ["written"] = ("""
            decl n int = (const 0):int
            decl inc var = (lambda ()
              assign (local n):int += (const 1):int
            )
            expr (invoke (local inc))
            return (local n):int
            """ + "\n",
            """
            decl <>8__1 <>c__DisplayClass_M_1 = (display-new class <>c__DisplayClass_M_1 hoisted (n))
            assign (display-field (local <>8__1) n):int = (const 0):int
            decl inc var = (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (link <>8__1 (local <>8__1))) (method ()
              assign (display-field (display-field (this) <>8__1) n):int += (const 1):int
            ))
            expr (invoke (local inc))
            return (display-field (local <>8__1) n):int
            """ + "\n"),
        ["nested"] = ("""
            decl a int = (const 1):int
            decl f var = (lambda (x)
              decl b int = (local x):int
              return (lambda (y) (Add (Add (local a):int (local b):int):int (local y):int):int)
            )
            return (local f)
            """ + "\n",
            """
            decl a int = (const 1):int
            decl f var = (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (a (local a):int)) (method (x)
              decl b int = (local x):int
              return (delegate-create <>c__DisplayClass_M_1.<M>b__1 (display-new class <>c__DisplayClass_M_1 (a (display-field (this) a):int) (b (local b):int)) (method (y) (Add (Add (display-field (this) a):int (display-field (this) b):int):int (local y):int):int))
            ))
            return (local f)
            """ + "\n"),
        ["loop"] = ("""
            decl fs var = (new List<Func<int>>)
            foreach (v) in (local xs)
              decl w int = (Mul (local v):int (const 2):int):int
              decl c int = (const 0):int
              expr (call Add (local fs) (lambda ()
                assign (local c):int += (local w):int
                return (local c):int
              ))
            return (local fs)
            """ + "\n",
            """
            decl fs var = (new List<Func<int>>)
            foreach (v) in (local xs)
              decl <>8__1 <>c__DisplayClass_M_1 = (display-new class <>c__DisplayClass_M_1 hoisted (c))
              decl w int = (Mul (local v):int (const 2):int):int
              assign (display-field (local <>8__1) c):int = (const 0):int
              expr (call Add (local fs) (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (link <>8__1 (local <>8__1)) (w (local w):int)) (method ()
                assign (display-field (display-field (this) <>8__1) c):int += (display-field (this) w):int
                return (display-field (display-field (this) <>8__1) c):int
              )))
            return (local fs)
            """ + "\n"),
        ["fork"] = ("""
            return (fork (lambda () (call fib _ (local a):int)) (lambda () (call fib _ (local b):int)))
            """ + "\n",
            """
            decl <>d__0 <>c__DisplayClass_M_0 = (display-new struct fork <>c__DisplayClass_M_0 (a (local a)))
            decl <>d__1 <>c__DisplayClass_M_1 = (display-new struct fork <>c__DisplayClass_M_1 (b (local b)))
            return (fork (delegate-create <>c__DisplayClass_M_0.<M>b__0 ref (local <>d__0) (method static (ref <>c__DisplayClass_M_0 <>d) (call fib _ (display-field (local <>d) a):int))) (delegate-create <>c__DisplayClass_M_1.<M>b__1 ref (local <>d__1) (method static (ref <>c__DisplayClass_M_1 <>d) (call fib _ (display-field (local <>d) b):int))))
            """ + "\n"),
        ["par"] = ("""
            decl s int = (const 2):int
            decl hits int = (const 0):int
            expr (par range (length Length (local xs)) (lambda hot (lo, hi)
              for i var = (local lo):int; < (local hi):int; ++
                assign (index (local xs) (local i):int) = (Mul (index (local xs) (local i):int) (local s):int):int
            )):void
            expr (invoke (lambda ()
              assign (local hits):int += (const 1):int
            ))
            return (local hits):int
            """ + "\n",
            """
            decl s int = (const 2):int
            decl hits int = (const 0):int
            decl <>d__0 <>c__DisplayClass_M_0 = (display-new struct range <>c__DisplayClass_M_0 (xs (local xs)) (s (local s):int))
            expr (par range (length Length (local xs)) (delegate-create <>c__DisplayClass_M_0.<M>b__0 ref (local <>d__0) (method static hot (ref <>c__DisplayClass_M_0 <>d, lo, hi)
              for i var = (local lo):int; < (local hi):int; ++
                assign (index (display-field (local <>d) xs) (local i):int) = (Mul (index (display-field (local <>d) xs) (local i):int) (display-field (local <>d) s):int):int
            ))):void
            decl <>d__1 <>c__DisplayClass_M_1 = (display-new struct <>c__DisplayClass_M_1 (ref hits (local hits):int))
            expr (invoke (delegate-create <>c__DisplayClass_M_1.<M>b__1 ref (local <>d__1) (method static (ref <>c__DisplayClass_M_1 <>d)
              assign (display-field (local <>d) hits):int += (const 1):int
            )))
            return (local hits):int
            """ + "\n"),
        ["field"] = ("""
            decl k int = (const 1):int
            assign (member handler (this)) = (lambda () (local k):int)
            assign (local k):int = (const 5):int
            """ + "\n",
            """
            decl <>8__1 <>c__DisplayClass_M_1 = (display-new class <>c__DisplayClass_M_1 hoisted (k))
            assign (display-field (local <>8__1) k):int = (const 1):int
            assign (member handler (this)) = (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (link <>8__1 (local <>8__1))) (method () (display-field (display-field (this) <>8__1) k):int))
            assign (display-field (local <>8__1) k):int = (const 5):int
            """ + "\n"),
        ["this"] = ("""
            decl f var = (lambda () (Add (member count (this)) `bonus`):int)
            decl g var = (lambda () (call Step _ `1`))
            """ + "\n",
            """
            decl f var = (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (this <>4__this (this))) (method () (Add (member count (display-field (this) <>4__this)) (member bonus (display-field (this) <>4__this))):int))
            decl g var = (delegate-create <>c__DisplayClass_M_1.<M>b__1 (display-new class <>c__DisplayClass_M_1 (this <>4__this (this))) (method () (call Step (display-field (this) <>4__this) `1`)))
            """ + "\n"),
        ["static"] = ("""
            decl abs var = (lambda static (int x) `global::System.Math.Abs(x)`)
            decl sq var = (lambda (int x) (Mul (local x):int (local x):int):int)
            """ + "\n",
            """
            decl abs var = (delegate-create <>c.<M>b__0 cached <>9__M_0 (method static (int x) `global::System.Math.Abs(x)`))
            decl sq var = (delegate-create <>c.<M>b__1 cached <>9__M_1 (method static (int x) (Mul (local x):int (local x):int):int))
            """ + "\n"),
        ["opaque"] = ("""
            decl n int = (const 0):int
            decl f var = (lambda ()
              expr `n++`
              return `n * 2 + "n".Length`
            )
            return (local f)
            """ + "\n",
            """
            decl <>8__1 <>c__DisplayClass_M_1 = (display-new class <>c__DisplayClass_M_1 hoisted (n))
            assign (display-field (local <>8__1) n):int = (const 0):int
            decl f var = (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (link <>8__1 (local <>8__1))) (method ()
              expr `this.<>8__1.n++`
              return `this.<>8__1.n * 2 + "n".Length`
            ))
            return (local f)
            """ + "\n"),
        ["parwrite"] = ("""
            decl seen int = (const 0):int
            expr (par range (local n):int (lambda (lo, hi)
              assign (local seen):int = (local hi):int
            )):void
            return (local seen):int
            """ + "\n",
            """
            decl <>8__1 <>c__DisplayClass_M_1 = (display-new class <>c__DisplayClass_M_1 hoisted (seen))
            assign (display-field (local <>8__1) seen):int = (const 0):int
            expr (par range (local n):int (delegate-create <>c__DisplayClass_M_0.<M>b__0 (display-new class <>c__DisplayClass_M_0 (link <>8__1 (local <>8__1))) (method (lo, hi)
              assign (display-field (display-field (this) <>8__1) seen):int = (local hi):int
            ))):void
            return (display-field (local <>8__1) seen):int
            """ + "\n"),
        ["forvar"] = ("""
            decl fs var = (new List<Func<int>>)
            for i var = (const 0):int; < (const 3):int; ++
              expr (call Add (local fs) (lambda () (local i):int))
            """ + "\n",
            """
            decl fs var = (new List<Func<int>>)
            for i var = (const 0):int; < (const 3):int; ++
              expr (call Add (local fs) (lambda () (local i):int))
            """ + "\n"),
    };

    [Theory]
    [InlineData("none")]
    [InlineData("read")]
    [InlineData("written")]
    [InlineData("nested")]
    [InlineData("loop")]
    [InlineData("fork")]
    [InlineData("par")]
    [InlineData("field")]
    [InlineData("this")]
    [InlineData("static")]
    [InlineData("opaque")]
    [InlineData("parwrite")]
    public void Shape_dumps_before_and_after(string shape)
    {
        var (body, ctx) = Shape(shape);
        var (before, after, pass) = Convert(body, ctx);
        Assert.Equal(Expected[shape].Before, before);
        Assert.Equal(Expected[shape].After, after);
        Assert.Empty(pass.Unsupported);
        Assert.DoesNotContain("(lambda", after);
    }

    [Fact]
    public void Struct_displays_can_be_switched_off()
    {
        foreach (var shape in new[] { "fork", "par" })
        {
            var (body, ctx) = Shape(shape);
            ctx!.StructDisplays = false;
            var (_, after, pass) = Convert(body, ctx);
            Assert.Empty(pass.Unsupported);
            Assert.DoesNotContain("display-new struct", after);
            Assert.DoesNotContain(" ref (local", after);
            Assert.All(pass.Displays, d => Assert.Equal(AirDisplayKind.Class, d.Kind));
        }
        // off, a written capture of a directly invoked lambda is hoisted like any escaping one
        var (b, c) = Shape("par");
        c!.StructDisplays = false;
        Assert.Contains("(display-new class <>c__DisplayClass_M_2 hoisted (hits))", Convert(b, c).After);
    }

    [Fact]
    public void Unconvertible_body_is_left_untouched_with_a_reason()
    {
        var (body, ctx) = Shape("forvar");
        var (before, after, pass) = Convert(body, ctx);
        Assert.Equal(before, after);
        Assert.Equal(Expected["forvar"].Before, before);
        Assert.Equal(["written LoopVar `i` captured by an escaping closure (cannot be hoisted)"], pass.Unsupported);
    }

    [Fact]
    public void Out_and_pattern_variables_are_captured_locals()
    {
        var body = new List<MStmt>
        {
            new MExprStmt { E = new MCall { Target = L("d"), Name = "TryGetValue", Args = { AirConst.Int(1), new MArg { Prefix = "out var ", E = L("v") } } } },
            new MIf { Cond = new MIs { E = L("o"), TypeCs = "string s" }, Then = [new MReturn { Value = new MLambda { ExprBody = Add(L("v"), L("s")) } }] },
            new MReturn { Value = new AirDefault(null) },
        };
        var (_, after, pass) = Convert(body, Ctx(("d", null), ("o", "object")));
        Assert.Empty(pass.Unsupported);
        Assert.Contains("(display-new class <>c__DisplayClass_M_0 (v (local v)) (s (local s)))", after);
    }

    [Fact]
    public void Declarations_list_displays_and_static_methods()
    {
        var (body, ctx) = Shape("nested");
        Convert(body, ctx);
        var (displays, statics) = ClosureConvert.Declarations(body);
        Assert.Empty(statics);
        Assert.Equal(["<>c__DisplayClass_M_0", "<>c__DisplayClass_M_1"], displays.Select(d => d.Name));
        Assert.All(displays, d => Assert.Equal(AirDisplayKind.Class, d.Kind));

        (body, ctx) = Shape("par");
        var (_, _, pass) = Convert(body, ctx);
        (displays, statics) = ClosureConvert.Declarations(body);
        Assert.Equal(pass.Displays, displays);
        Assert.All(displays, d => Assert.Equal(AirDisplayKind.Struct, d.Kind));
        var hits = Assert.Single(displays[1].Fields);
        Assert.True(hits is { Field: "hits", ByRef: true, Written: true });
        Assert.All(displays[0].Fields, f => Assert.False(f.ByRef));
        Assert.True(Assert.Single(displays[0].Methods) is { Static: true, Hot: true });
        // the kernel goes to Par.Range<TKernel> (a plain struct); the directly invoked one is a ref struct
        Assert.True(displays[0] is { Role: AirKernelRole.Range, ByRefLike: false });
        Assert.True(displays[1] is { Role: AirKernelRole.None, ByRefLike: true });

        (body, ctx) = Shape("fork");
        Convert(body, ctx);
        Assert.All(ClosureConvert.Declarations(body).Displays, d => Assert.True(d is { Kind: AirDisplayKind.Struct, Role: AirKernelRole.Fork, ByRefLike: false }));

        (body, ctx) = Shape("static");
        Convert(body, ctx);
        (displays, statics) = ClosureConvert.Declarations(body);
        Assert.Empty(displays);
        Assert.Equal(["<>9__M_0", "<>9__M_1"], statics.Select(m => m.CachedField!));
    }

    [Fact]
    public void Debug_printer_spells_a_csharp_equivalent()
    {
        var (body, ctx) = Shape("written");
        Convert(body, ctx);
        var (displays, statics) = ClosureConvert.Declarations(body);
        Assert.Equal("""
            sealed class <>c__DisplayClass_M_1
            {
                public int n;
            }
            sealed class <>c__DisplayClass_M_0
            {
                public <>c__DisplayClass_M_1 <>8__1;
                public <M>b__0()
                {
                    this.<>8__1.n += 1;
                }
            }

            """.Replace("\r", ""), Printer.ClosureDeclarations(displays, statics));
        var decl = (MDecl)body[2];
        Assert.Equal("new <>c__DisplayClass_M_0 { <>8__1 = <>8__1 }.<M>b__0", Printer.E(decl.Init!));
        (body, ctx) = Shape("none");
        Convert(body, ctx);
        Assert.Equal("(<>c.<>9__M_0 ??= <>c.<M>b__0)", Printer.E(((MDecl)body[0]).Init!));
    }

    const string NL = "\n";
    const string SumSrc = "def f(xs : Array(Float64))" + NL + "  xs.sum { |x| Math.Sqrt(x) }" + NL + "end" + NL;

    [Fact]
    public void Csharp_backend_never_runs_the_pass()
    {
        Assert.Equal(AirBackend.CSharp, Pipeline.Backend);
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(SumSrc));
        Assert.Contains("(lambda (__lo", dump);
        Assert.DoesNotContain("delegate-create", dump);
        // the IL flag without a method context (helper fragments) does not run it either
        Pipeline.Backend = AirBackend.Il;
        try { Assert.DoesNotContain("delegate-create", MirDump.Collect(() => AliceCompiler.Transpile(SumSrc))); }
        finally { Pipeline.Backend = AirBackend.CSharp; }
    }

    [Fact]
    public void Il_pipeline_converts_a_lowered_par_reduce_kernel()
    {
        Pipeline.Backend = AirBackend.Il;
        ClosureConvert.Current = new ClosureContext { Method = "f", Params = { ("xs", "double[]") } };
        string dump;
        try { dump = MirDump.Collect(() => AliceCompiler.Transpile(SumSrc)); }
        finally { Pipeline.Backend = AirBackend.CSharp; ClosureConvert.Current = null; }
        Assert.Contains("  decl <>d__0 <>c__DisplayClass_f_0 = (display-new struct reduce <>c__DisplayClass_f_0 (xs (local xs)))\n", dump);
        Assert.Contains("(delegate-create <>c__DisplayClass_f_0.<f>b__0 ref (local <>d__0) (method static (ref <>c__DisplayClass_f_0 <>d, __lo", dump);
        Assert.Contains("(display-field (local <>d) xs):double[]", dump);
        Assert.Contains("(delegate-create <>c.<f>b__1 cached <>9__f_1 (method static (a, b) (Add (param a) (param b))))", dump);
        Assert.DoesNotContain("(lambda", dump);
    }

    /// <summary>Runs converted closures through the IL emitter; waits for il-emit's AirToIl to consume the closure
    /// nodes (AirDelegateCreate, AirDisplayNew, AirDisplayField).</summary>
    [Fact(Skip = "pending: AirToIl (il-emit, D1.4) does not consume closure nodes yet")]
    [Trait("Category", "Pending")]
    public void Converted_closures_run_under_the_il_backend()
    {
        // counter closure (hoisted written capture), Par kernel (struct display), static lambda (cached delegate):
        // compile "def f(n : Int32) : Int32 ... end" with --backend il and compare stdout with the C# backend.
        Assert.Fail("enable once AirToIl handles closure nodes");
    }
}
