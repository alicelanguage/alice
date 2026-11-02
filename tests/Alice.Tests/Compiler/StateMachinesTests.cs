using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

/// <summary>
/// The IL backend's state-machine pass (Air/Passes/StateMachines*.cs, backend plan D1.8). For each shape: the dump of
/// the generated machine (fields, MoveNext, Dispose...) and the kickoff body, then a run of the generated MIR with a
/// small evaluator (<see cref="SmEval"/>) driven by the real BCL builders, awaiters and ManualResetValueTaskSourceCore,
/// compared with what C# does for the same method. The runs through the IL emitter are Pending until AirToIl
/// (D1.4) consumes AirTypeDecl and the jump nodes.
/// </summary>
public class StateMachinesTests
{
    // ---------------------------------------------------------------- types and builders

    static AirType Clr(Type t)
    {
        if (t == typeof(int)) return I;
        if (t == typeof(bool)) return B;
        if (t == typeof(void)) return V;
        if (t == typeof(string)) return S;
        if (t == typeof(object)) return O;
        if (t == typeof(short)) return new AirType.Prim(PrimKind.Short);
        if (t.IsGenericType)
        {
            string n = t.GetGenericTypeDefinition().FullName!;
            return new AirType.ClrRef("global::" + n[..n.IndexOf('`')], t, t.GetGenericArguments().Select(Clr).ToList());
        }
        return new AirType.ClrRef("global::" + t.FullName!.Replace('+', '.'), t, []);
    }

    static readonly AirType I = new AirType.Prim(PrimKind.Int);
    static readonly AirType B = new AirType.Prim(PrimKind.Bool);
    static readonly AirType V = new AirType.Prim(PrimKind.Void);
    static readonly AirType S = new AirType.Prim(PrimKind.String);
    static readonly AirType O = new AirType.Prim(PrimKind.Object);
    static readonly AirType LogT = Clr(typeof(List<object>));
    static readonly AirType TaskT = Clr(typeof(Task));
    static readonly AirType TaskInt = Clr(typeof(Task<int>));

    static object?[] Seq(params object?[] xs) => xs;
    static AirLocalRef L(string n, AirType t) => new(n, t);
    static AirConst K(object v) => AirConst.Of(v);
    static MCall Call(MExpr? t, string n, AirType? ty, params MExpr[] a) => new() { Target = t, Name = n, Type = ty, Args = [.. a] };
    static MCall Static(Type t, string n, AirType? ty, params MExpr[] a) => Call(new AirTypeRef(Clr(t)), n, ty, a);
    static MUnary Await(MExpr e, AirType? t) => new() { Op = AirUnOp.Await, E = e, Type = t };
    static MExprStmt Do(MExpr e) => new() { E = e };
    static MExprStmt Log(object v) => Do(Call(L("log", LogT), "Add", V, K(v)));
    static MExprStmt LogE(MExpr e) => Do(Call(L("log", LogT), "Add", V, e));
    static MDecl Decl(string n, AirType t, MExpr? init) => new() { Name = n, TypeCs = t.ToCs(), Init = init };
    static MBinary Bin(AirBinOp op, MExpr l, MExpr r, AirType t) => new() { Op = op, L = l, R = r, Type = t };
    static MExpr Delay() => Await(Static(typeof(Task), "Delay", TaskT, K(1)), V);
    static MExpr FromResult(MExpr v) => Static(typeof(Task), "FromResult", TaskInt, v);
    static MExprStmt Yield(MExpr v) => Do(new MArg { Prefix = "yield return ", E = v });

    static StateMachineMethod Method(string name, AirType ret, bool async, bool iterator, params (string, AirType)[] ps) => new()
    {
        Name = name, Ret = ret, Async = async, Iterator = iterator, Params = ps.Select(p => new AirParamDecl(p.Item1, p.Item2)).ToList(),
    };

    static StateMachineResult Build(StateMachineMethod m, List<MStmt> body, bool debug = false)
    {
        var r = StateMachines.Run(m, body, new StateMachineOptions { Debug = debug });
        Assert.True(r.Unsupported.Count == 0, string.Join("; ", r.Unsupported));
        return r;
    }

    // ---------------------------------------------------------------- dump

    /// <summary>The machine and its kickoff (<see cref="StateMachineDump"/>); the result stays runnable.</summary>
    static string Dump(StateMachineResult r) => StateMachineDump.Of(r);

    // ================================================================ 1. simple await

    // async def f(log) : Int32 / log << 1 / await Task.Delay(1) / log << 2 / 42
    static List<MStmt> SimpleAwait() =>
    [
        Log(1),
        Do(Delay()),
        Log(2),
        new MReturn { Value = K(42) },
    ];

    [Fact]
    public void Simple_await_dump()
    {
        var r = Build(Method("f", TaskInt, true, false, ("log", LogT)), SimpleAwait());
        Assert.Equal(StateMachineKind.Async, r.Kind);
        Assert.Equal(SimpleAwaitDump, Dump(r));
    }

    const string SimpleAwaitDump =
        "type struct beforefieldinit <f>d__0 : global::System.Runtime.CompilerServices.IAsyncStateMachine\n" +
        "  field public int <>1__state\n" +
        "  field public global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> <>t__builder\n" +
        "  field public global::System.Collections.Generic.List<object> log\n" +
        "  field private global::System.Runtime.CompilerServices.TaskAwaiter <>u__1\n" +
        "  method private virtual final void MoveNext() implements global::System.Runtime.CompilerServices.IAsyncStateMachine.MoveNext\n" +
        "    decl <>r int\n" +
        "    decl <>a__1 global::System.Runtime.CompilerServices.TaskAwaiter\n" +
        "    try\n" +
        "      switch (member <>1__state (this):<f>d__0):int 0: <>resume_1\n" +
        "      expr (call Add (member log (this):<f>d__0):global::System.Collections.Generic.List<object> (const 1):int):void\n" +
        "      assign (local <>a__1):global::System.Runtime.CompilerServices.TaskAwaiter = (call GetAwaiter (call Delay (type global::System.Threading.Tasks.Task) (const 1):int):global::System.Threading.Tasks.Task):global::System.Runtime.CompilerServices.TaskAwaiter\n" +
        "      if (Not (member IsCompleted (local <>a__1):global::System.Runtime.CompilerServices.TaskAwaiter):bool):bool\n" +
        "        assign (member <>1__state (this):<f>d__0):int = (const 0):int\n" +
        "        assign (member <>u__1 (this):<f>d__0):global::System.Runtime.CompilerServices.TaskAwaiter = (local <>a__1):global::System.Runtime.CompilerServices.TaskAwaiter\n" +
        "        expr (call AwaitUnsafeOnCompleted (member <>t__builder (this):<f>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> (arg \"ref \" (member <>u__1 (this):<f>d__0):global::System.Runtime.CompilerServices.TaskAwaiter):global::System.Runtime.CompilerServices.TaskAwaiter (arg \"ref \" (this):<f>d__0):<f>d__0):void\n" +
        "        return\n" +
        "        label <>resume_1\n" +
        "        assign (local <>a__1):global::System.Runtime.CompilerServices.TaskAwaiter = (member <>u__1 (this):<f>d__0):global::System.Runtime.CompilerServices.TaskAwaiter\n" +
        "        assign (member <>u__1 (this):<f>d__0):global::System.Runtime.CompilerServices.TaskAwaiter = (default):global::System.Runtime.CompilerServices.TaskAwaiter\n" +
        "        assign (member <>1__state (this):<f>d__0):int = (const -1):int\n" +
        "      expr (call GetResult (local <>a__1):global::System.Runtime.CompilerServices.TaskAwaiter):void\n" +
        "      expr (call Add (member log (this):<f>d__0):global::System.Collections.Generic.List<object> (const 2):int):void\n" +
        "      assign (local <>r):int = (const 42):int\n" +
        "      leave <>exit\n" +
        "    catch global::System.Exception <>ex\n" +
        "      assign (member <>1__state (this):<f>d__0):int = (const -2):int\n" +
        "      expr (call SetException (member <>t__builder (this):<f>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> (local <>ex):global::System.Exception):void\n" +
        "      return\n" +
        "    label <>exit\n" +
        "    assign (member <>1__state (this):<f>d__0):int = (const -2):int\n" +
        "    expr (call SetResult (member <>t__builder (this):<f>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> (local <>r):int):void\n" +
        "    return\n" +
        "  method private virtual final void SetStateMachine(global::System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) implements global::System.Runtime.CompilerServices.IAsyncStateMachine.SetStateMachine\n" +
        "    expr (call SetStateMachine (member <>t__builder (this):<f>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> (param stateMachine):global::System.Runtime.CompilerServices.IAsyncStateMachine):void\n" +
        "    return\n" +
        "kickoff\n" +
        "  decl <>sm <f>d__0 = (default):<f>d__0\n" +
        "  assign (member <>t__builder (local <>sm):<f>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> = (call Create (type global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int>)):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int>\n" +
        "  assign (member log (local <>sm):<f>d__0):global::System.Collections.Generic.List<object> = (param log):global::System.Collections.Generic.List<object>\n" +
        "  assign (member <>1__state (local <>sm):<f>d__0):int = (const -1):int\n" +
        "  expr (call Start (member <>t__builder (local <>sm):<f>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int> (arg \"ref \" (local <>sm):<f>d__0):<f>d__0):void\n" +
        "  return (member Task (member <>t__builder (local <>sm):<f>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder<int>):global::System.Threading.Tasks.Task<int>\n";

