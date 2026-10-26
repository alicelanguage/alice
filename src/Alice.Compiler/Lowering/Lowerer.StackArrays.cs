using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Heap-free small arrays (escape analysis). A local bound exactly once to a small fixed-size array of unmanaged
/// elements, `rgb = Array(Float64).new(3, 0.0)` or `p = [x, y]`, whose only uses are element reads/writes,
/// `.size` and `for v in p`, never escapes: it becomes one `stackalloc` buffer declared at method entry and
/// re-initialized where the source declared the array. One buffer, so a declaration inside a loop does not
/// grow the stack per iteration; the GC never sees it. Anything else (returned, passed, aliased, captured by a
/// block or lambda, declared outside the loop that uses it) keeps the heap array.
/// </summary>
public sealed partial class Lowerer
{
    const int MaxStackElements = 256;
    const int MaxStackBytes = 2048;

    static int ElemSize(string cs) => cs switch { "long" or "double" or "ulong" => 8, "int" or "float" or "uint" => 4, "short" or "ushort" or "char" => 2, _ => 1 };

    /// <summary>Stack-array locals of the method being lowered: element type and length.</summary>
    Dictionary<LocalSym, (string Elem, int Length)> stackArrays = new();

    static readonly HashSet<string> Unmanaged = ["int", "long", "double", "float", "short", "sbyte", "byte", "ushort", "uint", "ulong", "bool", "char"];

    void FindStackArrays(List<Stmt> body)
    {
        stackArrays = new();
        if (Method is { IsAsync: true } || Method is { UsesYield: true }) return;   // spans cannot live in async/iterator state machines
        var decls = new Dictionary<LocalSym, (string, int)>();
        var assigns = new Dictionary<LocalSym, int>();
        foreach (var s in WalkStmts(body))
        {
            LocalSym? l = s switch
            {
                Assign { Declares: { } d } => d,
                Assign { Target: NameExpr { Sym: LocalSym t } } => t,
                _ => null,
            };
            if (l == null) { if (s is MultiAssign ma) foreach (var m in ma.Locals) assigns[m] = 2; continue; }
            assigns[l] = assigns.GetValueOrDefault(l) + 1;
            if (s is Assign { Declares: { } dl, Op: "=" } a && SmallArray(a.Value) is { } shape) decls[dl] = shape;
        }
        // a frame that recurses would take one buffer per level: the heap version survives depths that would overflow the stack
        if (Method != null && Walk(body).OfType<CallExpr>().Any(c => ReferenceEquals(c.Sym, Method))) return;
        int budget = MaxStackBytes;   // per method, in declaration order: many arrays must not add up to a huge frame
        foreach (var (l, shape) in decls)
        {
            int bytes = shape.Item2 * ElemSize(shape.Item1);
            if (bytes > budget || assigns[l] != 1 || !OnlyElementUses(body, l)) continue;
            stackArrays[l] = shape;
            budget -= bytes;
        }
    }

    /// <summary>`Array(T).new(...)` binds as a call wrapping the NewExpr.</summary>
    static Syntax.Expr Unwrap(Syntax.Expr e) => e is CallExpr { Res: "asnew", Sym: NewExpr ne } ? ne : e;

    static (string, int)? SmallArray(Syntax.Expr e)
    {
        e = Unwrap(e);
        if (e is NewExpr { Type: { Kind: "array", Elem: { } el }, Block: null, AsCall: null } ne && ne.Args.Count is 1 or 2
            && ne.Args[0].Value is LitExpr { Kind: "int" } n && Unmanaged.Contains(el.Cs)
            && int.TryParse(n.Text.Replace("_", ""), out int len) && len is > 0 and <= MaxStackElements)
            return (el.Cs, len);
        if (e is ArrayLit { Items.Count: > 0 and <= 32 } lit && lit.Type.Elem is { } le && Unmanaged.Contains(le.Cs))
            return (le.Cs, lit.Items.Count);
        return null;
    }

    /// <summary>Every mention of l is `l[i]`, `l.size` / `l.length`, or `for v in l`, outside lambdas/blocks, and every
    /// loop that mentions l also declares it (the buffer must not carry values across iterations of a loop it was built outside of).</summary>
    bool OnlyElementUses(List<Stmt> body, LocalSym l)
    {
        int mentions = 0, allowed = 0;
        foreach (var e in Walk(body))
        {
            switch (e)
            {
                case NameExpr { Sym: LocalSym x } when x == l: mentions++; break;
                case IndexExpr { Target: NameExpr { Sym: LocalSym x } } when x == l: allowed++; break;
                case CallExpr { Res: "ext", Args.Count: 0, Block: null, Recv: NameExpr { Sym: LocalSym x } } c when x == l && Binder.Mangle(c.Name) is "size" or "length": allowed++; break;
                case LambdaExpr lam when WalkExpr(lam).OfType<NameExpr>().Any(n => n.Sym == l): return false;
                case CallExpr { Block: { } blk } when Walk(blk.Body).OfType<NameExpr>().Any(n => n.Sym == l): return false;
            }
        }
        foreach (var s in WalkStmts(body))
        {
            if (s is ForStmt { Iter: NameExpr { Sym: LocalSym it } } && it == l) allowed++;
            if (s is Assign { Declares: { } dd, Target: NameExpr } && dd == l) allowed++;   // the declaration's own name
            if (s is ForStmt or WhileStmt)
            {
                var inner = s is ForStmt f ? f.Body : ((WhileStmt)s).Body;
                bool uses = Walk(inner).OfType<NameExpr>().Any(n => n.Sym == l);
                bool declares = WalkStmts(inner).Any(x => x is Assign { Declares: { } d } && d == l);
                if (uses && !declares) return false;
            }
        }
        return mentions == allowed;
    }

    /// <summary>At method entry: one stack buffer per stack-array local.</summary>
    IEnumerable<MStmt> StackArrayDecls(int line) => stackArrays.Select(kv => (MStmt)new MDecl
    {
        Line = line,
        Name = kv.Key.CsName,
        TypeCs = $"global::System.Span<{kv.Value.Elem}>",
        Init = new MCode($"stackalloc {kv.Value.Elem}[{kv.Value.Length}]"),
    });

    /// <summary>Where the source declared the array: re-initialize the buffer (left-to-right for literals).</summary>
    bool TryStackArrayInit(List<MStmt> o, Assign a)
    {
        if (inParallelGen || a.Declares is not { } l || !stackArrays.ContainsKey(l)) return false;
        string name = l.CsName;
        switch (Unwrap(a.Value))
        {
            case ArrayLit lit:
                for (int k = 0; k < lit.Items.Count; k++)
                    o.Add(new MAssign { Target = new MIndex { Target = new MCode(name), Args = { new MCode(k.ToString()) } }, Value = Expr(lit.Items[k]) });
                return true;
            case NewExpr ne when ne.Args.Count == 2:
                o.Add(new MExprStmt { E = new MCall { Target = new MCode(name), Name = "Fill", Args = { Expr(ne.Args[1].Value!) } } });
                return true;
            default:
                o.Add(new MExprStmt { E = new MCall { Target = new MCode(name), Name = "Clear" } });
                return true;
        }
    }
}
