using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Stream fusion gates (ADR 0024). A chain `src.map{}.select{}.take(n).sink` runs as one loop only when interleaving
/// the stages per element is unobservable: every stage block that is followed by another block (or by an early exit:
/// take, any?, all?, find, first) must be effect-free. An effectful stage ends the chain: it and everything below it
/// are evaluated stage by stage, as written.
/// </summary>
public sealed partial class Lowerer
{
    /// <summary>Sinks that can stop before the last element: a stage before them would otherwise run fewer times.</summary>
    static readonly HashSet<string> EarlyExitSinks = ["any_q", "all_q", "find", "first"];

    /// <summary>Extension methods that change their receiver or the outside world.</summary>
    static readonly HashSet<string> Mutators =
    [
        "push", "pop", "shift", "unshift", "append", "prepend", "insert", "delete", "delete_at", "delete_if", "keep_if", "clear",
        "concat", "add", "remove", "fill", "replace", "store", "update", "call", "send", "puts", "print", "printf", "write", "set",
    ];

    static readonly HashSet<string> PureStatics = ["Math", "MathF", "Char", "Convert", "Int32", "Int64", "Double", "Single", "BitConverter"];

    /// <summary>`.take(n)` with a side-effect-free count: a fusable stage.</summary>
    static bool IsTakeStage(Syntax.Expr e, out CallExpr take)
    {
        take = (e as CallExpr)!;
        return e is CallExpr { Res: "ext", Name: "take", Block: null, Recv: not null, Args: [{ Mod: "", Value: { } n }] } && n.Type.IsInteger && SimpleRead(n);
    }

    static bool SimpleRead(Syntax.Expr e) => e switch
    {
        LitExpr or NameExpr { Res: "local" } => true,
        BinaryExpr b => SimpleRead(b.L) && SimpleRead(b.R),
        UnaryExpr { Op: "-" } u => SimpleRead(u.E),
        _ => false,
    };

    /// <summary>
    /// No effect visible outside one element's evaluation: writes only locals the block declares (or arrays it
    /// allocates), calls only pure Alice methods and non-mutating library methods, raises nothing explicitly.
    /// Implicit exceptions (overflow, bounds) are not effects here: a fused chain can raise after earlier sink work.
    /// </summary>
    bool EffectFree(LambdaExpr b)
    {
        var own = new HashSet<LocalSym>(b.ParamSyms);
        foreach (var s in WalkStmts(b.Body))
        {
            if (s is Assign { Declares: { } d }) own.Add(d);
            if (s is LocalDecl { Local: { } ld }) own.Add(ld);
            if (s is ForStmt f) own.UnionWith(f.Locals);
        }
        var fresh = FreshArrays(b.Body);
        foreach (var s in WalkStmts(b.Body))
        {
            bool ok = s switch
            {
                ExprStmt or LocalDecl or IfStmt or WhileStmt or ForStmt or CaseStmt or BreakStmt or NextStmt => true,
                Assign { Declares: not null } => true,
                Assign { Target: NameExpr { Sym: LocalSym l } } => own.Contains(l),
                Assign { Target: IndexExpr { Target: NameExpr { Sym: LocalSym fl } } } => own.Contains(fl) && fresh.Contains(fl),
                _ => false,
            };
            if (!ok) return false;
        }
        foreach (var e in Walk(b.Body))
        {
            if (!NodeEffectFree(e)) return false;
            if (e is CallExpr { Block: { } inner } && !EffectFree(inner)) return false;
        }
        return true;
    }

    bool NodeEffectFree(Syntax.Expr e) => e switch
    {
        LambdaExpr or InvokeExpr or YieldExpr => false,
        BinaryExpr { Op: "<<" } b => b.L.Type.IsInteger,   // `xs << v` appends
        NameExpr { Res: "call" or "selfcall" } n => n.Sym is MethodSym m && MethodPure(m),
        NewExpr { Type.Kind: "array" } => true,
        NewExpr n => n.AsCall == null && n.Type.Sym is TypeSym ts && CtorPure(ts, n.Args.Count),
        CallExpr c => c.Args.All(a => a.Mod == "") && c.Res switch
        {
            "alice" => c.Sym is MethodSym m && MethodPure(m),
            "ext" => Binder.Mangle(c.Name) is var n && !Mutators.Contains(n) && !n.EndsWith("_b"),
            "clr" => c.Sym is System.Reflection.MethodInfo mi && PureRuntime(mi) || c.Recv is ConstExpr { Ref.Name: var cn } && PureStatics.Contains(cn)
                     || c.Recv?.Type is { } rt && (rt.Kind is "prim" or "enum" || rt.IsString),
            "field" => true,
            "free" => c.Name == "format",
            "asnew" => c.Sym is NewExpr ne && NodeEffectFree(ne),
            _ => false,
        },
        _ => true,
    };

    /// <summary>
    /// Small literal / fixed-size arrays declared in a block that is about to be inlined into a loop: they become one
    /// stack buffer declared before that loop (StackArrays rules, per block). Only outside other loops, so the
    /// `stackalloc` runs once per frame.
    /// </summary>
    void InlineStackArrays(IEnumerable<LambdaExpr?> blocks, List<MStmt> before)
    {
        if (inParallelGen || ctx.Contains("loop") || ctx.Contains("lambda") || Method is { IsAsync: true } or { UsesYield: true }) return;
        if (Method?.Decl is { } md && Walk(md.Body).OfType<CallExpr>().Any(c => ReferenceEquals(c.Sym, Method))) return;
        int budget = MaxStackBytes - stackArrays.Values.Sum(v => v.Length * ElemSize(v.Elem));
        foreach (var b in blocks)
        {
            if (b == null) continue;
            var count = new Dictionary<LocalSym, int>();
            var shapes = new Dictionary<LocalSym, (string, int)>();
            foreach (var s in WalkStmts(b.Body))
            {
                if (s is Assign { Target: NameExpr { Sym: LocalSym t } }) count[t] = count.GetValueOrDefault(t) + 1;
                if (s is MultiAssign ma) foreach (var m in ma.Locals) count[m] = 2;
                if (s is Assign { Declares: { } d, Op: "=" } a && SmallArray(a.Value) is { } shape) shapes[d] = shape;
            }
            foreach (var (l, (elem, len)) in shapes)
            {
                int bytes = len * ElemSize(elem);
                if (bytes > budget || count.GetValueOrDefault(l) != 1 || stackArrays.ContainsKey(l) || hoisted.Contains(l) || !OnlyElementUses(b.Body, l)) continue;
                stackArrays[l] = (elem, len);
                budget -= bytes;
                before.Add(new MDecl { Name = l.CsName, TypeCs = $"global::System.Span<{elem}>", Init = new MCode($"stackalloc {elem}[{len}]") });
            }
        }
    }
}
