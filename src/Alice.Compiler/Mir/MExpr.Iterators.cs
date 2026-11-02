namespace Alice.Compiler.Mir;

// AIR nodes of allocation-free iteration and the idiom rewrites (Lowerer.Iterators/Fusion/Idioms, ADR 0029/0032).
// C# forms are in Backend/Printer.Iterators.cs, dump forms in Mir/MirDump.Iterators.cs.

// ---- common leaves: TODO merge into MExpr.cs (D0.4 owns them; delete these when it lands) ----

/// <summary>A read (or, as an assignment target, a write) of a local or parameter by its C# name.</summary>
public sealed class AirLocalRef : MExpr
{
    public string Name;
    public AirLocalRef(string name, AirType? type = null) { Name = name; Type = type; }
}

/// <summary>
/// A literal: int, long, bool, char, string or null. <see cref="MExpr.Type"/> defaults to the CLR type of the value.
/// </summary>
public sealed class AirConst : MExpr
{
    public object? Value;

    public AirConst(object? value, AirType? type = null)
    {
        Value = value;
        Type = type ?? value switch
        {
            int => new AirType.Prim(PrimKind.Int),
            long => new AirType.Prim(PrimKind.Long),
            bool => new AirType.Prim(PrimKind.Bool),
            char => new AirType.Prim(PrimKind.Char),
            string => new AirType.Prim(PrimKind.String),
            null => new AirType.Prim(PrimKind.Null),
            _ => null,
        };
    }
}

/// <summary>A type used as the receiver of a static member or call: `global::System.Math`.</summary>
public sealed class AirTypeRef : MExpr
{
    public string TypeCs;
    public AirTypeRef(string typeCs) => TypeCs = typeCs;
}

/// <summary>`default(T)`; with no <see cref="TypeCs"/> the target-typed `default` (`default!` when <see cref="Suppress"/>).</summary>
public sealed class AirDefault : MExpr
{
    public string? TypeCs;
    /// <summary>Null-forgiving: the slot is written before any read (first/min/max accumulators).</summary>
    public bool Suppress;
}

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

/// <summary>The parallel gate: `Size >= Min` and more than one worker. Shared shape with the Parallel lowerings.</summary>
public sealed class AirParGate : MExpr
{
    public MExpr Size = null!;
    public long Min;
}
