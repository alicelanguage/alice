using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// SIMT vectorization (GPU-style execution on CPU SIMD lanes). A counted loop whose iterations contain a
/// data-dependent `while` runs Vector&lt;double&gt;.Count iterations at once: every lane keeps its own locals,
/// the while runs until all lanes are done, and updates are masked so finished lanes keep their values.
/// Lanes do exactly the scalar arithmetic in the same order, so results are identical. Integer locals are
/// carried in double lanes (exact for these magnitudes); integer division and Float32 keep the scalar loop.
/// </summary>
public sealed partial class Lowerer
{
    bool simtStrict;

    MStmt? TrySimtFor(ForStmt f)
    {
        if (f.Iter is not RangeExpr r || f.Locals.Count != 1 || f.Locals[0].Type.Cs != "int") return null;
        if (!f.Body.Any(s => s is WhileStmt) || noHoist > 0) return null;
        const string T = "double";
        var vt = AirVector.Of(AirVector.Prim(T), 0);
        var env = new Dictionary<LocalSym, string>();
        var accs = new Dictionary<LocalSym, string>();
        string px = Temp("px");
        string vpx = Temp("vpx");
        env[f.Locals[0]] = vpx;
        var vbody = new List<MStmt> { new MDecl { Name = vpx, Init = new MBinary { Op = AirBinOp.Add, Checked = false, L = VOp(AirVecOp.Splat, vt, LRef(px, IntT)), R = VOp(AirVecOp.Indices, vt), Type = vt } } };
        simtStrict = true;
        bool ok = SimtStmts(f.Body, env, vbody, null, accs, T);
        simtStrict = false;
        if (!ok) return null;

        var start = Expr(r.A);
        var end = Expr(r.B);
        vbody.Add(new MAssign { Target = LRef(px, IntT), Op = "+=", Value = VOp(AirVecOp.Count, vt) });
        var vec = new List<MStmt>();
        foreach (var (_, va) in accs) vec.Add(new MDecl { Name = va, Init = VOp(AirVecOp.Zero, vt) });
        vec.Add(new MWhile { Cond = Fits(AirFitsForm.Fits, vt, LRef(px, IntT), end, inclusive: !r.Excl), Body = vbody });
        foreach (var (acc, va) in accs)
        {
            var at = acc.Type.ToAir();
            vec.Add(new MAssign { Target = LRef(acc.CsName, at), Op = "+=", Value = new AirVectorReduce { Op = AirVecReduce.Sum, Vec = vt, Arg = LRef(va, vt), CastTo = at, Type = at } });
        }

        // scalar tail: the ordinary body for the remaining iterations
        ctx.Add("loop");
        var tail = new MFor { Var = f.Locals[0].CsName, Start = LRef(px, IntT), Cmp = r.Excl ? "<" : "<=", End = end, Body = Stmts(f.Body, Tail.None, null) };
        ctx.RemoveAt(ctx.Count - 1);
        return new MBlock
        {
            Body =
            {
                new MDecl { Name = px, TypeCs = "int", Init = start },
                new MIf { Cond = VOp(AirVecOp.IsAccelerated, vt), Then = vec },
                tail,
            },
        };
    }

    static bool LaneType(AType t) => t.Cs is "int" or "long" or "double" or "short" or "sbyte" or "byte";

    bool SimtStmts(List<Stmt> body, Dictionary<LocalSym, string> env, List<MStmt> o, string? active, Dictionary<LocalSym, string> accs, string T)
    {
        var vt = AirVector.Of(AirVector.Prim(T), 0);
        var longs = AirVector.Of(new AirType.Prim(PrimKind.Long), 0);
        foreach (var s in body)
        {
            switch (s)
            {
                case Assign { Declares: { } d } a:
                    {
                        if (!LaneType(d.Type) || VecE(a.Value, env, T, false) is not { } v) return false;
                        string name = Temp("l");
                        o.Add(new MDecl { Name = name, Init = v });
                        env[d] = name;
                        break;
                    }
                case Assign { Target: NameExpr { Sym: LocalSym l } } a when env.TryGetValue(l, out var cur):
                    {
                        if (VecE(a.Value, env, T, false) is not { } v) return false;
                        if (a.Op != "=")
                        {
                            if (a.Op is not ("+=" or "-=" or "*=") && !(a.Op == "/=" && l.Type.Cs == "double")) return false;
                            v = VOp(ArithOp(a.Op[0]), vt, LRef(cur, vt), v);
                        }
                        if (active != null) v = VOp(AirVecOp.ConditionalSelect, vt, LRef(active, vt), v, LRef(cur, vt));
                        o.Add(new MAssign { Target = LRef(cur, vt), Value = v });
                        break;
                    }
                case Assign { Target: NameExpr { Sym: LocalSym acc }, Op: "+=" or "-=" } a when active == null && acc.Type.IsNumeric && LaneType(acc.Type):
                    {
                        if (VecE(a.Value, env, T, false) is not { } v) return false;
                        string va = accs.TryGetValue(acc, out var x) ? x : accs[acc] = Temp("vacc");
                        o.Add(new MAssign { Target = LRef(va, vt), Op = a.Op, Value = v });
                        break;
                    }
                case IfStmt { Conds.Count: 1, Else: null } i when active == null && i.Bodies[0] is [Assign { Target: NameExpr { Sym: LocalSym acc }, Op: "+=" or "-=" } a] && !env.ContainsKey(acc) && LaneType(acc.Type):
                    {
                        if (VecE(i.Conds[0], env, T, true) is not { } m || VecE(a.Value, env, T, false) is not { } v) return false;
                        string va = accs.TryGetValue(acc, out var x) ? x : accs[acc] = Temp("vacc");
                        o.Add(new MAssign { Target = LRef(va, vt), Op = a.Op, Value = VOp(AirVecOp.ConditionalSelect, vt, m, v, VOp(AirVecOp.Zero, vt)) });
                        break;
                    }
                case WhileStmt w when active == null:
                    {
                        if (VecE(w.Cond, env, T, true) is not { } m) return false;
                        string act = Temp("act");
                        o.Add(new MDecl { Name = act, Init = m });
                        var inner = new List<MStmt>();
                        if (!SimtStmts(w.Body, env, inner, act, accs, T)) return false;
                        if (VecE(w.Cond, env, T, true) is not { } m2) return false;
                        inner.Add(new MAssign { Target = LRef(act, vt), Value = VOp(AirVecOp.BitwiseAnd, vt, LRef(act, vt), m2) });
                        o.Add(new MWhile
                        {
                            // until every lane's mask is clear
                            Cond = new MUnary { Op = AirUnOp.Not, E = VOp(AirVecOp.EqualsAll, longs, VOp(AirVecOp.AsVectorInt64, longs, LRef(act, vt)), VOp(AirVecOp.Zero, longs)), Type = BoolT },
                            Body = inner,
                        });
                        break;
                    }
                default:
                    return false;
            }
        }
        return true;
    }
}