    [Fact]
    public async Task Simple_await_runs()
    {
        var log = new List<object>();
        var r = Build(Method("f", TaskInt, true, false, ("log", LogT)), SimpleAwait());
        var t = (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.Equal(42, await t);
        Assert.Equal(Seq(1, 2), log);
    }

    [Fact]
    public void Debug_builds_a_class_machine_and_hoists_every_local()
    {
        List<MStmt> body = [Decl("x", I, K(1)), LogE(L("x", I)), Do(Delay())];
        var rel = Build(Method("f", TaskT, true, false, ("log", LogT)), body);
        var dbg = Build(Method("f", TaskT, true, false, ("log", LogT)), body, debug: true);
        Assert.Equal(AirTypeKind.Struct, rel.Machine!.Kind);
        Assert.Equal(AirTypeKind.Class, dbg.Machine!.Kind);
        Assert.DoesNotContain(rel.Machine.Fields, f => f.Name.StartsWith("<x>"));
        Assert.Contains(dbg.Machine.Fields, f => f.Name == "<x>5__1");
        Assert.Contains(dbg.Machine.Methods, md => md.Name == ".ctor");
        Assert.Contains("assign (local <>sm):<f>d__0 = (this):<f>d__0", Dump(dbg));
        Assert.Contains("decl <>sm <f>d__0 = (new <f>d__0):<f>d__0", Dump(dbg));
    }

    [Fact]
    public async Task Debug_class_machine_runs()
    {
        var log = new List<object>();
        var r = Build(Method("f", TaskInt, true, false, ("log", LogT)), SimpleAwait(), debug: true);
        var t = (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.Equal(42, await t);
    }

    // ================================================================ 2. await in a loop

    // async def sum(n, log) : Int32 / total = 0 / for i in 0...n / await Task.Delay(1) / next if i == 1 / total += await Task.FromResult(i) / end / total
    static List<MStmt> AwaitInLoop() =>
    [
        Decl("total", I, K(0)),
        new MFor
        {
            Var = "i", VarType = "int", Start = K(0), Cmp = "<", End = L("n", I), Step = "++",
            Body =
            [
                Do(Delay()),
                new MIf { Cond = Bin(AirBinOp.Eq, L("i", I), K(1), B), Then = [new MContinue()] },
                new MAssign { Target = L("total", I), Op = "+=", Value = Await(FromResult(L("i", I)), I) },
                LogE(L("total", I)),
            ],
        },
        new MReturn { Value = L("total", I) },
    ];

    [Fact]
    public void Await_in_loop_dump()
    {
        var d = Dump(Build(Method("sum", TaskInt, true, false, ("n", I), ("log", LogT)), AwaitInLoop()));
        // the counted loop became a while loop whose `continue` runs the step; total and i live in fields
        Assert.Contains("field private int <total>5__1", d);
        Assert.Contains("field private int <i>5__2", d);
        Assert.Contains("      while (Lt (member <i>5__2 (this):<sum>d__0):int (member n (this):<sum>d__0):int):bool\n", d);
        Assert.Contains("goto <>continue_1", d);
        Assert.Contains("label <>continue_1\n", d);
        Assert.Contains("assign (member <i>5__2 (this):<sum>d__0):int = (Add (member <i>5__2 (this):<sum>d__0):int (const 1):int):int", d);
        Assert.Contains("switch (member <>1__state (this):<sum>d__0):int 0: <>resume_2, <>resume_3", d);
        // `total += await x` is `total = total + await x`; the awaited value is a MoveNext local (no value before the resume)
        Assert.Contains("assign (local <>r__1):int = (call GetResult (local <>a__2):global::System.Runtime.CompilerServices.TaskAwaiter<int>):int\n", d);
        Assert.Contains("assign (member <total>5__1 (this):<sum>d__0):int = (Add (member <total>5__1 (this):<sum>d__0):int (local <>r__1):int):int", d);
    }

    [Fact]
    public async Task Await_in_loop_runs()
    {
        var log = new List<object>();
        var r = Build(Method("sum", TaskInt, true, false, ("n", I), ("log", LogT)), AwaitInLoop());
        var t = (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("n", 4), ("log", log))!;
        Assert.Equal(0 + 2 + 3, await t);
        Assert.Equal(Seq(0, 2, 5), log);
    }

    // ================================================================ 3. await in try/finally

    // async def f(log) : Int32
    //   begin / log << "a" / await Task.Delay(1) / log << "b" / raise InvalidOperationException
    //   rescue InvalidOperationException / log << "caught" / ensure / log << "fin" / end
    //   begin / await Task.Delay(1) / return 7 / ensure / await Task.Delay(1) / log << "async-fin" / end
    static List<MStmt> AwaitInTry() =>
    [
        new MTry
        {
            Body =
            [
                Log("a"), Do(Delay()), Log("b"),
                new MThrow { Value = new MNew { TypeCs = "global::System.InvalidOperationException", Type = Clr(typeof(InvalidOperationException)), Args = [K("x")] } },
            ],
            Catches = [new MCatch { TypeCs = "global::System.InvalidOperationException", Name = "e", Body = [Log("caught")] }],
            Finally = [Log("fin")],
        },
        new MTry
        {
            Body = [Do(Delay()), new MReturn { Value = K(7) }],
            Finally = [Do(Delay()), Log("async-fin")],
        },
        new MReturn { Value = K(0) },
    ];

    [Fact]
    public void Await_in_try_finally_dump()
    {
        var d = Dump(Build(Method("f", TaskInt, true, false, ("log", LogT)), AwaitInTry()));
        // the try holding an await: a label before it, a second dispatch inside, the finally guarded by the state
        Assert.Contains("      label <>try_2\n      try\n        switch (member <>1__state (this):<f>d__0):int 0: <>resume_3\n", d);
        Assert.Contains("      finally\n        if (Eq (member <>1__state (this):<f>d__0):int (const -1):int):bool\n", d);
        Assert.Contains("switch (member <>1__state (this):<f>d__0):int 0: <>try_2, <>try_4, <>resume_6", d);
        // the awaiting finally runs after a catch that stores the exception; the return is replayed after it
        Assert.Contains("assign (member <>ex__1 (this):<f>d__0):global::System.Exception? = (local <>c__3):global::System.Exception", d);
        Assert.Contains("expr (call Throw (call Capture (type global::System.Runtime.ExceptionServices.ExceptionDispatchInfo)", d);
        Assert.Contains("assign (member <>pb__2 (this):<f>d__0):int = (const 1):int\n", d);
        Assert.Contains("        if (Eq (member <>pb__2 (this):<f>d__0):int (const 1):int):bool\n          assign (local <>r):int = (member <>rv__4 (this):<f>d__0):int\n          leave <>exit\n", d);
        Assert.Contains("goto <>tryend_1", d);
    }

    [Fact]
    public async Task Await_in_try_finally_runs()
    {
        var log = new List<object>();
        var r = Build(Method("f", TaskInt, true, false, ("log", LogT)), AwaitInTry());
        var t = (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.Equal(7, await t);
        Assert.Equal(Seq("a", "b", "caught", "fin", "async-fin"), log);
    }

    [Fact]
    public async Task Exception_after_an_await_faults_the_task_and_runs_finally()
    {
        var log = new List<object>();
        List<MStmt> body =
        [
            new MTry
            {
                Body = [Do(Delay()), new MThrow { Value = new MNew { TypeCs = "global::System.ArgumentException", Type = Clr(typeof(ArgumentException)), Args = [K("boom")] } }],
                Finally = [Do(Delay()), Log("fin")],
            },
        ];
        var r = Build(Method("f", TaskT, true, false, ("log", LogT)), body);
        var t = (Task)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", log))!;
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => t);
        Assert.Equal("boom", ex.Message);
        Assert.Equal(Seq("fin"), log);
    }

    // ================================================================ 4. several awaits, locals across them

    // async def f(log) : Int32 / a = await Task.FromResult(1) / b = a + await Task.FromResult(2) / Math.Max(a + b, await Task.FromResult(10)) + b
    static List<MStmt> SeveralAwaits() =>
    [
        new MDecl { Name = "a", Init = Await(FromResult(K(1)), I) },
        new MDecl { Name = "b", Init = Bin(AirBinOp.Add, L("a", I), Await(FromResult(K(2)), I), I) },
        Do(Delay()),
        new MReturn { Value = Bin(AirBinOp.Add, Static(typeof(Math), "Max", I, Bin(AirBinOp.Add, L("a", I), L("b", I), I), Await(FromResult(K(10)), I)), L("b", I), I) },
    ];

    [Fact]
    public void Several_awaits_dump()
    {
        var d = Dump(Build(Method("f", TaskInt, true, false, ("log", LogT)), SeveralAwaits()));
        Assert.Contains("field private int <a>5__", d);
        Assert.Contains("field private int <b>5__", d);
        // one awaiter field per awaiter type
        Assert.Contains("field private global::System.Runtime.CompilerServices.TaskAwaiter<int> <>u__1\n", d);
        Assert.Contains("field private global::System.Runtime.CompilerServices.TaskAwaiter <>u__2\n", d);
        Assert.DoesNotContain("<>u__3", d);
        // `a + b` is evaluated before the third await: it lives in a hoisted spill temporary
        Assert.Contains("field private int <>s__", d);
        Assert.Contains("switch (member <>1__state (this):<f>d__0):int 0: <>resume_1, <>resume_2, <>resume_3, <>resume_4", d);
    }

    [Fact]
    public async Task Several_awaits_run()
    {
        var r = Build(Method("f", TaskInt, true, false, ("log", LogT)), SeveralAwaits());
        var t = (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", new List<object>()))!;
        Assert.Equal(Math.Max(1 + 3, 10) + 3, await t);
    }

    // ================================================================ 5. async Task<T> / ValueTask<T> / Task

    [Fact]
    public async Task Builders_follow_the_return_type()
    {
        List<MStmt> Body() => [Do(Delay()), new MReturn { Value = K(5) }];
        var vt = Build(Method("f", Clr(typeof(ValueTask<int>)), true, false), Body());
        Assert.Equal("global::System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<int>", vt.Machine!.Fields.First(f => f.Name == "<>t__builder").Type.ToCs());
        Assert.Equal(5, await (ValueTask<int>)new SmEval(vt.Machine).Kickoff(vt.Kickoff!, null)!);

        var plain = Build(Method("g", TaskT, true, false), [Do(Delay())]);
        Assert.Equal("global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder", plain.Machine!.Fields.First(f => f.Name == "<>t__builder").Type.ToCs());
        Assert.Contains("expr (call SetResult (member <>t__builder (this):<g>d__0):global::System.Runtime.CompilerServices.AsyncTaskMethodBuilder):void", Dump(Build(Method("g", TaskT, true, false), [Do(Delay())])));
        await (Task)new SmEval(plain.Machine).Kickoff(plain.Kickoff!, null)!;

        var vt0 = Build(Method("h", Clr(typeof(ValueTask)), true, false), [Do(Delay())]);
        Assert.Equal("global::System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder", vt0.Machine!.Fields.First(f => f.Name == "<>t__builder").Type.ToCs());
    }

    [Fact]
    public async Task Instance_method_keeps_this_in_a_field()
    {
        var owner = new AirType.ClrRef("global::System.Text.StringBuilder", typeof(System.Text.StringBuilder), []);
        var m = Method("f", TaskInt, true, false);
        m.Static = false;
        m.This = owner;
        List<MStmt> body = [Do(Delay()), new MReturn { Value = new MMember { Target = new AirThis { Type = owner }, Name = "Length", Type = I } }];
        var r = Build(m, body);
        Assert.Contains(r.Machine!.Fields, f => f.Name == "<>4__this" && f.Type.ToCs() == "global::System.Text.StringBuilder");
        Assert.Contains("assign (member <>4__this (local <>sm):<f>d__0):global::System.Text.StringBuilder = (this):global::System.Text.StringBuilder", Dump(Build(m, body)));
        var t = (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, new System.Text.StringBuilder("abc"))!;
        Assert.Equal(3, await t);
    }

    // ================================================================ 6. async void

    [Fact]
    public void Async_void_is_rejected_and_the_body_left_alone()
    {
        List<MStmt> body = [Do(Delay())];
        var r = StateMachines.Run(Method("f", V, true, false), body);
        Assert.Null(r.Machine);
        Assert.Null(r.Kickoff);
        Assert.Contains(r.Unsupported, u => u.Contains("async void"));
        Assert.IsType<MUnary>(((MExprStmt)body[0]).E);
    }

    [Fact]
    public void Alice_async_def_without_a_value_is_wrapped_in_Task()
    {
        var ast = Parser.ParseFile("async def tick(n : Int32)\n  await Task.Yield\n  puts n\nend\nasync def twice(x : Int32) : Int32\n  await Task.Yield\n  x * 2\nend\ndef nums : IEnumerable(Int32)\n  yield 1\nend\nasync def last_await(n : Int32)\n  await Task.Yield\nend\n", "main.al");
        var binder = new Alice.Compiler.Semantics.Binder([ast], new ClrTypes(null));
        binder.Bind();
        MethodSym Find(string n) => binder.Types.Values.SelectMany(t => t.Methods.Values).SelectMany(x => x).First(x => x.Name == n);
        var tick = StateMachineMethod.For(Find("tick"), 0);
        Assert.Equal("global::System.Threading.Tasks.Task", tick.Ret.ToCs());
        Assert.Equal(StateMachineKind.Async, StateMachines.KindOf(tick));
        Assert.Equal("global::System.Threading.Tasks.Task<int>", StateMachineMethod.For(Find("twice"), 1).Ret.ToCs());
        Assert.Equal(StateMachineKind.Iterator, StateMachines.KindOf(StateMachineMethod.For(Find("nums"), 2)));
        Assert.Equal(new[] { "n" }, tick.Params.Select(p => p.Name));
        // an await of a non-generic awaitable as the last expression: void, so Task (the C# emitter says Task<dynamic>)
        Assert.Equal("global::System.Threading.Tasks.Task", StateMachineMethod.For(Find("last_await"), 3).Ret.ToCs());
    }

    [Fact]
    public void Untyped_awaits_are_reported_not_guessed()
    {
        List<MStmt> body = [new MDecl { Name = "x", Init = Await(Call(null, "mystery", null), null) }, Do(Delay()), LogE(L("x", null!))];
        var r = StateMachines.Run(Method("f", TaskT, true, false, ("log", LogT)), body);
        Assert.Null(r.Machine);
        Assert.NotEmpty(r.Unsupported);
        // the emitter's inference is consulted when a node has no type
        var typed = StateMachines.Run(Method("f", TaskT, true, false, ("log", LogT)), body,
            new StateMachineOptions { TypeOf = e => e is MCall { Name: "mystery" } ? TaskInt : null });
        Assert.Empty(typed.Unsupported);
        Assert.Contains(typed.Machine!.Fields, f => f.Name == "<x>5__1" && f.Type.ToCs() == "int");
    }

    // ================================================================ 7. iterator, yield in a loop

    // def nums(n) : IEnumerable(Int32) / i = 0 / while i < n / yield i * 10 / i += 1 / end
    static List<MStmt> YieldInLoop() =>
    [
        Decl("i", I, K(0)),
        new MWhile
        {
            Cond = Bin(AirBinOp.Lt, L("i", I), L("n", I), B),
            Body = [Yield(Bin(AirBinOp.Mul, L("i", I), K(10), I)), new MAssign { Target = L("i", I), Op = "+=", Value = K(1) }],
        },
    ];

    static readonly AirType IEnumInt = Clr(typeof(IEnumerable<int>));

    [Fact]
    public void Iterator_dump()
    {
        var r = Build(Method("nums", IEnumInt, false, true, ("n", I)), YieldInLoop());
        Assert.Equal(StateMachineKind.Iterator, r.Kind);
        Assert.Equal(IteratorDump, Dump(r));
    }

    const string IteratorDump =
        "type class sealed beforefieldinit <nums>d__0 : object, global::System.Collections.Generic.IEnumerable<int>, global::System.Collections.IEnumerable, global::System.Collections.Generic.IEnumerator<int>, global::System.Collections.IEnumerator, global::System.IDisposable\n" +
        "  field public int <>1__state\n" +
        "  field private int <>2__current\n" +
        "  field private int <>l__initialThreadId\n" +
        "  field public int n\n" +
        "  field public int <>3__n\n" +
        "  field private int <i>5__1\n" +
        "  property int System.Collections.Generic.IEnumerator<int>.Current get=System.Collections.Generic.IEnumerator<int>.get_Current\n" +
        "  property object System.Collections.IEnumerator.Current get=System.Collections.IEnumerator.get_Current\n" +
        "  method private virtual final bool MoveNext() implements global::System.Collections.IEnumerator.MoveNext\n" +
        "    switch (member <>1__state (this):<nums>d__0):int 0: <>start_1, <>yield_2\n" +
        "    return (const false):bool\n" +
        "    label <>start_1\n" +
        "    assign (member <>1__state (this):<nums>d__0):int = (const -1):int\n" +
        "    assign (member <i>5__1 (this):<nums>d__0):int = (const 0):int\n" +
        "    while (Lt (member <i>5__1 (this):<nums>d__0):int (member n (this):<nums>d__0):int):bool\n" +
        "      assign (member <>2__current (this):<nums>d__0):int = (Mul (member <i>5__1 (this):<nums>d__0):int (const 10):int):int\n" +
        "      assign (member <>1__state (this):<nums>d__0):int = (const 1):int\n" +
        "      return (const true):bool\n" +
        "      label <>yield_2\n" +
        "      assign (member <>1__state (this):<nums>d__0):int = (const -1):int\n" +
        "      assign (member <i>5__1 (this):<nums>d__0):int += (const 1):int\n" +
        "    return (const false):bool\n" +
        "  method public specialname void .ctor(int <>1__state)\n" +
        "    expr (call .ctor (base):object):void\n" +
        "    assign (member <>1__state (this):<nums>d__0):int = (param <>1__state):int\n" +
        "    assign (member <>l__initialThreadId (this):<nums>d__0):int = (member CurrentManagedThreadId (type global::System.Environment)):int\n" +
        "    return\n" +
        "  method private virtual final void System.IDisposable.Dispose() implements global::System.IDisposable.Dispose\n" +
        "    assign (member <>1__state (this):<nums>d__0):int = (const -2):int\n" +
        "    return\n" +
        "  method private virtual final specialname int System.Collections.Generic.IEnumerator<int>.get_Current() implements global::System.Collections.Generic.IEnumerator<int>.get_Current\n" +
        "    return (member <>2__current (this):<nums>d__0):int\n" +
        "  method private virtual final specialname object System.Collections.IEnumerator.get_Current() implements global::System.Collections.IEnumerator.get_Current\n" +
        "    return (cast object (member <>2__current (this):<nums>d__0):int):object\n" +
        "  method private virtual final void System.Collections.IEnumerator.Reset() implements global::System.Collections.IEnumerator.Reset\n" +
        "    throw (new global::System.NotSupportedException):global::System.NotSupportedException\n" +
        "  method private virtual final global::System.Collections.Generic.IEnumerator<int> System.Collections.Generic.IEnumerable<int>.GetEnumerator() implements global::System.Collections.Generic.IEnumerable<int>.GetEnumerator\n" +
        "    decl <>d <nums>d__0\n" +
        "    if (And (Eq (member <>1__state (this):<nums>d__0):int (const -2):int):bool (Eq (member <>l__initialThreadId (this):<nums>d__0):int (member CurrentManagedThreadId (type global::System.Environment)):int):bool):bool\n" +
        "      assign (member <>1__state (this):<nums>d__0):int = (const 0):int\n" +
        "      assign (local <>d):<nums>d__0 = (this):<nums>d__0\n" +
        "    else\n" +
        "      assign (local <>d):<nums>d__0 = (new <nums>d__0 (const 0):int):<nums>d__0\n" +
        "    assign (member n (local <>d):<nums>d__0):int = (member <>3__n (this):<nums>d__0):int\n" +
        "    return (local <>d):<nums>d__0\n" +
        "  method private virtual final global::System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() implements global::System.Collections.IEnumerable.GetEnumerator\n" +
        "    return (call System.Collections.Generic.IEnumerable<int>.GetEnumerator (this):<nums>d__0):global::System.Collections.Generic.IEnumerator<int>\n" +
        "kickoff\n" +
        "  decl <>d <nums>d__0 = (new <nums>d__0 (const -2):int):<nums>d__0\n" +
        "  assign (member <>3__n (local <>d):<nums>d__0):int = (param n):int\n" +
        "  return (local <>d):<nums>d__0\n";

    [Fact]
    public void Iterator_runs_and_enumerates_twice()
    {
        var r = Build(Method("nums", IEnumInt, false, true, ("n", I)), YieldInLoop());
        var ev = new SmEval(r.Machine!);
        var seq = ev.Kickoff(r.Kickoff!, null, ("n", 3))!;
        Assert.Equal(Seq(0, 10, 20), ev.Enumerate(seq));
        Assert.Equal(Seq(0, 10, 20), ev.Enumerate(seq));   // Dispose finished it (-2): enumerable again
        var e1 = ev.GetEnumerator(seq);
        Assert.Same(seq, e1);                            // the first one on the creating thread reuses the object
    }

    // ================================================================ 8. iterator with try/finally, Dispose

    // def f(log) : IEnumerable(Int32)
    //   begin / yield 1 / begin / yield 2 / yield 3 / ensure / log << "inner" / end / ensure / log << "outer" / end
    //   yield 4
    static List<MStmt> YieldInTry() =>
    [
        new MTry
        {
            Body =
            [
                Yield(K(1)),
                new MTry { Body = [Yield(K(2)), Yield(K(3))], Finally = [Log("inner")] },
            ],
            Finally = [Log("outer")],
        },
        Yield(K(4)),
    ];

    [Fact]
    public void Iterator_try_finally_dump()
    {
        var d = Dump(Build(Method("f", IEnumInt, false, true, ("log", LogT)), YieldInTry()));
        Assert.Contains("  method private void <>m__Finally1()\n    assign (member <>1__state (this):<f>d__0):int = (const -1):int\n    expr (call Add (member log (this):<f>d__0)", d);
        Assert.Contains("  method private void <>m__Finally2()\n    assign (member <>1__state (this):<f>d__0):int = (const -3):int\n", d);
        Assert.Contains("      block\n        assign (member <>1__state (this):<f>d__0):int = (const -3):int\n", d);
        Assert.Contains("assign (member <>1__state (this):<f>d__0):int = (const -4):int", d);
        // Dispose runs the finally blocks of the states inside them, innermost first
        Assert.Contains(
            "    if (Or (Or (Or (Or (Eq (member <>1__state (this):<f>d__0):int (const -3):int):bool (Eq (member <>1__state (this):<f>d__0):int (const -4):int):bool):bool (Eq (member <>1__state (this):<f>d__0):int (const 2):int):bool):bool (Eq (member <>1__state (this):<f>d__0):int (const 3):int):bool):bool (Eq (member <>1__state (this):<f>d__0):int (const 1):int):bool):bool\n" +
            "      try\n" +
            "        if (Or (Or (Eq (member <>1__state (this):<f>d__0):int (const -4):int):bool (Eq (member <>1__state (this):<f>d__0):int (const 2):int):bool):bool (Eq (member <>1__state (this):<f>d__0):int (const 3):int):bool):bool\n" +
            "          try\n" +
            "          finally\n" +
            "            expr (call <>m__Finally2 (this):<f>d__0):void\n" +
            "      finally\n" +
            "        expr (call <>m__Finally1 (this):<f>d__0):void\n", d);
        // MoveNext runs Dispose when an exception escapes (a fault block in C#)
        Assert.Contains("    catch global::System.Exception <>ex\n      expr (call System.IDisposable.Dispose (this):<f>d__0):void\n", d);
    }

    [Fact]
    public void Iterator_finally_runs_on_completion_and_on_early_dispose()
    {
        var r = Build(Method("f", IEnumInt, false, true, ("log", LogT)), YieldInTry());
        var ev = new SmEval(r.Machine!);
        var log = new List<object>();
        var seq = ev.Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.Equal(Seq(1, 2, 3, 4), ev.Enumerate(seq));
        Assert.Equal(Seq("inner", "outer"), log);

        log.Clear();
        var e = ev.GetEnumerator(seq);
        Assert.True(ev.MoveNext(e));
        Assert.True(ev.MoveNext(e));
        Assert.Equal(2, ev.Current(e));
        ev.Dispose(e);                   // suspended inside both try blocks
        Assert.Equal(Seq("inner", "outer"), log);
        Assert.False(ev.MoveNext(e));    // disposed: finished

        log.Clear();
        e = ev.GetEnumerator(seq);
        Assert.True(ev.MoveNext(e));
        ev.Dispose(e);                   // only inside the outer one
        Assert.Equal(Seq("outer"), log);
    }

    [Fact]
    public void Iterator_break_and_yield_break_leave_try_blocks_through_their_finally()
    {
        // def f(log) / i = 0 / while true / begin / yield i / break if i == 2 / i += 1 / ensure / log << i / end / end / log << "after" / begin / yield 99 / return / ensure / log << "rb" / end / yield 100
        List<MStmt> body =
        [
            Decl("i", I, K(0)),
            new MWhile
            {
                Cond = AirConst.Bool(true),
                Body =
                [
                    new MTry
                    {
                        Body =
                        [
                            Yield(L("i", I)),
                            new MIf { Cond = Bin(AirBinOp.Eq, L("i", I), K(2), B), Then = [new MBreak()] },
                            new MAssign { Target = L("i", I), Op = "+=", Value = K(1) },
                        ],
                        Finally = [LogE(L("i", I))],
                    },
                ],
            },
            Log("after"),
            new MTry { Body = [Yield(K(99)), Do(new AirYieldBreak())], Finally = [Log("rb")] },
            Yield(K(100)),
        ];
        var r = Build(Method("f", IEnumInt, false, true, ("log", LogT)), body);
        var ev = new SmEval(r.Machine!);
        var log = new List<object>();
        Assert.Equal(Seq(0, 1, 2, 99), ev.Enumerate(ev.Kickoff(r.Kickoff!, null, ("log", log))!));
        Assert.Equal(Seq(1, 2, 2, "after", "rb"), log);
    }

    [Fact]
    public void Iterator_exception_runs_pending_finally_blocks()
    {
        List<MStmt> body =
        [
            new MTry
            {
                Body = [Yield(K(1)), new MThrow { Value = new MNew { TypeCs = "global::System.InvalidOperationException", Type = Clr(typeof(InvalidOperationException)), Args = [K("it")] } }],
                Finally = [Log("fin")],
            },
        ];
        var r = Build(Method("f", Clr(typeof(IEnumerator<int>)), false, true, ("log", LogT)), body);
        Assert.DoesNotContain(r.Machine!.Methods, md => md.Name.Contains("GetEnumerator"));
        var ev = new SmEval(r.Machine);
        var log = new List<object>();
        var e = ev.Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.True(ev.MoveNext(e));
        Assert.Throws<InvalidOperationException>(() => ev.MoveNext(e));
        Assert.Equal(Seq("fin"), log);
    }

    // ================================================================ 9. async iterator

    // async def ticks(n, log) : IAsyncEnumerable(Int32)
    //   begin / for i in 0...n / await Task.Delay(1) / yield i / end / ensure / await Task.Delay(1) / log << "done" / end
    static List<MStmt> AsyncIterator() =>
    [
        new MTry
        {
            Body =
            [
                new MFor
                {
                    Var = "i", VarType = "int", Start = K(0), Cmp = "<", End = L("n", I), Step = "= unchecked(i + 1)",
                    Body = [Do(Delay()), Yield(L("i", I))],
                },
            ],
            Finally = [Do(Delay()), Log("done")],
        },
    ];

    static readonly AirType IAsyncEnumInt = Clr(typeof(IAsyncEnumerable<int>));

    [Fact]
    public void Async_iterator_dump()
    {
        var r = Build(Method("ticks", IAsyncEnumInt, true, true, ("n", I), ("log", LogT)), AsyncIterator());
        Assert.Equal(StateMachineKind.AsyncIterator, r.Kind);
        var d = Dump(r);
        Assert.StartsWith("type class sealed beforefieldinit <ticks>d__0 : object, global::System.Collections.Generic.IAsyncEnumerable<int>, global::System.Collections.Generic.IAsyncEnumerator<int>, global::System.IAsyncDisposable, global::System.Threading.Tasks.Sources.IValueTaskSource<bool>, global::System.Threading.Tasks.Sources.IValueTaskSource, global::System.Runtime.CompilerServices.IAsyncStateMachine\n" +
            "  field public int <>1__state\n" +
            "  field public global::System.Runtime.CompilerServices.AsyncIteratorMethodBuilder <>t__builder\n" +
            "  field public global::System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> <>v__promiseOfValueOrEnd\n" +
            "  field private int <>2__current\n" +
            "  field private bool <>w__disposeMode\n" +
            "  field private int <>l__initialThreadId\n", d);
        // yields suspend with negative states and resolve the promise; a resume in dispose mode jumps to the finally
        Assert.Contains("assign (member <>1__state (this):<ticks>d__0):int = (const -4):int\n", d);
        Assert.Contains("expr (call SetResult (member <>v__promiseOfValueOrEnd (this):<ticks>d__0):global::System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> (const true):bool):void", d);
        Assert.Contains("if (member <>w__disposeMode (this):<ticks>d__0):bool\n", d);
        Assert.Contains("method private virtual final global::System.Threading.Tasks.ValueTask<bool> System.Collections.Generic.IAsyncEnumerator<int>.MoveNextAsync()", d);
        Assert.Contains("return (new global::System.Threading.Tasks.ValueTask<bool> (cast global::System.Threading.Tasks.Sources.IValueTaskSource<bool> (this):<ticks>d__0):global::System.Threading.Tasks.Sources.IValueTaskSource<bool> (local <>ver):short):global::System.Threading.Tasks.ValueTask<bool>", d);
        Assert.Contains("method private virtual final global::System.Threading.Tasks.ValueTask System.IAsyncDisposable.DisposeAsync()", d);
        // await states 0, 1 and yield state -4 share one table; the yield resumes inside the try (the finally awaits)
        Assert.Contains("      switch (member <>1__state (this):<ticks>d__0):int -4: <>try_3, _, _, _, <>try_3, <>resume_7\n", d);
        Assert.Contains("          label <>dispose_", d);
        Assert.Contains("                if (member <>w__disposeMode (this):<ticks>d__0):bool\n                  goto <>dispose_", d);
    }

    [Fact]
    public async Task Async_iterator_runs_to_the_end_and_disposes_early()
    {
        var r = Build(Method("ticks", IAsyncEnumInt, true, true, ("n", I), ("log", LogT)), AsyncIterator());
        var ev = new SmEval(r.Machine!);
        var log = new List<object>();
        var seq = ev.Kickoff(r.Kickoff!, null, ("n", 3), ("log", log))!;
        Assert.Equal(Seq(0, 1, 2), await ev.EnumerateAsync(seq, int.MaxValue));
        Assert.Equal(Seq("done"), log);

        log.Clear();
        Assert.Equal(Seq(0), await ev.EnumerateAsync(seq, 1));   // break after the first: DisposeAsync runs the finally
        Assert.Equal(Seq("done"), log);
    }

    [Fact]
    public async Task Await_foreach_and_await_using_are_lowered()
    {
        // async def f(log) : Int32 / total = 0 / await using r = Res.new(log) / for await x in Source.range(3) / total += x / end / end / total
        var src = Static(typeof(SmSources), nameof(SmSources.Range), IAsyncEnumInt, K(3), L("log", LogT));
        var res = new MNew { TypeCs = "global::Alice.Tests.Compiler.SmResource", Type = Clr(typeof(SmResource)), Args = [L("log", LogT)] };
        List<MStmt> body =
        [
            Decl("total", I, K(0)),
            new MUsing
            {
                Await = true, Name = "r", Value = res,
                Body = [new MForeach { Await = true, Vars = ["x"], Coll = src, Body = [new MAssign { Target = L("total", I), Op = "+=", Value = L("x", I) }, LogE(L("x", I))] }],
            },
            new MReturn { Value = L("total", I) },
        ];
        var r = Build(Method("f", TaskInt, true, false, ("log", LogT)), body);
        var d = Dump(Build(Method("f", TaskInt, true, false, ("log", LogT)), body));
        Assert.Contains("(call GetAsyncEnumerator (call Range (type global::Alice.Tests.Compiler.SmSources) (const 3):int (member log (this):<f>d__0):global::System.Collections.Generic.List<object>):global::System.Collections.Generic.IAsyncEnumerable<int> (default):global::System.Threading.CancellationToken)", d);
        // the MoveNextAsync result is assigned at the resume: a local, not a field
        Assert.DoesNotContain("field private bool", d);
        Assert.Contains("(call MoveNextAsync ", d);
        Assert.Contains("(call DisposeAsync ", d);
        var log = new List<object>();
        var t = (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.Equal(3, await t);
        Assert.Equal(Seq(0, 1, 2, "range-disposed", "disposed"), log);
    }

    [Fact]
    public void Unsupported_shapes_are_reported_and_leave_the_body_alone()
    {
        string[] Why(StateMachineMethod m, List<MStmt> body)
        {
            string before = MirDump.Of(body);
            var r = StateMachines.Run(m, body);
            Assert.Null(r.Machine);
            Assert.Equal(before, MirDump.Of(body));
            return [.. r.Unsupported];
        }
        // yield inside an ensure clause: Dispose could not run it (C# forbids it too)
        Assert.Contains(Why(Method("f", IEnumInt, false, true), [new MTry { Body = [Log(1)], Finally = [Yield(K(1))] }]), u => u.Contains("ensure"));
        // ref parameters
        Assert.Contains(Why(Method("f", TaskT, true, false, ("x", new AirType.ByRef(I, RefKind.Ref))), [Do(Delay())]), u => u.Contains("by reference"));
        // a lambda that would capture a hoisted local (closure conversion runs first in the IL pipeline)
        var lam = new MLambda { Params = [], ExprBody = L("x", I) };
        Assert.Contains(Why(Method("f", TaskT, true, false), [Decl("x", I, K(1)), Do(Delay()), Do(new MInvoke { Target = lam })]), u => u.Contains("lambda"));
    }

    [Fact]
    public void Generic_methods_get_a_generic_machine()
    {
        var tT = new AirType.TypeParam("T");
        var m = Method("first", new AirType.ClrRef("global::System.Collections.Generic.IEnumerable", typeof(IEnumerable<>), [tT]), false, true, ("x", tT));
        m.TypeParams = ["T"];
        var r = Build(m, [Yield(L("x", tT)), Yield(L("x", tT))]);
        Assert.Equal(["T"], r.Machine!.TypeParams);
        Assert.Equal("<first>d__0<T>", r.Machine.Self.ToCs());
        Assert.Contains(r.Machine.Fields, f => f.Name == "<>2__current" && f.Type.ToCs() == "T");
        Assert.Contains("decl <>d <first>d__0<T> = (new <first>d__0<T> (const -2):int):<first>d__0<T>", Dump(r));
    }

    static MNew Raise(string msg) => new() { TypeCs = "global::System.InvalidOperationException", Type = Clr(typeof(InvalidOperationException)), Args = [K(msg)] };

    [Fact]
    public async Task Return_of_a_void_await_in_a_Task_method_awaits_then_returns()
    {
        var yieldT = Clr(typeof(YieldAwaitable));
        var r = Build(Method("last_await", TaskT, true, false, ("log", LogT)), [Log(1), new MReturn { Value = Await(Static(typeof(Task), "Yield", yieldT), V) }]);
        Assert.Contains("YieldAwaitable.YieldAwaiter", Dump(r));
        var log = new List<object>();
        await (Task)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.Equal(Seq(1), log);
    }

    [Fact]
    public async Task Await_inside_a_rescue_clause_runs_after_the_try()
    {
        // async def f(log) : Int32 / begin / log << "a" / raise "x" / rescue InvalidOperationException => e / await Task.Delay(1) / log << e.message / ensure / log << "fin" / end / log << "after" / 1
        var ioe = Clr(typeof(InvalidOperationException));
        List<MStmt> body =
        [
            new MTry
            {
                Body = [Log("a"), new MThrow { Value = Raise("x") }, Log("unreached")],
                Catches = [new MCatch { TypeCs = "global::System.InvalidOperationException", Name = "e", Body = [Do(Delay()), LogE(new MMember { Target = L("e", ioe), Name = "Message", Type = S })] }],
                Finally = [Log("fin")],
            },
            Log("after"),
            new MReturn { Value = K(1) },
        ];
        var d = Dump(Build(Method("f", TaskInt, true, false, ("log", LogT)), body));
        // the clause body runs after the try, bound to the recorded exception (a field: it lives across the await)
        Assert.Contains("(cast global::System.InvalidOperationException (member <>cx__", d);
        Assert.Contains("if (Eq (member <>ci__", d);
        var r = Build(Method("f", TaskInt, true, false, ("log", LogT)), body);
        var log = new List<object>();
        Assert.Equal(1, await (Task<int>)new SmEval(r.Machine!).Kickoff(r.Kickoff!, null, ("log", log))!);
        Assert.Equal(Seq("a", "x", "fin", "after"), log);
    }

    [Fact]
    public void Yield_inside_begin_rescue_and_inside_the_rescue_clause()
    {
        // def f(log) : IEnumerable(Int32) / begin / yield 1 / raise "boom" / rescue InvalidOperationException => e / yield 10 / log << e.message / ensure / log << "fin" / end / yield 3
        var ioe = Clr(typeof(InvalidOperationException));
        List<MStmt> Body() =>
        [
            new MTry
            {
                Body = [Yield(K(1)), new MThrow { Value = Raise("boom") }, Yield(K(2))],
                Catches = [new MCatch { TypeCs = "global::System.InvalidOperationException", Name = "e", Body = [Yield(K(10)), LogE(new MMember { Target = L("e", ioe), Name = "Message", Type = S })] }],
                Finally = [Log("fin")],
            },
            Yield(K(3)),
        ];
        var d = Dump(Build(Method("f", IEnumInt, false, true, ("log", LogT)), Body()));
        Assert.Contains("method private void <>m__Finally1()", d);   // the ensure part is flattened
        Assert.Contains("label <>try_", d);                           // the rescue part stays a real try with its own dispatch
        var r = Build(Method("f", IEnumInt, false, true, ("log", LogT)), Body());
        var ev = new SmEval(r.Machine!);
        var log = new List<object>();
        var seq = ev.Kickoff(r.Kickoff!, null, ("log", log))!;
        Assert.Equal(Seq(1, 10, 3), ev.Enumerate(seq));
        Assert.Equal(Seq("boom", "fin"), log);

        log.Clear();
        var e = ev.GetEnumerator(seq);
        Assert.True(ev.MoveNext(e));
        Assert.True(ev.MoveNext(e));
        Assert.Equal(10, ev.Current(e));
        ev.Dispose(e);
        Assert.Equal(Seq("fin"), log);
    }

    [Fact]
    public void Iterator_break_through_a_nested_ensure_runs_finally_blocks_in_order()
    {
        // while true / begin / yield 1 / begin / break / ensure / log << "inner" / end / ensure / log << "outer" / end / end / log << "after"
        List<MStmt> body =
        [
            new MWhile
            {
                Cond = AirConst.Bool(true),
                Body = [new MTry { Body = [Yield(K(1)), new MTry { Body = [new MBreak()], Finally = [Log("inner")] }], Finally = [Log("outer")] }],
            },
            Log("after"),
        ];
        var r = Build(Method("f", IEnumInt, false, true, ("log", LogT)), body);
        Assert.Contains("label <>aftertry_", Dump(Build(Method("f", IEnumInt, false, true, ("log", LogT)), body)));
        var ev = new SmEval(r.Machine!);
        var log = new List<object>();
        Assert.Equal(Seq(1), ev.Enumerate(ev.Kickoff(r.Kickoff!, null, ("log", log))!));
        Assert.Equal(Seq("inner", "outer", "after"), log);
    }

    [Fact]
    public async Task Async_lambdas_get_their_own_machine()
    {
        // f = ->(x : Int32) { await Task.Delay(1); x * 2 }   (in a method that is not async)
        var lam = new MLambda
        {
            Static = true, Params = ["int x"], Type = Clr(typeof(Func<int, Task<int>>)),
            Body = [Do(Delay()), new MReturn { Value = Bin(AirBinOp.Mul, L("x", I), K(2), I) }],
        };
        List<MStmt> body = [new MDecl { Name = "f", Init = lam }];
        var r = StateMachines.AsyncLambdas(body, "g", null);
        Assert.Empty(r.Unsupported);
        var m = Assert.Single(r.Lambdas);
        Assert.Equal("<<g>b__0_0>d__0", m.Name);
        var newLam = (MLambda)((MDecl)body[0]).Init!;
        Assert.Contains("(call Start", MirDump.Of(newLam.Body!));
        Assert.Equal(42, await (Task<int>)new SmEval(m).Kickoff(newLam.Body!, null, ("x", 21))!);

        // a lambda that captures outer locals needs closure conversion first: reported, body untouched
        var cap = new MLambda { Static = false, Params = [], Body = [Do(Delay())] };
        List<MStmt> body2 = [Do(new MInvoke { Target = cap })];
        var r2 = StateMachines.AsyncLambdas(body2, "h", null);
        Assert.Contains(r2.Unsupported, u => u.Contains("closure conversion"));
        Assert.Same(cap, ((MInvoke)((MExprStmt)body2[0]).E).Target);
    }

    [Fact]
    public void Kinds_from_the_signature()
    {
        Assert.Equal(StateMachineKind.None, StateMachines.KindOf(Method("f", I, false, false)));
        Assert.Equal(StateMachineKind.None, StateMachines.Run(Method("f", I, false, false), []).Kind);
        Assert.Equal(StateMachineKind.AsyncIterator, StateMachines.KindOf(Method("f", Clr(typeof(IAsyncEnumerator<int>)), true, true)));
    }

    // ================================================================ through the IL emitter

    const string PendingIl = "pending: AirToIl (backend plan D1.4) does not consume AirTypeDecl / AirLabel / AirGoto / AirGotoTable yet; un-skip when it does";

    [Fact(Skip = PendingIl), Trait("Category", "Pending")]
    public void Il_async_matches_csharp() { }

    [Fact(Skip = PendingIl), Trait("Category", "Pending")]
    public void Il_iterator_matches_csharp() { }

    [Fact(Skip = PendingIl), Trait("Category", "Pending")]
    public void Il_async_iterator_matches_csharp() { }
}

public static class SmSources
{
    public static async IAsyncEnumerable<int> Range(int n, List<object> log)
    {
        try
        {
            for (int i = 0; i < n; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }
        finally { log.Add("range-disposed"); }
    }
}

public sealed class SmResource(List<object> log) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        log.Add("disposed");
    }
}

/// <summary>
/// A tree-walking evaluator for the MIR the state-machine pass builds. Machines are <see cref="Obj"/> instances that
/// implement IAsyncStateMachine and IValueTaskSource by running their declared methods, so the real
/// AsyncTaskMethodBuilder / AsyncIteratorMethodBuilder / ManualResetValueTaskSourceCore drive them. Struct machines
/// run as references (the builder's boxing is the CLR's business). Jumps into nested statement lists are supported,
/// jumps into a try block are an error (the pass must never produce one).
/// </summary>
sealed class SmEval(AirTypeDecl machine)
{
    public sealed class Obj : IAsyncStateMachine, IValueTaskSource<bool>, IValueTaskSource
    {
        public readonly Dictionary<string, object?> F = new();
        public SmEval E = null!;

        void IAsyncStateMachine.MoveNext() => E.Invoke(this, "MoveNext", [], "IAsyncStateMachine");
        void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine) => E.Invoke(this, "SetStateMachine", [stateMachine], "IAsyncStateMachine");
        bool IValueTaskSource<bool>.GetResult(short token) => (bool)E.Invoke(this, "GetResult", [token], "IValueTaskSource<bool>")!;
        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => (ValueTaskSourceStatus)E.Invoke(this, "GetStatus", [token], "IValueTaskSource<bool>")!;
        void IValueTaskSource<bool>.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f) => E.Invoke(this, "OnCompleted", [c, s, t, f], "IValueTaskSource<bool>");
        void IValueTaskSource.GetResult(short token) => E.Invoke(this, "GetResult", [token], "IValueTaskSource");
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => (ValueTaskSourceStatus)E.Invoke(this, "GetStatus", [token], "IValueTaskSource")!;
        void IValueTaskSource.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f) => E.Invoke(this, "OnCompleted", [c, s, t, f], "IValueTaskSource");
    }

