using Alice.Compiler.Semantics;

namespace Alice.Compiler.Mir;

/// <summary>Built-in types with a C# keyword (plus the type of the `nil` literal).</summary>
public enum PrimKind { Void, Bool, Char, SByte, Byte, Short, UShort, Int, UInt, Long, ULong, Float, Double, Decimal, String, Object, Dynamic, Null }

/// <summary>How a <see cref="AirType.ByRef"/> passes its referent.</summary>
public enum RefKind { In, Out, Ref }

/// <summary>
/// The type of an AIR node (ADR 0032). A closed sum: structure instead of C# text, so the C# printer, the IL
/// emitter and the wasm emitter read the same facts. <see cref="ToCs"/> spells it exactly as <see cref="AType.Cs"/>
/// does. Lists compare by reference: compare types with <see cref="Same"/>.
/// </summary>
public abstract record AirType
{
    /// <summary>`T?`: a nullable reference or a Nullable&lt;T&gt; value type.</summary>
    public bool Nullable { get; init; }

    private protected AirType() { }

    /// <summary>The C# spelling, identical to the <see cref="AType.Cs"/> it was converted from.</summary>
    public string ToCs() => Nullable ? Core() + "?" : Core();

    private protected abstract string Core();

    public bool Same(AirType o) => ToCs() == o.ToCs();

    public sealed override string ToString() => ToCs();

    /// <summary>Not inferred by the binder (prints `var`; the C# backend lets Roslyn infer it).</summary>
    public static readonly AirType Unknown = new UnknownType();

    public sealed record Prim(PrimKind Kind) : AirType
    {
        private protected override string Core() => Kind == PrimKind.Null ? "object" : Kind.ToString().ToLowerInvariant();
    }

    /// <summary>A .NET type: <paramref name="Name"/> is its C# name without type arguments (global::System.Collections.Generic.List).
    /// <paramref name="Clr"/> is the reflection type (open when the arguments are Alice types), null when the
    /// type is known only by name.</summary>
    public sealed record ClrRef(string Name, Type? Clr, IReadOnlyList<AirType> Args) : AirType
    {
        private protected override string Core() => Args.Count == 0 ? Name : Name + "<" + string.Join(", ", Args.Select(a => a.ToCs())) + ">";
    }

    /// <summary>A type declared in Alice source (class, struct, enum, sum-type variant, SoA store...).</summary>
    public sealed record AliceType(TypeSym Sym, IReadOnlyList<AirType> Args) : AirType
    {
        private protected override string Core() => Args.Count == 0 ? Sym.Type.Cs : Sym.Type.Cs + "<" + string.Join(", ", Args.Select(a => a.ToCs())) + ">";
    }

    public sealed record TypeParam(string Name) : AirType
    {
        private protected override string Core() => Name;
    }

    public sealed record Array(AirType Elem, int Rank = 1) : AirType
    {
        private protected override string Core() => Elem.ToCs() + "[" + new string(',', Rank - 1) + "]";
    }

    public sealed record ByRef(AirType Elem, RefKind Kind) : AirType
    {
        private protected override string Core() => Kind.ToString().ToLowerInvariant() + " " + Elem.ToCs();
    }

    public sealed record Pointer(AirType Elem) : AirType
    {
        private protected override string Core() => Elem.ToCs() + "*";
    }

    /// <summary>`delegate* unmanaged[Cdecl]&lt;int, void&gt;`; <paramref name="CallConv"/> is "" for a managed pointer.</summary>
    public sealed record FnPtr(IReadOnlyList<AirType> Params, AirType Ret, string CallConv) : AirType
    {
        private protected override string Core() =>
            "delegate*" + (CallConv.Length > 0 ? " " + CallConv : "") + "<" + string.Join(", ", Params.Append(Ret).Select(a => a.ToCs())) + ">";
    }

    /// <summary>A C# tuple `(int, string)`; <paramref name="Names"/> are the Alice element names when declared.</summary>
    public sealed record Tuple(IReadOnlyList<AirType> Items, IReadOnlyList<string>? Names) : AirType
    {
        private protected override string Core() =>
            "(" + string.Join(", ", Items.Select((t, i) => Names == null ? t.ToCs() : t.ToCs() + " " + Binder.Mangle(Names[i]))) + ")";
    }

    sealed record UnknownType : AirType
    {
        private protected override string Core() => "var";
    }
}

/// <summary>The one conversion from binder types to AIR types.</summary>
public static class AirTypes
{
    static readonly Dictionary<string, PrimKind> Keywords = new()
    {
        ["void"] = PrimKind.Void, ["bool"] = PrimKind.Bool, ["char"] = PrimKind.Char, ["sbyte"] = PrimKind.SByte,
        ["byte"] = PrimKind.Byte, ["short"] = PrimKind.Short, ["ushort"] = PrimKind.UShort, ["int"] = PrimKind.Int,
        ["uint"] = PrimKind.UInt, ["long"] = PrimKind.Long, ["ulong"] = PrimKind.ULong, ["float"] = PrimKind.Float,
        ["double"] = PrimKind.Double, ["decimal"] = PrimKind.Decimal, ["string"] = PrimKind.String,
        ["object"] = PrimKind.Object, ["dynamic"] = PrimKind.Dynamic,
    };

    /// <summary>
    /// The AIR form of a binder type; <c>t.ToAir().ToCs() == t.Cs</c> always holds. A spelling the structure does
    /// not reproduce (today: `Callback` function pointers, whose AType keeps no arguments) stays a by-name
    /// <see cref="AirType.ClrRef"/> without arguments.
    /// </summary>
    public static AirType ToAir(this AType t)
    {
        string cs = t.Cs;
        bool q = cs.EndsWith('?');
        string bare = q ? cs[..^1] : cs;
        var core = Core(t, bare);
        string c = core.ToCs();
        if (c == cs) return core;
        if (q && c == bare) return core with { Nullable = true };
        return new AirType.ClrRef(bare, t.Clr, []) { Nullable = q };
    }

    static AirType Core(AType t, string bare)
    {
        switch (t.Kind)
        {
            case "unknown": return AirType.Unknown;
            case "null": return new AirType.Prim(PrimKind.Null);
            case "param": return new AirType.TypeParam(bare);
            case "array" when t.Elem != null: return new AirType.Array(t.Elem.ToAir());
            case "tuple" when bare.StartsWith('('): return new AirType.Tuple(Args(t.Args), t.TupleNames);
            case "func":
                {
                    var ps = t.Args.Count > 0 && t.Args[^1].IsVoid ? t.Args.Take(t.Args.Count - 1) : t.Args;
                    return new AirType.ClrRef(BaseName(bare), t.Clr, Args(ps));
                }
        }
        if (Keywords.TryGetValue(bare, out var k)) return new AirType.Prim(k);
        if (bare.EndsWith('*') && !bare.StartsWith("delegate*"))
            return new AirType.Pointer(t.Clr is { IsPointer: true } p ? ClrTypes.FromClr(p.GetElementType()!).ToAir() : new AirType.ClrRef(bare[..^1], null, []));
        if (t.Sym != null) return new AirType.AliceType(t.Sym, Args(t.Args));
        return new AirType.ClrRef(t.Args.Count > 0 ? BaseName(bare) : bare, t.Clr ?? t.OpenClr, Args(t.Args));
    }

    static string BaseName(string cs) => cs.IndexOf('<') is int lt and > 0 ? cs[..lt] : cs;

    static List<AirType> Args(IEnumerable<AType> args) => args.Select(ToAir).ToList();
}
