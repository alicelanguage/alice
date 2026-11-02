using System.Text;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Mir;

// Typed declaration model (ADR 0032 "AirModule/AirType/AirMethod/AirField"; backend plan D1.6). Minimal on purpose:
// the members the C# compiler synthesizes today (records, sum types, derived operators, the IEnumerable bridge,
// auto-properties, default and struct constructors, field initializers, the module initializer), built by
// Air/Passes/Synthesize.cs for the IL backend only. D0.8 grows it into the full declaration model that replaces the
// declaration text of Backend/Emitter.cs. Bodies are ordinary MIR/AIR statement lists.

/// <summary>Accessibility of a synthesized member (CLR: Public, Private, Family, Assembly).</summary>
public enum AirVis { Public, Private, Protected, Internal }

/// <summary>What a method is besides its signature. <see cref="Virtual"/> alone is `virtual newslot`;
/// <see cref="Override"/> is `virtual` reusing the base slot; <see cref="Final"/> adds `final`; operators,
/// accessors and constructors are <see cref="SpecialName"/>.</summary>
[Flags]
public enum AirMethodFlags
{
    None = 0,
    Static = 1,
    Virtual = 2,
    Override = 4,
    Abstract = 8,
    Final = 16,
    SpecialName = 32,
    /// <summary>`.ctor`, or with <see cref="Static"/> the type initializer `.cctor`.</summary>
    Ctor = 64,
    /// <summary>An `init` setter: its return type carries modreq(System.Runtime.CompilerServices.IsExternalInit).</summary>
    InitOnly = 128,
}

/// <summary>The C# rule a synthesized member reproduces (<see cref="None"/>: a user-declared member).</summary>
public enum AirSynth
{
    None,
    // records and sum-type variants: Roslyn's record members, same shapes and formulas
    BackingField, Getter, Setter, PrimaryCtor, CopyCtor, Clone, With, Deconstruct,
    EqualityContract, EqualsObject, EqualsTyped, EqualsBase, GetHashCode, ToString, PrintMembers,
    OpEquality, OpInequality,
    // sum types
    PrivateCtor, Tag, IsVariant, UnitValue,
    // other C# lowering
    DerivedOperator, EnumerableBridge, DefaultCtor, StructCtor, TypeInitializer, ModuleInitializer, ToStringOverride,
}

public enum AirTypeKind { Class, Struct, Interface, Module }

public sealed record AirParamDecl(string Name, AirType Type);

public sealed class AirFieldDecl
{
    public string Name = "";
    public AirType Type = AirType.Unknown;
    public AirVis Vis = AirVis.Private;
    public bool Static;
    public bool ReadOnly;
    /// <summary>Compiler-generated (C# marks backing fields [CompilerGenerated]).</summary>
    public AirSynth Synth;
    /// <summary>The Alice field stored here, when there is one.</summary>
    public FieldSym? Field;
}

public sealed class AirMethodDecl
{
    /// <summary>The CLR name: `.ctor`, `.cctor`, `op_Equality`, `get_x`, `&lt;Clone&gt;$`, `System.Collections.IEnumerable.GetEnumerator`.</summary>
    public string Name = "";
    public AirVis Vis = AirVis.Public;
    public AirMethodFlags Flags;
    public List<AirParamDecl> Params = new();
    public AirType Ret = new AirType.Prim(PrimKind.Void);
    /// <summary>The body; null for an abstract method or for a user method whose body the lowering produces (<see cref="User"/>).</summary>
    public List<MStmt>? Body;
    public AirSynth Synth;
    /// <summary>The user-declared method this declaration names (`def to_s` declared as `ToString`).</summary>
    public MethodSym? User;
    /// <summary>Explicit interface implementation (a MethodImpl row): the interface and the method it implements.</summary>
    public (AirType Interface, string Method)? Implements;

    public bool Is(AirMethodFlags f) => (Flags & f) == f;
}

public sealed class AirPropertyDecl
{
    public string Name = "";
    public AirType Type = AirType.Unknown;
    public bool Static;
    public AirMethodDecl? Get;
    public AirMethodDecl? Set;
    public AirSynth Synth;
}

public sealed class AirTypeDecl
{
    public TypeSym Sym = null!;
    /// <summary>The CLR simple name (a nested type without its enclosing name).</summary>
    public string Name = "";
    public AirTypeKind Kind;
    public bool Sealed, Abstract, ReadOnly, IsRecord;
    /// <summary>No explicit static constructor (C# `beforefieldinit`): true unless a class body or explicit cctor needs exact timing.</summary>
    public bool BeforeFieldInit = true;
    public List<string> TypeParams = new();
    public AirType? Base;
    public List<AirType> Interfaces = new();
    public AirTypeDecl? Enclosing;
    public readonly List<AirFieldDecl> Fields = new();
    public readonly List<AirMethodDecl> Methods = new();
    public readonly List<AirPropertyDecl> Properties = new();
    public readonly List<AirTypeDecl> Nested = new();
    /// <summary>Instance field initializers in declaration order. The IL backend runs them at the start of every user
    /// constructor that does not chain to `this(...)`, before the base constructor call, as C# does. Synthesized
    /// constructors already contain them.</summary>
    public readonly List<MStmt> FieldInits = new();

    /// <summary>The type as used in its own signatures (over its own type parameters).</summary>
    public AirType Self = AirType.Unknown;

