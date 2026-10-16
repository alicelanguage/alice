using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

public sealed partial class Lowerer
{
    MExpr Expr(Syntax.Expr e)
    {
        switch (e)
        {
            case LitExpr l:
                return l.Kind == "str" ? new MCode("\"" + l.Text + "\"") : new MCode(l.Text);
            case InterpExpr ie:
                return new MInterp { Chunks = ie.Chunks, Holes = ie.Holes.Select(Hole).ToList() };
            case NameExpr n:
                return Name(n);
            case IvarExpr iv:
                return new MMember { Target = new MCode("this"), Name = Binder.Mangle(iv.Name) };
            case SelfExpr:
                return new MCode(Type.Kind == "extension" ? "self" : "this");
            case SuperExpr sup:
                {
                    var args = sup.Args != null ? Args(sup.Args) : Method?.Params.Select(p => (MExpr)new MCode(p.CsName)).ToList() ?? new List<MExpr>();
                    return new MCall { Target = new MCode("base"), Name = Method?.CsName ?? "", Args = args };
                }
            case ConstExpr ce:
                return Const(ce);
            case CallExpr c:
                return Call(c);
            case NewExpr { AsCall: not null } nc:
                return Expr(nc.AsCall);
            case NewExpr ne:
                {
                    var args = Args(ne.Args);
                    if (ne.Block != null) args.Add(Lambda(ne.Block));
                    if (ne.Type.Kind == "array") return new MNewArray { ElemCs = ne.Type.Elem!.Cs, Size = args.Count > 0 ? args[0] : new MCode("0") };
                    return new MNew { TypeCs = ne.Type.Cs == "var" ? ne.TypeRef.Name : ne.Type.Cs, Args = args };
                }
            case IndexExpr ix:
                return new MIndex { Target = Expr(ix.Target), Args = Args(ix.Args) };
            case BinaryExpr b:
                return Binary(b);
            case UnaryExpr u:
                return new MUnary { Op = u.Op, E = Expr(u.E) };
            case TernaryExpr t:
                {
                    var c = Expr(t.C);
                    noHoist++;
                    var r = new MCond { C = c, A = Expr(t.A), B = Expr(t.B) };
                    noHoist--;
                    return r;
                }
            case RangeExpr r:
                return new MCall { Target = new MCode("Prelude"), Name = "range", Args = { Expr(r.A), Expr(r.B), new MCode(r.Excl ? "true" : "false") } };
            case ArrayLit a:
                {
                    var items = a.Items.Select(Expr).ToList();
                    if (a.Of != null) return new MNew { TypeCs = a.Type.Cs, Init = items };
                    return new MNewArray { ElemCs = a.Type.Elem?.Cs ?? "", Items = items };
                }
            case HashLit h:
                {
                    if (!h.Type.IsKnown)
                        return new MCall { Target = new MCode("Prelude"), Name = "hash", Args = h.Keys.Select((k, i) => (MExpr)new MTuple { Items = { Expr(k), Expr(h.Values[i]) } }).ToList() };
                    var init = h.Keys.Select((k, i) => (MExpr)new MPair { K = Expr(k), V = Expr(h.Values[i]) }).ToList();
                    return new MNew { TypeCs = h.Type.Cs, Init = init };
                }
            case TupleLit tl:
                return new MTuple { Items = tl.Items.Select(Expr).ToList() };
            case LambdaExpr lam:
                return Lambda(lam);
            case TypeTestExpr tt:
                return TypeTest(tt);
            case InvokeExpr inv:
                return new MInvoke { Target = Expr(inv.Target), Args = Args(inv.Args) };
            case AwaitExpr aw:
                return new MUnary { Op = "await ", E = Expr(aw.E) };
            case SpawnExpr sp:
                return new MCall { Target = new MCode("Prelude"), Name = "spawn", Args = { Lambda(sp.Body) } };
            case YieldExpr y:
                return new MInvoke { Target = new MCode(((ParamSym)y.Sym!).CsName), Args = Args(y.Args) };
            case StmtExpr se:
                return StmtValue(se);
        }
        throw new CompileException($"line {e.Line}: cannot lower {e.GetType().Name}");
    }

    /// <summary>Interpolation holes keep their static type: numbers/chars format straight into the handler (no boxing).</summary>
    MExpr Hole(Syntax.Expr h)
    {
        var v = Expr(h);
        var t = h.Type;
        if (t.IsString || t.IsNumeric || t.Cs == "char" || (t.Clr != null && t.Clr.IsEnum)) return v;
        if (t.IsBool) return new MCond { C = v, A = new MCode("\"true\""), B = new MCode("\"false\"") };
        return new MCall { Target = new MCode("Prelude"), Name = "str", Args = { v } };
    }

    MExpr Name(NameExpr n)
    {
        switch (n.Res)
        {
            case "local" when soa != null && n.Sym is LocalSym sl && soa.Ps.Contains(sl):
                return new MIndex { Target = new MCode(soa.Store), Args = { new MCode(soa.Idx) } };
            case "local":
                return new MCode(((LocalSym)n.Sym!).CsName);
            case "field":
                return new MCode(((FieldSym)n.Sym!).CsName);
            case "call":
                if (n.Sym is MethodSym em && em.Owner.Kind == "extension") return new MCall { Target = new MCode("self"), Name = em.CsName };
                return new MCall { Name = n.Sym is MethodSym m ? m.CsName : Binder.Mangle(n.Name) };
            case "selfcall":
                return Expr((CallExpr)n.Sym!);
        }
        string cs = Binder.Mangle(n.Name);
        return zeroArg.Contains(cs) && !props.Contains(cs) ? new MCall { Name = cs } : new MCode(cs);
    }

