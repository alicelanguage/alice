using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    void BindAll()
    {
        foreach (var t in Types.Values.Distinct().Concat(Extensions).Append(Program))
            foreach (var list in t.Methods.Values)
                foreach (var m in list)
                {
                    m.NeedsGeneric = false;
                    foreach (var inst in m.Instances.Values) inst.Seen = false;
                }
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
                case MethodDecl { IsTemplate: true }:
                    break;
                case MethodDecl m:
                    BindMethod(m, owner, null);
                    var ms = (MethodSym)m.Sym!;
                    foreach (var inst in ms.Instances.Values.ToList()) BindMethod(m, owner, inst);
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

    /// <summary>Re-binds a method body (generic form or one specialization) right before it is lowered.</summary>
    public void Rebind(MethodDecl d, TypeSym owner, Instance? inst)
    {
        bool saved = changed;
        file = FileOf(d);
        BindMethod(d, owner, inst);
        changed = saved;
    }

    readonly Dictionary<MethodDecl, string> methodFiles = new();
    string FileOf(MethodDecl d) => methodFiles.TryGetValue(d, out var f) ? f : file;

    void BindMethod(MethodDecl d, TypeSym owner, Instance? inst)
    {
        methodFiles[d] = file;
        var m = (MethodSym)d.Sym!;
        BeginBody(owner, m, d.Hoisted);
        typeParams = new List<string>(owner.TypeParams);
        typeParams.AddRange(m.TypeParams);
        for (int i = 0; i < m.Params.Count; i++)
        {
            var ps = m.Params[i];
            var pt = inst != null ? inst.Args[i] : ps.Type;
            var l = Declare(d, ps.Name, ps.Splat ? AType.Array(pt) : pt);
            l.CsName = ps.CsName;
        }
        foreach (var p in d.Params)
            if (p.Default != null) BindExpr(p.Default);
        if (d.SuperInit != null) BindArgs(d.SuperInit);
        if (!d.HasBody) return;
        BindBlock(d.Body);
        d.HoistedLocals = hoisted;
        if (inst != null)
        {
            var r = m.RetDeclared ? m.Ret : InferRet(d.Body);
            if (!r.Same(inst.Ret)) { inst.Ret = r; changed = true; }
            return;
        }
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
                    foreach (var b in c.Else == null ? c.Whens.Select(w => w.Body) : c.Whens.Select(w => w.Body).Append(c.Else))
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
