using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

public sealed partial class Lowerer
{
    void Stmt(List<MStmt> o, Stmt s, Tail tail, string? target)
    {
        switch (s)
        {
            case ExprStmt es:
                {
                    // a declared/inferred non-void result means the tail IS the value, even when its static type is unknown
                    bool value = tail != Tail.None && (Binder.ValueOf(es.Expr) != null || (tail == Tail.Return && es.Expr.Type.IsUnknown));
                    var e = Expr(es.Expr);
                    if (value)
                    {
                        if (tail == Tail.Return) o.Add(new MReturn { Value = e });
                        else o.Add(new MAssign { Target = new MCode(target!), Value = e });
                    }
                    else if (e != MCode.Empty) o.Add(new MExprStmt { E = e });
                    break;
                }
            case LocalDecl d:
                o.Add(new MDecl { Name = d.Local!.CsName, TypeCs = d.Type != null ? TypeCs(d.Local.Type) : null, Init = d.Value != null ? Expr(d.Value) : new MCode("default!") });
                break;
            case Assign a:
                AssignStmt(o, a);
                break;
            case MultiAssign ma:
                {
                    var v = ma.Values.Count == 1 ? Expr(ma.Values[0]) : new MTuple { Items = ma.Values.Select(Expr).ToList() };
                    o.Add(new MDeconstruct { Names = ma.Locals.Select(l => l.CsName).ToList(), Declare = ma.Declares && !ma.Locals.Any(hoisted.Contains), Value = v });
                    break;
                }
            case IfStmt i:
                o.Add(IfChain(i.Conds, i.Bodies, i.Else, 0, tail, target));
                break;
            case WhileStmt w:
                o.Add(While(w));
                break;
            case ForStmt f:
                o.Add(For(f));
                break;
            case CaseStmt c:
                Case(o, c, tail, target);
                break;
            case RetryStmt:
                o.Add(new MContinue());
                break;
            case BeginStmt b when b.Rescues.Any(r => Contains(r.Body, s => s is RetryStmt)):
                {
                    // `retry` re-runs the begin body: try { body; break; } catch { ... continue; } inside a loop
                    var t = new MTry { Body = Stmts(b.Body, tail, target) };
                    t.Body.Add(new MBreak());
                    foreach (var r in b.Rescues)
                        t.Catches.Add(new MCatch { TypeCs = r.Resolved.Cs, Name = r.Local?.CsName, Body = Stmts(r.Body, tail, target) });
                    if (b.Ensure != null) t.Finally = Stmts(b.Ensure, Tail.None, null);
                    foreach (var c in t.Catches) c.Body.Add(new MBreak());
                    o.Add(new MWhile { Cond = new MCode("true"), Body = { t } });
                    break;
                }
            case BeginStmt b:
                {
                    var t = new MTry { Body = Stmts(b.Body, tail, target) };
                    foreach (var r in b.Rescues)
                        t.Catches.Add(new MCatch { TypeCs = r.Resolved.Cs, Name = r.Local?.CsName, Body = Stmts(r.Body, tail, target) });
                    if (b.Ensure != null) t.Finally = Stmts(b.Ensure, Tail.None, null);
                    o.Add(t);
                    break;
                }
            case ReturnStmt r:
                o.Add(new MReturn { Value = r.Value != null ? Expr(r.Value) : null });
                break;
            case BreakStmt:
                o.Add(InLambda ? new MReturn() : new MBreak());
                break;
            case NextStmt n:
                if (InLambda) o.Add(new MReturn { Value = n.Value != null ? Expr(n.Value) : null });
                else o.Add(new MContinue());
                break;
            case FusedSoaStmt fs:
                LowerFusedSoa(o, fs);
                break;
            case RaiseStmt rs:
                {
                    var v = Expr(rs.Value);
                    o.Add(new MThrow { Value = rs.Value is NewExpr ? v : new MCall { Target = new MCode("Prelude"), Name = "raise", Args = { v } } });
                    break;
                }
        }
    }

    void AssignStmt(List<MStmt> o, Assign a)
    {
        if (a.Declares == null && TryCounterUpdate(o, a)) return;
        if (a.Declares != null)
        {
            var v = Expr(a.Value);
            if (hoisted.Contains(a.Declares)) o.Add(new MAssign { Target = new MCode(a.Declares.CsName), Value = v });
            else o.Add(new MDecl { Name = a.Declares.CsName, Init = v });
            return;
        }
        var target = Expr(a.Target);
        var value = Expr(a.Value);
        switch (a.Op)
        {
            case "**=":
                o.Add(new MAssign { Target = target, Value = new MCall { Target = new MCode("Prelude"), Name = "pow", Args = { target, value } } });
                break;
            case "||=":
                o.Add(new MAssign { Target = target, Op = "??=", Value = value });
                break;
            case "&&=":
                o.Add(new MAssign { Target = target, Value = new MBinary { Op = "&&", L = target, R = value } });
                break;
            default:
                o.Add(new MAssign { Target = target, Op = a.Op, Value = value });
                break;
        }
    }