    public AirMethodDecl? Method(string name, int arity = -1) => Methods.FirstOrDefault(m => m.Name == name && (arity < 0 || m.Params.Count == arity));
}

/// <summary>Everything synthesized for one program.</summary>
public sealed class AirModuleDecl
{
    /// <summary>Types that got synthesized members, in declaration order (sum-type variants are <see cref="AirTypeDecl.Nested"/>).</summary>
    public readonly List<AirTypeDecl> Types = new();
    /// <summary>`&lt;Module&gt;..cctor`: runs the class bodies in declaration order (`[ModuleInitializer]` in the C# backend). Null when none.</summary>
    public AirMethodDecl? ModuleInitializer;

    /// <summary>The synthesized declaration of an Alice type (also on <see cref="TypeSym.Synth"/>).</summary>
    public AirTypeDecl? For(TypeSym s) => s.Synth;
}

/// <summary>Stable text form of synthesized declarations for tests: one line per member, bodies as <see cref="MirDump"/>.</summary>
public static class AirDeclDump
{
    public static string Of(AirModuleDecl m)
    {
        var sb = new StringBuilder();
        foreach (var t in m.Types) Type(sb, t, 0);
        if (m.ModuleInitializer is { } mi) Method(sb, mi, 0, "<Module>.");
        return sb.ToString();
    }

    public static string Of(AirTypeDecl t)
    {
        var sb = new StringBuilder();
        Type(sb, t, 0);
        return sb.ToString();
    }

    static string Pad(int n) => new(' ', n * 2);

    static void Type(StringBuilder sb, AirTypeDecl t, int ind)
    {
        var mods = new List<string> { t.Kind.ToString().ToLowerInvariant() };
        if (t.IsRecord) mods.Add("record");
        if (t.Abstract) mods.Add("abstract");
        if (t.Sealed) mods.Add("sealed");
        if (t.ReadOnly) mods.Add("readonly");
        if (t.BeforeFieldInit) mods.Add("beforefieldinit");
        string gen = t.TypeParams.Count > 0 ? "<" + string.Join(", ", t.TypeParams) + ">" : "";
        var bases = (t.Base != null ? new List<AirType> { t.Base } : new List<AirType>()).Concat(t.Interfaces).Select(b => b.ToCs()).ToList();
        sb.Append(Pad(ind)).Append("type ").Append(string.Join(" ", mods)).Append(' ').Append(t.Name).Append(gen)
          .Append(bases.Count > 0 ? " : " + string.Join(", ", bases) : "").Append('\n');
        foreach (var f in t.Fields)
            sb.Append(Pad(ind + 1)).Append("field ").Append(f.Vis.ToString().ToLowerInvariant()).Append(f.Static ? " static" : "")
              .Append(f.ReadOnly ? " readonly" : "").Append(' ').Append(f.Type.ToCs()).Append(' ').Append(f.Name).Append(Tag(f.Synth)).Append('\n');
        foreach (var p in t.Properties)
            sb.Append(Pad(ind + 1)).Append("property ").Append(p.Static ? "static " : "").Append(p.Type.ToCs()).Append(' ').Append(p.Name)
              .Append(p.Get != null ? " get=" + p.Get.Name : "").Append(p.Set != null ? " set=" + p.Set.Name : "").Append(Tag(p.Synth)).Append('\n');
        if (t.FieldInits.Count > 0)
        {
            sb.Append(Pad(ind + 1)).Append("field-inits\n");
            Body(sb, t.FieldInits, ind + 2);
        }
        foreach (var m in t.Methods) Method(sb, m, ind + 1, "");
        foreach (var n in t.Nested) Type(sb, n, ind + 1);
    }

    static string Tag(AirSynth s) => s == AirSynth.None ? "" : " [" + s + "]";

    static readonly AirMethodFlags[] Shown =
    [
        AirMethodFlags.Static, AirMethodFlags.Abstract, AirMethodFlags.Virtual, AirMethodFlags.Override,
        AirMethodFlags.Final, AirMethodFlags.SpecialName, AirMethodFlags.InitOnly,
    ];

    static void Method(StringBuilder sb, AirMethodDecl m, int ind, string prefix)
    {
        var mods = new List<string> { m.Vis.ToString().ToLowerInvariant() };
        foreach (var f in Shown)
            if ((m.Flags & f) != 0) mods.Add(f.ToString().ToLowerInvariant());
        string impl = m.Implements is { } i ? " implements " + i.Interface.ToCs() + "." + i.Method : "";
        string user = m.User != null ? " user=" + m.User.Name : "";
        sb.Append(Pad(ind)).Append("method ").Append(string.Join(" ", mods)).Append(' ').Append(m.Ret.ToCs()).Append(' ').Append(prefix).Append(m.Name)
          .Append('(').Append(string.Join(", ", m.Params.Select(p => p.Type.ToCs() + " " + p.Name))).Append(')').Append(impl).Append(user).Append(Tag(m.Synth)).Append('\n');
        if (m.Body != null) Body(sb, m.Body, ind + 1);
    }

    static void Body(StringBuilder sb, List<MStmt> body, int ind)
    {
        foreach (var line in MirDump.Of(body).Split('\n'))
            if (line.Length > 0) sb.Append(Pad(ind)).Append(line).Append('\n');
    }
}
