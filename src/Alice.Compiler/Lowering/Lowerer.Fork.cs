using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Fork/join of divide-and-conquer recursion, BendRT's parallel let inferred instead of written:
/// <code>
/// a = f(x)        // two consecutive declarations, both calls to pure user methods,
/// b = f(y)        // the second independent of the first, at least one recursive
/// </code>
/// becomes `if (Par.Forkable) (a, b) = Par.Fork(() => f(x), () => f(y)); else { a = f(x); b = f(y); }`.
/// Lazy task creation: only the top ~log2(cores)+3 levels spawn tasks, deeper levels pay one thread-static read.
/// Purity is the same interprocedural gate as loop parallelization: impure code is never forked.
/// </summary>
public sealed partial class Lowerer
{
    /// <summary>Lowers a fork at body[i] and returns how many statements it consumed (0: not a fork).</summary>
    int TryFork(List<MStmt> outp, List<Stmt> body, int i, int end)
    {
        if (inParallelGen || noHoist > 0 || Method == null || Method.IsAsync || Method.TypeParams.Count > 0 || ctx.Contains("lambda")) return 0;
        LocalSym l1, l2;
        CallExpr c1, c2;
        int consumed;
        bool declares = true;
        if (i >= end) return 0;
        if (body[i] is MultiAssign { Locals.Count: 2, Values: [CallExpr m1, CallExpr m2] } ma)   // `a, b = f(x), g(y)`: Bend's parallel call
        {
            (l1, l2, c1, c2, consumed) = (ma.Locals[0], ma.Locals[1], m1, m2, 1);
            declares = ma.Declares;
        }
        else if (i + 1 < end && body[i] is Assign { Declares: LocalSym d1, Value: CallExpr v1 } && body[i + 1] is Assign { Declares: LocalSym d2, Value: CallExpr v2 })
        {
            (l1, l2, c1, c2, consumed) = (d1, d2, v1, v2, 2);
        }
        else return 0;
        bool asked = body[i] is MultiAssign { Par: true };   // `par a, b = ...`: the programmer says the work divides
        if (!ForkableCall(c1) || !ForkableCall(c2) || !(asked || c1.Sym == Method || c2.Sym == Method)) return 0;   // recursion: the work divides
        if (!l1.Type.IsKnown || !l2.Type.IsKnown) return 0;
        if (WalkExpr(c2).Any(n => n is NameExpr { Sym: LocalSym s } && s == l1)) return 0;   // b = f(a): dependent

        var left = new List<MStmt>();
        var right = new List<MStmt>();
        var lv = Isolated(c1, left);
        var rv = Isolated(c2, right);
        if (left.Count > 0 || right.Count > 0) return 0;   // argument lowering needed statements: keep it serial

        // hoisted locals (assigned in a branch) are already declared at the top of the method
        if (declares && !hoisted.Contains(l1)) outp.Add(new MDecl { Name = l1.CsName, TypeCs = TypeCs(l1.Type) });
        if (declares && !hoisted.Contains(l2)) outp.Add(new MDecl { Name = l2.CsName, TypeCs = TypeCs(l2.Type) });
        if (!EmitFork(outp, l1.CsName, l2.CsName, TypeCs(l1.Type), TypeCs(l2.Type), c1, c2, lv, rv)) return 0;
        return consumed;
    }

    /// <summary>
    /// `if (Par.Forkable) (n1, n2) = __fork_m_k(captured...); else { n1 = c1; n2 = c2; }`.
    /// The lambdas live in a helper: capturing this method's locals would allocate a closure on every call,
    /// forking or not; in the helper only the calls near the root that actually fork pay for it.
    /// </summary>
    bool EmitFork(List<MStmt> outp, string n1, string n2, string t1, string t2, CallExpr c1, CallExpr c2, MExpr lv, MExpr rv)
    {
        var captured = new List<LocalSym>();
        foreach (var n in WalkExpr(c1).Concat(WalkExpr(c2)).OfType<NameExpr>())
            if (n is { Res: "local", Sym: LocalSym l } && !captured.Contains(l)) captured.Add(l);
        // nullable locals (`l`, `r` of a tree node) stay serial: passing them with `!` would compile, but node-by-node tree
        // walks give each task well under a microsecond of work. Measured on benchmarks/gc trees (8191-node trees):
        // forking `1 + l.check + r.check` took 21.5 -> 32.6 ms. A program that knows its subtrees are big can write `par`
        if (captured.Any(l => !l.Type.IsKnown || l.Type.Nullable)) return false;
        string helper = $"__fork_{(Method!.CsName ?? "main").TrimStart('@')}_{++helperN}";
        var hp = new Backend.Printer("", 1);
        hp.Root(new MReturn
        {
            Value = new MCall { Target = new MCode("global::Alice.Runtime.Par"), Name = "Fork", Args = { new MLambda { ExprBody = lv }, new MLambda { ExprBody = rv } } },
        });
        Extra.Add($"private {HelperStatic}({t1}, {t2}) {helper}({string.Join(", ", captured.Select(l => $"{LocalTypeCs(l)} {l.CsName}"))})\n{{\n{hp}}}\n");
        var fork = new MCode($"{helper}({string.Join(", ", captured.Select(l => l.CsName))})");
        outp.Add(new MIf
        {
            Cond = new MCode("global::Alice.Runtime.Par.Forkable"),
            Then = { new MDeconstruct { Names = { n1, n2 }, Value = fork } },
            Else = new List<MStmt>
            {
                new MAssign { Target = new MCode(n1), Value = Isolated(c1, []) },
                new MAssign { Target = new MCode(n2), Value = Isolated(c2, []) },
            },
        });
        return true;
    }

