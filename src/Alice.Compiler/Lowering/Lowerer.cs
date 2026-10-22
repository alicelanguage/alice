using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>How the value of the last statement of a body is consumed.</summary>
public enum Tail { None, Return, Assign }

/// <summary>
/// HIR -> MIR. Removes implicit allocations: literal blocks on Prelude iterators become loops (fused across
/// map/select chains), if/case expressions become assignments to typed temps, ranges become counters.
/// </summary>
public sealed partial class Lowerer
{
    readonly Binder binder;
    readonly HashSet<string> zeroArg;
    readonly HashSet<string> props;
    List<MStmt> pre = new();
    readonly List<string> ctx = new();      // loop | lambda | inline
    HashSet<LocalSym> hoisted = new();
    int tmpN;
    public MethodSym? Method;

    /// <summary>Helper methods generated while lowering the current method (e.g. parallel kernels).</summary>
    public readonly List<string> Extra = new();
    int helperN;
    public TypeSym Type = null!;

    public Lowerer(Binder binder)
    {
        this.binder = binder;
        zeroArg = new HashSet<string>(Builtins.FreeFunctions);
        props = new HashSet<string>();
        foreach (var t in binder.Types.Values.Distinct())
        {
            foreach (var list in t.Methods.Values)
                foreach (var m in list)
                    if (m.Required == 0 && !m.IsCtor) zeroArg.Add(m.CsName);
            foreach (var f in t.Fields.Values) props.Add(f.CsName);
        }
        foreach (var m in typeof(Alice.Runtime.Prelude).GetMethods())
        {
            var ps = m.GetParameters();
            if (ps.Length >= 1 && ps.Skip(1).All(p => p.IsOptional)) zeroArg.Add(m.Name);
        }
        zeroArg.UnionWith(Builtins.PreludeZeroArg.Keys);
    }

    public static string TypeCs(AType t) => t.Kind switch
    {
        "unknown" => "dynamic",
        "null" => "object?",
        _ => t.Cs,
    };

    string Temp(string hint = "t") => $"__{hint}{++tmpN}";

    /// <summary>Lowers a method/Main/lambda body. Hoisted Ruby locals are declared up front.</summary>
    /// <summary>Locals that always equal `arr.Length` (n = arr.size, neither reassigned): loop bounds use the length directly.</summary>
    Dictionary<LocalSym, string> lengthAlias = new();

    void FindLengthAliases(List<Stmt> body)
    {
        lengthAlias = new();
        var written = new Dictionary<LocalSym, int>();
        void Count(LocalSym l) => written[l] = written.GetValueOrDefault(l) + 1;
        var all = WalkStmts(body).ToList();
        foreach (var e in Walk(body))
            if (e is LambdaExpr lam) all.AddRange(WalkStmts(lam.Body));
            else if (e is CallExpr { Block: { } blk }) all.AddRange(WalkStmts(blk.Body));
        foreach (var s in all)
        {
            if (s is Assign { Declares: { } d }) Count(d);
            else if (s is Assign { Target: NameExpr { Sym: LocalSym l } }) Count(l);
            else if (s is MultiAssign ma) foreach (var l2 in ma.Locals) Count(l2);
            else if (s is ForStmt f) foreach (var l3 in f.Locals) Count(l3);
        }
        foreach (var s in all)
            if (s is Assign { Declares: { } d, Value: CallExpr { Res: "ext", Args.Count: 0, Block: null, Recv: NameExpr { Res: "local", Sym: LocalSym arr, Type.Kind: "array" } } c }
                && Binder.Mangle(c.Name) is "size" or "length" && written.GetValueOrDefault(d) == 1 && !written.ContainsKey(arr) && !arr.IsVal == !arr.IsVal)
                lengthAlias[d] = arr.CsName + ".Length";
    }

    public List<MStmt> LowerBody(List<Stmt> body, Tail tail, List<LocalSym> hoistedLocals, int line)
    {
        tmpN = 0;
        CheckDeclaredEffects(line);
        FindLengthAliases(body);
        FindStackArrays(body);
        FindFlatRows(body);
        FindRanges(body);
        FindOwned(body, tail);
        CheckOwnArguments(body);
        hoisted = new HashSet<LocalSym>(hoistedLocals);
        var outp = new List<MStmt>(StackArrayDecls(line));
        foreach (var h in hoistedLocals)
            if (!stackArrays.ContainsKey(h)) outp.Add(new MDecl { Line = line, Name = h.CsName, TypeCs = TypeCs(h.Type), Init = new MCode("default!") });
        outp.AddRange(Stmts(body, tail, null));
        if (tail == Tail.None) outp.AddRange(ReleaseOwnedAtEnd());
        return outp;
    }

    List<MStmt> Stmts(List<Stmt> body, Tail tail, string? target)
    {
        if (!inParallelGen && soa == null) body = FuseSoa(body);
        var outp = new List<MStmt>();
        for (int i = 0; i < body.Count; i++)
        {
            // a fork consumes one statement (`a, b = f(x), g(y)`) or two (`a = f(x)` / `b = g(y)`), never the tail value
            if (TryFork(outp, body, i, body.Count - (tail == Tail.None ? 0 : 1)) is int used and > 0) { i += used - 1; continue; }
            if (body[i] is MultiAssign { Par: true } pm)
                throw new CompileException($"{File}:{pm.Line}:{Math.Max(1, pm.Col)}: cannot fork `par`: both values must be calls to pure methods with pure-read arguments, the second must not read the first result, and the method must not be async, generic or a lambda");
            var forked = TryForkOperands(outp, body[i]);
            Into(outp, body[i], i == body.Count - 1 ? tail : Tail.None, target);
            foreach (var c in forked) forkedCalls.Remove(c);
            if (Presize(body, i) is MStmt reserve) outp.Add(reserve);
        }
        return outp;
    }

    void Into(List<MStmt> outp, Stmt s, Tail tail, string? target)
    {
        var saved = pre;
        pre = new List<MStmt>();
        var produced = new List<MStmt>();
        var lent = tail == Tail.None ? LendOwnedResults(s) : [];
        Stmt(produced, s, tail, target);
        foreach (var (call, temp, el) in lent)
        {
            forkedCalls.Remove(call);
            produced.Add(Release(temp, el));
        }
        foreach (var m in pre) if (m.Line == 0) m.Line = s.Line;
        foreach (var m in produced) if (m.Line == 0) m.Line = s.Line;
        outp.AddRange(pre);
        outp.AddRange(produced);
        pre = saved;
    }

    /// <summary>Lower an expression whose side statements must stay inside a nested statement list.</summary>
    MExpr Isolated(Expr e, List<MStmt> into)
    {
        var saved = pre;
        pre = new List<MStmt>();
        var r = Expr(e);
        into.AddRange(pre);
        pre = saved;
        return r;
    }

    bool InLambda => ctx.Count > 0 && ctx[^1] == "lambda";
}
