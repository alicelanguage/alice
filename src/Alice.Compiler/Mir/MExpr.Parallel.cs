namespace Alice.Compiler.Mir;

/// <summary>
/// Parallel and ownership AIR nodes (ADR 0032 step 2; fork-join ADR 0015, owned buffers ADR 0016). Each replaces C#
/// text the lowerer used to build as an opaque <see cref="MCode"/>; its printer (Backend/Printer.Parallel.cs) spells
/// exactly that text, so emitted C# stays byte-identical. Children that were inside the old text are
/// <em>frozen</em>: <see cref="Map"/> does not hand them to the MIR passes (which never saw them), while
/// <see cref="Children"/> still exposes them to MirFacts analyses. Every node prints as an atomic C# operand, as the
/// MCode it replaces did.
/// </summary>
public abstract class AirParExpr : MExpr
{
    /// <summary>Every sub-expression, for analyses (<c>MirFacts.Children</c>).</summary>
    public abstract IEnumerable<MExpr> Children();

    /// <summary>Rewrites the sub-expressions the MIR passes rewrote before (MCall arguments); frozen ones are skipped.</summary>
    public virtual void Map(Func<MExpr, MExpr> f) { }
}

public enum AirParKind { Range, Reduce }

/// <summary>
/// `Par.Range(count, (lo, hi) => kernel[, grain])` or `Par.Reduce&lt;T&gt;(count, kernel, combine[, grain])`: the
/// kernel runs over chunks [lo, hi) of [0, count) on the worker pool; Reduce folds the chunk results with
/// <see cref="Combine"/> (a static lambda). <see cref="Grain"/> is the minimum chunk length (default: runtime's).
/// </summary>
public sealed class AirPar : AirParExpr
{
    public AirParKind Kind;
    /// <summary>The Reduce result type, as C# (`Reduce&lt;T&gt;`).</summary>
    public string? ResultCs;
    public MExpr Count = null!;
    /// <summary>`(lo, hi) => { ... }`; rewritten by the passes like any lambda argument.</summary>
    public MExpr Kernel = null!;
    /// <summary>Reduce only: `static (a, b) => a + b`. Frozen.</summary>
    public MExpr? Combine;
    public int? Grain;

    public override IEnumerable<MExpr> Children() => Combine != null ? [Count, Kernel, Combine] : [Count, Kernel];
    public override void Map(Func<MExpr, MExpr> f) => Kernel = f(Kernel);
}

/// <summary>`count >= min &amp;&amp; Par.Workers > 1[ &amp;&amp; distinct...]`: whether the parallel path pays off.</summary>
public sealed class AirParGuard : AirParExpr
{
    public MExpr Count = null!;
    public int Min;
    /// <summary>Runtime alias checks between a written and a stencil-read array (Lowerer.LoopPar AliasGuards).</summary>
    public List<AirReferenceEqualsGuard> Distinct = new();

    public override IEnumerable<MExpr> Children() => Distinct.Prepend(Count);
}

/// <summary>`!Object.ReferenceEquals(w, r)`: true when the written and the read array are different objects.</summary>
public sealed class AirReferenceEqualsGuard : AirParExpr
{
    public MExpr W = null!;
    public MExpr R = null!;

    public override IEnumerable<MExpr> Children() => [W, R];
}

public enum AirProbeKind
{
    /// <summary>`i == start + 1`: the top of the second iteration (the first ran cold).</summary>
    Second,
    /// <summary>`Stopwatch.GetTimestamp()`.</summary>
    Timestamp,
    /// <summary>
    /// `k >= 2 &amp;&amp; (k &amp; (k - 1)) == 0 &amp;&amp; Par.Offload(t0, k - 1, count - k)` with k = i - start: at
    /// iterations 2, 4, 8, ... whether the projected rest pays for a parallel dispatch.
    /// </summary>
    Offload,
}

/// <summary>The timing probe of a serial loop that may offload its rest to the parallel helper (Lowerer.LoopPar).</summary>
public sealed class AirParProbe : AirParExpr
{
    public AirProbeKind Kind;
    public MExpr? I, Start, T0, Count;

    public override IEnumerable<MExpr> Children() => new[] { I, Start, T0, Count }.OfType<MExpr>();
}

/// <summary>`Par.Fork(() => a, () => b[, () => c])` (ADR 0015): the branches run in parallel, a tuple of results.</summary>
public sealed class AirForkJoin : AirParExpr
{
    public List<MExpr> Branches = new();

