using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Alice.Runtime;

/// <summary>
/// Ruby-style dynamic dispatch (`send`, `respond_to?`). Method lookup is cached per (type, name, arity),
/// so repeated dynamic calls cost one dictionary hit plus a reflection invoke.
/// </summary>
public static partial class Prelude
{
    static readonly ConcurrentDictionary<(Type, string, int), MethodInfo[]> SendCache = new();

    [UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "Dynamic dispatch is opt-in Ruby semantics.")]
    static MethodInfo[] Candidates(Type t, string name, int argc) =>
        SendCache.GetOrAdd((t, name, argc), static k =>
        {
            var r = new List<MethodInfo>();
            foreach (var m in k.Item1.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == k.Item2 && m.GetParameters().Length == k.Item3) r.Add(m);
            var p = k.Item3 == 0 ? k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance) : null;
            if (p?.GetMethod != null) r.Add(p.GetMethod);
            return r.ToArray();
        });

    static MethodInfo? Resolve(Type t, string name, object?[] args)
    {
        foreach (var m in Candidates(t, name, args.Length))
        {
            var ps = m.GetParameters();
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
                ok = args[i] == null ? !ps[i].ParameterType.IsValueType : ps[i].ParameterType.IsInstanceOfType(args[i]);
            if (ok) return m;
        }
        return null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Dynamic dispatch is opt-in Ruby semantics.")]
    public static bool respond_to_q(object? target, string name) =>
        target != null && (target.GetType().GetMember(name).Length > 0 || DefinedMethod(target.GetType(), name) != null);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Delegate> Defined = new();

    /// <summary>Runtime `define_method name { ... }`: callable through send / public_send, visible to respond_to?.</summary>
    public static void define_method(Type owner, string name, Delegate body) => Defined[(owner, name)] = body;

    static Delegate? DefinedMethod(Type t, string name)
    {
        for (var ty = t; ty != null; ty = ty.BaseType)
            if (Defined.TryGetValue((ty, name), out var d)) return d;
        return null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Dynamic dispatch is opt-in Ruby semantics.")]
    public static dynamic? send(object target, string name, params object?[] args)
    {
        var m = Resolve(target.GetType(), name, args);
        if (m != null) return m.Invoke(target, args);
        if (DefinedMethod(target.GetType(), name) is { } dm) return dm.DynamicInvoke(args);
        // Ruby: unknown messages go to method_missing(name, args) when the class defines it
        var mm = target.GetType().GetMethod("method_missing", [typeof(string), typeof(object?[])]);
        if (mm != null) return mm.Invoke(target, [name, args]);
        throw new MissingMethodException(target.GetType().Name, name);
    }
}
