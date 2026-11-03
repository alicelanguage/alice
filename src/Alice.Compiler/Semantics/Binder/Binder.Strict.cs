using System.Collections;
using System.Reflection;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Strict typing (AL0220): `[build] strict = true`, `--strict`, or `@[Strict]` on a method or on a class, struct or
/// module (its own methods). Every expression, local, parameter and return of an emitted body must have a static
/// type; `dynamic` is accepted only where it comes from an `Any` the user wrote (<see cref="AType.Any"/>). Each body
/// is checked right before it is lowered (after its specialization's rebind), so the types are the ones the backend
/// sees. Only root causes are reported: an untyped expression whose operands are already untyped is not reported again.
/// </summary>
public sealed partial class Binder
{
    public const string StrictCode = "AL0220";
    public bool Strict;
    public readonly List<Diagnostic> StrictErrors = new();
    readonly HashSet<(string, int, int, string)> strictAt = new();
    readonly HashSet<LocalSym> strictLocals = new();
    TypeSym? strictOwner;

    bool StrictFor(TypeSym owner, MethodDecl? d) =>
        Strict || (d?.Attrs.Contains("Strict") ?? false) || (owner.Decl?.Attrs.Contains("Strict") ?? false);

    /// <summary>Checks one method body (generic form or one specialization) as it is about to be emitted.</summary>
    public void CheckStrict(MethodDecl d, TypeSym owner, Instance? inst, AType ret)
    {
        if (!StrictFor(owner, d) || !d.HasBody) return;
        var m = (MethodSym)d.Sym!;
        string f = FileOf(d);
        strictOwner = owner;
        int before = StrictErrors.Count;
        for (int i = 0; i < m.Params.Count && i < d.Params.Count; i++)
        {
            var ps = m.Params[i];
            var pt = inst != null && i < inst.Args.Count ? inst.Args[i] : ps.Type;
            if (pt.IsUntyped)
                StrictReport(f, d.Params[i].Line > 0 ? d.Params[i] : d, ps.Name.Length, $"parameter `{ps.Name}` of `{d.Name}` has no static type");
            if (ps.Local is { } pl) strictLocals.Add(pl);   // reported as the parameter, not as a local
        }
        CheckStrictStmts(f, d.Body);
        if (ret.IsUntyped && !m.IsCtor && StrictErrors.Count == before)
            StrictReport(f, d, d.Name.Length, $"the return type of `{d.Name}` could not be inferred");
        strictOwner = null;
    }

    /// <summary>Checks a file's top-level statements (the program's `Main`).</summary>
    public void CheckStrictMain(string path, List<Stmt> body)
    {
        if (!Strict) return;
        strictOwner = Program;
        CheckStrictStmts(path, body);
        strictOwner = null;
    }

    void CheckStrictStmts(string f, List<Stmt> body)
    {
        AstWalk.Each(body, n =>
        {
            switch (n)
            {
                case LambdaExpr lam:
                    for (int i = 0; i < lam.ParamSyms.Count; i++)
                    {
                        var p = lam.ParamSyms[i];
                        if (p.Type.IsUntyped && strictLocals.Add(p))
                            StrictReport(f, i < lam.Params.Count && lam.Params[i].Line > 0 ? lam.Params[i] : lam, p.Name.Length,
                                $"block parameter `{p.Name}` has no static type");
                    }
                    break;
                case Expr e when TypeOf(e).IsUntyped && Reportable(e) && !ChildUntyped(e):
                    if (StrictWhat(e) is { } what) StrictReport(f, e, StrictLength(e), what);
                    break;
            }
            // locals (and block parameters) typed from an untyped value: the value is the root cause, reported there
            switch (n)
            {
                case Assign { Target: NameExpr { Sym: LocalSym l } } a when Dyn(TypeOf(a.Value)):
                    strictLocals.Add(l);
                    break;
                case LocalDecl { Local: { } l, Value: { } v } when Dyn(TypeOf(v)):
                    strictLocals.Add(l);
                    break;
                case UsingStmt { Local: { } l } u when Dyn(TypeOf(u.Value)):
                    strictLocals.Add(l);
                    break;
                case MultiAssign ma when ma.Values.Any(v => Dyn(TypeOf(v))):
                    strictLocals.UnionWith(ma.Locals);
                    break;
                case ForStmt fs when Dyn(TypeOf(fs.Iter)):
                    strictLocals.UnionWith(fs.Locals);
                    break;
                case CallExpr { Block: { } blk, Recv: { } r } when Dyn(TypeOf(r)):
                    strictLocals.UnionWith(blk.ParamSyms);
                    break;
            }
        });
    }

    static bool Dyn(AType t) => t.Kind is "unknown" or "dynamic";

    /// <summary>Containers and non-values: their parts are checked, not they.</summary>
    static bool Reportable(Expr e) => e is not (LambdaExpr or StmtExpr or ArrayPatternExpr or BindPatternExpr or HashPatternExpr)
        && e.Res is not ("type" or "namespace")
        && !(e is RangeExpr r && !ChildUntyped(r));   // `when 3..15`: a range pattern is two comparisons, typed by its bounds (fuzz F1)