    MExpr Const(ConstExpr ce)
    {
        if (ce.Res == "type" && ce.Sym is AType t)
            return new MCode(t.Sym is { Kind: "variant", FieldOrder.Count: 0 } ? t.Cs + ".Value" : t.Cs);
        if (ce.Res == "path" && ce.Sym is ConstPath cp)
        {
            MExpr cur = new MCode(cp.OwnerText ?? cp.Owner.Cs);
            foreach (var (name, call) in cp.Members)
                cur = call ? new MCall { Target = cur, Name = name } : new MMember { Target = cur, Name = name };
            return cur;
        }
        var segs = ce.Ref.Name.Split('.');
        segs[0] = Binder.PrimCs(segs[0]);
        return new MCode(string.Join(".", segs));
    }

    MExpr Binary(BinaryExpr b)
    {
        var l = Expr(b.L);
        bool lazy = b.Op is "&&" or "||";
        if (lazy) noHoist++;
        var r = Expr(b.R);
        if (lazy) noHoist--;
        return b.Op switch
        {
            "**" => new MCall { Target = new MCode("Prelude"), Name = "pow", Args = { l, r } },
            "<=>" => new MCall { Target = l, Name = "CompareTo", Args = { r } },
            _ => new MBinary { Op = b.Op, L = l, R = r },
        };
    }

    MExpr TypeTest(TypeTestExpr tt)
    {
        switch (tt.Kind)
        {
            case "is": return new MIs { E = Expr(tt.E!), TypeCs = ((AType)tt.Sym!).Cs };
            case "as": return new MCast { TypeCs = ((AType)tt.Sym!).Cs, E = Expr(tt.E!) };
            case "nil?": return new MIs { E = Expr(tt.E!), TypeCs = "null" };
            case "not_nil!": return new MUnary { Op = "!", E = Expr(tt.E!), Postfix = true };
            case "default": return new MCode($"default({tt.Type.Cs})");
            default: return new MCode($"{tt.Kind}({((AType)tt.Sym!).Cs})");
        }
    }

    List<MExpr> Args(List<Arg> args)
    {
        var r = new List<MExpr>();
        foreach (var a in args)
        {
            if (a.OutLocal != null) { r.Add(new MArg { Prefix = "out var ", E = new MCode(a.OutLocal.CsName) }); continue; }
            var v = Expr(a.Value!);
            if (a.Mod != "") r.Add(new MArg { Prefix = a.Mod + " ", E = v });
            else if (a.Name != null) r.Add(new MArg { Prefix = Binder.Mangle(a.Name) + ": ", E = v });
            else r.Add(v);
        }
        return r;
    }

    /// <summary>Non-inlined block/lambda. Capture-free lambdas are `static` so the C# compiler caches the delegate (zero allocation).</summary>
    MExpr Lambda(LambdaExpr lam)
    {
        var ps = new List<string>();
        for (int i = 0; i < lam.ParamSyms.Count; i++)
        {
            var p = lam.Params[i];
            ps.Add(p.Type != null ? Lowerer.TypeCs(lam.ParamSyms[i].Type) + " " + lam.ParamSyms[i].CsName : lam.ParamSyms[i].CsName);
        }
        if (ps.Any(x => x.Contains(' ')) && ps.Any(x => !x.Contains(' ')))
            for (int i = 0; i < ps.Count; i++)
                if (!ps[i].Contains(' ')) ps[i] = Lowerer.TypeCs(lam.ParamSyms[i].Type) + " " + ps[i];
        var ml = new MLambda { Params = ps, Static = !lam.Captures };
        var savedPre = pre;
        pre = new List<MStmt>();
        int savedNoHoist = noHoist;
        noHoist = 0;
        ctx.Add("lambda");
        if (lam.Body.Count == 1 && lam.Body[0] is ExprStmt es)
        {
            var body = Expr(es.Expr);
            if (pre.Count == 0) ml.ExprBody = body;
            else
            {
                ml.Body = new List<MStmt>(pre);
                ml.Body.Add(lam.ReturnsValue ? new MReturn { Value = body } : new MExprStmt { E = body });
            }
        }
        else ml.Body = Stmts(lam.Body, lam.ReturnsValue ? Tail.Return : Tail.None, null);
        ctx.RemoveAt(ctx.Count - 1);
        noHoist = savedNoHoist;
        pre = savedPre;
        return ml;
    }

    /// <summary>if/case/begin as a value: hoisted into a typed temp assigned on every branch.</summary>
    MExpr StmtValue(StmtExpr se)
    {
        string tmp = Temp("v");
        pre.Add(new MDecl { Name = tmp, TypeCs = Lowerer.TypeCs(se.Type), Init = new MCode("default!") });
        Into(pre, se.Stmt, Tail.Assign, tmp);
        return new MCode(tmp);
    }
}
