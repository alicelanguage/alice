using System.Globalization;

namespace Alice.Compiler.Mir;

// Typed AIR leaves (ADR 0032 migration step 2, task D0.4). Each one derives from MCode for now: the ADR 0014 passes
// still match leaf text (MirFacts.IntLiteral, Affine.Identifier, `Target: MCode { Text: "Prelude" }`), so the C#
// spelling is derived from the typed fields once, in the constructor, and the passes and the C# printer keep
// reading Text unchanged (byte-identical output). The fields are get-only so Text cannot drift from them. D0.9 ports
// the passes to the fields and drops the MCode base.
//
// No text escape hatches: every leaf is typed (lead decision 2026-11-02). Names the binder could not resolve in
// this compilation (a type of an unreferenced package, a sibling file's module) lower as by-name AirTypeRef /
// AirLocalRef with an Unknown type, which the AIR verifier rejects for the IL backend.

/// <summary>Literal kinds of <see cref="AirConst"/>; `nil` is <see cref="AirNull"/>.</summary>
public enum AirConstKind { Int, Float, Char, String, Bool }

/// <summary>A literal constant. <see cref="Literal"/> is the value spelled in C# (a string: its escaped body, no quotes).</summary>
public sealed class AirConst : MCode
{
    public AirConstKind Kind { get; }
    public string Literal { get; }
    /// <summary>The CLR value when the constant was built from one (<see cref="Of"/>), else null.</summary>
    public object? Value { get; private init; }

    public AirConst(AirConstKind kind, string literal, AirType? type = null) : base(kind == AirConstKind.String ? "\"" + literal + "\"" : literal)
    {
        Kind = kind;
        Literal = literal;
        Type = type;
    }

    public static AirConst Int(long v) => new(AirConstKind.Int, v.ToString(CultureInfo.InvariantCulture), new AirType.Prim(PrimKind.Int));

    public static AirConst Bool(bool v) => new(AirConstKind.Bool, v ? "true" : "false", new AirType.Prim(PrimKind.Bool));

    /// <summary>A string literal from an already C#-escaped body.</summary>
    public static AirConst Str(string escaped) => new(AirConstKind.String, escaped, new AirType.Prim(PrimKind.String));

    /// <summary>
    /// A constant from a CLR value (int, long, bool, char, string), spelled as C# source: `5`, `5L`, `'\''`,
    /// `"a\"b\n"`. Its type is the value's.
    /// </summary>
    public static AirConst Of(object value, AirType? type = null) => value switch
    {
        int i => new(AirConstKind.Int, i.ToString(CultureInfo.InvariantCulture), type ?? new AirType.Prim(PrimKind.Int)) { Value = value },
        long l => new(AirConstKind.Int, l.ToString(CultureInfo.InvariantCulture) + "L", type ?? new AirType.Prim(PrimKind.Long)) { Value = value },
        bool b => new(AirConstKind.Bool, b ? "true" : "false", type ?? new AirType.Prim(PrimKind.Bool)) { Value = value },
        char ch => new(AirConstKind.Char, "'" + Escape(ch.ToString(), '\'') + "'", type ?? new AirType.Prim(PrimKind.Char)) { Value = value },
        string s => new(AirConstKind.String, Escape(s, '"'), type ?? new AirType.Prim(PrimKind.String)) { Value = value },
        _ => throw new ArgumentException("no AIR constant for " + value.GetType().Name),
    };

    /// <summary>C# escapes for a char or string literal body.</summary>
    public static string Escape(string s, char quote)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s)
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when ch == quote => "\\" + quote,
                _ => ch.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>The kinds of <see cref="Syntax.LitExpr.Kind"/> except `nil`.</summary>
    public static AirConstKind KindOf(string litKind) => litKind switch
    {
        "int" => AirConstKind.Int,
        "float" => AirConstKind.Float,
        "char" => AirConstKind.Char,
        "str" => AirConstKind.String,
        "bool" => AirConstKind.Bool,
        _ => throw new ArgumentException("no AIR literal kind for `" + litKind + "`"),
    };
}