    MStmt IfChain(List<Syntax.Expr> conds, List<List<Stmt>> bodies, List<Stmt>? els, int k, Tail tail, string? target)
    {
        var node = new MIf { Cond = Expr(conds[k]), Then = Stmts(bodies[k], tail, target) };
        if (k + 1 < conds.Count)
        {
            var inner = new List<MStmt>();
            var saved = pre;
            pre = new List<MStmt>();
            var nested = IfChain(conds, bodies, els, k + 1, tail, target);
            inner.AddRange(pre);
            inner.Add(nested);
            pre = saved;
            node.Else = inner;
        }
        else if (els != null) node.Else = Stmts(els, tail, target);
        return node;
    }

    MStmt While(WhileStmt w)
    {
        var condPre = new List<MStmt>();
        var cond = Isolated(w.Cond, condPre);
        ctx.Add("loop");
        var body = Stmts(w.Body, Tail.None, null);
        ctx.RemoveAt(ctx.Count - 1);
        if (condPre.Count == 0) return new MWhile { Cond = cond, Body = body };
        condPre.Add(new MIf { Cond = new MUnary { Op = "!", E = cond }, Then = { new MBreak() } });
        condPre.AddRange(body);
        return new MWhile { Cond = new MCode("true"), Body = condPre };
    }

    MStmt For(ForStmt f)
    {
        if (TrySplitLoop(f) is MStmt split) return split;
        if (TryParallelFor(f) is MStmt par) return par;
        return SerialFor(f);
    }

    MStmt SerialFor(ForStmt f)
    {
        if (TrySimtFor(f) is MStmt simt) return simt;
        ctx.Add("loop");
        MStmt r;
        if (f.Iter is RangeExpr re)
        {
            var end = re.B is NameExpr { Sym: LocalSym bl } && lengthAlias.TryGetValue(bl, out var len) && re.Excl ? new MCode(len) : Expr(re.B);
            r = new MFor { Var = f.Locals[0].CsName, Start = Expr(re.A), Cmp = re.Excl ? "<" : "<=", End = end, Body = Stmts(f.Body, Tail.None, null) };
        }
        else
        {
            var coll = Expr(f.Iter);
            r = new MForeach { Vars = f.Locals.Select(l => l.CsName).ToList(), Coll = coll, Body = Stmts(f.Body, Tail.None, null) };
        }
        ctx.RemoveAt(ctx.Count - 1);
        return r;
    }

    void Case(List<MStmt> o, CaseStmt c, Tail tail, string? target)
    {
        MExpr? subj = null;
        if (c.Subject != null)
        {
            var v = Expr(c.Subject);
            if (v is MCode code && IsSimple(code.Text)) subj = v;
            else
            {
                string tmp = Temp("case");
                o.Add(new MDecl { Name = tmp, Init = v });
                subj = new MCode(tmp);
            }
        }
        var conds = new List<Syntax.Expr>();
        MIf? first = null;
        MIf? last = null;
        foreach (var w in c.Whens)
        {
            MExpr? cond = null;
            var binds = new List<MStmt>();
            foreach (var p in w.Patterns)
            {
                MExpr one = Pattern(subj, p, binds);
                cond = cond == null ? one : new MBinary { Op = "||", L = cond, R = one };
            }
            var node = new MIf { Cond = cond!, Then = binds };
            node.Then.AddRange(Stmts(w.Body, tail, target));
            if (first == null) first = node; else last!.Else = new List<MStmt> { node };
            last = node;
        }
        if (c.Else != null)
        {
            if (last == null) { o.AddRange(Stmts(c.Else, tail, target)); return; }
            last.Else = Stmts(c.Else, tail, target);
        }
        else if (c.Exhaustive && last != null)
            last.Else = new List<MStmt> { new MThrow { Value = new MNew { TypeCs = "global::System.Diagnostics.UnreachableException" } } };
        if (first != null) o.Add(first);
    }

    MExpr Pattern(MExpr? subj, Syntax.Expr p, List<MStmt> binds)
    {
        if (subj == null) return Expr(p);
        if (p.Res == "destructure" && p.Sym is Destructure d)
        {
            string tmp = Temp("p");
            MExpr cond = new MIs { E = subj, TypeCs = d.Type.Cs + " " + tmp };
            foreach (var (field, bind, eq) in d.Parts)
            {
                var fe = new MMember { Target = new MCode(tmp), Name = field };
                if (bind != null) binds.Add(new MDecl { Name = bind.CsName, Init = fe });
                else if (eq != null) cond = new MBinary { Op = "&&", L = cond, R = new MBinary { Op = "==", L = fe, R = Expr(eq) } };
            }
            return cond;
        }
        if (p.Res == "type") return new MIs { E = subj, TypeCs = p.Type.Cs };
        if (p is RangeExpr r)
            return new MBinary
            {
                Op = "&&",
                L = new MBinary { Op = ">=", L = subj, R = Expr(r.A) },
                R = new MBinary { Op = r.Excl ? "<" : "<=", L = subj, R = Expr(r.B) },
            };
        return new MBinary { Op = "==", L = subj, R = Expr(p) };
    }

    static bool IsSimple(string text) => text.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '@' || ch == '.');
}
