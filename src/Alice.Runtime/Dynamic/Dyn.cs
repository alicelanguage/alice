using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// The dispatcher behind `Any` (dynamic) values on the IL backend (ADR 0033): no Microsoft.CSharp call-site binder.
/// Every dynamic operation in a program has its own static <see cref="DynSite"/>; the first call at a site resolves the
/// member by reflection for the receiver's (and arguments') runtime types and caches a thunk, later calls with the same
/// types run the thunk directly (a type compare per argument, then a <see cref="MethodInvoker"/> call: no allocation
/// except boxing value-type arguments and results).
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Only public members are ever touched, on any type, Alice-compiled or not (security audit: no back door to
/// private state; `instance_variable_get` is the explicit, `[Ambient]` reflection API).</item>
/// <item>A name is tried as the compiler spelled it (`total_area`, `empty_q`), then in .NET spelling (`TotalArea`,
/// `IsEmpty`) via <see cref="Names.Dotnet"/>, so a .NET object answers its Alice spelling.</item>
/// <item>Overloads are chosen by the arguments' runtime types: an exact type, then a reference conversion, then implicit
/// numeric widening (Int32 -> Int64 -> Float64), then Object; nil fits any reference or nullable parameter. Ties go to
/// the more specific signature; a real tie is an <see cref="AmbiguousMatchException"/>.</item>
/// <item>Operators on numbers follow Alice, as statically typed code does: binary numeric promotion, checked integer
/// arithmetic, floored `/` and `%` on Int32/Int64 (-7 / 2 == -4) and floored `%` on floats; other operands use their
/// public `op_*` operators. (The C# backend's `dynamic` truncates instead: docs/production.md.)</item>
/// <item>Unknown members fall back to `define_method` bodies and then to `method_missing(name, args)`, like `send`.</item>
/// </list>
/// Dynamic sites are a compile error under `native = true` and on wasm (AL0232); the trimming annotations below keep
/// the reflected members of statically known types, and a JIT build keeps everything.
/// </remarks>
public static partial class Dyn
{
    const DynamicallyAccessedMemberTypes Members =
        DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields;

    const string TrimJustification = "Any (dynamic) dispatch reflects over runtime types by design; AL0232 rejects it under NativeAOT.";

    // ---- entry points (one per DynKind); the fixed-arity overloads let IL callers pass arguments without an array

    /// <summary>`target.name(args)`.</summary>
    public static object? Call(ref DynSite site, object? target, ReadOnlySpan<object?> args)
    {
        if (target == null) return site.Safe ? null : throw Nil(site, "method");
        var t = target.GetType();
        var e = site.Find(t, null, args) ?? Resolve(ref site, t, null, target, args);
        return e.Thunk.Invoke(target, args);
    }

    public static object? Call(ref DynSite site, object? target) => Call(ref site, target, ReadOnlySpan<object?>.Empty);

    public static object? Call(ref DynSite site, object? target, object? a0) => Call(ref site, target, new ReadOnlySpan<object?>(ref a0));

    public static object? Call(ref DynSite site, object? target, object? a0, object? a1)
    {
        Args2 b = default;
        b[0] = a0;
        b[1] = a1;
        ReadOnlySpan<object?> s = b;
        return Call(ref site, target, s);
    }

    public static object? Call(ref DynSite site, object? target, object? a0, object? a1, object? a2)
    {
        Args3 b = default;
        b[0] = a0;
        b[1] = a1;
        b[2] = a2;
        ReadOnlySpan<object?> s = b;
        return Call(ref site, target, s);
    }

    /// <summary>`target.name`: a property, a public field, a zero-argument method.</summary>
    public static object? GetMember(ref DynSite site, object? target)
    {
        if (target == null) return site.Safe ? null : throw Nil(site, "method or property");
        var t = target.GetType();
        var e = site.Find(t, null, default) ?? Resolve(ref site, t, null, target, default);
        return e.Thunk.Invoke(target, default);
    }

    /// <summary>`target.name = value`.</summary>
    public static void SetMember(ref DynSite site, object? target, object? value)
    {
        if (target == null) { if (site.Safe) return; throw Nil(site, "setter"); }
        var t = target.GetType();
        var args = new ReadOnlySpan<object?>(ref value);
        var e = site.Find(t, null, args) ?? Resolve(ref site, t, null, target, args);
        e.Thunk.Invoke(target, args);
    }

    /// <summary>`target[args]`.</summary>
    public static object? Index(ref DynSite site, object? target, ReadOnlySpan<object?> args)
    {
        if (target == null) return site.Safe ? null : throw Nil(site, "indexer");
        var t = target.GetType();
        var e = site.Find(t, null, args) ?? Resolve(ref site, t, null, target, args);
        return e.Thunk.Invoke(target, args);
    }

    public static object? Index(ref DynSite site, object? target, object? i0) => Index(ref site, target, new ReadOnlySpan<object?>(ref i0));

    /// <summary>`target[args] = value`; <paramref name="argsAndValue"/> holds the index arguments, then the value.</summary>
    public static void SetIndex(ref DynSite site, object? target, ReadOnlySpan<object?> argsAndValue)
    {
        if (target == null) { if (site.Safe) return; throw Nil(site, "indexer"); }
        var t = target.GetType();
        var e = site.Find(t, null, argsAndValue) ?? Resolve(ref site, t, null, target, argsAndValue);
        e.Thunk.Invoke(target, argsAndValue);
    }

    public static void SetIndex(ref DynSite site, object? target, object? i0, object? value)
    {
        Args2 b = default;
        b[0] = i0;
        b[1] = value;
        ReadOnlySpan<object?> s = b;
        SetIndex(ref site, target, s);
    }

    /// <summary>`left op right` (the operator is <see cref="DynSite.Op"/>).</summary>
    public static object? BinaryOp(ref DynSite site, object? left, object? right)
    {
        if (Operators.Primitive(site.Op, left, right, out var r)) return r;
        var lt = left?.GetType() ?? typeof(NilType);
        var rt = right?.GetType() ?? typeof(NilType);
        var args = new ReadOnlySpan<object?>(ref right);
        var e = site.Find(lt, rt, default) ?? ResolveOperator(ref site, lt, rt, left, right);
        return e.Thunk.Invoke(left, args);
    }

    /// <summary>`op operand`.</summary>
    public static object? UnaryOp(ref DynSite site, object? operand)
    {
        if (Operators.PrimitiveUnary(site.Op, operand, out var r)) return r;
        if (operand == null) throw new InvalidOperationException($"operator `{Operators.Spelling(site.Op)}` is not defined for nil");
        var t = operand.GetType();
        var e = site.Find(t, null, default) ?? ResolveUnary(ref site, t, operand);
        return e.Thunk.Invoke(operand, default);
    }

    /// <summary>A dynamic value into a static slot of type <typeparamref name="T"/>: implicit conversions for a Convert
    /// site (numeric widening, nil to a reference or nullable type, user `op_Implicit`), explicit ones for a Cast site.</summary>
    public static T Convert<T>(ref DynSite site, object? value)
    {
        if (value is T t) return t;
        return (T)ConvertSlow(ref site, Reflectable(typeof(T)), value)!;
    }

    /// <summary>`target.(args)`: invokes a delegate.</summary>
    public static object? Invoke(ref DynSite site, object? target, ReadOnlySpan<object?> args)
    {
        if (target == null) return site.Safe ? null : throw new NullReferenceException("cannot call nil");
        var t = target.GetType();
        var e = site.Find(t, null, args) ?? ResolveInvoke(ref site, t, args);
        return e.Thunk.Invoke(target, args);
    }

    public static object? Invoke(ref DynSite site, object? target) => Invoke(ref site, target, ReadOnlySpan<object?>.Empty);

    public static object? Invoke(ref DynSite site, object? target, object? a0) => Invoke(ref site, target, new ReadOnlySpan<object?>(ref a0));

    public static object? Invoke(ref DynSite site, object? target, object? a0, object? a1)
    {
        Args2 b = default;
        b[0] = a0;
        b[1] = a1;
        ReadOnlySpan<object?> s = b;
        return Invoke(ref site, target, s);
    }

    [InlineArray(2)] struct Args2 { object? _e; }
    [InlineArray(3)] struct Args3 { object? _e; }

    /// <summary>The runtime "type" of a nil operand in operator caches.</summary>
    sealed class NilType;

    // ---- resolution (cache misses)

    /// <summary>The trimming boundary: a runtime type is reflected over as if its public members were annotated.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2068", Justification = TrimJustification)]
    [return: DynamicallyAccessedMembers(Members)]
    static Type Reflectable(Type t) => t;

    static DynEntry Resolve(ref DynSite site, Type rt, Type? t2, object target, ReadOnlySpan<object?> args)
    {
        var t = Reflectable(rt);
        var thunk = site.Kind switch
        {
            DynKind.Call => ResolveCall(in site, t, target, args),
            DynKind.GetMember => ResolveGet(in site, t, target),
            DynKind.SetMember => ResolveSet(in site, t, args[0]),
            DynKind.GetIndex => ResolveIndex(in site, t, args, set: false),
            DynKind.SetIndex => ResolveIndex(in site, t, args, set: true),
            _ => throw new InvalidOperationException("dynamic site of kind " + site.Kind + " used as a member site"),
        };
        var e = new DynEntry(t, t2, args, thunk);
        site.Add(e);
        return e;
    }

    static DynThunk ResolveCall(in DynSite site, [DynamicallyAccessedMembers(Members)] Type t, object target, ReadOnlySpan<object?> args)
    {
        var cands = Candidates.Methods(t, site.Name, site.DotnetName);
        if (Overloads.Pick(cands, args, site.Name, t) is { } m) return MethodThunk.For(m);
        if (Prelude.DefinedMethodFor(t, site.Name) is { } d) return new DefinedThunk(d);
        if (Candidates.MethodMissing(t) is { } mm) return new MissingThunk(mm, site.Name);
        throw Undefined("method", site.Name, t, args, cands);
    }

    static DynThunk ResolveGet(in DynSite site, [DynamicallyAccessedMembers(Members)] Type t, object target)
    {
        if (Candidates.Property(t, site.Name, site.DotnetName) is { GetMethod: { IsPublic: true } g }) return MethodThunk.For(g);
        if (Candidates.Field(t, site.Name, site.DotnetName) is { } f) return new FieldGetThunk(f);
        var cands = Candidates.Methods(t, site.Name, site.DotnetName);
        if (Overloads.Pick(cands, default, site.Name, t) is { } m) return MethodThunk.For(m);
        if (Prelude.DefinedMethodFor(t, site.Name) is { } d) return new DefinedThunk(d);
        if (Candidates.MethodMissing(t) is { } mm) return new MissingThunk(mm, site.Name);
        throw Undefined("method or property", site.Name, t, default, cands);
    }

    static DynThunk ResolveSet(in DynSite site, [DynamicallyAccessedMembers(Members)] Type t, object? value)
    {
        if (Candidates.Property(t, site.Name, site.DotnetName) is { } p)
        {
            if (p.SetMethod is { IsPublic: true } s) return MethodThunk.For(s);
            throw new MissingMemberException($"`{site.Name}` of {Alice(t)} is read-only");
        }
        if (Candidates.Field(t, site.Name, site.DotnetName) is { } f)
        {
            if (f.IsInitOnly || f.IsLiteral) throw new MissingMemberException($"`{site.Name}` of {Alice(t)} is read-only");
            return new FieldSetThunk(f);
        }
        // the Alice setter `def name=(v)` compiles to __set_name
        var cands = Candidates.Methods(t, "__set_" + site.Name, "__set_" + site.Name);
        if (Overloads.Pick(cands, new ReadOnlySpan<object?>(ref value), site.Name + "=", t) is { } m) return MethodThunk.For(m);
        throw new MissingMemberException($"undefined setter `{site.Name}=` for {Alice(t)}");
    }

    static DynThunk ResolveIndex(in DynSite site, [DynamicallyAccessedMembers(Members)] Type t, ReadOnlySpan<object?> args, bool set)
    {
        int n = set ? args.Length - 1 : args.Length;
        if (t.IsArray)
        {
            if (t.GetArrayRank() != n) throw new ArgumentException($"{Alice(t)} needs {t.GetArrayRank()} indices, got {n}");
            return new ArrayThunk(t.GetElementType()!, set);
        }
        var cands = new List<MethodInfo>();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == n && (set ? p.SetMethod : p.GetMethod) is { IsPublic: true } acc) cands.Add(acc);
        if (Overloads.Pick(cands, args, set ? "[]=" : "[]", t) is { } m) return MethodThunk.For(m);
        throw Undefined(set ? "indexer setter `[]=`" : "indexer `[]`", null, t, args, cands);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = TrimJustification)]
    static DynEntry ResolveOperator(ref DynSite site, Type lt, Type rt, object? left, object? right)
    {
        var op = site.Op;
        var cands = new List<MethodInfo>();
        foreach (var name in Operators.MethodNames(op))
        {
            if (lt != typeof(NilType)) Candidates.AddStatic(cands, lt, name, 2);
            if (rt != typeof(NilType) && rt != lt) Candidates.AddStatic(cands, rt, name, 2);
            if (cands.Count > 0) break;   // op_CheckedAddition before op_Addition (checked context, ADR 0013)
        }
        Args2 b = default;
        b[0] = left;
        b[1] = right;
        ReadOnlySpan<object?> both = b;
        var m = Overloads.Pick(cands, both, Operators.Spelling(op), lt);
        DynThunk thunk;
        if (m != null) thunk = MethodThunk.For(m.Value, receiverIsArg0: true);
        else if (op is DynOp.Eq or DynOp.Ne) thunk = new EqualityThunk(op == DynOp.Ne);
        else if (op == DynOp.Add && (lt == typeof(string) || rt == typeof(string))) thunk = ConcatThunk.Instance;
        else throw new InvalidOperationException($"operator `{Operators.Spelling(op)}` is not defined for {AliceOrNil(lt)} and {AliceOrNil(rt)}");
        var e = new DynEntry(lt, rt, default, thunk);
        site.Add(e);
        return e;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = TrimJustification)]
    static DynEntry ResolveUnary(ref DynSite site, Type t, object operand)
    {
        var cands = new List<MethodInfo>();
        foreach (var name in Operators.MethodNames(site.Op))
        {
            Candidates.AddStatic(cands, t, name, 1);
            if (cands.Count > 0) break;
        }
        object? arg = operand;
        if (Overloads.Pick(cands, new ReadOnlySpan<object?>(ref arg), Operators.Spelling(site.Op), t) is not { } m)
            throw new InvalidOperationException($"operator `{Operators.Spelling(site.Op)}` is not defined for {Alice(t)}");
        var e = new DynEntry(t, null, default, MethodThunk.For(m, receiverIsArg0: true));
        site.Add(e);
        return e;
    }

    static DynEntry ResolveInvoke(ref DynSite site, Type rt, ReadOnlySpan<object?> args)
    {
        var t = Reflectable(rt);
        if (!typeof(Delegate).IsAssignableFrom(t) || t.GetMethod("Invoke") is not { } inv)
            throw new MissingMethodException($"{Alice(t)} is not callable: `.()` needs a function value (a lambda or a method reference)");
        if (inv.GetParameters().Length != args.Length)
            throw new ArgumentException($"function {Alice(t)} takes {inv.GetParameters().Length} arguments, called with {args.Length}");
        if (Overloads.Pick([inv], args, "()", t) is not { } dm)
            throw new ArgumentException($"function {Alice(t)} cannot take ({ArgList(args)})");
        var e = new DynEntry(t, null, args, MethodThunk.For(dm));
        site.Add(e);
        return e;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = TrimJustification)]
    static object? ConvertSlow(ref DynSite site, [DynamicallyAccessedMembers(Members)] Type to, object? value)
    {
        bool cast = site.Kind == DynKind.Cast;
        if (value == null)
        {
            if (!to.IsValueType || Nullable.GetUnderlyingType(to) != null) return null;
            throw new InvalidCastException($"cannot convert nil to {Alice(to)}");
        }
        var target = Nullable.GetUnderlyingType(to) ?? to;
        if (target.IsInstanceOfType(value)) return value;
        if (Numbers.TryConvert(value, target, cast, out var n)) return n;
        if (cast && target.IsEnum && Numbers.TryConvert(value, Enum.GetUnderlyingType(target), true, out var raw)) return Enum.ToObject(target, raw!);
        var t = value.GetType();
        var e = site.Find(t, null, default);
        if (e == null)
        {
            var cands = new List<MethodInfo>();
            foreach (var name in cast ? new[] { "op_Implicit", "op_Explicit" } : ["op_Implicit"])
            {
                Candidates.AddConversions(cands, t, name, target);
                Candidates.AddConversions(cands, target, name, target);
            }
            var arg = new ReadOnlySpan<object?>(ref value);
            if (Overloads.Pick(cands, arg, "conversion", t) is not { } m)
                throw new InvalidCastException($"cannot convert {Alice(t)} to {Alice(to)}{(cast ? "" : "; use `.as(" + Alice(to) + ")` for an explicit conversion")}");
            e = new DynEntry(t, null, default, MethodThunk.For(m, receiverIsArg0: true));
            site.Add(e);
        }
        return e.Thunk.Invoke(value, default);
    }

    // ---- messages

    static NullReferenceException Nil(in DynSite site, string what) => new($"undefined {what} `{site.Name}` for nil");

    static MissingMemberException Undefined(string what, string? name, Type t, ReadOnlySpan<object?> args, List<MethodInfo> cands)
    {
        string head = name != null ? $"undefined {what} `{name}` for {Alice(t)}" : $"{Alice(t)} has no {what}";
        string called = args.Length == 0 ? "" : $" (called with {ArgList(args)})";
        string has = cands.Count == 0 ? "" : "; " + Alice(t) + " has " + string.Join(", ", cands.Select(Signature));
        return what == "method" ? new MissingMethodException(head + called + has) : new MissingMemberException(head + called + has);
    }

    internal static string ArgList(ReadOnlySpan<object?> args)
    {
        var parts = new string[args.Length];
        for (int i = 0; i < args.Length; i++) parts[i] = args[i] == null ? "nil" : Alice(args[i]!.GetType());
        return string.Join(", ", parts);
    }

    internal static string Signature(MethodInfo m) => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => Alice(p.ParameterType))) + ")";

    static string AliceOrNil(Type t) => t == typeof(NilType) ? "nil" : Alice(t);

    /// <summary>Alice's name of a .NET type in messages: Int32, Float64, Bool, String, Array(Int32).</summary>
    internal static string Alice(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } u) return Alice(u) + "?";
        if (t.IsArray) return "Array(" + Alice(t.GetElementType()!) + ")";
        return Type.GetTypeCode(t) switch
        {
            TypeCode.Boolean => "Bool",
            TypeCode.Double => "Float64",
            TypeCode.Single => "Float32",
            TypeCode.SByte => "Int8",
            TypeCode.Byte => "UInt8",
            TypeCode.Int16 => "Int16",
            TypeCode.UInt16 => "UInt16",
            TypeCode.Int32 => "Int32",
            TypeCode.UInt32 => "UInt32",
            TypeCode.Int64 => "Int64",
            TypeCode.UInt64 => "UInt64",
            _ when t.IsGenericType => t.Name[..t.Name.IndexOf('`')] + "(" + string.Join(", ", t.GetGenericArguments().Select(Alice)) + ")",
            _ => t.Name,
        };
    }

    internal static Exception Unwrap(TargetInvocationException e)
    {
        if (e.InnerException is { } inner) ExceptionDispatchInfo.Capture(inner).Throw();
        return e;
    }
}