/// <summary>The `nil` literal.</summary>
public sealed class AirNull : MCode
{
    public AirNull() : base("null") => Type = new AirType.Prim(PrimKind.Null);
}

/// <summary>The receiver: `this`, or the `self` parameter of an extension method (a monkey patch of a .NET type).</summary>
public sealed class AirThis : MCode
{
    public bool Extension { get; }

    public AirThis(bool extension = false) : base(extension ? "self" : "this") => Extension = extension;
}

/// <summary>`base`, the receiver of a `super` call.</summary>
public sealed class AirBase : MCode
{
    public AirBase() : base("base") { }
}

/// <summary>A read of a local (or a lowering temp). <see cref="Narrowed"/>: a flow-narrowed Nullable&lt;T&gt; local
/// read through `.Value`.</summary>
public sealed class AirLocalRef : MCode
{
    public string Name { get; }
    public bool Narrowed { get; }

    public AirLocalRef(string name, AirType? type = null, bool narrowed = false) : base(narrowed ? name + ".Value" : name)
    {
        Name = name;
        Narrowed = narrowed;
        Type = type;
    }
}

/// <summary>A read of a method parameter.</summary>
public sealed class AirParamRef : MCode
{
    public string Name { get; }

    public AirParamRef(string name, AirType? type = null) : base(name)
    {
        Name = name;
        Type = type;
    }
}

/// <summary>A field of the current type read by its bare name (C# resolves `this.` or the static owner).</summary>
public sealed class AirFieldRef : MCode
{
    public string Name { get; }

    public AirFieldRef(string name, AirType? type = null) : base(name)
    {
        Name = name;
        Type = type;
    }
}

/// <summary>`arr.Length` of a local array, from the lowering's length aliases (`n = xs.size` never rewritten).</summary>
public sealed class AirArrayLength : MCode
{
    public string Array { get; }

    public AirArrayLength(string array) : base(array + ".Length")
    {
        Array = array;
        Type = new AirType.Prim(PrimKind.Int);
    }

    /// <summary>From the `name.Length` text the lowerer's alias table holds.</summary>
    public static AirArrayLength Of(string alias) =>
        alias.EndsWith(".Length", StringComparison.Ordinal) ? new(alias[..^".Length".Length]) : throw new ArgumentException("not a length alias: " + alias);
}

/// <summary>A type in expression position: the receiver of a static call or member (`Prelude.str`, `Math.ScaleB`).
/// The node's <see cref="MExpr.Type"/> is null: it has no value.</summary>
public sealed class AirTypeRef : MCode
{
    public AirType Ref { get; }

    public AirTypeRef(AirType type) : base(type.ToCs()) => Ref = type;

    /// <summary>A type known only by its C# name (runtime helpers: `Prelude`, `global::System.Math`).</summary>
    public static AirTypeRef Named(string cs) => new(new AirType.ClrRef(cs, null, []));

    /// <summary>The runtime's `Prelude` class, by the short name generated code imports it under.</summary>
    public static AirTypeRef Prelude() => Named("Prelude");
}

/// <summary>A static method used as a delegate value (method group): `sq`, `Geometry.area`.</summary>
public sealed class AirMethodGroup : MCode
{
    public AirType? Owner { get; }
    public string Name { get; }

    public AirMethodGroup(AirType? owner, string name) : base(owner == null ? name : owner.ToCs() + "." + name)
    {
        Owner = owner;
        Name = name;
    }
}

/// <summary>`default(T)`, or the target-typed `default` when <see cref="MExpr.Type"/> is null; <see cref="Forgiving"/>
/// adds `!` (a definitely-assigned-later local of a non-nullable reference type).</summary>
public sealed class AirDefault : MCode
{
    public bool Forgiving { get; }
    /// <summary>Spelled `default` although <see cref="MExpr.Type"/> is known (the declaration gives the type).</summary>
    public bool TargetTyped { get; }

