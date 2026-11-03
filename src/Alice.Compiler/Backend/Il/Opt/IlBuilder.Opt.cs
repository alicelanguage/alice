using System.Reflection.Metadata;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// il-opt's instruction-level peepholes (ADR 0033 quality targets, item 7), run after IlBuilder's own set
/// (<see cref="IlPeephole"/>) on the same instruction list, then that set again on what they exposed. Every rewrite
/// is local (adjacent instructions, no label in between) and type-agnostic, so it holds for any operand types:
/// <list type="bullet">
/// <item>null tests: `ldnull; cgt.un; brX` (an `x != null` value) is `brX` on the reference itself; `ldnull; ceq;
/// brX` is the inverted branch;</item>
/// <item>constant branches: `ldc.i4 c; brfalse` with c != 0 and `ldc.i4 0; brtrue` never jump (the `&& true` arms of
/// lowered patterns) and disappear; the always-taken forms (`while true`) are `br`;</item>
/// <item>type tests (<see cref="OptTypeTests"/>): `ldnull; stloc p; ld v; isinst T; brfalse L; ld v; castclass T;
/// stloc p` is Roslyn's `isinst T; stloc p; ldloc p; brfalse L`, so RyuJIT keeps one method-table compare per arm and
/// no castclass re-check. The `isinst T; dup; stloc p; brfalse L` form measured 24-50 % slower on symreg (the
/// `dup`ed result is materialized and re-tested) and is not used;</item>
/// <item>compare-branch fusion: `ceq|cgt|clt|cgt.un|clt.un; brtrue` and `ceq; brfalse` are one conditional branch,
/// `ldc.i4 0; ceq; brX` is the inverted `brX` (the remaining `brfalse` forms depend on int/float operands).</item>
/// </list>
/// </summary>
public sealed partial class IlBuilder
{
    partial void OptimizeExtra()
    {
        if (!Opt.IlOpt.On(Opt.IlOptPass.Peephole)) return;
        var sub = Environment.GetEnvironmentVariable("ALICE_IL_PEEP") ?? "";
        for (int round = 0; round < 8; round++)
        {
            bool changed = !sub.Contains("-null") && OptNullTests();
            changed |= !sub.Contains("-const") && OptConstBranches();
            changed |= !sub.Contains("-fuse") && OptCompareBranches();
            changed |= !sub.Contains("-cast") && OptTypeTests();
            if (!changed) break;
            if ((Peephole & IlPeephole.Temps) != 0) Temps();
            if ((Peephole & IlPeephole.Branches) != 0) Branches();
        }
    }

    /// <summary>The previous instruction when only sequence points or scope markers (no label) separate it from <paramref name="i"/>.</summary>
    int PrevOp(int i)
    {
        for (int p = i - 1; p >= 0; p--)
        {
            if (code[p].Kind == IK.Op) return p;
            if (code[p].Kind == IK.Label) return -1;
        }
        return -1;
    }

    bool IsOp(int i, ILOpCode op) => i >= 0 && code[i].Kind == IK.Op && code[i].Op == op;

    Instr Br(ILOpCode op, long target) => new() { Kind = IK.Op, Op = op, I = target };

    bool OptNullTests()
    {
        bool changed = false;
        for (int i = 0; i < code.Count; i++)
        {
            if (!IsOp(i, ILOpCode.Ldnull) || PrefixedAt(i)) continue;
            int j = NextOp(i), k = j < 0 ? -1 : NextOp(j);
            if (k < 0 || code[k].Op is not (ILOpCode.Brtrue or ILOpCode.Brfalse)) continue;
            if (code[j].Op == ILOpCode.Cgt_un) { Remove(i, j); changed = true; i--; continue; }
            if (code[j].Op == ILOpCode.Ceq)
            {
                code[k] = Br(code[k].Op == ILOpCode.Brtrue ? ILOpCode.Brfalse : ILOpCode.Brtrue, code[k].I);
                Remove(i, j); changed = true; i--;
            }
        }
        return changed;
    }

    bool OptConstBranches()
    {
        bool changed = false;
        for (int i = 0; i < code.Count; i++)
        {
            if (!IsOp(i, ILOpCode.Ldc_i4) || PrefixedAt(i)) continue;
            int j = NextOp(i);
            if (j < 0) continue;
            if (code[j].Op is not (ILOpCode.Brtrue or ILOpCode.Brfalse)) continue;
            bool taken = code[j].Op == ILOpCode.Brtrue == (code[i].I != 0);
            if (taken)
            {
                // `while true`: `ldc.i4 1; brtrue L` is `br L` (the code after it is reachable only through labels)
                code[j] = Br(ILOpCode.Br, code[j].I);
                Remove(i); changed = true; i--;
                continue;
            }
            Remove(i, j); changed = true; i--;
        }
        return changed;
    }

