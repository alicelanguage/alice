using System.Reflection;

namespace Alice.Compiler.Semantics;

/// <summary>Reflection view of referenced .NET assemblies: resolves type names and member signatures.</summary>
public sealed class ClrTypes
{
    public static readonly string[] DefaultNamespaces =
        ["System", "System.Collections.Generic", "System.Linq", "System.Text", "System.IO", "System.Threading", "System.Threading.Tasks", "Alice.Runtime"];

    readonly List<Assembly> asms = new();
    readonly Dictionary<string, Type?> cache = new();
    bool allLoaded;

    public ClrTypes(IEnumerable<string>? extraRefs = null)
    {
        foreach (var t in new[] { typeof(object), typeof(List<>), typeof(Enumerable), typeof(Console), typeof(Alice.Runtime.Prelude), typeof(System.Collections.Concurrent.ConcurrentDictionary<,>), typeof(Uri) })
            if (!asms.Contains(t.Assembly)) asms.Add(t.Assembly);
        foreach (var r in extraRefs ?? [])
        {
            try { asms.Add(Assembly.LoadFrom(Path.GetFullPath(r))); } catch (Exception) { }
        }
    }

    void LoadAll()
    {
        if (allLoaded) return;
        allLoaded = true;
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (path.Length == 0) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith("System") && !name.StartsWith("Microsoft") && !name.StartsWith("Alice")) continue;
            try
            {
                var a = Assembly.Load(new AssemblyName(name));
                if (!asms.Contains(a)) asms.Add(a);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Finds a type by Alice/C# name (dotted, optional global::) with the given generic arity.</summary>
    public Type? Find(string name, int arity, IReadOnlyCollection<string> usings)
    {
        string key = name + "`" + arity + "|" + string.Join(",", usings);
        if (cache.TryGetValue(key, out var hit)) return hit;
        Type? r = Search(name, arity, usings);
        if (r == null && !allLoaded)
        {
            LoadAll();
            r = Search(name, arity, usings);
        }
        cache[key] = r;
        return r;
    }

    Type? Search(string name, int arity, IReadOnlyCollection<string> usings)
    {
        if (name.StartsWith("global::")) name = name.Substring(8);
        string suffix = arity > 0 ? "`" + arity : "";
        var cands = new List<string> { name };
        foreach (var ns in usings.Concat(DefaultNamespaces)) cands.Add(ns + "." + name);
        foreach (var c in cands)
        {
            foreach (var a in asms)
            {
                var t = a.GetType(c + suffix, false);
                if (t != null && t.IsPublic) return t;
                int dot = c.LastIndexOf('.');
                if (dot > 0)
                {
                    t = a.GetType(c.Substring(0, dot) + "+" + c.Substring(dot + 1) + suffix, false);
                    if (t != null && (t.IsNestedPublic || t.IsPublic)) return t;
                }
            }
        }
        return null;
    }

    public static AType FromClr(Type t)
    {
        if (t == typeof(void)) return AType.Void;
        foreach (var pr in AType.Prims) if (pr.Clr == t) return pr;
        if (t.IsGenericParameter || t.IsByRef || t.IsPointer && t.GetElementType() is { IsGenericParameter: true }) return AType.Unknown;
        if (t.IsArray && t.GetArrayRank() == 1) return AType.Array(FromClr(t.GetElementType()!));
        if (Nullable.GetUnderlyingType(t) is Type u) return FromClr(u).AsNullable();
        if (t.IsPointer) return new AType("prim", FromClr(t.GetElementType()!).Cs + "*", t);
        string kind = t.IsEnum ? "enum" : t.IsInterface ? "iface" : t.IsValueType ? "struct" : "class";
        if (t.ContainsGenericParameters && !t.IsGenericTypeDefinition) return AType.Unknown;
        var r = new AType(kind, CsName(t), t);
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments()) r.Args.Add(FromClr(a));
        if (r.Args.Any(a => a.IsUnknown)) return AType.Unknown;
        if (t.Name.StartsWith("ValueTuple`")) r.Kind = "tuple";
        return r;
    }

    static string CsName(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        foreach (var pr in AType.Prims) if (pr.Clr == t) return pr.Cs;
        if (t.IsArray) return CsName(t.GetElementType()!) + "[]";
        string name = t.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name.Substring(0, tick);
        string outer = t.IsNested ? CsName(t.DeclaringType!) + "." : "global::" + (t.Namespace != null ? t.Namespace + "." : "");
        if (t.IsNested && t.DeclaringType!.IsGenericType) return "var";
        if (!t.IsGenericType) return outer + name;
        return outer + name + "<" + string.Join(", ", t.GetGenericArguments().Select(CsName)) + ">";
    }