/// <summary>Public members by name, per type, cached for the process (megamorphic sites resolve from here).</summary>
internal static class Candidates
{
    static readonly ConcurrentDictionary<(Type, string), MethodInfo[]> methods = new();

    public static List<MethodInfo> Methods([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type t, string name, string dotnet)
    {
        if (!methods.TryGetValue((t, name), out var found))
        {
            found = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                // generic method definitions too: Overloads infers their type arguments from the runtime arguments
                .Where(m => m.Name == name && (!m.ContainsGenericParameters || m.IsGenericMethodDefinition && !m.DeclaringType!.ContainsGenericParameters) && !m.IsSpecialName).ToArray();
            methods.TryAdd((t, name), found);
        }
        if (found.Length == 0 && dotnet != name) return Methods(t, dotnet, dotnet);
        return [.. found];
    }

    public static PropertyInfo? Property([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type t, string name, string dotnet)
    {
        foreach (var n in name == dotnet ? [name] : new[] { name, dotnet })
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.Name == n && p.GetIndexParameters().Length == 0) return p;
        return null;
    }

    public static FieldInfo? Field([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type t, string name, string dotnet) =>
        t.GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? (dotnet != name ? t.GetField(dotnet, BindingFlags.Public | BindingFlags.Instance) : null);

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Dynamic dispatch reflects over runtime types by design.")]
    public static MethodInfo? MethodMissing(Type t) => t.GetMethod("method_missing", BindingFlags.Public | BindingFlags.Instance, [typeof(string), typeof(object?[])]);

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Dynamic dispatch reflects over runtime types by design.")]
    public static void AddStatic(List<MethodInfo> into, Type t, string name, int arity)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            if (m.Name == name && m.GetParameters().Length == arity && !m.ContainsGenericParameters && !into.Contains(m)) into.Add(m);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Dynamic dispatch reflects over runtime types by design.")]
    public static void AddConversions(List<MethodInfo> into, Type t, string name, Type to)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == name && m.ReturnType == to && m.GetParameters().Length == 1 && !into.Contains(m)) into.Add(m);
    }
}