    sealed class TypeMark(Type t) { public readonly Type T = t; }
    sealed class GotoSig(string l) : Exception { public readonly string Label = l; }
    sealed class RetSig(object? v) : Exception { public readonly object? V = v; }
    sealed class BreakSig : Exception;
    sealed class ContinueSig : Exception;

    readonly object gate = new();

    // ---------------------------------------------------------------- driving

    public object? Kickoff(List<MStmt> body, object? self, params (string, object?)[] args)
    {
        var env = new Dictionary<string, object?> { ["this"] = self };
        foreach (var (n, v) in args) env[n] = v;
        try { Exec(body, env); }
        catch (RetSig r) { return r.V; }
        return null;
    }

    public object GetEnumerator(object seq) => Invoke((Obj)seq, "GetEnumerator", [], "IEnumerable<")!;
    public bool MoveNext(object e) => (bool)Invoke((Obj)e, "MoveNext", [], "IEnumerator")!;
    public object? Current(object e) => Invoke((Obj)e, "get_Current", [], "IEnumerator<");
    public void Dispose(object e) => Invoke((Obj)e, "Dispose", [], "IDisposable");

    public List<object?> Enumerate(object seq)
    {
        var e = GetEnumerator(seq);
        var outp = new List<object?>();
        try
        {
            while (MoveNext(e)) outp.Add(Current(e));
        }
        finally { Dispose(e); }
        return outp;
    }