    public AirDefault(AirType? type, bool forgiving = false, bool targetTyped = false)
        : base((type == null || targetTyped ? "default" : "default(" + type.ToCs() + ")") + (forgiving ? "!" : ""))
    {
        Type = type;
        Forgiving = forgiving;
        TargetTyped = targetTyped;
    }
}

/// <summary>`typeof(T)`: a System.Type value.</summary>
public sealed class AirTypeOf : MCode
{
    public AirType Of { get; }

    public AirTypeOf(AirType of) : base("typeof(" + of.ToCs() + ")")
    {
        Of = of;
        Type = new AirType.ClrRef("global::System.Type", typeof(System.Type), []);
    }
}

/// <summary>`sizeof(T)` of an unmanaged type, an Int32.</summary>
public sealed class AirSizeOf : MCode
{
    public AirType Of { get; }

    public AirSizeOf(AirType of) : base("sizeof(" + of.ToCs() + ")")
    {
        Of = of;
        Type = new AirType.Prim(PrimKind.Int);
    }
}

/// <summary>The discard `_` as an assignment target.</summary>
public sealed class AirDiscard : MCode
{
    public AirDiscard() : base("_") { }
}

/// <summary>`yield break` in an iterator method (an expression statement until the state-machine pass owns it).</summary>
public sealed class AirYieldBreak : MCode
{
    public AirYieldBreak() : base("yield break") { }
}

// ---- shared structural nodes of the parallel/ownership lowerings (frozen AirParExpr children; printed by
// Backend/Printer.Parallel.cs, dumped by Mir/MirDump.Parallel.cs) ----

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
/// The shared leaf factory of the parallel/ownership lowerings: types used everywhere, and leaves built as the Core
/// nodes above.
/// </summary>
public static class AirLeaf
{
    public static readonly AirType Int = new AirType.Prim(PrimKind.Int);
    public static readonly AirType Long = new AirType.Prim(PrimKind.Long);
    public static readonly AirType Bool = new AirType.Prim(PrimKind.Bool);
    public static readonly AirType Void = new AirType.Prim(PrimKind.Void);

    /// <summary>A local or lowering temp read by name.</summary>
    public static AirLocalRef Name(string name, AirType? type = null) => new(name, type);

    /// <summary>An integer literal as C# spells it (`0`, `1L`).</summary>
    public static AirConst Lit(string text, AirType? type = null) => new(AirConstKind.Int, text, type);

    /// <summary>A type used as a member-access target (`global::System.Math`).</summary>
    public static AirTypeRef TypeName(string cs) => AirTypeRef.Named(cs);

    /// <summary>The target-typed `default` of the given type.</summary>
    public static AirDefault Default(AirType? type) => new(type, targetTyped: true);
}

/// <summary>
/// A member chain read off a typed head, kept as one leaf (`MASK.Length`, `ARGV.Length`, `Rules.WIDTH`): the
/// passes and lowerings see it as the single leaf it always was.
/// </summary>
public sealed class AirMemberPath : MCode
{
    public MCode Head { get; }
    public IReadOnlyList<string> Members { get; }

    public AirMemberPath(MCode head, IReadOnlyList<string> members, AirType? type = null) : base(head.Text + "." + string.Join(".", members))
    {
        Head = head;
        Members = members;
        Type = type;
    }
}

/// <summary>
/// An expression printed exactly as lowered: the MIR passes do not rewrite inside it (its child is frozen), while
/// analyses still see it. The `gpu for` reduction value: its C# was always fixed before the passes ran.
/// </summary>
public sealed class AirFrozen : AirParExpr
{
    public MExpr E = null!;

    public override IEnumerable<MExpr> Children() => [E];
}

/// <summary>An assignment used as a value (`x ??= v` as an initializer). Frozen children, like the text it replaces.</summary>
public sealed class AirAssignExpr : AirParExpr
{
    public MExpr Target = null!;
    public string Op = "=";
    public MExpr Value = null!;

    public override IEnumerable<MExpr> Children() => [Target, Value];
}
