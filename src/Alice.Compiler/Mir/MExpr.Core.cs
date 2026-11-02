using System.Globalization;

namespace Alice.Compiler.Mir;

// Typed AIR leaves (ADR 0032 migration step 2, task D0.4). Each one derives from MCode for now: the ADR 0014 passes
// still match leaf text (MirFacts.IntLiteral, Affine.Identifier, `Target: MCode { Text: "Prelude" }`), so the C#
// spelling is derived from the typed fields once, in the constructor, and the passes and the C# printer keep
// reading Text unchanged (byte-identical output). The fields are get-only so Text cannot drift from them. D0.9 ports
// the passes to the fields and drops the MCode base.
//
// Binder gaps (count toward the "MCode deleted" gate like AirRawCs; the fix is binder resolution, not a node):
//   AirName sites: Lowerer.Calls.cs `"field" when soa` (SoA store path), Lowerer.Expressions.cs Name() SoA store path,
//   Name() fallback for an unresolved name, Const() ConstPath.OwnerText, Const() unresolved constant path.
//   AirRawCs sites: Lowerer.Statements.cs event-adapter lambda, `gpu for` reduction value (printed before the passes
//   so the kernel stays byte-identical; making it typed is an ADR 0032 follow-up).

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

    public AirDefault(AirType? type, bool forgiving = false) : base((type == null ? "default" : "default(" + type.ToCs() + ")") + (forgiving ? "!" : ""))
    {
        Type = type;
        Forgiving = forgiving;
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

/// <summary>
/// A dotted name the binder left unresolved, emitted as written for C# to resolve (a CLR namespace or type path, an
/// implicit-`this` member of a .NET base type, a name only generated code declares). Not C# code: a name path; the IL
/// backend must reject it (ADR 0032, AirVerifier).
/// </summary>
public sealed class AirName : MCode
{
    public string Path { get; }

    public AirName(string path) : base(path) => Path = path;
}

/// <summary>
/// Escape hatch: C# text no typed node expresses yet. Every construction site carries a comment saying why; the
/// count is tracked per migration batch and must reach zero before the IL backend (ADR 0032).
/// </summary>
public sealed class AirRawCs : MCode
{
    public AirRawCs(string cs) : base(cs) { }
}