    public async Task<List<object?>> EnumerateAsync(object seq, int max)
    {
        var e = (Obj)Invoke((Obj)seq, "GetAsyncEnumerator", [CancellationToken.None], "IAsyncEnumerable<")!;
        var outp = new List<object?>();
        try
        {
            while (outp.Count < max && await (ValueTask<bool>)Invoke(e, "MoveNextAsync", [], "IAsyncEnumerator<")!)
                outp.Add(Invoke(e, "get_Current", [], "IAsyncEnumerator<"));
        }
        finally { await (ValueTask)Invoke(e, "DisposeAsync", [], "IAsyncDisposable")!; }
        return outp;
    }

    public object? Invoke(Obj o, string name, object?[] args, string? iface)
    {
        var md = machine.Methods.Where(x => x.Params.Count == args.Length && (x.Name == name || x.Implements?.Method == name))
            .OrderBy(x => iface != null && x.Implements is { } im && Short(im.Interface.ToCs()).StartsWith(iface) ? 0 : x.Name == name ? 1 : 2).First();
        var env = new Dictionary<string, object?> { ["this"] = o };
        for (int i = 0; i < args.Length; i++) env[md.Params[i].Name] = args[i];
        lock (gate)
        {
            try { Exec(md.Body!, env); }
            catch (RetSig r) { return r.V; }
        }
        return null;
    }