    static ILOpCode? Fused(ILOpCode cmp, bool onTrue) => (cmp, onTrue) switch
    {
        (ILOpCode.Ceq, true) => ILOpCode.Beq,
        (ILOpCode.Ceq, false) => ILOpCode.Bne_un,
        (ILOpCode.Cgt, true) => ILOpCode.Bgt,
        (ILOpCode.Clt, true) => ILOpCode.Blt,
        (ILOpCode.Cgt_un, true) => ILOpCode.Bgt_un,
        (ILOpCode.Clt_un, true) => ILOpCode.Blt_un,
        _ => null,
    };

    bool OptCompareBranches()
    {
        bool changed = false;
        for (int i = 0; i < code.Count; i++)
        {
            var c = code[i];
            if (c.Kind != IK.Op || c.Op is not (ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Clt or ILOpCode.Cgt_un or ILOpCode.Clt_un)) continue;
            int j = NextOp(i);
            if (j < 0 || code[j].Op is not (ILOpCode.Brtrue or ILOpCode.Brfalse)) continue;
            bool onTrue = code[j].Op == ILOpCode.Brtrue;
            // `x == 0` as a branch condition: branch on x itself
            int z = PrevOp(i);
            if (c.Op == ILOpCode.Ceq && IsOp(z, ILOpCode.Ldc_i4) && code[z].I == 0 && NextOp(z) == i && !PrefixedAt(z))
            {
                code[j] = Br(onTrue ? ILOpCode.Brfalse : ILOpCode.Brtrue, code[j].I);
                Remove(z, i); changed = true; i = Math.Max(-1, z - 1); continue;
            }
            if (Fused(c.Op, onTrue) is not { } op) continue;
            code[j] = Br(op, code[j].I);
            Remove(i); changed = true; i--;
        }
        return changed;
    }

    /// <summary>The next instruction after <paramref name="i"/>, skipping sequence points (not labels or scopes).</summary>
    int NextOpSeq(int i)
    {
        for (int p = i + 1; p < code.Count; p++)
        {
            if (code[p].Kind == IK.Op) return p;
            if (code[p].Kind != IK.Seq) return -1;
        }
        return -1;
    }

    static bool SameLoad(Instr a, Instr b) => a.Op == b.Op && a.I == b.I && a.Op is ILOpCode.Ldarg or ILOpCode.Ldloc;

    /// <summary>
    /// Type-test patterns as Roslyn shapes `e is T p`: `ldnull; stloc p; ld v; isinst T; brfalse L; ld v; castclass T;
    /// stloc p` becomes `ld v; isinst T; stloc p; ldloc p; brfalse L`. On the false path p held null before and holds
    /// the (null) isinst result now, so every later read of p sees the same value. RyuJIT then does one method-table
    /// compare per arm instead of the isinst compare plus the castclass re-check (symreg `expr_eval` Tier1: 11 compares
    /// to 6, 503 to 356 bytes, the C# build's size). Not the `dup; stloc p; brfalse` form (measured slower, see the summary).
    /// </summary>
    bool OptTypeTests()
    {
        bool changed = false;
        for (int i = 0; i < code.Count; i++)
        {
            if (!IsOp(i, ILOpCode.Ldnull) || PrefixedAt(i)) continue;
            int st = NextOpSeq(i), ld = st < 0 ? -1 : NextOpSeq(st), ii = ld < 0 ? -1 : NextOpSeq(ld), bf = ii < 0 ? -1 : NextOpSeq(ii);
            if (bf < 0 || !IsOp(st, ILOpCode.Stloc) || !IsOp(ii, ILOpCode.Isinst) || !IsOp(bf, ILOpCode.Brfalse)) continue;
            if (code[ld].Op is not (ILOpCode.Ldarg or ILOpCode.Ldloc) || code[ld].Op == ILOpCode.Ldloc && code[ld].I == code[st].I) continue;
            int ld2 = NextOpSeq(bf), cc = ld2 < 0 ? -1 : NextOpSeq(ld2), st2 = cc < 0 ? -1 : NextOpSeq(cc);
            if (st2 < 0 || !SameLoad(code[ld], code[ld2]) || !IsOp(cc, ILOpCode.Castclass) || !IsOp(st2, ILOpCode.Stloc)) continue;
            if (code[st2].I != code[st].I || !(Equals(code[cc].Obj, code[ii].Obj) || code[cc].Obj?.ToString() is { } tn && tn == code[ii].Obj?.ToString())) continue;
            if (PrefixedAt(ld) || PrefixedAt(ld2) || PrefixedAt(cc)) continue;
            var p = code[st];
            var load = new Instr { Kind = IK.Op, Op = ILOpCode.Ldloc, I = p.I };
            // ld v; isinst T; stloc p; ldloc p; brfalse L  (the castclass block and the null store go)
            code.RemoveRange(ld2, st2 - ld2 + 1);
            code.Insert(bf, load);
            code.Insert(bf, p);
            code.RemoveAt(st);
            code.RemoveAt(i);
            RebuildLabels();
            changed = true;
        }
        return changed;
    }
}
