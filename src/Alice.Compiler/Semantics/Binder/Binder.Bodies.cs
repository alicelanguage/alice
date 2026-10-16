using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    void BindAll()
    {
        foreach (var f in Files)
        {
            file = f.Path;
            BindItems(f.Items, Program);
            BeginBody(Program, null, f.Hoisted);
            typeParams = new List<string>();
            scopes[0]["ARGV"] = new LocalSym { Name = "ARGV", CsName = "ARGV", Type = AType.Array(AType.String) };
            foreach (var it in f.Items)
                if (it is StmtItem s) BindStmt(s.Stmt);
            f.HoistedLocals = hoisted;
        }
    }

    void BindItems(List<Item> items, TypeSym owner)
    {
        foreach (var it in items)
        {
            switch (it)
            {
                case NamespaceItem n:
                    BindItems(n.Items, Program);
                    break;
                case TypeDecl d:
                    BindItems(d.Members, (TypeSym)d.Sym!);
                    break;
                case MethodDecl m:
                    BindMethod(m, owner);
                    break;
                case AccessorDecl a when a.Init != null:
                    BindInit(a.Init, owner);
                    break;
                case FieldDecl fd when fd.Init != null:
                    BindInit(fd.Init, owner);
                    break;
                case ConstDecl c:
                    BindInit(c.Value, owner);
                    break;
                case EnumDecl e:
                    foreach (var v in e.Values)
                        if (v != null) BindInit(v, owner);
                    break;
            }
        }
    }

    void BindInit(Expr e, TypeSym owner)
    {
        BeginBody(owner, null, new HashSet<string>());
        typeParams = owner.TypeParams;
        BindExpr(e);
    }

    void BindMethod(MethodDecl d, TypeSym owner)
    {
        var m = (MethodSym)d.Sym!;
        BeginBody(owner, m, d.Hoisted);
        typeParams = new List<string>(owner.TypeParams);
        typeParams.AddRange(m.TypeParams);
        foreach (var ps in m.Params)
        {
            var l = Declare(d, ps.Name, ps.Splat ? AType.Array(ps.Type) : ps.Type);
            l.CsName = ps.CsName;
        }
        foreach (var p in d.Params)
            if (p.Default != null) BindExpr(p.Default);
        if (d.SuperInit != null) BindArgs(d.SuperInit);
        if (!d.HasBody) return;
        BindBlock(d.Body);
        d.HoistedLocals = hoisted;
        if (!m.RetDeclared)
        {
            var inferred = InferRet(d.Body);
            if (!inferred.Same(m.Ret))
            {
                m.Ret = inferred;
                changed = true;
            }
        }
    }

    AType InferRet(List<Stmt> body)
    {
        var all = new List<AType>(returns);
        var tail = TailType(body);
        if (tail != null) all.Add(tail);
        if (all.Count == 0) return AType.Void;
        var u = Unify(all);
        if (u.Kind == "null") return AType.Object.AsNullable();
        return u.IsUnknown ? AType.Dynamic : u;
    }

    /// <summary>Type of the value a statement list produces in tail position, or null when it produces none.</summary>
    public static AType? TailType(List<Stmt> body)
    {
        if (body.Count == 0) return null;
        switch (body[^1])
        {
            case ExprStmt es:
                return ValueOf(es.Expr);
            case IfStmt i:
                {
                    if (i.Else == null) return null;
                    var ts = new List<AType>();
                    foreach (var b in i.Bodies.Append(i.Else))
                    {
                        var t = TailType(b);
                        if (t == null) return null;
                        ts.Add(t);
                    }
                    return Unify(ts);
                }
            case CaseStmt c:
                {
                    if (c.Else == null) return null;
                    var ts = new List<AType>();
                    foreach (var b in c.Whens.Select(w => w.Body).Append(c.Else))
                    {
                        var t = TailType(b);
                        if (t == null) return null;
                        ts.Add(t);
                    }
                    return Unify(ts);
                }
            case BeginStmt bs:
                return TailType(bs.Body);
        }
        return null;
    }

    public static AType? ValueOf(Expr e)
    {
        if (e.Type.IsVoid) return null;
        if (e.Type.IsUnknown) return IsCallLike(e) ? null : AType.Dynamic;
        return e.Type;
    }

    public static bool IsCallLike(Expr e) => e switch
    {
        CallExpr => true,
        InvokeExpr => true,
        YieldExpr => true,
        SuperExpr => true,
        NameExpr n => n.Res is "call" or "unknown",
        _ => false,
    };
}
