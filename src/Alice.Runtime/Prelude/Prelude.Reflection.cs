using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Alice.Runtime;

/// <summary>
/// Ruby runtime reflection: instance_variable_get/set, instance_variables, methods, public_send.
/// Alice instance variables are fields (auto-property backing fields map to `@name`); lookups are cached.
/// Reflection sees what Alice code could see, never more (security audit F4):
/// <list type="bullet">
/// <item>every instance field of a type compiled from Alice ([AliceType]); only public fields of any other type
/// (the runtime's own, the BCL's, a NuGet package's), so private invariants such as a tensor's shape stay private;</item>
/// <item>readonly fields are read, never written;</item>
/// <item>an array held by a readonly or foreign field comes back as a copy.</item>
/// </list>
/// </summary>
public static partial class Prelude
{
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    // lazy: initialized on first use, not when a program first touches Prelude (StartupTests)
    static class FieldCacheHolder { public static readonly ConcurrentDictionary<(Type, string), FieldInfo?> Value = new(); }

    static string IvarName(FieldInfo f) =>
        f.Name.StartsWith('<') && f.Name.Contains(">k__BackingField") ? f.Name[1..f.Name.IndexOf('>')] : f.Name.TrimStart('@');

    static bool IsAliceType(Type t) => t.IsDefined(typeof(AliceTypeAttribute), false);

    /// <summary>The fields of one type (not its bases) that are ivars: all of an Alice type, public ones otherwise.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Ruby reflection is opt-in.")]
    static FieldInfo[] IvarFields(Type t) =>
        t.GetFields((IsAliceType(t) ? Inst : BindingFlags.Instance | BindingFlags.Public) | BindingFlags.DeclaredOnly);

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Ruby reflection is opt-in.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    static FieldInfo? Ivar(Type t, string name) => FieldCacheHolder.Value.GetOrAdd((t, name.TrimStart('@')), FindIvar);

    [UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "Ruby reflection is opt-in.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    static FieldInfo? FindIvar((Type, string) k)
    {
        for (var ty = k.Item1; ty != null; ty = ty.BaseType)
            foreach (var f in IvarFields(ty))
                if (IvarName(f) == k.Item2) return f;
        return null;
    }

    [Ambient]
    public static object? instance_variable_get(this object o, string name)
    {
        var f = Ivar(o.GetType(), name) ?? throw new MissingFieldException(o.GetType().Name, name);
        var v = f.GetValue(o);
        // the caller may not write through what it may not assign: a readonly or foreign array is handed out as a copy
        return v is Array a && (f.IsInitOnly || f.DeclaringType is not { } dt || !IsAliceType(dt)) ? a.Clone() : v;
    }

    [Ambient]
    public static object? instance_variable_set<T>(this object o, string name, T value)
    {
        var f = Ivar(o.GetType(), name) ?? throw new MissingFieldException(o.GetType().Name, name);
        if (f.IsInitOnly || f.IsLiteral) throw new FieldAccessException($"{o.GetType().Name}.@{IvarName(f)} is readonly");
        object? v = value is IConvertible && f.FieldType != typeof(object) && f.FieldType.IsPrimitive ? Convert.ChangeType(value, f.FieldType, System.Globalization.CultureInfo.InvariantCulture) : value;
        f.SetValue(o, v);
        return value;
    }

    [Ambient]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    public static string[] instance_variables(this object o)
    {
        var names = new List<string>();
        var chain = new List<Type>();
        for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType) chain.Insert(0, t);
        foreach (var t in chain)
            foreach (var f in IvarFields(t).OrderBy(f => f.MetadataToken))
                names.Add("@" + IvarName(f));
        return names.Distinct().ToArray();
    }

    [Ambient]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    public static string[] methods(this object o) =>
        o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).Distinct().ToArray();

    [Ambient]
    public static dynamic? public_send(this object o, string name, params object?[] args) => send(o, name, args);
}

/// <summary>
/// Marks a type compiled from Alice source (the binder adds it to every class, struct and record): its instance
/// fields are Alice ivars, reachable by `instance_variable_get/set`. Fields of any other type are reachable only
/// when public.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = true)]
public sealed class AliceTypeAttribute : Attribute;

/// <summary>
/// A runtime entry point with ambient authority (reflection, dynamic dispatch, I/O, process): `safe def` (AL0212)
/// rejects every call of it.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
public sealed class AmbientAttribute : Attribute;

/// <summary>Alice blocks/Func values as CLR event handlers; the same source delegate maps to the same handler (so -= works).</summary>
public static class EventAdapters
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Delegate, Delegate> Cache = new();

    public static TD Handler<TD>(Delegate f, Func<Delegate, TD> make) where TD : Delegate =>
        f is TD d ? d : (TD)Cache.GetValue(f, k => make(k));
}