    static string Short(string cs)
    {
        cs = cs.Replace("global::", "");
        int lt = cs.IndexOf('<');
        string head = lt >= 0 ? cs[..lt] : cs;
        return head[(head.LastIndexOf('.') + 1)..] + (lt >= 0 ? cs[lt..] : "");
    }

    Obj New()
    {
        var o = new Obj { E = this };
        foreach (var f in machine.Fields) o.F[f.Name] = Default(f.Type);
        return o;
    }

    // ---------------------------------------------------------------- statements

    static bool Has(List<MStmt> list, string label) => list.Any(s => Has(s, label));

    static bool Has(MStmt s, string label) => s switch
    {
        AirLabel l => l.Name == label,
        MIf i => Has(i.Then, label) || i.Else != null && Has(i.Else, label),
        MWhile w => Has(w.Body, label),
        MBlock b => Has(b.Body, label),
        MTry t => Has(t.Body, label) || t.Catches.Any(c => Has(c.Body, label)) || t.Finally != null && Has(t.Finally, label),
        _ => false,
    };

    void Exec(List<MStmt> list, Dictionary<string, object?> env, string? seek = null)
    {
        int i = 0;
        while (true)
        {
            try
            {
                if (seek != null)
                {
                    string s0 = seek;
                    seek = null;
                    i = list.FindIndex(x => Has(x, s0));
                    if (list[i] is not AirLabel) Seek(list[i], env, s0);
                    i++;
                }
                for (; i < list.Count; i++) Stmt(list[i], env);
                return;
            }
            catch (GotoSig g) when (Has(list, g.Label))
            {
                seek = g.Label;
            }
        }
    }

