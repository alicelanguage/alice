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
            case IvarExpr { Sym: FieldSym { IsStatic: true } cv }:
                return new MMember { Target = new MCode(cv.Owner.Type.Cs), Name = cv.CsName };
            case IvarExpr iv:
                return new MMember { Target = new MCode("this"), Name = iv.Sym is FieldSym ivf ? ivf.CsName : Binder.Mangle(iv.Name) };
            case SelfExpr:
                return new MCode(Type.Kind == "extension" ? "self" : "this");
            case SuperExpr sup:
                {
                    var args = sup.Args != null ? Args(sup.Args) : Method?.Params.Select(p => (MExpr)new MCode(p.CsName)).ToList() ?? new List<MExpr>();
                    return new MCall { Target = new MCode("base"), Name = Method?.CsName ?? "", Args = args };
                }
            case ConstExpr ce:
                return Const(ce);
            case CallExpr c when forkedCalls.TryGetValue(c, out var forked):
                return new MCode(forked);
            case CallExpr c:
                return Call(c);
            case NewExpr { AsCall: not null } nc:
                return Expr(nc.AsCall);
            case NewExpr ne when TryShared(ne) is MExpr shared:
                return shared;
            case NewExpr ne:
                {
                    var args = Args(ne.Args);
                    if (ne.Block != null) args.Add(Lambda(ne.Block));
                    if (ne.Type.Kind == "array")
                    {
                        // Array(T).new(n, v): a default fill (0, 0.0, false, nil) is what `new T[n]` already gives
                        if (ne.Block != null && ne.Args.Count == 1)   // Array(T).new(n) { |i| ... }
                            return new MCall { Target = new MCode("Prelude"), Name = $"filled<{ne.Type.Elem!.Cs}>", Args = { args[0], args[1] } };
                        if (ne.Args.Count >= 2 && !(ne.Args[1].Value is LitExpr { Text: "0" or "0.0" or "false" or "nil" or "0L" }))
                            return new MCall { Target = new MCode("Prelude"), Name = $"filled<{ne.Type.Elem!.Cs}>", Args = { args[0], args[1] } };
                        return new MNewArray { ElemCs = ne.Type.Elem!.Cs, Size = args.Count > 0 ? args[0] : new MCode("0") };
                    }
                    return new MNew { TypeCs = ne.Type.Cs == "var" ? ne.TypeRef.Name : ne.Type.Cs, Args = args };
                }
            case IndexExpr ix when TryFlatIndex(ix) is MExpr flat:
                return flat;
            case IndexExpr { TupleElement: { } el } te:
                return new MMember { Target = Expr(te.Target), Name = el };
            case IndexExpr ix:
                return new MIndex { Target = Expr(ix.Target), Args = Args(ix.Args) };
            case BinaryExpr b:
                return Binary(b);
            case UnaryExpr { Op: "!" } nu when Truthy(nu.E, new MCode("x")) is MBinary tb:
                return new MBinary { Op = tb.Op == "!=" ? "==" : "!=", L = Expr(nu.E), R = tb.R };   // `!s`: s is nil (Bool?: not true)
            case UnaryExpr u:
                return new MUnary { Op = u.Op, E = Expr(u.E) };
            case TernaryExpr t when TrySignExtend(t) is MExpr signExtended:
                return signExtended;
            case TernaryExpr t:
                {
                    var c = Truthy(t.C, Expr(t.C));
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
            case TupleLit { Names: { } names } ntl:   // (a: 1, b: "x")
                return new MTuple { Items = ntl.Items.Select((x, i) => (MExpr)new MArg { Prefix = Binder.Mangle(names[i]) + ": ", E = Expr(x) }).ToList() };
            case TupleLit tl:
                return new MTuple { Items = tl.Items.Select(Expr).ToList() };
            case LambdaExpr lam:
                return Lambda(lam);
            case TypeTestExpr tt:
                return TypeTest(tt);
            case InvokeExpr inv when SpecializedInvoke(ParamOf(inv.Target), inv.Args) is MExpr si:   // ADR 0026
                return si;
            case InvokeExpr inv:
                return new MInvoke { Target = Expr(inv.Target), Args = Args(inv.Args) };
            case RegexExpr rx:
                return new MCall { Target = new MCode("Prelude"), Name = "regex", Args = { new MCode("\"" + rx.Pattern + "\""), new MCode("\"" + rx.Flags + "\"") } };
            case AwaitExpr aw:
                return new MUnary { Op = "await ", E = Expr(aw.E) };
            case SpawnExpr sp:
                return new MCall { Target = new MCode("Prelude"), Name = "spawn", Args = { Lambda(sp.Body) } };
            case YieldExpr { Sym: null } yi:   // iterator method
                return new MArg { Prefix = "yield return ", E = yi.Args.Count == 1 ? Expr(yi.Args[0].Value!) : new MCode("default") };
            case YieldExpr y when SpecializedInvoke(y.Sym as ParamSym, y.Args) is MExpr sy:   // ADR 0026
                return sy;
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
        if (IsFloat(t)) return new MCall { Target = new MCode("Prelude"), Name = "str", Args = { v } };   // 2.0 prints "2.0", not "2"
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
                return new MCode(((LocalSym)n.Sym!).CsName + (n.Narrowed ? ".Value" : ""));
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
        string? helper = b.Res switch
        {
            "match" => "match_index",
            "nomatch" => "no_match",
            "format" => "sprintf",
            "repeat" => "str_mul",
            "append" => "append_op",
            "set+" => "arr_plus",
            "set-" => "arr_minus",
            "set&" => "arr_and",
            "set|" => "arr_or",
            _ => null,
        };
        if (helper != null) return new MCall { Target = new MCode("Prelude"), Name = helper, Args = { l, r } };
        if (b.Res == "coalesce") return new MBinary { Op = "??", L = l, R = r };
        if (b.Res == "usercmp") return new MCall { Target = l, Name = "op_Cmp", Args = { r } };
        if (Contract(b, l, r) is MExpr fused) return fused;
        if (b.Op is "&+" or "&-" or "&*") return new MCall { Name = "unchecked", Args = { new MBinary { Op = b.Op[1..], L = l, R = r } } };
        if (UncheckedIfProven(b, l, r) is MExpr proven) return proven;
        if (FlooredHelper(b.Op, b.Type) is string fl) return new MCall { Target = new MCode("Prelude"), Name = fl, Args = { l, r } };
        return b.Op switch
        {
            // exact forms of Math.Pow: 2.0 ** n is a power of two (ScaleB), x ** 2 is one rounded multiply
            "**" when b.L is LitExpr { Kind: "float", Text: "2.0" } && b.R.Type.Cs is "int" or "short" or "sbyte" or "byte" or "ushort"
                => new MCall { Target = new MCode("global::System.Math"), Name = "ScaleB", Args = { new MCode("1.0"), r } },
            "**" when b.R is LitExpr { Kind: "int", Text: "2" } && IsFloat(b.L.Type) && l is MCode
                => new MBinary { Op = "*", L = l, R = l },
            "**" => new MCall { Target = new MCode("Prelude"), Name = "pow", Args = { l, r } },
            "<=>" => new MCall { Target = l, Name = "CompareTo", Args = { r } },
            _ => new MBinary { Op = b.Op, L = l, R = r },
        };
    }

    /// <summary>Signed `/` and `%` are floored (-7 / 2 == -4, -7 % 3 == 2), float `%` too; tdiv/remainder truncate.</summary>
    internal static string? FlooredHelper(string op, AType t) => (op, t.Cs) switch
    {
        ("/", "int" or "long") => "floor_div",
        ("%", "int" or "long") => "floor_mod",
        ("%", "double" or "float") => "floor_fmod",   // a distinct name: passes may treat floor_mod as integer-only
        _ => null,
    };

    bool StrictMath => Method?.Decl?.Attrs.Contains("StrictMath") == true;

    static bool IsFloat(AType t) => t.Cs is "double" or "float";

    /// <summary>Inside a `gpu for` kernel: no CPU intrinsics (Prelude.fma tests Fma.IsSupported, which no GPU backend has).</summary>
    bool inGpuKernel;

    MExpr Fma(MExpr a, MExpr b, MExpr c, AType t) => new MCall { Target = new MCode("Prelude"), Name = "fma", Args = { a, b, c } };

    /// <summary>
    /// Floating-point contraction, like clang's default -ffp-contract=on: a*b+c, c+a*b, a*b-c, c-a*b become one
    /// fused multiply-add (single rounding, ~2x throughput on the dependency chain). `@[StrictMath]` opts out.
    /// </summary>
    MExpr? Contract(BinaryExpr b, MExpr l, MExpr r)
    {
        // in a GPU kernel the device compiler contracts a*b+c itself (OpenCL mad, CUDA fmad); an explicit
        // FusedMultiplyAdd was rejected by an OpenCL driver, and Prelude.fma's CPU feature test cannot compile there
        if (StrictMath || inGpuKernel || b.Op is not ("+" or "-") || !IsFloat(b.Type) || !IsFloat(b.L.Type) || !IsFloat(b.R.Type)) return null;
        if (l is MBinary { Op: "*" } ml && b.L is BinaryExpr { Op: "*" } bl && IsFloat(bl.L.Type) && IsFloat(bl.R.Type))
            return Fma(ml.L, ml.R, b.Op == "+" ? r : new MUnary { Op = "-", E = r }, b.Type);
        if (r is MBinary { Op: "*" } mr && b.R is BinaryExpr { Op: "*" } br && IsFloat(br.L.Type) && IsFloat(br.R.Type))
            return Fma(b.Op == "+" ? mr.L : new MUnary { Op = "-", E = mr.L }, mr.R, l, b.Type);
        return null;
    }

    MExpr TypeTest(TypeTestExpr tt)
    {
        switch (tt.Kind)
        {
            case "is": return new MIs { E = Expr(tt.E!), TypeCs = ((AType)tt.Sym!).Cs };
            case "as": return new MCast { TypeCs = ((AType)tt.Sym!).Cs, E = Expr(tt.E!) };
            case "nil?" when tt.E!.Type is { IsKnown: true, Nullable: false } vt && vt.Kind is "prim" or "struct" or "enum":
                return new MCode("false");   // a non-nullable value is never nil
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
        // `->sq` of a static method: the method group itself, not a lambda around it. C# caches the delegate, and APIs
        // that need the real method (ILGPU kernels, MethodInfo-based registration) get it
        if (lam.MethodRef is { } mr && (mr.Recv is null or ConstExpr) && lam.Body is [ExprStmt { Expr: CallExpr { Res: "alice", Sym: MethodSym m } }]
            && m.IsStatic && m.TypeParams.Count == 0 && m.Params.All(p => !p.HasDefault && !p.Splat && !p.Block && p.Mod == "")
            && (m.Owner.Methods.TryGetValue(m.Name, out var same) ? same.Count == 1 : true))
            return new MCode(m.Owner == Type || m.Owner.Kind == "program" ? m.CsName : m.Owner.Type.Cs + "." + m.CsName);
        var ps = new List<string>();
        for (int i = 0; i < lam.ParamSyms.Count; i++)
        {
            var p = lam.Params[i];
            bool typed = p.Type != null || lam.MethodRef != null && lam.ParamSyms[i].Type.IsKnown;   // `->sq`: types from the method
            ps.Add(typed ? Lowerer.TypeCs(lam.ParamSyms[i].Type) + " " + lam.ParamSyms[i].CsName : lam.ParamSyms[i].CsName);
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
        if (lam.Body.Count == 1 && lam.Body[0] is ExprStmt es && es.Expr is not CallExpr { Res: "inline" })
        {
            var body = Expr(es.Expr);
            if (lam.DiscardValue && !es.Expr.Type.IsVoid && es.Expr.Type.IsKnown)
                ml.Body = [.. pre, new MAssign { Target = new MCode("_"), Value = body }];
            else if (pre.Count == 0) ml.ExprBody = body;
            else
            {
                ml.Body = new List<MStmt>(pre);
                ml.Body.Add(lam.ReturnsValue ? new MReturn { Value = body } : new MExprStmt { E = body });
            }
        }
        else ml.Body = Stmts(lam.Body, lam.ReturnsValue && !lam.DiscardValue ? Tail.Return : Tail.None, null);   // an Action drops the tail value
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