    public override IEnumerable<MExpr> Children() => Branches;
    public override void Map(Func<MExpr, MExpr> f)
    {
        for (int i = 0; i < Branches.Count; i++) Branches[i] = f(Branches[i]);
    }
}

/// <summary>`Par.Forkable`: true while the fork depth allows another fork.</summary>
public sealed class AirForkable : AirParExpr
{
    public override IEnumerable<MExpr> Children() => [];
}

/// <summary>`Own&lt;T&gt;.Rent(len[, fill])` (ADR 0016): a pooled buffer for an owned local; <see cref="PoolCs"/> is the pool type.</summary>
public sealed class AirRent : AirParExpr
{
    public string PoolCs = "";
    public List<MExpr> Args = new();

    public override IEnumerable<MExpr> Children() => Args;
    public override void Map(Func<MExpr, MExpr> f)
    {
        for (int i = 0; i < Args.Count; i++) Args[i] = f(Args[i]);
    }
}

/// <summary>`Own&lt;T&gt;.Release(x)` (ADR 0016): returns an owned local's buffer to its pool.</summary>
public sealed class AirRelease : AirParExpr
{
    public string PoolCs = "";
    public MExpr Target = null!;

    public override IEnumerable<MExpr> Children() => [Target];
}

// TODO merge into MExpr.cs (common nodes, air-b1)
/// <summary>
/// `x.Length` / `x.Count` of an array, list or SoA store. Deliberately not an <see cref="MMember"/>: the
/// InductionVariables pass bounds a counter by an MMember `.Length` and would rewrite the loop step, changing C#
/// that used to be opaque text. Frozen.
/// </summary>
public sealed class AirLength : AirParExpr
{
    public MExpr Target = null!;
    public string Member = "Length";

    public override IEnumerable<MExpr> Children() => [Target];
}

// TODO merge into MExpr.cs (common nodes, air-b1)
/// <summary>
/// `end - start[ + 1]`, the trip count of a range loop. <see cref="End"/> is parenthesized unless it is atomic or an
/// additive or multiplicative operation (`(c ? a : b) - start`); frozen.
/// </summary>
public sealed class AirTripCount : AirParExpr
{
    public MExpr End = null!;
    public MExpr Start = null!;
    public bool Inclusive;

    public override IEnumerable<MExpr> Children() => [End, Start];
}

// TODO merge into MExpr.cs (common nodes, air-b1)
/// <summary>
/// `base + offset`, a chunk bound (`start + lo`) known to lie in range. Not an <see cref="MBinary"/>: the loop
/// passes would derive affine bounds from it and change the chunk loop. Frozen.
/// </summary>
public sealed class AirOffset : AirParExpr
{
    public MExpr Base = null!;
    public MExpr Offset = null!;

    public override IEnumerable<MExpr> Children() => [Base, Offset];
}

/// <summary>
/// `Math.Max(0, (to) - (from)[ + 1])`: the capacity a fill loop needs (Lowerer.Presize). The bounds are optimized
/// standalone when lowered, as the text form was, and frozen.
/// </summary>
public sealed class AirPresizeCount : AirParExpr
{
    public MExpr From = null!;
    public MExpr To = null!;
    public bool Inclusive;

    public override IEnumerable<MExpr> Children() => [To, From];
}

// TODO merge into MExpr.cs (common leaves, air-b1)
/// <summary>
/// Typed leaves (locals, literals, type names, `default`). They stay <see cref="MCode"/> instances until the passes
/// are ported (ADR 0032 step 4): the passes and several lowerings match leaves as `MCode { Text: ... }`, so a new leaf
/// class would change the emitted C#. The factory is the one place that spells them.
/// </summary>
public static class AirLeaf
{
    public static readonly AirType Int = new AirType.Prim(PrimKind.Int);
    public static readonly AirType Long = new AirType.Prim(PrimKind.Long);
    public static readonly AirType Bool = new AirType.Prim(PrimKind.Bool);
    public static readonly AirType Void = new AirType.Prim(PrimKind.Void);

    /// <summary>A local, parameter, temporary or static field read by name.</summary>
    public static MCode Name(string name, AirType? type = null) => new(name) { Type = type };

    /// <summary>A literal as C# spells it (`0`, `1L`).</summary>
    public static MCode Lit(string text, AirType? type = null) => new(text) { Type = type };

    /// <summary>A type used as a member-access target (`global::System.Math`).</summary>
    public static MCode TypeName(string cs) => new(cs);

    /// <summary>`default` of the given type.</summary>
    public static MCode Default(AirType? type) => new("default") { Type = type };
}
