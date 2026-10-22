using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Automatic parallelization of outermost counted loops (`for i in a...b`). Proven-independent iterations
/// run in chunks across cores. Allowed effects: locals declared in the body, one numeric `acc += / -= e`
/// reduction (per-chunk partials, summed), and `arr[i] = e` at the loop index. Anything else stays serial.
/// </summary>
public sealed partial class Lowerer
{
    sealed class LoopPlan
    {
        public LocalSym LoopVar = null!;
        public LocalSym? Acc;
        public bool Heavy;               // nested loops: parallel even for small trip counts
        public readonly HashSet<LocalSym> Inner = new();
        public HashSet<LocalSym> Fresh = new();   // locals only ever bound to arrays allocated in this body
    }

    MStmt? TryParallelFor(ForStmt f)
    {
        if (inParallelGen || ctx.Contains("lambda") || noHoist > 0 || Method is { TypeParams.Count: > 0 } || Method?.IsAsync == true) return null;
        if (f.Iter is not RangeExpr r || f.Locals.Count != 1 || !f.Locals[0].Type.IsInteger || f.Locals[0].Type.Cs != "int") return null;
        var plan = new LoopPlan { LoopVar = f.Locals[0] };
        plan.Inner.Add(plan.LoopVar);
        if (!LoopBody(f.Body, plan, 0)) return null;
        // inside a serial loop, a parallel dispatch per outer iteration only pays off for long inner loops
        bool nested = ctx.Contains("loop");
        if (nested) plan.Heavy = false;
        // constant trip count below the threshold: no parallel path at all
        if (r.A is LitExpr { Kind: "int" } la && r.B is LitExpr { Kind: "int" } lb && int.TryParse(la.Text.Replace("_", ""), out int ca) && int.TryParse(lb.Text.Replace("_", ""), out int cb)
            && (long)cb - ca + (r.Excl ? 0 : 1) < (plan.Heavy ? 2 : Threshold)) return null;
        if (plan.Acc != null && WalkReads(f.Body).Any(n => n.Sym == plan.Acc)) return null;
        if (plan.Acc == null && !WalkStmts(f.Body).Any(s => s is Assign { Target: IndexExpr })) return null;   // no observable effect to parallelize

        // captured read-only locals become helper parameters
        var captured = new List<LocalSym>();
        foreach (var n in WalkReads(f.Body).Concat(WalkExpr(r.A)).Concat(WalkExpr(r.B)).OfType<NameExpr>())
            if (n is { Res: "local", Sym: LocalSym l } && !plan.Inner.Contains(l) && l != plan.Acc && !captured.Contains(l)) captured.Add(l);
        foreach (var s in WalkStmts(f.Body))
            if (s is Assign { Target: IndexExpr { Target: NameExpr { Sym: LocalSym arr } } } && !captured.Contains(arr)) captured.Add(arr);
        if (captured.Any(l => !l.Type.IsKnown) || WalkReads(f.Body).Any(e => e is NameExpr { Res: "field" or "call" or "selfcall" })) return null;

        string start = Temp("a"), count = Temp("n");
        pre.Add(new MDecl { Name = start, TypeCs = "int", Init = Expr(r.A) });
        var end = Expr(r.B);
        pre.Add(new MDecl { Name = count, TypeCs = "int", Init = new MCode($"{Backend.Printer.E(end)} - {start}{(r.Excl ? "" : " + 1")}") });

        // serial path: the ordinary loop
        var serial = new List<MStmt> { SerialFor(f) };

        // parallel helper
        string lo = Temp("lo"), hi = Temp("hi"), i = plan.LoopVar.CsName;
        bool saved = inParallelGen;
        inParallelGen = true;
        var savedPre = pre;
        pre = new List<MStmt>();
        ctx.Add("loop");
        var body = Stmts(f.Body, Tail.None, null);
        ctx.RemoveAt(ctx.Count - 1);
        var bodyPre = pre;
        pre = savedPre;
        inParallelGen = saved;
        var chunk = new List<MStmt>();
        string? accT = plan.Acc != null ? TypeCs(plan.Acc.Type) : null;
        if (plan.Acc != null) chunk.Add(new MDecl { Name = plan.Acc.CsName, TypeCs = accT, Init = new MCode("default") });
        chunk.AddRange(bodyPre);
        chunk.Add(new MFor { VarType = "int", Var = i, Start = new MCode($"{start} + {lo}"), Cmp = "<", End = new MCode($"{start} + {hi}"), Body = body });
        string grain = plan.Heavy ? "1" : "8192";
        string helper = $"__parfor_{(Method?.CsName ?? "main").TrimStart('@')}_{++helperN}";
        var ps = captured.Select(l => $"{LocalTypeCs(l)} {l.CsName}").Append($"int {start}").Append($"int {count}");
        var args = captured.Select(l => l.CsName).Append(start).Append(count);
        var hp = new Backend.Printer("", 1);
        if (plan.Acc != null)
        {
            chunk.Add(new MReturn { Value = new MCode(plan.Acc.CsName) });
            hp.Root(new MReturn
            {
                Value = new MCall
                {
                    Target = new MCode("global::Alice.Runtime.Par"),
                    Name = $"Reduce<{accT}>",
                    Args = { new MCode(count), new MLambda { Params = { lo, hi }, Body = chunk }, new MCode("static (x, y) => x + y"), new MCode(grain) },
                },
            });
            Extra.Add($"private {HelperStatic}{accT} {helper}({string.Join(", ", ps)})\n{{\n{hp}}}\n");
        }
        else
        {
            hp.Root(new MExprStmt { E = new MCall { Target = new MCode("global::Alice.Runtime.Par"), Name = "Range", Args = { new MCode(count), new MLambda { Params = { lo, hi }, Body = chunk }, new MCode(grain) } } });
            Extra.Add($"private {HelperStatic}void {helper}({string.Join(", ", ps)})\n{{\n{hp}}}\n");
        }
        string call = $"{helper}({string.Join(", ", args)})";
        MStmt par = plan.Acc != null
            ? new MAssign { Target = new MCode(plan.Acc.CsName), Op = "+=", Value = new MCode(call) }
            : new MExprStmt { E = new MCode(call) };
        string min = plan.Heavy ? "2" : Threshold.ToString();
        return new MIf { Cond = new MCode($"{count} >= {min} && global::Alice.Runtime.Par.Workers > 1"), Then = { par }, Else = serial };
    }

