using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Presizing: `xs = List(T).new` (or `SoA(T).new`) directly followed by `for i in a...b` whose body adds to `xs`
/// exactly once, unconditionally, with no `break`/`next`/`return`/`raise` anywhere in the body, gets
/// `xs.EnsureCapacity(count)` (`xs.reserve(count)`) before the loop. The collection is sized once: no growth copies,
/// and for large collections no large-object-heap garbage that only gen2 collections reclaim.
/// </summary>
public sealed partial class Lowerer
{
    /// <summary>The capacity call for body[i] when body[i + 1] is a loop that fills it, or null.</summary>
    MStmt? Presize(List<Stmt> body, int i)
    {
        if (i + 1 >= body.Count || inParallelGen) return null;
        if (body[i] is not Assign { Op: "=", Value: NewExpr { Args.Count: 0, Block: null, AsCall: null } ne } a || Assigned(a) is not { } l) return null;
        string? method = ne.Type.Sym?.SoaOf != null ? "reserve"
            : ne.Type.Clr is { IsGenericType: true } g && g.GetGenericTypeDefinition() == typeof(List<>) ? "EnsureCapacity" : null;
        if (method == null) return null;
        if (body[i + 1] is not ForStmt { Iter: RangeExpr r, Par: false, Await: false } f) return null;
        var plan = new LoopPlan { LoopVar = new LocalSym() };
        if (!PureRead(r.A, plan) || !PureRead(r.B, plan)) return null;
        if (WalkStmts(f.Body).Any(s => s is BreakStmt or NextStmt or ReturnStmt or RaiseStmt or RetryStmt)) return null;
        if (WalkStmts(f.Body).Any(s => s is Assign x && Assigned(x) == l)) return null;
        // exactly one add, at the top level of the body
        int adds = f.Body.Count(s => IsAdd(s, l));
        int mentions = Walk(f.Body).Count(e => e is NameExpr { Sym: LocalSym x } && x == l);
        if (adds != 1 || mentions != 1) return null;
        var to = Optimized(Expr(r.B));
        var count = new AirPresizeCount { To = to, From = Optimized(Expr(r.A)), Inclusive = !r.Excl, Type = AirLeaf.Int };
        return new MExprStmt { E = new MCall { Target = AirLeaf.Name(l.CsName, l.Type.ToAir()), Name = method, Args = { count }, Type = AirLeaf.Void } };
    }

    static bool IsAdd(Stmt s, LocalSym l) => s switch
    {
        ExprStmt { Expr: CallExpr { Recv: NameExpr { Sym: LocalSym x }, Args.Count: 1, Block: null } c } => x == l && c.Name is "add" or "Add" or "push",
        ExprStmt { Expr: BinaryExpr { Op: "<<", L: NameExpr { Sym: LocalSym x } } } => x == l,
        _ => false,
    };

    /// <summary>A bound run through the MIR passes on its own (the presize count is frozen in the method body).</summary>
    static MExpr Optimized(MExpr e)
    {
        var s = new MExprStmt { E = e };
        Optimizer.Pipeline.Run([s]);
        return s.E;
    }
}
