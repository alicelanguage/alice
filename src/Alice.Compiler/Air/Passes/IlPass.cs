using System.Reflection;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Air.Passes;

/// <summary>State shared by the IL lowering passes of one body: fresh temporaries and what was left unlowered.</summary>
public sealed class IlLowerContext
{
    int tempN;

    /// <summary>Constructs the passes could not lower (missing types, `await foreach`...): the IL backend reports them.</summary>
    public readonly List<string> Unsupported = new();

    /// <summary>Tuple element names of lowered tuple-typed locals (pre-order, null = unnamed) for PDB TupleElementNames.</summary>
    public readonly Dictionary<string, IReadOnlyList<string?>> TupleLocalNames = new();

    /// <summary>A fresh local name; `__il` never collides with the lowerer's `__hintN` temporaries.</summary>
    public string Temp(string hint) => $"__il{hint}{++tempN}";
}

/// <summary>
/// Base of the IL-only lowering passes: the shared <see cref="MirRewriter"/> traversal (post-order), plus statement
/// replacement. <see cref="Lower(MStmt)"/> may return the statements that replace a statement; they are spliced into
/// the enclosing list once its owner is visited, so a pass never revisits its own output. Expressions are rewritten
/// through <see cref="LowerExpr"/>, inside lambda bodies and <see cref="AirSeq"/> blocks too.
/// </summary>
public abstract class IlPass : MirRewriter, IMirPass
{
    protected readonly IlLowerContext Ctx;
    readonly Dictionary<MStmt, List<MStmt>> replaced = new(ReferenceEqualityComparer.Instance);

    protected IlPass(IlLowerContext? ctx) => Ctx = ctx ?? new IlLowerContext();

    public abstract string Name { get; }

    public new void Run(List<MStmt> body)
    {
        Before(body);
        Stmts(body);
        Splice(body);
    }

    /// <summary>Called once before the traversal (SwitchLower marks chain heads here).</summary>
    protected virtual void Before(List<MStmt> body) { }

    /// <summary>The replacement of a statement whose children are already lowered, or null to keep it.</summary>
    protected virtual List<MStmt>? Lower(MStmt s) => null;

    /// <summary>The replacement of an expression whose children are already lowered.</summary>
    protected virtual MExpr LowerExpr(MExpr e) => e;

    protected sealed override void Visit(MStmt s)
    {
        if (s is AirSwitch sw)
        {
            sw.Value = Expr(sw.Value);
            foreach (var c in sw.Cases) { Stmts(c); Splice(c); }
            if (sw.Default != null) { Stmts(sw.Default); Splice(sw.Default); }
        }
        foreach (var b in Bodies(s)) Splice(b);
        if (Lower(s) is { } rep)
        {
            foreach (var r in rep)
                if (r.Span == null && r.Line == 0) { if (s.Span != null) r.Span = s.Span; else r.Line = s.Line; }
            replaced[s] = rep;
        }
    }

    protected sealed override MExpr Rewrite(MExpr e)
    {
        switch (e)
        {
            case AirSeq q:
                Stmts(q.Pre);
                Splice(q.Pre);
                q.Value = Expr(q.Value);
                break;
            case AirAs a:
                a.E = Expr(a.E);
                break;
            case MLambda { Body: { } b }:
                Splice(b);
                break;
        }
        return LowerExpr(e);
    }

