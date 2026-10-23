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
            case ExprStmt { Expr: CallExpr { Res: "inline", Sym: StmtExpr inl } }:
                Into(o, inl.Stmt, tail, target);
                break;
            case BeginStmt { Rescues.Count: 0, Ensure: null } plain:
                {
                    var block = new MBlock { Body = Stmts(plain.Body, tail, target), Unchecked = plain.Unchecked, Unsafe = plain.Unsafe, Lock = plain.Lock != null ? Expr(plain.Lock) : null };
                    if (plain.Scope && tail == Tail.None && !inParallelGen && releaseAt.TryGetValue(plain, out var dying))   // lifetime region ends
                        foreach (var l in dying.Where(owned.ContainsKey)) block.Body.Add(Release(l, owned[l]));
                    o.Add(block);
                    break;
                }
            case ExprStmt es:
                {
                    // a declared/inferred non-void result means the tail IS the value, even when its static type is unknown
                    bool value = tail != Tail.None && (Binder.ValueOf(es.Expr) != null || (tail == Tail.Return && es.Expr.Type.IsUnknown));
                    var e = Expr(es.Expr);
                    if (value)
                    {
                        if (tail == Tail.Return)
                        {
                            MExpr? rv = e;
                            ReleaseOwnedAt(o, es, ref rv, es.Expr);
                            o.Add(new MReturn { Value = rv });
                        }
                        else o.Add(new MAssign { Target = new MCode(target!), Value = e });
                    }
                    else if (e != MCode.Empty) o.Add(new MExprStmt { E = e });
                    break;
                }
            case LocalDecl d:
                o.Add(new MDecl { Name = d.Local!.CsName, TypeCs = d.Type != null || d.Value == null ? TypeCs(d.Local.Type) : null, Init = d.Value != null ? Expr(d.Value) : new MCode("default!") });
                break;
            case Assign a:
                AssignStmt(o, a);
                // Ruby: an assignment's value is the assigned value (`def cache; @@c ||= List.new; end`)
                if (a.Declares == null && a.Target is IvarExpr && tail == Tail.Return && Method is { RetDeclared: true, Ret: { IsVoid: false, IsKnown: true } })
                    o.Add(new MReturn { Value = Expr(a.Target) });
                break;
            case MultiAssign ma:
                {
                    MExpr v;
                    // q, r = a.divmod(b): straight into the two floored results, no [q, r] array
                    if (ma.Values is [CallExpr { Name: "divmod", Res: "ext", Args.Count: 1, Block: null, Recv: { Type.Cs: "int" or "long" } dr } dm] && ma.Names.Count == 2)
                    {
                        string a = Temp("dv"), d = Temp("dv");
                        o.Add(new MDecl { Name = a, Init = Expr(dr) });
                        o.Add(new MDecl { Name = d, Init = Expr(dm.Args[0].Value!) });
                        v = new MTuple { Items = { new MCall { Target = new MCode("Prelude"), Name = "floor_div", Args = { new MCode(a), new MCode(d) } }, new MCall { Target = new MCode("Prelude"), Name = "floor_mod", Args = { new MCode(a), new MCode(d) } } } };
                    }
                    else if (ma.Values.Count == 1 && ma.Names.Count > 1 && ma.Values[0].Type.Kind != "tuple" && !ma.Values[0].Type.IsString && Builtins.ElemOf(ma.Values[0].Type).IsKnown)
                    {
                        string seq = Temp("m");
                        o.Add(new MDecl { Name = seq, Init = Expr(ma.Values[0]) });
                        v = new MTuple { Items = Enumerable.Range(0, ma.Names.Count).Select(k => (MExpr)new MIndex { Target = new MCode(seq), Args = { new MCode(k.ToString()) } }).ToList() };
                    }
                    else v = ma.Values.Count == 1 ? Expr(ma.Values[0]) : new MTuple { Items = ma.Values.Select(Expr).ToList() };
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
            case UsingStmt us:
                o.Add(new MUsing { Await = us.Await, Name = us.Local!.CsName, Value = Expr(us.Value), Body = Stmts(us.Body, Tail.None, null) });
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
            case ReturnStmt { Value: null } when Method is { IsIterator: true } && !InLambda:
                o.Add(new MExprStmt { E = new MCode("yield break") });
                break;
            case ReturnStmt r:
                {
                    MExpr? rv = r.Value != null ? Expr(r.Value) : null;
                    ReleaseOwnedAt(o, r, ref rv, r.Value);
                    o.Add(new MReturn { Value = rv });
                    break;
                }
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
        if (TryOwnedAssign(o, a)) return;
        if (a.Declares == null && TryCounterUpdate(o, a)) return;
        if (TryStackArrayInit(o, a)) return;
        if (TryFlatDecl(o, a) || TryFlatRowWrite(o, a)) return;
        if (a.Declares != null)
        {
            var v = Expr(a.Value);
            if (hoisted.Contains(a.Declares)) o.Add(new MAssign { Target = new MCode(a.Declares.CsName), Value = v });
            else o.Add(new MDecl { Name = a.Declares.CsName, Init = v });
            return;
        }
        var target = Expr(a.Target);
        var value = Expr(a.Value);
        // `obj.Event += block`: adapt the Alice delegate (any return type) to the event's delegate type
        if (a.Op is "+=" or "-=" && a.Target is CallExpr { Recv: { Type.Clr: { } rc }, Args.Count: 0 } ev && rc.GetEvent(ev.Name) is { EventHandlerType: { } ht }
            && ht.GetMethod("Invoke") is { } inv && inv.ReturnType == typeof(void))
        {
            string hn = "global::" + (ht.FullName ?? ht.Name).Replace('+', '.');
            var ps = string.Join(", ", inv.GetParameters().Select((_, i) => "__e" + i));
            var make = new MCode($"static __f => new {hn}(({ps}) => {{ __f.DynamicInvoke({ps}); }})");
            o.Add(new MAssign { Target = target, Op = a.Op, Value = new MCall { Target = new MCode("global::Alice.Runtime.EventAdapters"), Name = $"Handler<{hn}>", Args = { value, make } } });
            return;
        }
        // x += a*b / x -= a*b on floats -> x = fma(+-a, b, x)
        if (!StrictMath && a.Op is "+=" or "-=" && IsFloat(a.Target.Type) && a.Value is BinaryExpr { Op: "*" } vb && IsFloat(vb.L.Type) && IsFloat(vb.R.Type) && value is MBinary { Op: "*" } mv)
        {
            o.Add(new MAssign { Target = target, Value = Fma(a.Op == "+=" ? mv.L : new MUnary { Op = "-", E = mv.L }, mv.R, target) });
            return;
        }
        switch (a.Op)
        {
            case "/=" or "%=" when FlooredHelper(a.Op[..1], a.Target.Type) is string fl:
                o.Add(new MAssign { Target = target, Value = new MCast { TypeCs = a.Target.Type.Cs, E = new MCall { Target = new MCode("Prelude"), Name = fl, Args = { target, value } } } });
                break;
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
        if (f.Await) return SerialFor(f);
        if (f.Par)
            return TryParallelFor(f) ?? throw new CompileException($"{File}:{f.Line}:{Math.Max(1, f.Col)}: cannot parallelize `par for`: iterations are not provably independent (each may write only locals declared in the body, `a[i]` or `a[i * w + j]` at its own index, one `+=` reduction, and call only pure code)");
        if (TrySplitLoop(f) is MStmt split) return split;
        if (TryParallelFor(f) is MStmt par) return par;
        return SerialFor(f);
    }

    MStmt SerialFor(ForStmt f)
    {
        if (f.Await)
        {
            ctx.Add("loop");
            var af = new MForeach { Await = true, Vars = f.Locals.Select(l => l.CsName).ToList(), Coll = Expr(f.Iter), Body = Stmts(f.Body, Tail.None, null) };
            ctx.RemoveAt(ctx.Count - 1);
            return af;
        }
        if (TrySimtFor(f) is MStmt simt) return simt;
        ctx.Add("loop");
        MStmt r;
        if (f.Iter is RangeExpr re)
        {
            var end = re.B is NameExpr { Sym: LocalSym bl } && lengthAlias.TryGetValue(bl, out var len) && re.Excl ? new MCode(len) : Expr(re.B);
            // the counter has the unified type of both bounds: `0...m` with m : Int64 is a long loop, not an int one
            var loop = new MFor { Var = f.Locals[0].CsName, VarType = f.Locals[0].Type.IsInteger ? f.Locals[0].Type.Cs : null, Start = Expr(re.A), Cmp = re.Excl ? "<" : "<=", End = end, Body = Stmts(f.Body, Tail.None, null) };
            r = loop;
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
            if (w.Guard != null) cond = new MBinary { Op = "&&", L = cond!, R = Expr(w.Guard) };
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
                // `is var` binds inside the condition, so a guard (`in C(r) if r > 1`) can use the names
                if (bind != null) cond = new MBinary { Op = "&&", L = cond, R = new MIs { E = fe, TypeCs = "var " + bind.CsName } };
                else if (eq != null) cond = new MBinary { Op = "&&", L = cond, R = new MBinary { Op = "==", L = fe, R = Expr(eq) } };
            }
            return cond;
        }
        if (p.Res == "wild") return new MCode("true");
        if (p.Res == "bindpat" && p is BindPatternExpr bp) return new MIs { E = subj, TypeCs = "var " + bp.Local!.CsName };
        if (p.Res == "hashpat" && p is HashPatternExpr hp)
        {
            MExpr? hc = null;
            for (int k = 0; k < hp.Keys.Count; k++)
            {
                string slot = hp.Binds[k]?.CsName ?? Temp("hv");
                MExpr one = new MCall { Target = subj, Name = "TryGetValue", Args = { Expr(hp.Keys[k]), new MArg { Prefix = "out var ", E = new MCode(slot) } } };
                if (hp.Binds[k] == null && hp.Values[k] is not BindPatternExpr)
                    one = new MBinary { Op = "&&", L = one, R = new MBinary { Op = "==", L = new MCode(slot), R = Expr(hp.Values[k]) } };
                hc = hc == null ? one : new MBinary { Op = "&&", L = hc, R = one };
            }
            return hc ?? new MCode("true");
        }
        if (p.Res == "arraypat" && p is ArrayPatternExpr ap)
        {
            string len = ap.Type.Kind == "array" || ap.Type.IsString ? "Length" : "Count";
            MExpr cond = new MBinary { Op = ap.Rest != null ? ">=" : "==", L = new MMember { Target = subj, Name = len }, R = new MCode(ap.Names.Count.ToString()) };
            for (int i = 0; i < ap.Literals.Count; i++)
                if (ap.Literals[i] is { } lit)
                    cond = new MBinary { Op = "&&", L = cond, R = new MBinary { Op = "==", L = new MIndex { Target = subj, Args = { new MCode(i.ToString()) } }, R = Expr(lit) } };
            for (int i = 0; i < ap.Binds.Count; i++)
                if (ap.Binds[i] != null) cond = new MBinary { Op = "&&", L = cond, R = new MIs { E = new MIndex { Target = subj, Args = { new MCode(i.ToString()) } }, TypeCs = "var " + ap.Binds[i].CsName } };
            if (ap.RestSym != null)
                cond = new MBinary { Op = "&&", L = cond, R = new MIs { TypeCs = "var " + ap.RestSym.CsName, E = new MCall { Target = new MCode("global::System.Linq.Enumerable"), Name = "ToArray", Args = { new MCall { Target = new MCode("global::System.Linq.Enumerable"), Name = "Skip", Args = { subj, new MCode(ap.Names.Count.ToString()) } } } } } };
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
