using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Alice.Runtime;

/// <summary>
/// Ruby runtime reflection: instance_variable_get/set, instance_variables, methods, public_send.
/// Alice instance variables are fields (auto-property backing fields map to `@name`); lookups are cached.
/// </summary>
public static partial class Prelude
{
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    // lazy: initialized on first use, not when a program first touches Prelude (StartupTests)
    static class FieldCacheHolder { public static readonly ConcurrentDictionary<(Type, string), FieldInfo?> Value = new(); }

    static string IvarName(FieldInfo f) =>
        f.Name.StartsWith('<') && f.Name.Contains(">k__BackingField") ? f.Name[1..f.Name.IndexOf('>')] : f.Name.TrimStart('@');

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Ruby reflection is opt-in.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    static FieldInfo? Ivar(Type t, string name) => FieldCacheHolder.Value.GetOrAdd((t, name.TrimStart('@')), FindIvar);

    [UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "Ruby reflection is opt-in.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    static FieldInfo? FindIvar((Type, string) k)
    {
        for (var ty = k.Item1; ty != null; ty = ty.BaseType)
            foreach (var f in ty.GetFields(Inst | BindingFlags.DeclaredOnly))
                if (IvarName(f) == k.Item2) return f;
        return null;
    }

    public static object? instance_variable_get(this object o, string name) =>
        (Ivar(o.GetType(), name) ?? throw new MissingFieldException(o.GetType().Name, name)).GetValue(o);

    public static object? instance_variable_set<T>(this object o, string name, T value)
    {
        var f = Ivar(o.GetType(), name) ?? throw new MissingFieldException(o.GetType().Name, name);
        object? v = value is IConvertible && f.FieldType != typeof(object) && f.FieldType.IsPrimitive ? Convert.ChangeType(value, f.FieldType, System.Globalization.CultureInfo.InvariantCulture) : value;
        f.SetValue(o, v);
        return value;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    public static string[] instance_variables(this object o)
    {
        var names = new List<string>();
        var chain = new List<Type>();
        for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType) chain.Insert(0, t);
        foreach (var t in chain)
            foreach (var f in t.GetFields(Inst | BindingFlags.DeclaredOnly).OrderBy(f => f.MetadataToken))
                names.Add("@" + IvarName(f));
        return names.Distinct().ToArray();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Ruby reflection is opt-in.")]
    public static string[] methods(this object o) =>
        o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).Distinct().ToArray();

    public static dynamic? public_send(this object o, string name, params object?[] args) => send(o, name, args);
}

/// <summary>Alice blocks/Func values as CLR event handlers; the same source delegate maps to the same handler (so -= works).</summary>
public static class EventAdapters
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Delegate, Delegate> Cache = new();

    public static TD Handler<TD>(Delegate f, Func<Delegate, TD> make) where TD : Delegate =>
        f is TD d ? d : (TD)Cache.GetValue(f, k => make(k));
}
