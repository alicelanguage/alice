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
    }

    public static string TypeCs(AType t) => t.Kind switch
    {
        "unknown" => "dynamic",
        "null" => "object?",
        _ => t.Cs,
    };

    string Temp(string hint = "t") => $"__{hint}{++tmpN}";

    /// <summary>Lowers a method/Main/lambda body. Hoisted Ruby locals are declared up front.</summary>
    public List<MStmt> LowerBody(List<Stmt> body, Tail tail, List<LocalSym> hoistedLocals, int line)
    {
        tmpN = 0;
        hoisted = new HashSet<LocalSym>(hoistedLocals);
        var outp = new List<MStmt>();
        foreach (var h in hoistedLocals)
            outp.Add(new MDecl { Line = line, Name = h.CsName, TypeCs = TypeCs(h.Type), Init = new MCode("default!") });
        outp.AddRange(Stmts(body, tail, null));
        return outp;
    }

    List<MStmt> Stmts(List<Stmt> body, Tail tail, string? target)
    {
        var outp = new List<MStmt>();
        for (int i = 0; i < body.Count; i++) Into(outp, body[i], i == body.Count - 1 ? tail : Tail.None, target);
        return outp;
    }

    void Into(List<MStmt> outp, Stmt s, Tail tail, string? target)
    {
        var saved = pre;
        pre = new List<MStmt>();
        var produced = new List<MStmt>();
        Stmt(produced, s, tail, target);
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
