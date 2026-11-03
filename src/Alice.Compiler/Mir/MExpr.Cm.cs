namespace Alice.Compiler.Mir;

/// <summary>
/// Connection-Machine primitive nodes (ADR 0039, IL form ADR 0040 section 3): the communication steps the shape
/// matcher (Lowering/Lowerer.Cm.cs) found in a counted loop, as calls into <c>Alice.Runtime.Cm</c>. Both printers
/// spell them as the same runtime call (Backend/Printer.Cm.cs, Backend/Il/AirToIl.Cm.cs), so the C# backend is the
/// differential oracle of the IL one. Every array operand is a whole array; <see cref="Count"/> elements from index 0
/// take part (spans <c>[0, Count)</c>). <see cref="Elem"/> is the element type the kernel is instantiated at;
/// <see cref="Ordered"/> is true when the result equals the serial left fold bit for bit (every integer kernel, and
/// any kernel the matcher only emits in index order); false for float kernels under the fixed-chunking contract.
/// Children are frozen: the MIR passes never saw them as code (the C# they replace was a loop over the same reads).
/// </summary>
public abstract class AirCmExpr : AirParExpr
{
    public AirType Elem = AirLeaf.Int;
    public bool Ordered = true;
    public MExpr Count = null!;
}

/// <summary>An associative operator of a CM kernel (mirrors <c>Alice.Runtime.CmOp</c>).</summary>
public enum AirCmOp { Add, Mul, And, Or, Xor, Min, Max }

/// <summary>
/// `Cm.InclusiveScan&lt;T&gt;(src[0, n), dst[0, n), op)` or `ExclusiveScan`: <see cref="Dst"/> may be <see cref="Src"/>
/// (the fused form scans in place after the map pass wrote e(i) into the destination).
/// </summary>
public sealed class AirScan : AirCmExpr
{
    public AirCmOp Op;
    public bool Inclusive = true;
    public MExpr Src = null!;
    public MExpr Dst = null!;
    /// <summary>The element expression is not a plain array read: a map pass materialized it first.</summary>
    public bool Fused;

    public override IEnumerable<MExpr> Children() => [Src, Dst, Count];
}

/// <summary>`Cm.SegmentedScan&lt;T&gt;(src[0, n), flags[0, n), dst[0, n), op)`: the scan restarts where a flag is set.</summary>
public sealed class AirSegmented : AirCmExpr
{
    public AirCmOp Op;
    public MExpr Src = null!;
    public MExpr Flags = null!;
    public MExpr Dst = null!;

    public override IEnumerable<MExpr> Children() => [Src, Flags, Dst, Count];
}

/// <summary>
/// Combining send `target[idx[i]] += vals[i]`: `Cm.Histogram&lt;T&gt;(idx[0, n), target)` when <see cref="Vals"/> is
/// null (every value 1), else `Cm.ScatterAdd&lt;T&gt;(target, idx[0, n), vals[0, n))`. <see cref="IdxElem"/> is Int32 or
/// UInt32 (UInt32 indices are reinterpreted: one at or above 2^31 is out of range either way).
/// </summary>
public sealed class AirCombine : AirCmExpr
{
    public MExpr Target = null!;
    public MExpr Idx = null!;
    public MExpr? Vals;
    public AirType IdxElem = AirLeaf.Int;
    public bool Fused;

    public override IEnumerable<MExpr> Children() => Vals != null ? [Target, Idx, Vals, Count] : [Target, Idx, Count];
}

/// <summary>`Cm.Compact&lt;T&gt;(src, flags[0, n), dst)` (an Int32: how many were kept). <see cref="Src"/> has exactly
/// <see cref="Count"/> elements (the matcher guards it).</summary>
public sealed class AirCompact : AirCmExpr
{
    public MExpr Src = null!;
    public MExpr Flags = null!;
    public MExpr Dst = null!;

    public override IEnumerable<MExpr> Children() => [Src, Flags, Dst, Count];
}

/// <summary>
/// `Cm.Permute&lt;T&gt;(src, perm[0, n), dst)`: `dst[perm[i]] = src[i]`. <see cref="Proof"/> says why the targets are
/// distinct and in range (AL0249); "runtime" means the kernel checks and the matcher falls back to the serial loop
/// when the check fails, so the inferred form never raises where the loop would not.
/// </summary>
public sealed class AirPermute : AirCmExpr
{
    public MExpr Src = null!;
    public MExpr Perm = null!;
    public MExpr Dst = null!;
    public string Proof = "runtime";

    public override IEnumerable<MExpr> Children() => [Src, Perm, Dst, Count];
}

/// <summary>`!Object.ReferenceEquals(w, r)`: a written and a read array of a CM shape are different objects (the
/// kernels' guard; the IL backend emits it as `ceq` on the references).</summary>
public sealed class AirCmDistinct : AirCmExpr
{
    public MExpr W = null!;
    public MExpr R = null!;

    public override IEnumerable<MExpr> Children() => [W, R];
}