    /// <summary>Calls already forked into temporaries: lowering the statement reads the temporary instead.</summary>
    readonly Dictionary<CallExpr, string> forkedCalls = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// HVM reduces every independent redex of a term at once; this is the CPU-sized version: a statement whose value
    /// is a pure arithmetic term over reads and exactly two forkable calls (one recursive), `fib(n - 1) + fib(n - 2)`,
    /// forks the two calls into temporaries before the statement. Calls under `&&`, `||`, `??` or `?:` are not
    /// collected: they may never run. Returns the forked calls for the caller to release after lowering the statement.
    /// </summary>
    CallExpr[] TryForkOperands(List<MStmt> outp, Stmt s)
    {
        if (inParallelGen || noHoist > 0 || Method == null || Method.IsAsync || Method.TypeParams.Count > 0 || ctx.Contains("lambda")) return [];
        var value = s switch
        {
            ReturnStmt { Value: { } v } => v,
            ExprStmt { Expr: BinaryExpr or UnaryExpr } es => es.Expr,
            Assign { Target: NameExpr { Res: "local" }, Value: var v } => v,
            _ => null,
        };
        if (value is not (BinaryExpr or UnaryExpr)) return [];
        var calls = new List<CallExpr>();
        Operands(value, calls);
        if (calls.Count != 2 || !calls.Any(c => c.Sym == Method) || !calls.All(ForkableCall)) return [];
        if (!PureRead(value, new LoopPlan { LoopVar = new LocalSym() })) return [];   // reordering the calls first must not move an effect
        if (!calls.All(c => c.Type.IsKnown)) return [];
        var left = new List<MStmt>();
        var right = new List<MStmt>();
        var lv = Isolated(calls[0], left);
        var rv = Isolated(calls[1], right);
        if (left.Count > 0 || right.Count > 0) return [];
        string n1 = $"__fl{helperN + 1}", n2 = $"__fr{helperN + 1}";
        var decls = new List<MStmt>
        {
            new MDecl { Name = n1, TypeCs = TypeCs(calls[0].Type) },
            new MDecl { Name = n2, TypeCs = TypeCs(calls[1].Type) },
        };
        if (!EmitFork(decls, n1, n2, TypeCs(calls[0].Type), TypeCs(calls[1].Type), calls[0], calls[1], lv, rv)) return [];
        foreach (var m in decls) m.Line = s.Line;
        outp.AddRange(decls);
        forkedCalls[calls[0]] = n1;
        forkedCalls[calls[1]] = n2;
        return [.. calls];
    }

    void Operands(Expr e, List<CallExpr> calls)
    {
        switch (e)
        {
            case BinaryExpr { Op: not ("&&" or "||" or "and" or "or" or "??") } b:
                Operands(b.L, calls);
                Operands(b.R, calls);
                break;
            case UnaryExpr u:
                Operands(u.E, calls);
                break;
            case CallExpr { Res: "alice" or "call", Sym: MethodSym } c:
                if (calls.Count < 3) calls.Add(c);
                break;
        }
    }

    /// <summary>A call to a user method proven pure, with pure-read arguments and no block.</summary>
    bool ForkableCall(CallExpr c)
    {
        if (c.Sym is not MethodSym m || c.Res is not ("alice" or "call") || c.Block != null || !MethodPure(m)) return false;
        var plan = new LoopPlan { LoopVar = new LocalSym() };
        return (c.Recv == null || PureRead(c.Recv, plan)) && c.Args.All(a => a.Mod == "" && a.Name == null && a.Value != null && PureRead(a.Value, plan));
    }
}
