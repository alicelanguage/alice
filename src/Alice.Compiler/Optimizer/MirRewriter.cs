using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer;

/// <summary>
/// The one MIR traversal every pass shares: children are rewritten first (post-order), then the node itself is
/// offered to <see cref="Rewrite(MExpr)"/> / <see cref="Visit(MStmt)"/>. A pass overrides only the node kinds it
/// cares about; opaque <see cref="MCode"/> text is left untouched, so every rewrite stays conservative.
/// </summary>
public abstract class MirRewriter
{
    /// <summary>Called on an expression after its children were rewritten; return a replacement or the node.</summary>
    protected virtual MExpr Rewrite(MExpr e) => e;

    /// <summary>Called on a statement after its expressions and nested bodies were rewritten.</summary>
    protected virtual void Visit(MStmt s) { }

    public void Run(List<MStmt> body) => Stmts(body);

    protected void Stmts(List<MStmt>? body)
    {
        if (body == null) return;
        foreach (var s in body) Stmt(s);
    }

    void Stmt(MStmt s)
    {
        switch (s)
        {
            case MDecl d: if (d.Init != null) d.Init = Expr(d.Init); break;
            case MDeconstruct dc: dc.Value = Expr(dc.Value); break;
            case MAssign a: a.Target = Expr(a.Target); a.Value = Expr(a.Value); break;
            case MExprStmt es: es.E = Expr(es.E); break;
            case MIf i: i.Cond = Expr(i.Cond); Stmts(i.Then); Stmts(i.Else); break;
            case MWhile w: w.Cond = Expr(w.Cond); Stmts(w.Body); break;
            case MFor f: f.Start = Expr(f.Start); f.End = Expr(f.End); Stmts(f.Body); break;
            case MForeach fe: fe.Coll = Expr(fe.Coll); Stmts(fe.Body); break;
            case MReturn r: if (r.Value != null) r.Value = Expr(r.Value); break;
            case MThrow t: t.Value = Expr(t.Value); break;
            case MTry t: Stmts(t.Body); foreach (var c in t.Catches) Stmts(c.Body); Stmts(t.Finally); break;
            case MBlock b: Stmts(b.Body); break;
            case MUsing u: u.Value = Expr(u.Value); Stmts(u.Body); break;
        }
        Visit(s);
    }

    protected MExpr Expr(MExpr e)
    {
        switch (e)
        {
            case MCall c: if (c.Target != null) c.Target = Expr(c.Target); List(c.Args); break;
            case MMember m: m.Target = Expr(m.Target); break;
            case MInvoke i: i.Target = Expr(i.Target); List(i.Args); break;
            case MNew n: List(n.Args); if (n.Init != null) List(n.Init); break;
            case MNewArray a: if (a.Size != null) a.Size = Expr(a.Size); if (a.Items != null) List(a.Items); break;
            case MIndex ix: ix.Target = Expr(ix.Target); List(ix.Args); break;
            case MBinary b: b.L = Expr(b.L); b.R = Expr(b.R); break;
            case MUnary u: u.E = Expr(u.E); break;
            case MCond c: c.C = Expr(c.C); c.A = Expr(c.A); c.B = Expr(c.B); break;
            case MLambda l: if (l.ExprBody != null) l.ExprBody = Expr(l.ExprBody); Stmts(l.Body); break;
            case MCast c: c.E = Expr(c.E); break;
            case MIs i: i.E = Expr(i.E); break;
            case MInterp ip: List(ip.Holes); break;
            case MTuple t: List(t.Items); break;
            case MPair p: p.K = Expr(p.K); p.V = Expr(p.V); break;
            case MArg a: a.E = Expr(a.E); break;
        }
        return Rewrite(e);
    }

    void List(List<MExpr> xs)
    {
        for (int i = 0; i < xs.Count; i++) xs[i] = Expr(xs[i]);
    }
}