    static readonly Dictionary<Type, FieldInfo[]> exprFields = new();

    /// <summary>Direct sub-expressions (through arguments, not into block bodies).</summary>
    static IEnumerable<Expr> Children(Node n)
    {
        FieldInfo[] fs;
        lock (exprFields)
            if (!exprFields.TryGetValue(n.GetType(), out fs!))
                exprFields[n.GetType()] = fs = n.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Where(x => typeof(Node).IsAssignableFrom(x.FieldType) || (typeof(IEnumerable).IsAssignableFrom(x.FieldType) && x.FieldType != typeof(string))).ToArray();
        foreach (var fi in fs)
        {
            var v = fi.GetValue(n);
            if (v is Expr e) yield return e;
            else if (v is Arg a && a.Value != null) yield return a.Value;
            else if (v is IEnumerable list and not string)
                foreach (var x in list)
                    if (x is Expr xe) yield return xe;
                    else if (x is Arg xa && xa.Value != null) yield return xa.Value;
        }
    }

    /// <summary>An operand is already untyped (reported there), or derives from an `Any` (accepted).</summary>
    static bool ChildUntyped(Expr e) => Children(e).Any(c => c is LambdaExpr lam
        ? lam.ParamSyms.Any(p => p.Type.IsUntyped) || lam.ReturnsValue && lam.Result.IsUntyped
        : TypeOf(c).Kind is "unknown" or "dynamic");

    /// <summary>A local's name carries its local's type (an assignment target's node is not typed itself).</summary>
    static AType TypeOf(Expr e) => e is NameExpr { Sym: LocalSym l } ? l.Type : e.Type;

    static string Show(AType t) => t.Kind == "unknown" ? "an untyped value" : $"`{Alice.Compiler.Diagnostics.AliceNames.Type(t.Cs)}`";

    string? StrictWhat(Expr e)
    {
        switch (e)
        {
            case NameExpr { Sym: LocalSym l } n:
                return strictLocals.Add(l) ? $"local `{n.Name}` has no static type" : null;
            case NameExpr n:
                return $"`{n.Name}` has no static type (resolved as {(n.Res == "" ? "nothing" : n.Res)})";
            case ConstExpr ce:
                return $"`{ce.Ref.Name}` has no static type (resolved as {(ce.Res == "" ? "nothing" : ce.Res)})";
            case IvarExpr iv:
                return $"field `@{iv.Name}` of `{strictOwner?.Name ?? "self"}` has no static type";
            case CallExpr c when c.Recv != null:
                return $"call `{c.Name}` on {Show(c.Recv.Type)} has no static type{CallWhy(c)}";
            case CallExpr { Sym: MethodSym ms } c:
                return $"call to `{c.Name}` has no static type (`{ms.Owner.Name}.{ms.Name}` has no inferred return type)";
            case CallExpr c:
                return $"call to `{c.Name}` has no static type{CallWhy(c)}";
            case IndexExpr ix:
                return $"indexing {Show(ix.Target.Type)} has no static type";
            case BinaryExpr b:
                return $"`{b.Op}` on {Show(b.L.Type)} and {Show(b.R.Type)} has no static type";
            case UnaryExpr u:
                return $"`{u.Op}` on {Show(u.E.Type)} has no static type";
            case InvokeExpr:
                return "this block call has no static type";
            case YieldExpr:
                return "this `yield` has no static type (the block's result type is unknown)";
            case SuperExpr:
                return "this `super` call has no static type";
            case ArrayLit:
                return "this array literal has no element type";
            case HashLit:
                return "this hash literal has no key/value types";
            case TernaryExpr:
                return "the branches of this `?:` have no common static type";
            case AwaitExpr aw:
                return $"`await` on {Show(aw.E.Type)} has no static type";
            default:
                return $"this {e.GetType().Name.Replace("Expr", "").ToLowerInvariant()} expression has no static type";
        }
    }

    static string CallWhy(CallExpr c) => c.Res switch
    {
        "dyncall" or "field" => " (dynamic dispatch)",
        "" => " (no method found)",
        var r => $" (resolved as {r})",
    };

    static int StrictLength(Expr e) => e switch
    {
        NameExpr n => n.Name.Length,
        IvarExpr iv => iv.Name.Length + 1,
        CallExpr c => c.Name.Length + (c.Recv != null ? 1 : 0),   // `.name`: a member call's node starts at the dot
        _ => 1,
    };

    void StrictReport(string f, Node at, int len, string what)
    {
        if (!strictAt.Add((f, at.Line, at.Col, what))) return;
        StrictErrors.Add(new Diagnostic("error", StrictCode, f, at.Line, Math.Max(1, at.Col),
            $"strict: {what}; annotate it, cast it (`x.as(T)`), or declare it `Any` on purpose", Math.Max(1, len)));
    }
}
