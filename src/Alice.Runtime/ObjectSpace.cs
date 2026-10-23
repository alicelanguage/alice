using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Alice.Runtime;

/// <summary>
/// Ruby's class discovery over the program's own types (every loaded assembly compiled against Alice.Runtime):
/// `Controller.subclasses`, `Controller.descendants`, `ObjectSpace.each_object(typeof(Controller)) { |c| }`. Live
/// object enumeration, the other half of Ruby's ObjectSpace, has no safe equivalent on the .NET GC and is not offered.
/// </summary>
public static class ObjectSpace
{
    /// <summary>Classes defined by the program, excluding compiler-generated ones.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Class discovery is opt-in Ruby reflection.")]
    public static Type[] classes()
    {
        var self = typeof(ObjectSpace).Assembly.GetName().Name;
        var r = new List<Type>();
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || a.GetName().Name == self || !a.GetReferencedAssemblies().Any(n => n.Name == self)) continue;
            Type[] ts;
            try { ts = a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in ts)
                if (t.IsClass && !t.Name.StartsWith('<') && !t.Name.StartsWith("__") && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                    r.Add(t);
        }
        return [.. r];
    }

    /// <summary>Calls <paramref name="f"/> for every program class that is <paramref name="kind"/> or derives from it
    /// (excluding <paramref name="kind"/> itself); returns how many there were.</summary>
    public static int each_object(Type kind, Action<Type> f)
    {
        int n = 0;
        foreach (var t in classes())
            if (t != kind && kind.IsAssignableFrom(t)) { f(t); n++; }
        return n;
    }

    public static Type[] subclasses(Type t) => classes().Where(x => x.BaseType == t).ToArray();

    public static Type[] descendants(Type t) => classes().Where(x => x != t && t.IsAssignableFrom(x)).ToArray();
}
