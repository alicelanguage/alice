using System.Reflection;
using System.Runtime.CompilerServices;

namespace Alice.Compiler.Semantics;

/// <summary>Reflection view of referenced .NET assemblies: resolves type names and member signatures.</summary>
public sealed class ClrTypes
{
    public static readonly string[] DefaultNamespaces =
        ["System", "System.Collections.Generic", "System.Linq", "System.Text", "System.IO", "System.Threading", "System.Threading.Tasks", "Alice.Runtime"];

    readonly List<Assembly> asms = new();
    readonly Dictionary<string, Type?> cache = new();
    bool allLoaded;

    public ClrTypes(IEnumerable<string>? extraRefs = null) : this(extraRefs, Assembly.LoadFrom) { }

    static readonly ConditionalWeakTable<Assembly, Source> Sources = new();
    sealed record Source(string Path);
    public static string AssemblyPath(Assembly assembly) => assembly.Location.Length > 0 ? assembly.Location : Sources.TryGetValue(assembly, out var source) ? source.Path : "";

    /// <summary>Tooling may supply a collectible, non-locking loader for long-lived editor sessions.</summary>
    public ClrTypes(IEnumerable<string>? extraRefs, Func<string, Assembly> loadAssembly)
    {
        foreach (var t in new[] { typeof(object), typeof(List<>), typeof(Enumerable), typeof(Console), typeof(Alice.Runtime.Prelude), typeof(System.Collections.Concurrent.ConcurrentDictionary<,>), typeof(Uri) })
            if (!asms.Contains(t.Assembly)) asms.Add(t.Assembly);
        foreach (var r in extraRefs ?? [])
        {
            try
            {
                var path = Path.GetFullPath(r);
                var assembly = loadAssembly(path);
                if (assembly.Location.Length == 0) Sources.GetValue(assembly, _ => new Source(path));
                if (!asms.Contains(assembly)) asms.Add(assembly);
            }
            catch (Exception) { }
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
        lock (cache) return FindCore(name, arity, usings);
    }

    Type? FindCore(string name, int arity, IReadOnlyCollection<string> usings)
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

    // Alice types used as CLR generic arguments (List(Entity)) get an empty stand-in CLR type, so the closed
    // generic exists and reflection lookups (Count, indexer, Add, IEnumerable<T>) work; FromClr maps it back.
    static readonly System.Reflection.Emit.ModuleBuilder ProxyModule =
        System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AliceTypeProxies"), System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule("proxies");
    static readonly Dictionary<Type, AType> ProxyToAlice = new();
    static readonly Dictionary<string, Type> AliceToProxy = new();
    static int proxyN;

    /// <summary>The stand-in CLR type for an Alice-declared type (null for types that can't be proxied).</summary>
    public static Type? Proxy(AType a)
    {
        if (a.Clr != null) return a.Clr;
        if (a.Sym == null || a.Kind is not ("class" or "struct")) return null;
        lock (ProxyToAlice)
        {
            if (AliceToProxy.TryGetValue(a.Cs, out var p)) return p;
            var tb = a.Kind == "struct"
                ? ProxyModule.DefineType("AliceProxy" + proxyN++, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType))
                : ProxyModule.DefineType("AliceProxy" + proxyN++, TypeAttributes.Public | TypeAttributes.Class);
            p = tb.CreateType();
            AliceToProxy[a.Cs] = p;
            ProxyToAlice[p] = a;
            return p;
        }
    }

    static bool HasProxy(Type t) =>
        ProxyToAlice.ContainsKey(t) || (t.HasElementType && HasProxy(t.GetElementType()!)) || (t.IsGenericType && t.GetGenericArguments().Any(HasProxy));

    public static AType FromClr(Type t)
    {
        if (t == typeof(void)) return AType.Void;
        lock (ProxyToAlice) if (ProxyToAlice.TryGetValue(t, out var alice)) return alice;
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
        lock (ProxyToAlice) if (ProxyToAlice.TryGetValue(t, out var alice)) return alice.Cs;
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
        var clrArgs = args.Select(a => a.Clr ?? Proxy(a)).ToArray();
        if (clrArgs.Any(a => a == null)) return null;
        try { return open.MakeGenericType(clrArgs!); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>snake_case -> PascalCase (`write_line` -> `WriteLine`); null when the name is not lower snake case.</summary>
    public static string? Pascal(string name)
    {
        if (name.Length == 0 || !char.IsLower(name[0]) || name.Any(ch => !(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_'))) return null;
        return string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>
    /// .NET names for an Alice spelling, most specific first: `write_line` -> WriteLine, `empty?` -> IsEmpty/Empty,
    /// `contains_key?` -> IsContainsKey/ContainsKey. Empty when the name is not lower snake case.
    /// </summary>
    public static IEnumerable<string> ClrSpellings(string name)
    {
        if (name.EndsWith('?') && Pascal(name[..^1]) is { } q) { yield return "Is" + q; yield return q; yield break; }
        if (Pascal(name) is { } p && p != name) yield return p;
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
            int bestScore = int.MinValue;
            foreach (var m in ty.GetMethods(flags))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                int req = ps.Count(p => !p.IsOptional && !p.IsDefined(typeof(ParamArrayAttribute)));
                bool variadic = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute));
                if (argc < req || (argc > ps.Length && !variadic)) continue;
                int score = (ps.Length == argc ? 1 : 0) + OverloadScore(ps, args);
                if (best == null || score > bestScore) { best = m; bestScore = score; }
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

    static readonly Type[] NumericOrder = [typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double)];

    /// <summary>How well known argument types fit (exact 4, numeric widening 2, assignable 1, mismatch -50).</summary>
    static int OverloadScore(ParameterInfo[] ps, List<AType>? args)
    {
        if (args == null) return 0;
        int score = 0;
        for (int i = 0; i < args.Count && i < ps.Length; i++)
        {
            var a = args[i].Clr;
            var p = ps[i].ParameterType;
            if (a == null || p.ContainsGenericParameters) continue;
            if (p.IsByRef) p = p.GetElementType()!;
            if (p == a) score += 4;
            else if (Array.IndexOf(NumericOrder, a) is int ai and >= 0 && Array.IndexOf(NumericOrder, p) is int pi and >= 0)
                score += pi > ai ? 2 - (pi - ai) / 4 : -50;   // widening is fine; narrowing would not compile
            else if (p.IsAssignableFrom(a)) score += 1;
            else score -= 50;
        }
        return score;
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
