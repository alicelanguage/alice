namespace Alice.Compiler.Mir;

// AIR nodes of allocation-free iteration and the idiom rewrites (Lowerer.Iterators/Fusion/Idioms, ADR 0029/0032).
// C# forms are in Backend/Printer.Iterators.cs, dump forms in Mir/MirDump.Iterators.cs.

// Common leaves (AirConst, AirLocalRef, AirTypeRef, AirDefault...) are in MExpr.Core.cs.

// ---- iterator / idiom nodes ----

/// <summary>Element count of the Int32 range `Lo..Hi` (`Lo...Hi` when <see cref="Exclusive"/>), clamped at 0.</summary>
public sealed class AirRangeLength : MExpr
{
    public MExpr Lo = null!;
    public MExpr Hi = null!;
    public bool Exclusive;
}

/// <summary>
/// `ref d[k]`, inserting default(V) when the key is missing: one hash probe for a counter update
/// (CollectionsMarshal.GetValueRefOrAddDefault). <see cref="Dict"/> may be an alternate lookup.
/// </summary>
public sealed class AirRefOrAddDefault : MExpr
{
    public MExpr Dict = null!;
    public MExpr Key = null!;
}

/// <summary>`ref d[k]` or a null ref when the key is missing (CollectionsMarshal.GetValueRefOrNullRef).</summary>
public sealed class AirRefOrNullRef : MExpr
{
    public MExpr Dict = null!;
    public MExpr Key = null!;
}

/// <summary>A dictionary's alternate lookup keyed by <see cref="KeyTypeCs"/> (string keys probed with a span).</summary>
public sealed class AirAlternateLookup : MExpr
{
    public MExpr Dict = null!;
    public string KeyTypeCs = "global::System.ReadOnlySpan<char>";
}

/// <summary>The ranges of `Str` split at the char `Sep`, without allocating substrings (MemoryExtensions.Split).</summary>
public sealed class AirSpanSplit : MExpr
{
    public MExpr Str = null!;
    public MExpr Sep = null!;
}

/// <summary>A stack buffer of <see cref="Length"/> elements, live for the frame (`stackalloc`).</summary>
public sealed class AirStackBuffer : MExpr
{
    public string ElemCs = "";
    public int Length;
}
