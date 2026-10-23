namespace Alice.Cli.Proof.Z3;

/// <summary>A Z3 term. Terms live as long as their context (Z3_mk_context: no per-term reference counts).</summary>
class Expr(IntPtr h) { public readonly IntPtr H = h; }
sealed class BoolExpr(IntPtr h) : Expr(h);
sealed class BitVecExpr(IntPtr h, uint size) : Expr(h) { public readonly uint SortSize = size; }

enum Status { UNSATISFIABLE, UNKNOWN, SATISFIABLE }

/// <summary>The subset of Z3 `alice prove --smt` uses, named as in Z3's .NET binding.</summary>
sealed class Context : IDisposable
{
    readonly IntPtr c;

    public Context()
    {
        var cfg = Z3Native.MkConfig();
        Z3Native.SetParamValue(cfg, "model", "true");
        c = Z3Native.MkContext(cfg);
        Z3Native.DelConfig(cfg);
        Z3Native.SetErrorHandler(c);
    }

    public void Dispose() => Z3Native.DelContext(c);

    BoolExpr B(IntPtr h) => new(h);
    BitVecExpr V(IntPtr h, uint w) => new(h, w);

    public BoolExpr MkTrue() => B(Z3Native.Op0("Z3_mk_true", c));
    public BoolExpr MkFalse() => B(Z3Native.Op0("Z3_mk_false", c));
    public BoolExpr MkBool(bool v) => v ? MkTrue() : MkFalse();
    public BoolExpr MkBoolConst(string name) => B(Z3Native.Const(c, name, Z3Native.BoolSort(c)));
    public BitVecExpr MkBVConst(string name, uint w) => V(Z3Native.Const(c, name, Z3Native.BvSort(c, w)), w);
    public BitVecExpr MkBV(long v, uint w) => V(Z3Native.Int64(c, v, Z3Native.BvSort(c, w)), w);

    public BoolExpr MkNot(BoolExpr a) => B(Z3Native.Op1("Z3_mk_not", c, a.H));
    public BoolExpr MkAnd(params BoolExpr[] xs) => xs.Length == 0 ? MkTrue() : B(Z3Native.Nary("Z3_mk_and", c, xs.Select(x => x.H).ToArray()));
    public BoolExpr MkOr(params BoolExpr[] xs) => xs.Length == 0 ? MkFalse() : B(Z3Native.Nary("Z3_mk_or", c, xs.Select(x => x.H).ToArray()));
    public BoolExpr MkEq(Expr a, Expr b) => B(Z3Native.Op2("Z3_mk_eq", c, a.H, b.H));
    public Expr MkITE(BoolExpr cond, Expr a, Expr b)
    {
        var h = Z3Native.Op3("Z3_mk_ite", c, cond.H, a.H, b.H);
        return a switch { BitVecExpr v => V(h, v.SortSize), BoolExpr => B(h), _ => new Expr(h) };
    }

    BitVecExpr Bv(string f, BitVecExpr a, BitVecExpr b) => V(Z3Native.Op2(f, c, a.H, b.H), a.SortSize);
    BoolExpr Cmp(string f, BitVecExpr a, BitVecExpr b) => B(Z3Native.Op2(f, c, a.H, b.H));

    public BitVecExpr MkBVAdd(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvadd", a, b);
    public BitVecExpr MkBVSub(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvsub", a, b);
    public BitVecExpr MkBVMul(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvmul", a, b);
    public BitVecExpr MkBVSDiv(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvsdiv", a, b);
    public BitVecExpr MkBVSRem(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvsrem", a, b);
    public BitVecExpr MkBVUDiv(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvudiv", a, b);
    public BitVecExpr MkBVURem(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvurem", a, b);
    public BitVecExpr MkBVAND(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvand", a, b);
    public BitVecExpr MkBVOR(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvor", a, b);
    public BitVecExpr MkBVXOR(BitVecExpr a, BitVecExpr b) => Bv("Z3_mk_bvxor", a, b);
    public BitVecExpr MkBVNeg(BitVecExpr a) => V(Z3Native.Op1("Z3_mk_bvneg", c, a.H), a.SortSize);
    public BitVecExpr MkBVNot(BitVecExpr a) => V(Z3Native.Op1("Z3_mk_bvnot", c, a.H), a.SortSize);

    public BoolExpr MkBVSLT(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvslt", a, b);
    public BoolExpr MkBVSLE(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvsle", a, b);
    public BoolExpr MkBVSGT(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvsgt", a, b);
    public BoolExpr MkBVSGE(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvsge", a, b);
    public BoolExpr MkBVULT(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvult", a, b);
    public BoolExpr MkBVULE(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvule", a, b);
    public BoolExpr MkBVUGT(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvugt", a, b);
    public BoolExpr MkBVUGE(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvuge", a, b);

    public BoolExpr MkBVAddNoOverflow(BitVecExpr a, BitVecExpr b, bool signed) => B(Z3Native.Op2Signed("Z3_mk_bvadd_no_overflow", c, a.H, b.H, signed));
    public BoolExpr MkBVAddNoUnderflow(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvadd_no_underflow", a, b);
    public BoolExpr MkBVSubNoOverflow(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvsub_no_overflow", a, b);
    public BoolExpr MkBVSubNoUnderflow(BitVecExpr a, BitVecExpr b, bool signed) => B(Z3Native.Op2Signed("Z3_mk_bvsub_no_underflow", c, a.H, b.H, signed));
    public BoolExpr MkBVMulNoOverflow(BitVecExpr a, BitVecExpr b, bool signed) => B(Z3Native.Op2Signed("Z3_mk_bvmul_no_overflow", c, a.H, b.H, signed));
    public BoolExpr MkBVMulNoUnderflow(BitVecExpr a, BitVecExpr b) => Cmp("Z3_mk_bvmul_no_underflow", a, b);

    public BitVecExpr MkSignExt(uint i, BitVecExpr a) => V(Z3Native.Ext("Z3_mk_sign_ext", c, i, a.H), a.SortSize + i);
    public BitVecExpr MkZeroExt(uint i, BitVecExpr a) => V(Z3Native.Ext("Z3_mk_zero_ext", c, i, a.H), a.SortSize + i);
    public BitVecExpr MkExtract(uint hi, uint lo, BitVecExpr a) => V(Z3Native.Extract(c, hi, lo, a.H), hi - lo + 1);

    public Solver MkSolver() => new(c);
}

/// <summary>One query: assert, check, read the model.</summary>
sealed class Solver(IntPtr c) : IDisposable
{
    readonly IntPtr s = Z3Native.MkSolver(c);

    public void Dispose() => Z3Native.DelSolver(c, s);
    public uint TimeoutMs { set => Z3Native.SolverTimeout(c, s, value); }
    public void Assert(params BoolExpr[] xs) { foreach (var x in xs) Z3Native.Assert(c, s, x.H); }
    public Status Check() => Z3Native.Check(c, s) switch { -1 => Status.UNSATISFIABLE, 1 => Status.SATISFIABLE, _ => Status.UNKNOWN };
    public string ReasonUnknown => Z3Native.ReasonUnknown(c, s);

    /// <summary>The model's value of `t` after a satisfiable check: a signed decimal for bit vectors.</summary>
    public string Value(Expr t)
    {
        string raw = Z3Native.Eval(c, s, t.H);
        if (t is not BitVecExpr v || !System.Numerics.BigInteger.TryParse(raw, out var u)) return raw;
        var top = System.Numerics.BigInteger.One << (int)(v.SortSize - 1);
        if (u >= top) u -= top << 1;   // two's complement
        return u.ToString();
    }
}