    /// <summary>Checks every statement for loop-carried dependencies; fills the plan.</summary>
    bool LoopBody(List<Stmt> body, LoopPlan plan, int depth)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case ExprStmt es when PureRead(es.Expr, plan):
                    break;
                case LocalDecl d when d.Value == null || PureRead(d.Value, plan):
                    if (d.Local != null) plan.Inner.Add(d.Local);
                    break;
                case Assign a when PureRead(a.Value, plan):
                    if (a.Declares != null) { plan.Inner.Add(a.Declares); break; }
                    if (a.Target is NameExpr { Sym: LocalSym l })
                    {
                        if (plan.Inner.Contains(l)) break;
                        if (a.Op is "+=" or "-=" && l.Type.IsNumeric && (plan.Acc == null || plan.Acc == l)) { plan.Acc = l; break; }
                        return false;
                    }
                    if (a.Op == "=" && a.Target is IndexExpr { Target: NameExpr { Sym: LocalSym arr, Type.Kind: "array" }, Args: [{ Value: NameExpr { Sym: LocalSym idx } }] }
                        && idx == plan.LoopVar && !plan.Inner.Contains(arr)) break;
                    return false;
                case IfStmt i when i.Conds.All(c => PureRead(c, plan)):
                    if (!i.Bodies.All(b => LoopBody(b, plan, depth)) || (i.Else != null && !LoopBody(i.Else, plan, depth))) return false;
                    break;
                case WhileStmt w when PureRead(w.Cond, plan):
                    plan.Heavy = true;
                    if (!LoopBody(w.Body, plan, depth + 1)) return false;
                    break;
                case ForStmt fr when fr.Iter is RangeExpr rr && PureRead(rr.A, plan) && PureRead(rr.B, plan):
                    plan.Heavy = true;
                    plan.Inner.UnionWith(fr.Locals);
                    if (!LoopBody(fr.Body, plan, depth + 1)) return false;
                    break;
                case NextStmt { Value: null }:
                    break;
                case BreakStmt when depth > 0:
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>Side-effect-free expression (reads of locals/arrays/fields, arithmetic, Math.*).</summary>
    bool PureRead(Syntax.Expr e, LoopPlan plan) => e switch
    {
        LitExpr => true,
        NameExpr { Res: "local" or "field" } => true,
        IvarExpr or SelfExpr => true,
        TypeTestExpr tt => tt.E == null || PureRead(tt.E, plan),
        NewExpr { Type.Kind: "array", Block: null, AsCall: null } ne => ne.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),   // allocation is not an effect
        InterpExpr ie => ie.Holes.All(h => PureRead(h, plan)),
        ConstExpr { Res: "type" } => true,   // `Module.f(x)`: naming a type reads no state
        ArrayLit lit => lit.Items.All(x => PureRead(x, plan)),   // a fresh array: allocation is not an effect
        CallExpr { Res: "clr", Block: null, Sym: System.Reflection.MethodInfo mi } rc when PureRuntime(mi) => rc.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        BinaryExpr b => PureRead(b.L, plan) && PureRead(b.R, plan),
        UnaryExpr { Op: "-" or "!" or "~" } u => PureRead(u.E, plan),
        TernaryExpr t => PureRead(t.C, plan) && PureRead(t.A, plan) && PureRead(t.B, plan),
        IndexExpr ix => PureRead(ix.Target, plan) && ix.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        CallExpr { Res: "field" } f => f.Recv != null && PureRead(f.Recv, plan),
        CallExpr { Recv: ConstExpr { Ref.Name: "Math" or "MathF" } } m => m.Block == null && m.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        CallExpr { Res: "ext" } x => PureExt.Contains(Binder.Mangle(x.Name)) && x.Block == null && x.Recv != null && PureRead(x.Recv, plan) && x.Args.All(a => PureRead(a.Value!, plan)),
        CallExpr { Res: "alice", Sym: MethodSym m, Block: null } c => MarkHeavy(plan) && MethodPure(m) && (c.Recv == null || PureRead(c.Recv, plan)) && c.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        NameExpr { Res: "call", Sym: MethodSym m } => MarkHeavy(plan) && MethodPure(m),
        NewExpr { AsCall: null, Block: null } n => n.Type.Sym is { Kind: "class" or "struct" or "record" or "record struct" } ts && CtorPure(ts, n.Args.Count) && n.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        NewExpr { AsCall: { } ac } => PureRead(ac, plan),
        _ => false,
    };

    static bool MarkHeavy(LoopPlan plan)
    {
        plan.Heavy = true;   // user calls have unknown cost: parallelize even short loops
        return true;
    }

    readonly Dictionary<MethodSym, bool> purity = new();

    /// <summary>Loop helpers of instance methods are instance methods too (bodies may read @ivars / self).</summary>
    string HelperStatic => Method is { IsStatic: false } && Type.Kind != "extension" ? "" : "static ";

    /// <summary>
    /// Interprocedural purity: writes only its own locals (and, for constructors, the fresh object's fields),
    /// calls only pure methods, raises nothing. Recursion is assumed pure while being analysed.
    /// </summary>
    /// <summary>A static Alice.Runtime method marked [Pure]: the runtime vouches for it; arbitrary .NET code is never trusted.</summary>
    /// Compared by name: referenced assemblies may load in a separate metadata context.
    static bool PureRuntime(System.Reflection.MethodInfo mi) =>
        mi.IsStatic && mi.DeclaringType?.Assembly.GetName().Name == "Alice.Runtime"
        && mi.CustomAttributes.Any(a => a.AttributeType.FullName == "Alice.Runtime.PureAttribute");

    bool MethodPure(MethodSym m)
    {
        if (purity.TryGetValue(m, out var known)) return known;
        if (m.Decl == null || !m.Decl.HasBody || m.UsesYield || m.IsAsync || m.Mutates && !m.IsCtor) return purity[m] = false;
        purity[m] = true;
        var plan = new LoopPlan { LoopVar = new LocalSym(), Fresh = FreshArrays(m.Decl.Body) };
        bool ok = PureMethodBody(m.Decl.Body, plan, m.IsCtor);
        purity[m] = ok;
        return ok;
    }

    bool CtorPure(TypeSym t, int argc)
    {
        if (t.Kind is "record" or "record struct") return true;   // positional records only store their arguments
        var ctor = t.Methods.TryGetValue("initialize", out var list) ? list.FirstOrDefault(c => c.Accepts(argc)) : null;
        return ctor == null || MethodPure(ctor);
    }

    bool PureMethodBody(List<Stmt> body, LoopPlan plan, bool ctor)
    {
        foreach (var s in body)
        {
            bool ok = s switch
            {
                ExprStmt es => PureRead(es.Expr, plan),
                LocalDecl d => d.Value == null || PureRead(d.Value, plan),
                Assign a => PureRead(a.Value, plan) && (a.Declares != null || a.Target is NameExpr { Res: "local" } || (ctor && a.Target is IvarExpr)
                    || (a.Target is IndexExpr { Target: NameExpr { Sym: LocalSym fl } } ix && plan.Fresh.Contains(fl) && ix.Args.All(x => x.Mod == "" && PureRead(x.Value!, plan)))),
                ReturnStmt r => r.Value == null || PureRead(r.Value, plan),
                MultiAssign ma => ma.Values.All(v => PureRead(v, plan)),   // targets are locals
                IfStmt i => i.Conds.All(c => PureRead(c, plan)) && i.Bodies.All(b => PureMethodBody(b, plan, ctor)) && (i.Else == null || PureMethodBody(i.Else, plan, ctor)),
                WhileStmt w => PureRead(w.Cond, plan) && PureMethodBody(w.Body, plan, ctor),
                ForStmt f => PureRead(f.Iter is RangeExpr rr ? rr.A : f.Iter, plan) && (f.Iter is not RangeExpr r2 || PureRead(r2.B, plan)) && PureMethodBody(f.Body, plan, ctor),
                BreakStmt or NextStmt { Value: null } => true,
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Locals whose every assignment in the body is a new array: writes through them cannot reach any caller's
    /// data (no alias of a parameter, field or other local), so they do not make the method impure.
    /// </summary>
    static HashSet<LocalSym> FreshArrays(List<Stmt> body)
    {
        var fresh = new HashSet<LocalSym>();
        var tainted = new HashSet<LocalSym>();
        foreach (var s in WalkStmts(body))
        {
            if (s is not Assign a) continue;
            var l = a.Declares ?? (a.Target as NameExpr)?.Sym as LocalSym;
            if (l == null) continue;
            if (a.Op == "=" && a.Value is NewExpr { Type.Kind: "array" } or ArrayLit) fresh.Add(l);
            else tainted.Add(l);
        }
        fresh.ExceptWith(tainted);
        return fresh;
    }

    /// <summary>Every expression read in the body (assignment targets excluded, their indices included).</summary>
    static IEnumerable<Syntax.Expr> WalkReads(List<Stmt> body)
    {
        foreach (var s in WalkStmts(body))
        {
            IEnumerable<Syntax.Expr?> roots = s switch
            {
                ExprStmt es => [es.Expr],
                Assign a => a.Target is IndexExpr ix ? ix.Args.Select(x => x.Value).Append(a.Value) : [a.Value],
                LocalDecl d => [d.Value],
                IfStmt i => i.Conds,
                WhileStmt w => [w.Cond],
                ForStmt fr => [fr.Iter],
                _ => [],
            };
            foreach (var r in roots)
                if (r != null)
                    foreach (var x in WalkExpr(r)) yield return x;
        }
    }
}
