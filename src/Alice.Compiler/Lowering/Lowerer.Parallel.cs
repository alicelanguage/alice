using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Automatic parallelization. A pipeline whose blocks are provably pure (arithmetic, comparisons, Math.*,
/// reads, and writes only to the current SoA row) is emitted twice from the same kernel generator:
/// serial for small inputs, and chunked across cores (Par.Reduce / Par.Range) for n >= Threshold.
/// Each chunk runs the same vectorized kernel over its [lo, hi) range.
/// </summary>
public sealed partial class Lowerer
{
    const int Threshold = 65_536;
    string? boundLo, boundHi;
    bool inParallelGen;

    static readonly HashSet<string> PureExt = new() { "abs", "sqrt", "floor", "ceil", "round", "to_i", "to_f", "to_f32", "to_i64", "even_q", "odd_q", "zero_q", "succ", "pred" };

    MExpr? ParallelOrSerial(CallExpr c, string name)
    {
        if (inParallelGen || noHoist > 0 || ParallelPlan(c, name) is not { } plan) return Pipeline(c, name);
        var (n, combine, resultType) = plan;

        var savedPre = pre;
        var serial = new List<MStmt>();
        pre = serial;
        var rSerial = Pipeline(c, name);
        if (rSerial == null) { pre = savedPre; return null; }

        string lo = Temp("lo"), hi = Temp("hi");
        var kernel = new List<MStmt>();
        pre = kernel;
        inParallelGen = true;
        (boundLo, boundHi) = (lo, hi);
        var rPar = Pipeline(c, name);
        (boundLo, boundHi) = (null, null);
        inParallelGen = false;
        pre = savedPre;
        if (rPar == null) { pre.AddRange(serial); return rSerial; }

        // The parallel path lives in its own static helper: its closure is allocated only when it runs,
        // so small inputs keep the allocation-free serial loop.
        var captured = Captured(c);
        if (captured == null) { pre.AddRange(serial); return rSerial; }
        string helper = $"__par_{(Method?.CsName ?? "main").TrimStart('@')}_{++helperN}";
        string ps = string.Join(", ", captured.Select(l => $"{TypeCs(l.Type)} {l.CsName}"));
        string args = string.Join(", ", captured.Select(l => l.CsName));
        var big = new MCode($"{n} >= {Threshold} && global::Alice.Runtime.Par.Workers > 1");
        var hp = new Backend.Printer("", 1);
        if (resultType == null)
        {
            var range = new MCall { Target = new MCode("global::Alice.Runtime.Par"), Name = "Range", Args = { new MCode(n), new MLambda { Params = { lo, hi }, Body = kernel } } };
            hp.Stmt(new MExprStmt { E = range });
            Extra.Add($"private static void {helper}({ps})\n{{\n{hp}}}\n");
            pre.Add(new MIf { Cond = big, Then = { new MExprStmt { E = new MCode($"{helper}({args})") } }, Else = serial });
            return MCode.Empty;
        }
        string res = Temp("pr");
        string T = TypeCs(resultType);
        kernel.Add(new MReturn { Value = rPar });
        var reduce = new MCall
        {
            Target = new MCode("global::Alice.Runtime.Par"),
            Name = $"Reduce<{T}>",
            Args = { new MCode(n), new MLambda { Params = { lo, hi }, Body = kernel }, new MCode(combine!) },
        };
        hp.Stmt(new MReturn { Value = reduce });
        Extra.Add($"private static {T} {helper}({ps})\n{{\n{hp}}}\n");
        pre.Add(new MDecl { Name = res, TypeCs = T, Init = new MCode("default") });
        serial.Add(new MAssign { Target = new MCode(res), Value = rSerial });
        pre.Add(new MIf { Cond = big, Then = { new MAssign { Target = new MCode(res), Value = new MCode($"{helper}({args})") } }, Else = serial });
        return new MCode(res);
    }

    /// <summary>Locals the kernel reads from the enclosing method (passed by value to the helper); null if unsupported.</summary>
    List<LocalSym>? Captured(CallExpr sink)
    {
        if (Method != null && Method.TypeParams.Count > 0) return null;
        var inside = new HashSet<LocalSym>();
        var used = new List<LocalSym>();
        foreach (var e in WalkExpr(sink))
        {
            if (e is LambdaExpr or CallExpr { Block: not null })
            {
                var lam = e as LambdaExpr ?? ((CallExpr)e).Block!;
                inside.UnionWith(lam.ParamSyms);
                foreach (var s in WalkStmts(lam.Body))
                {
                    if (s is Assign { Declares: { } d }) inside.Add(d);
                    if (s is LocalDecl { Local: { } ld }) inside.Add(ld);
                }
            }
            if (e is NameExpr { Res: "local", Sym: LocalSym l } && !used.Contains(l)) used.Add(l);
            if (e is NameExpr { Res: "field" or "call" or "selfcall" } || e is IvarExpr || e is SelfExpr) return null;
        }
        var outside = used.Where(l => !inside.Contains(l)).ToList();
        return outside.All(l => l.Type.IsKnown) ? outside : null;
    }

    /// <summary>(element count expr, combine lambda, result type) when the call can run in parallel; null otherwise.</summary>
    (string N, string? Combine, AType? Result)? ParallelPlan(CallExpr sink, string name)
    {
        var stages = new List<Stage>();
        Syntax.Expr src = sink.Recv!;
        while (src is CallExpr st && st.Res == "ext" && st.Block != null && StageNames.Contains(st.Name) && st.Recv != null)
        {
            if (st.Block.ParamSyms.Count != 1 || !PureBlock(st.Block, null) || !HasValueTail(st.Block)) return null;
            stages.Insert(0, new Stage(st.Name, st.Block));
            src = st.Recv;
        }
        if (src is not (NameExpr { Res: "local" } or IvarExpr)) return null;
        bool soaSrc = src.Type.Sym?.SoaOf != null;
        if (src.Type.Kind != "array" && !soaSrc) return null;
        string s = Backend.Printer.E(Expr(src));
        string n = soaSrc ? s + ".Count" : s + ".Length";
        var sb = sink.Block;
        if (sb != null && (sb.ParamSyms.Count != 1 || !PureBlock(sb, soaSrc && name == "each" ? sb.ParamSyms[0] : null))) return null;
        if (soaSrc && stages.Count > 0) return null;
        AType elem = soaSrc ? src.Type.Sym!.SoaOf!.Type : Builtins.ElemOf(src.Type);
        foreach (var st in stages) if (st.Kind == "map") elem = st.Block.Result;
        switch (name)
        {
            case "each" when soaSrc && sb != null:
                return (n, null, null);
            case "sum":
                {
                    var t = sb != null ? sb.Result : elem;
                    return t.IsNumeric ? (n, "static (a, b) => a + b", t) : null;
                }
            case "count" when sb != null || stages.Any(x => x.Kind != "map"):
                return (n, "static (a, b) => a + b", AType.Int);
            case "max" or "min" when sb == null && elem.IsNumeric && !soaSrc:
                return (n, $"static (a, b) => global::System.Math.{(name == "max" ? "Max" : "Min")}(a, b)", elem);
        }
        return null;
    }

    /// <summary>Pure = no effects visible outside the element: only block-local writes (or the current SoA row).</summary>
    static bool PureBlock(LambdaExpr lam, LocalSym? row)
    {
        var locals = new HashSet<LocalSym>(lam.ParamSyms);
        return lam.Body.All(s => PureStmt(s, locals, row));
    }

    static bool PureStmt(Stmt s, HashSet<LocalSym> locals, LocalSym? row)
    {
        switch (s)
        {
            case ExprStmt es:
                return PureExpr(es.Expr, locals);
            case LocalDecl d:
                if (d.Local != null) locals.Add(d.Local);
                return d.Value == null || PureExpr(d.Value, locals);
            case Assign a:
                if (!PureExpr(a.Value, locals)) return false;
                if (a.Declares != null) { locals.Add(a.Declares); return true; }
                if (a.Target is NameExpr { Sym: LocalSym l }) return locals.Contains(l);
                return row != null && a.Target is CallExpr { Res: "field", Recv: NameExpr { Sym: LocalSym r } } && r == row;
            case IfStmt i:
                return i.Conds.All(c => PureExpr(c, locals)) && i.Bodies.All(b => b.All(x => PureStmt(x, locals, row))) && (i.Else == null || i.Else.All(x => PureStmt(x, locals, row)));
        }
        return false;
    }

    static bool PureExpr(Syntax.Expr e, HashSet<LocalSym> locals) => e switch
    {
        LitExpr => true,
        NameExpr { Res: "local" } => true,
        BinaryExpr b => PureExpr(b.L, locals) && PureExpr(b.R, locals),
        UnaryExpr { Op: "-" or "!" or "~" } u => PureExpr(u.E, locals),
        TernaryExpr t => PureExpr(t.C, locals) && PureExpr(t.A, locals) && PureExpr(t.B, locals),
        CallExpr { Res: "field" } f => f.Recv != null && PureExpr(f.Recv, locals),
        CallExpr { Recv: ConstExpr { Ref.Name: "Math" or "MathF" } } m => m.Block == null && m.Args.All(a => a.Mod == "" && PureExpr(a.Value!, locals)),
        CallExpr { Res: "ext" } x => PureExt.Contains(Binder.Mangle(x.Name)) && x.Block == null && x.Recv != null && PureExpr(x.Recv, locals) && x.Args.All(a => PureExpr(a.Value!, locals)),
        _ => false,
    };
}