    /// <summary>Close an open generic CLR type with resolved Alice arguments when they are all CLR types.</summary>
    public static Type? Close(Type open, List<AType> args)
    {
        if (!open.IsGenericTypeDefinition) return open;
        if (args.Any(a => a.Clr == null)) return null;
        try { return open.MakeGenericType(args.Select(a => a.Clr!).ToArray()); }
        catch (ArgumentException) { return null; }
    }

    public sealed record Member(string Kind, AType Type, bool IsStatic, MethodInfo? Method = null);

    /// <summary>Instance or static member lookup: property, field, or method with a compatible arity.</summary>
    public static Member? Lookup(Type t, string name, int argc, bool hasArgs, bool wantStatic, List<AType>? args = null)
    {
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (wantStatic ? BindingFlags.Static : BindingFlags.Instance);
        var types = new List<Type> { t };
        if (t.IsInterface) types.AddRange(t.GetInterfaces());
        foreach (var ty in types)
        {
            if (!hasArgs)
            {
                foreach (var pr in ty.GetProperties(flags))
                    if (pr.Name == name && pr.GetIndexParameters().Length == 0) return new Member("prop", FromClr(pr.PropertyType), wantStatic);
                foreach (var f in ty.GetFields(flags))
                    if (f.Name == name) return new Member(f.IsLiteral ? "const" : "field", FromClr(f.FieldType), wantStatic);
            }
            MethodInfo? best = null;
            foreach (var m in ty.GetMethods(flags))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                int req = ps.Count(p => !p.IsOptional && !p.IsDefined(typeof(ParamArrayAttribute)));
                bool variadic = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute));
                if (argc < req || (argc > ps.Length && !variadic)) continue;
                if (best == null || ps.Length == argc) best = m;
            }
            if (best != null)
            {
                var ret = best.ReturnType.ContainsGenericParameters ? InferGeneric(best, args) : FromClr(best.ReturnType);
                return new Member("method", ret, wantStatic, best);
            }
        }
        if (t.IsInterface && !wantStatic) return Lookup(typeof(object), name, argc, hasArgs, false);
        return null;
    }

    /// <summary>Infers a generic method's type arguments from argument types (arrays, generic types, lambdas as Func/Action).</summary>
    static AType InferGeneric(MethodInfo m, List<AType>? args)
    {
        if (args == null || !m.IsGenericMethodDefinition) return AType.Unknown;
        var map = new Dictionary<Type, Type>();
        var ps = m.GetParameters();
        for (int i = 0; i < ps.Length && i < args.Count; i++) Match(ps[i].ParameterType, args[i], map);
        var targs = m.GetGenericArguments();
        if (!targs.All(map.ContainsKey)) return AType.Unknown;
        try
        {
            var closed = m.MakeGenericMethod(targs.Select(g => map[g]).ToArray());
            return closed.ReturnType.ContainsGenericParameters ? AType.Unknown : FromClr(closed.ReturnType);
        }
        catch (ArgumentException) { return AType.Unknown; }
    }

    static void Match(Type p, AType a, Dictionary<Type, Type> map)
    {
        if (p.IsGenericParameter)
        {
            if (a.Clr != null && !map.ContainsKey(p)) map[p] = a.Clr;
            return;
        }
        if (p.IsArray && a.Kind == "array" && a.Elem != null) { Match(p.GetElementType()!, a.Elem, map); return; }
        if (!p.IsGenericType) return;
        var pargs = p.GetGenericArguments();
        if (a.Kind == "func")
        {
            // Func<A.., R> / Action<A..> against a bound lambda (Args = params..., result)
            for (int i = 0; i < pargs.Length && i < a.Args.Count; i++) Match(pargs[i], a.Args[i], map);
            return;
        }
        if (a.Kind == "array" && a.Elem != null && pargs.Length == 1) { Match(pargs[0], a.Elem, map); return; }
        if (a.Clr is { IsGenericType: true } ac)
        {
            var src = ac.GetGenericTypeDefinition() == p.GetGenericTypeDefinition() ? ac
                : ac.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == p.GetGenericTypeDefinition());
            if (src != null)
                for (int i = 0; i < pargs.Length; i++) Match(pargs[i], FromClr(src.GetGenericArguments()[i]), map);
        }
    }

    /// <summary>Element type of a CLR enumerable (IEnumerable&lt;T&gt;), or null.</summary>
    public static Type? ElementOf(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        if (t == typeof(string)) return typeof(char);
        foreach (var i in t.GetInterfaces().Prepend(t))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return i.GetGenericArguments()[0];
        return null;
    }
}
