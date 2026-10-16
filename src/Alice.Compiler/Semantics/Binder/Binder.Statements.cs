using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    void BindBlock(List<Stmt> body)
    {
        foreach (var s in body) BindStmt(s);
    }

    void Scoped(List<Stmt> body)
    {
        PushScope();
        BindBlock(body);
        PopScope();
    }

    void BindStmt(Stmt s)
    {
        switch (s)
        {
            case ExprStmt es:
                BindExpr(es.Expr);
                break;
            case LocalDecl d:
                {
                    AType vt = d.Value != null ? BindExpr(d.Value) : AType.Unknown;
                    AType t = d.Type != null ? ResolveType(d.Type) : vt;
                    d.Local = Declare(d, d.Name, t, d.IsVal);
                    break;
                }
            case Assign a:
                BindAssign(a);
                break;
            case MultiAssign ma:
                BindMultiAssign(ma);
                break;
            case IfStmt i:
                foreach (var c in i.Conds) BindExpr(c);
                foreach (var b in i.Bodies) Scoped(b);
                if (i.Else != null) Scoped(i.Else);
                break;
            case WhileStmt w:
                BindExpr(w.Cond);
                Scoped(w.Body);
                break;
            case ForStmt f:
                BindFor(f);
                break;
            case CaseStmt c:
                BindCase(c);
                break;
            case BeginStmt b:
                Scoped(b.Body);
                foreach (var r in b.Rescues)
                {
                    PushScope();
                    r.Resolved = r.Type != null ? ResolveType(r.Type) : ClrTypes.FromClr(typeof(Exception));
                    if (r.Name != null) r.Local = Declare(r, r.Name, r.Resolved);
                    BindBlock(r.Body);
                    PopScope();
                }
                if (b.Ensure != null) Scoped(b.Ensure);
                break;
            case ReturnStmt r:
                if (r.Value != null)
                {
                    var t = BindExpr(r.Value);
                    if (lambdas.Count == 0) returns.Add(t);
                }
                break;
            case NextStmt n:
                if (n.Value != null) BindExpr(n.Value);
                break;
            case RaiseStmt rs:
                BindExpr(rs.Value);
                break;
            case BreakStmt:
                break;
        }
    }

    void BindAssign(Assign a)
    {
        if (a.Target is NameExpr n)
        {
            var l = FindLocal(n.Name);
            if (l == null && a.Op == "=")
            {
                AType vt = BindExpr(a.Value);
                a.Declares = Declare(a, n.Name, vt);
                n.Sym = a.Declares;
                n.Res = "local";
                n.Type = vt;
                return;
            }
            if (l == null)
            {
                Hoist(n.Name);
                BindExpr(a.Value);
                n.Res = "unknown";
                return;
            }
            CheckWrite(a, l);
            n.Sym = l;
            n.Res = "local";
            n.Type = l.Type;
            UseLocal(l);
            AType v = BindExpr(a.Value);
            if (!l.Type.IsKnown && v.IsKnown && a.Op == "=") l.Type = v;
            return;
        }
        BindExpr(a.Target);
        BindExpr(a.Value);
    }

    /// <summary>Rust-style write rules: `val` locals are immutable; spawned tasks may not write captured locals.</summary>
    void CheckWrite(Node at, LocalSym l)
    {
        if (l.IsVal) throw Err(at, $"cannot assign to immutable '{l.Name}' (declared with val)");
        if (l.SpawnDepth < spawnDepth)
            throw Err(at, $"data race: spawned task writes captured variable '{l.Name}'; send it through a Channel or use an Actor");
    }

    void Hoist(string name)
    {
        if (hoist.Add(name)) changed = true;
    }

    void BindMultiAssign(MultiAssign ma)
    {
        var vts = ma.Values.Select(BindExpr).ToList();
        if (ma.Values.Count == 1 && vts[0].Kind == "tuple" && vts[0].Args.Count == ma.Names.Count) vts = vts[0].Args;
        ma.Locals = new List<LocalSym>();
        ma.Declares = false;
        var existing = ma.Names.Select(FindLocal).ToList();
        if (existing.All(e => e != null))
        {
            foreach (var l in existing) { CheckWrite(ma, l!); UseLocal(l!); }
            ma.Locals = existing.Select(e => e!).ToList();
            return;
        }
        if (existing.Any(e => e != null)) throw Err(ma, "mixing new and existing variables in multiple assignment");
        ma.Declares = true;
        for (int i = 0; i < ma.Names.Count; i++)
            ma.Locals.Add(Declare(ma, ma.Names[i], i < vts.Count ? vts[i] : AType.Unknown));
    }

    void BindFor(ForStmt f)
    {
        AType elem;
        if (f.Iter is RangeExpr r)
        {
            var a = BindExpr(r.A);
            var b = BindExpr(r.B);
            elem = Unify([a, b]);
            if (!elem.IsKnown) elem = AType.Int;
            r.Type = elem;
        }
        else elem = Builtins.ElemOf(BindExpr(f.Iter));
        PushScope();
        f.Locals = new List<LocalSym>();
        if (f.Names.Count == 1) f.Locals.Add(Declare(f, f.Names[0], elem));
        else
        {
            var parts = elem.Args.Count == f.Names.Count ? elem.Args : null;
            for (int i = 0; i < f.Names.Count; i++) f.Locals.Add(Declare(f, f.Names[i], parts?[i] ?? AType.Unknown));
        }
        BindBlock(f.Body);
        PopScope();
    }

    void BindCase(CaseStmt c)
    {
        if (c.Subject != null) BindExpr(c.Subject);
        foreach (var w in c.Whens)
        {
            foreach (var p in w.Patterns)
            {
                if (c.Subject != null && p is ConstExpr ce && ce.Ref.Args.Count == 0 && TypeByName(ce.Ref.Name, ce.Ref.Args) is AType pt && !IsConstValue(ce))
                {
                    p.Res = "type";
                    p.Type = pt;
                }
                else if (p is RangeExpr r)
                {
                    BindExpr(r.A);
                    BindExpr(r.B);
                }
                else BindExpr(p);
            }
            Scoped(w.Body);
        }
        if (c.Else != null) Scoped(c.Else);
    }

    bool IsConstValue(ConstExpr ce) => curType.FindField(ce.Ref.Name) != null || Program.Fields.ContainsKey(ce.Ref.Name);
}