    void Splice(List<MStmt>? list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (!replaced.Remove(list[i], out var rep)) continue;
            list.RemoveAt(i);
            list.InsertRange(i, rep);
            i += rep.Count - 1;
        }
    }

    /// <summary>The statement lists directly owned by <paramref name="s"/>.</summary>
    protected static IEnumerable<List<MStmt>> Bodies(MStmt s)
    {
        switch (s)
        {
            case MIf i:
                yield return i.Then;
                if (i.Else != null) yield return i.Else;
                break;
            case MWhile w: yield return w.Body; break;
            case MFor f: yield return f.Body; break;
            case MForeach fe: yield return fe.Body; break;
            case MTry t:
                yield return t.Body;
                foreach (var c in t.Catches) yield return c.Body;
                if (t.Finally != null) yield return t.Finally;
                break;
            case MBlock b: yield return b.Body; break;
            case MUsing u: yield return u.Body; break;
            case AirSwitch sw:
                foreach (var c in sw.Cases) yield return c;
                if (sw.Default != null) yield return sw.Default;
                break;
        }
    }

    /// <summary>Applies <paramref name="f"/> to the expressions <paramref name="s"/> evaluates itself (not its bodies).</summary>
    protected static void MapRoots(MStmt s, Func<MExpr, MExpr> f)
    {
        switch (s)
        {
            case MDecl d: if (d.Init != null) d.Init = f(d.Init); break;
            case MDeconstruct dc: dc.Value = f(dc.Value); break;
            case MAssign a: a.Target = f(a.Target); a.Value = f(a.Value); break;
            case MExprStmt es: es.E = f(es.E); break;
            case MIf i: i.Cond = f(i.Cond); break;
            case MWhile w: w.Cond = f(w.Cond); break;
            case MFor fo: fo.Start = f(fo.Start); fo.End = f(fo.End); break;
            case MForeach fe: fe.Coll = f(fe.Coll); break;
            case MReturn r: if (r.Value != null) r.Value = f(r.Value); break;
            case MThrow t: t.Value = f(t.Value); break;
            case MBlock b: if (b.Lock != null) b.Lock = f(b.Lock); break;
            case MUsing u: u.Value = f(u.Value); break;
            case AirSwitch sw: sw.Value = f(sw.Value); break;
        }
    }

    protected void Unsupported(MStmt? s, string why) => Ctx.Unsupported.Add(s is { Line: > 0 } ? $"line {s.Line}: {why}" : why);

    // ---- node builders ----

    protected static AirLocalRef Local(string name, AirType? t) => new(name, t);

    protected static MDecl Decl(string name, AirType t, MExpr? init) => new() { Name = name, TypeCs = t.ToCs(), Init = init };

    protected static MCall Call(MExpr? target, string name, AirType? type, params MExpr[] args) =>
        new() { Target = target, Name = name, Args = [.. args], Type = type };

    protected static MMember Member(MExpr target, string name, AirType? type) => new() { Target = target, Name = name, Type = type };

    protected static MExprStmt Do(MExpr e) => new() { E = e };

    static readonly Alice.Core.Text.Regex Ident = new(@"\A@?[A-Za-z_][A-Za-z0-9_]*\Z");

    /// <summary>A plain identifier (a local or parameter name).</summary>
    protected static bool IsIdent(string s) => Ident.IsMatch(s) && s is not ("true" or "false" or "null" or "this" or "base" or "default");

    /// <summary>Reading it twice has no effect and gives the same value: locals, parameters, literals.</summary>
    protected static bool IsSimple(MExpr e) =>
        e is AirLocalRef { Narrowed: false } or AirParamRef or AirConst or AirNull || e.GetType() == typeof(MCode) && e is MCode c && (IsIdent(c.Text) || IlTypes.IntLiteral(c.Text) != null);

    /// <summary>A second read of a simple expression: a fresh node, so the tree never shares one.</summary>
    protected static MExpr Reread(MExpr e) => e switch
    {
        AirLocalRef l => new AirLocalRef(l.Name, l.Type, l.Narrowed),
        AirParamRef p => new AirParamRef(p.Name, p.Type),
        AirConst k => new AirConst(k.Kind, k.Literal, k.Type),
        AirNull => new AirNull(),
        MCode c when c.GetType() == typeof(MCode) => new MCode(c.Text) { Type = c.Type },
        _ => e,
    };

    /// <summary>A literal: `nil` for null, else int, long, bool, char or string.</summary>
    protected static MExpr Const(object? v) => v == null ? new AirNull() : AirConst.Of(v);
}

/// <summary>Type facts the lowering passes need, from <see cref="AirType"/> and reflection.</summary>
public static class IlTypes
{
    public static readonly AirType Int = new AirType.Prim(PrimKind.Int);
    public static readonly AirType Bool = new AirType.Prim(PrimKind.Bool);
    public static readonly AirType Str = new AirType.Prim(PrimKind.String);
    public static readonly AirType Obj = new AirType.Prim(PrimKind.Object);
    public static readonly AirType Void = new AirType.Prim(PrimKind.Void);

    public static bool Known(AirType? t) => t != null && t is not AirType.TypeParam && t.ToCs() != "var";

    /// <summary>True for value types, false for reference types, null when unknown (type parameters, Unknown).</summary>
    public static bool? IsValueType(AirType? t)
    {
        if (t == null || !Known(t)) return null;
        if (t.Nullable) return IsValueType(t with { Nullable = false }) == true ? true : false;
        return t switch
        {
            AirType.Prim p => p.Kind is not (PrimKind.String or PrimKind.Object or PrimKind.Dynamic or PrimKind.Null or PrimKind.Void),
            AirType.ClrRef c => c.Clr?.IsValueType,
            AirType.AliceType a => a.Sym.Kind is "struct" or "enum",
            AirType.Tuple => true,
            AirType.Array => false,
            AirType.Pointer or AirType.FnPtr => true,
            _ => null,
        };
    }

    /// <summary>A Nullable&lt;T&gt; of a known value type.</summary>
    public static bool IsNullableValue(AirType? t) => t is { Nullable: true } && IsValueType(t with { Nullable = false }) == true;