    void Seek(MStmt s, Dictionary<string, object?> env, string label)
    {
        switch (s)
        {
            case MIf i:
                Exec(Has(i.Then, label) ? i.Then : i.Else!, env, label);
                break;
            case MBlock b:
                Exec(b.Body, env, label);
                break;
            case MWhile w:
                Loop(w, env, label);
                break;
            default:
                throw new InvalidOperationException("jump into " + s.GetType().Name + " (" + label + ")");
        }
    }

    void Loop(MWhile w, Dictionary<string, object?> env, string? seek)
    {
        bool first = seek != null;
        while (first || (bool)Eval(w.Cond, env)!)
        {
            try { Exec(w.Body, env, first ? seek : null); }
            catch (BreakSig) { break; }
            catch (ContinueSig) { }
            first = false;
        }
    }

    void Stmt(MStmt s, Dictionary<string, object?> env)
    {
        switch (s)
        {
            case MDecl d: env[d.Name] = d.Init != null ? Eval(d.Init, env) : null; break;
            case MAssign a:
                {
                    var v = Eval(a.Value, env);
                    if (a.Op != "=") v = Arith(a.Op[..^1] switch { "+" => AirBinOp.Add, "-" => AirBinOp.Sub, "*" => AirBinOp.Mul, _ => throw new NotSupportedException(a.Op) }, Eval(a.Target, env), v, true);
                    Store(a.Target, v, env);
                    break;
                }
            case MExprStmt x: Eval(x.E, env); break;
            case MIf i:
                if ((bool)Eval(i.Cond, env)!) Exec(i.Then, env);
                else if (i.Else != null) Exec(i.Else, env);
                break;
            case MWhile w: Loop(w, env, null); break;
            case MBlock b: Exec(b.Body, env); break;
            case MReturn r: throw new RetSig(r.Value != null ? Eval(r.Value, env) : null);
            case MBreak: throw new BreakSig();
            case MContinue: throw new ContinueSig();
            case MThrow t: throw (Exception)Eval(t.Value, env)!;
            case AirLabel: break;
            case AirGoto g: throw new GotoSig(g.Label);
            case AirGotoTable t:
                {
                    int k = (int)Eval(t.Value, env)! - t.Min;
                    if (k >= 0 && k < t.Labels.Count && t.Labels[k] is { } l) throw new GotoSig(l);
                    break;
                }
            case MTry t:
                try
                {
                    Exec(t.Body, env);
                }
                catch (Exception ex) when (ex is not (GotoSig or RetSig or BreakSig or ContinueSig) && t.Catches.Any(c => Catches(c, ex)))
                {
                    var c = t.Catches.First(c => Catches(c, ex));
                    if (c.Name != null) env[c.Name] = ex;
                    Exec(c.Body, env);
                }
                finally
                {
                    if (t.Finally != null) Exec(t.Finally, env);
                }
                break;
            default: throw new NotSupportedException(s.GetType().Name);
        }
    }