    public static bool IsIntegral(AirType? t) =>
        t is AirType.Prim { Nullable: false } p && p.Kind is PrimKind.Int or PrimKind.Long or PrimKind.Short or PrimKind.Byte
            or PrimKind.SByte or PrimKind.UShort or PrimKind.UInt or PrimKind.Char;

    /// <summary>The value of an integer or char literal (AirConst or C# text), null otherwise.</summary>
    public static long? IntLiteral(MExpr e) => e switch
    {
        AirConst { Kind: AirConstKind.Int or AirConstKind.Char } k => IntLiteral(k.Literal),
        AirConst => null,
        MCode c when c.GetType() == typeof(MCode) => IntLiteral(c.Text),
        _ => null,
    };

    public static long? IntLiteral(string text)
    {
        if (text.Length == 3 && text[0] == '\'' && text[2] == '\'' && text[1] != '\\') return text[1];
        string t = text.EndsWith('L') ? text[..^1] : text;
        return t.Length > 0 && t.All(ch => char.IsAsciiDigit(ch) || ch == '-') && long.TryParse(t, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : null;
    }

    /// <summary>The value of a string literal without escapes (AirConst or C# text), null otherwise.</summary>
    public static string? StringLiteral(MExpr e) => e switch
    {
        AirConst { Kind: AirConstKind.String, Literal: var lit } when !lit.Contains('\\') => lit,
        MCode { Text: ['"', .. var body, '"'] } c when c.GetType() == typeof(MCode) && !body.Contains('\\') && !body.Contains('"') => body,
        _ => null,
    };

    /// <summary>The reflection type of a known CLR type (open definition when its arguments are Alice types).</summary>
    public static Type? Clr(AirType? t) => t switch
    {
        AirType.ClrRef c when !t.Nullable => c.Clr,
        AirType.Prim p when !t.Nullable => p.Kind switch
        {
            PrimKind.String => typeof(string),
            PrimKind.Object => typeof(object),
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// The AIR type of a reflection type seen through <paramref name="owner"/>: generic parameters of the owner's
    /// definition map to its arguments (List&lt;T&gt;.Enumerator of a List(Point) is List&lt;Point&gt;.Enumerator).
    /// </summary>
    public static AirType? FromClr(Type t, AirType.ClrRef? owner)
    {
        if (t.IsGenericParameter)
        {
            if (owner == null || t.DeclaringMethod != null) return null;
            int k = t.GenericParameterPosition;
            return k < owner.Args.Count ? owner.Args[k] : null;
        }
        if (t.IsByRef) return FromClr(t.GetElementType()!, owner);
        if (t.IsArray) return t.GetArrayRank() == 1 && FromClr(t.GetElementType()!, owner) is { } el ? new AirType.Array(el) : null;
        if (Nullable.GetUnderlyingType(t) is { } u) return FromClr(u, owner) is { } ua ? ua with { Nullable = true } : null;
        if (t.IsGenericType || t.IsNested && t.DeclaringType!.IsGenericType)
        {
            var args = new List<AirType>();
            foreach (var a in t.GetGenericArguments())
                if (FromClr(a, owner) is { } aa) args.Add(aa);
                else return null;
            var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
            var clr = t.ContainsGenericParameters ? def : t;
            if (def.IsNested && def.DeclaringType!.IsGenericType) return new AirType.ClrRef(Spell(def, args), clr, []);
            return new AirType.ClrRef(Spell(def, []), clr, args);
        }
        var r = ClrTypes.FromClr(t).ToAir();
        return Known(r) ? r : null;
    }

    /// <summary>C# spelling of a generic definition with <paramref name="args"/> (outer type's first, as the CLR orders them).</summary>
    static string Spell(Type def, IReadOnlyList<AirType> args)
    {
        string name = def.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        int outerN = def.IsNested ? def.DeclaringType!.GetGenericArguments().Length : 0;
        string prefix = def.IsNested ? Spell(def.DeclaringType!, args.Take(outerN).ToList()) + "." : "global::" + (def.Namespace != null ? def.Namespace + "." : "");
        var own = args.Skip(outerN).ToList();
        return prefix + name + (own.Count > 0 ? "<" + string.Join(", ", own.Select(a => a.ToCs())) + ">" : "");
    }

    public static MethodInfo? Method(Type t, string name, int argc) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc && !m.IsGenericMethodDefinition)
        ?? (t.IsInterface ? t.GetInterfaces().Select(i => Method(i, name, argc)).FirstOrDefault(m => m != null) : null);

    public static Type? Property(Type t, string name) =>
        t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.PropertyType
        ?? (t.IsInterface ? t.GetInterfaces().Select(i => i.GetProperty(name)?.PropertyType).FirstOrDefault(p => p != null) : null);
}