    static bool Catches(MCatch c, Exception ex) => ClrType(c.TypeCs) is { } t && t.IsInstanceOfType(ex);

    static Type? ClrType(string cs) => Type.GetType(cs.Replace("global::", "")) ?? typeof(SmEval).Assembly.GetType(cs.Replace("global::", ""));

    void Store(MExpr target, object? v, Dictionary<string, object?> env)
    {
        switch (target)
        {
            case AirLocalRef l: env[l.Name] = v; break;
            case AirParamRef p: env[p.Name] = v; break;
            case MMember mm:
                {
                    var o = Eval(mm.Target, env);
                    if (o is Obj so) so.F[mm.Name] = v;
                    else throw new NotSupportedException("store to " + mm.Name);
                    break;
                }
            default: throw new NotSupportedException("store to " + target.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- expressions

    static Type? ClrOf(AirType? t) => t switch
    {
        null => null,
        AirType.Prim p => p.Kind switch
        {
            PrimKind.Int => typeof(int), PrimKind.Bool => typeof(bool), PrimKind.Short => typeof(short), PrimKind.String => typeof(string),
            PrimKind.Object => typeof(object), PrimKind.Void => typeof(void), _ => null,
        },
        AirType.ClrRef { Clr: { } c } when !c.ContainsGenericParameters => c,
        _ => null,
    };

    object? Default(AirType? t)
    {
        if (t is AirType.AliceType a && a.Sym == machine.Sym) return New();
        var c = ClrOf(t);
        if (t is { Nullable: true } || c == null || !c.IsValueType) return null;
        return Activator.CreateInstance(c);
    }

    object? Eval(MExpr e, Dictionary<string, object?> env)
    {
        switch (e)
        {
            case AirConst k:
                return k.Value ?? k.Kind switch
                {
                    AirConstKind.Int => int.Parse(k.Literal),
                    AirConstKind.Bool => k.Literal == "true",
                    _ => k.Literal,
                };
            case AirNull: return null;
            case AirDefault d: return Default(d.Type);
            case AirThis: return env["this"];
            case AirBase: return null;
            case AirLocalRef l: return env[l.Name];
            case AirParamRef p: return env[p.Name];
            case AirTypeRef tr: return new TypeMark(ClrOf(tr.Ref) ?? throw new NotSupportedException(tr.Ref.ToCs()));
            case MCast c: return Eval(c.E, env);
            case MUnary { Op: AirUnOp.Not } u: return !(bool)Eval(u.E, env)!;
            case MArg a: return Eval(a.E, env);
            case MBinary { Op: AirBinOp.And } b: return (bool)Eval(b.L, env)! && (bool)Eval(b.R, env)!;
            case MBinary { Op: AirBinOp.Or } b: return (bool)Eval(b.L, env)! || (bool)Eval(b.R, env)!;
            case MBinary b: return Arith(b.Op, Eval(b.L, env), Eval(b.R, env), b.Checked);
            case MMember mm:
                {
                    var o = Eval(mm.Target, env);
                    if (o is Obj so) return so.F[mm.Name];
                    if (o is TypeMark tm)
                        return tm.T.IsEnum ? Enum.Parse(tm.T, mm.Name) : (object?)tm.T.GetProperty(mm.Name)?.GetValue(null) ?? tm.T.GetField(mm.Name)!.GetValue(null);
                    var ot = o!.GetType();
                    var pi = ot.GetProperty(mm.Name) ?? ot.GetInterfaces().Select(i => i.GetProperty(mm.Name)).FirstOrDefault(x => x != null);
                    return pi != null ? pi.GetValue(o) : ot.GetField(mm.Name)!.GetValue(o);
                }
            case MNew n:
                {
                    if (n.Type is AirType.AliceType at && at.Sym == machine.Sym)
                    {
                        var o = New();
                        Invoke(o, ".ctor", n.Args.Select(a => Eval(a, env)).ToArray(), null);
                        return o;
                    }
                    var t = ClrOf(n.Type) ?? throw new NotSupportedException("new " + n.TypeCs);
                    var args = n.Args.Select(a => Eval(a, env)).ToArray();
                    var ctor = t.GetConstructors().First(c => c.GetParameters().Length == args.Length && c.GetParameters().Zip(args).All(p => p.Second == null || p.First.ParameterType.IsInstanceOfType(p.Second)));
                    return Unwrap(() => ctor.Invoke(args));
                }
            case MCall c: return CallE(c, env);
        }
        throw new NotSupportedException(e.GetType().Name);
    }

    static object Arith(AirBinOp op, object? l, object? r, bool chk)
    {
        if (op is AirBinOp.Eq) return Equals(l, r);
        if (op is AirBinOp.Ne) return !Equals(l, r);
        long a = Convert.ToInt64(l), b = Convert.ToInt64(r);
        return op switch
        {
            AirBinOp.Lt => a < b, AirBinOp.Le => a <= b, AirBinOp.Gt => a > b, AirBinOp.Ge => a >= b,
            AirBinOp.Add => chk ? checked((int)(a + b)) : unchecked((int)(a + b)),
            AirBinOp.Sub => chk ? checked((int)(a - b)) : unchecked((int)(a - b)),
            AirBinOp.Mul => chk ? checked((int)(a * b)) : unchecked((int)(a * b)),
            _ => throw new NotSupportedException(op.ToString()),
        };
    }

    static object? Unwrap(Func<object?> f)
    {
        try { return f(); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    object? CallE(MCall c, Dictionary<string, object?> env)
    {
        if (c.Target is AirBase) return null;   // object's constructor
        var recv = Eval(c.Target!, env);
        var args = c.Args.Select(a => Eval(a, env)).ToArray();
        if (recv is Obj so) return Invoke(so, c.Name, args, null);
        bool isStatic = recv is TypeMark;
        var type = recv is TypeMark tm ? tm.T : recv!.GetType();
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var all = type.GetMethods(flags).AsEnumerable();
        if (!isStatic) all = all.Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()));
        var mi = all.Where(x => x.Name == c.Name && x.GetParameters().Length == args.Length)
            .Select(x => x.IsGenericMethodDefinition ? Close(x, args) : x)
            .FirstOrDefault(x => x != null && x.GetParameters().Zip(args).All(p => p.Second == null || (p.First.ParameterType.IsByRef ? p.First.ParameterType.GetElementType()! : p.First.ParameterType).IsInstanceOfType(p.Second)))
            ?? throw new NotSupportedException($"call {c.Name} on {type.Name}");
        var result = Unwrap(() => mi.Invoke(isStatic ? null : recv, args));
        // by-reference arguments: write the callee's value back (a struct field or local)
        var ps = mi.GetParameters();
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].ParameterType.IsByRef && c.Args[i] is MArg { E: var target } && target is not AirThis && args[i] is not Obj)
                Store(target, args[i], env);
        return result;
    }

    static MethodInfo? Close(MethodInfo def, object?[] args)
    {
        var gps = def.GetGenericArguments();
        var map = new Type?[gps.Length];
        var ps = def.GetParameters();
        for (int i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            if (pt.IsByRef) pt = pt.GetElementType()!;
            if (pt.IsGenericParameter && args[i] != null) map[pt.GenericParameterPosition] = args[i]!.GetType();
        }
        if (map.Any(x => x == null)) return null;
        try { return def.MakeGenericMethod(map!); }
        catch (ArgumentException) { return null; }
    }
}
