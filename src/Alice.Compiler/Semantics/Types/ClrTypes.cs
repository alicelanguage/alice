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
                var path = ImplementationOf(Path.GetFullPath(r));
                var assembly = loadAssembly(path);
                if (assembly.Location.Length == 0) Sources.GetValue(assembly, _ => new Source(path));
                if (!asms.Contains(assembly)) asms.Add(assembly);
            }
            catch (Exception) { }
        }
    }

    /// <summary>The C# name of the static class declaring extension method <paramref name="name"/> whose first
    /// parameter accepts <paramref name="receiver"/> (and whose second, when given, is <paramref name="second"/>-like),
    /// so generated code can call it fully qualified without importing its namespace.</summary>
    public string? ExtensionHost(string name, Type receiver, Func<Type, bool>? second = null)
    {
        foreach (var a in asms)
        {
            Type?[] types;
            try { types = a.GetExportedTypes(); }
            catch (Exception) { continue; }
            foreach (var t in types)
            {
                if (t is not { IsSealed: true, IsAbstract: true }) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != name || !m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                    var ps = m.GetParameters();
                    var p0 = ps[0].ParameterType;
                    bool fits = p0.IsAssignableFrom(receiver) || p0.IsGenericType && receiver.IsGenericType && p0.GetGenericTypeDefinition() == receiver.GetGenericTypeDefinition();
                    if (fits && (second == null || ps.Length > 1 && second(ps[1].ParameterType))) return "global::" + t.FullName!.Replace('+', '.');
                }
            }
        }
        return null;
    }

    HashSet<string>? extensionNames;

    /// <summary>
    /// Whether a referenced assembly (a NuGet package, a project) defines an extension method of this name, so
    /// `query.ToQueryString` without parentheses is a call, as `xs.Sum` is for LINQ.
    /// </summary>
    public bool IsExtensionName(string name)
    {
        if (extensionNames == null)
        {
            extensionNames = [];
            foreach (var a in asms)
            {
                Type?[] types;
                try { types = a.GetExportedTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (var t in types)
                {
                    if (t is not { IsSealed: true, IsAbstract: true } || !t.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) extensionNames.Add(m.Name);
                }
            }
        }
        return extensionNames.Contains(name);
    }

    /// <summary>
    /// The implementation assembly behind a reference assembly, for reflection. Reference assemblies (NuGet `ref/`,
    /// a project's `obj/.../ref/`) strip private members, private interface implementations included, so the runtime
    /// cannot load their types (Avalonia's StyledElement: TypeLoadException). Roslyn still compiles against `r`.
    /// </summary>
    public static string ImplementationOf(string r)
    {
        var dir = Path.GetDirectoryName(r);
        var file = Path.GetFileName(r);
        if (dir == null) return r;
        var tfm = Path.GetFileName(dir);
        var parent = Path.GetDirectoryName(dir);
        if (parent != null && Path.GetFileName(parent) == "ref")   // NuGet: <pkg>/ref/<tfm>/X.dll -> <pkg>/lib/<tfm>/X.dll
        {
            var lib = Path.Combine(Path.GetDirectoryName(parent)!, "lib", tfm, file);
            if (File.Exists(lib)) return lib;
            if (SharedFrameworkOf(Path.GetDirectoryName(parent)!, file) is { } shared) return shared;
        }
        if (tfm is "ref" or "refint" && parent != null)   // a project's obj/<cfg>/<tfm>/ref/X.dll -> obj/<cfg>/<tfm>/X.dll
        {
            var impl = Path.Combine(parent, file);
            if (File.Exists(impl)) return impl;
        }
        return r;
    }

    /// <summary>A framework reference pack (`dotnet/packs/Microsoft.AspNetCore.App.Ref/10.0.14/ref/net10.0/X.dll`) ->
    /// the installed shared framework's X.dll (`dotnet/shared/Microsoft.AspNetCore.App/10.0.14/`, or the newest
    /// installed version with the same major). Microsoft.NETCore.App is left alone: the running runtime is it.</summary>
    static string? SharedFrameworkOf(string versionDir, string file)
    {
        var packDir = Path.GetDirectoryName(versionDir);
        var packs = packDir == null ? null : Path.GetDirectoryName(packDir);
        string pack = packDir == null ? "" : Path.GetFileName(packDir);
        if (packs == null || Path.GetFileName(packs) != "packs" || !pack.EndsWith(".Ref") || pack == "Microsoft.NETCore.App.Ref") return null;
        var shared = Path.Combine(Path.GetDirectoryName(packs)!, "shared", pack[..^".Ref".Length]);
        if (!Directory.Exists(shared)) return null;
        string version = Path.GetFileName(versionDir);
        var exact = Path.Combine(shared, version, file);
        if (File.Exists(exact)) return exact;
        string major = version.Split('.')[0] + ".";
        return Directory.GetDirectories(shared)
            .Where(d => Path.GetFileName(d).StartsWith(major) && Version.TryParse(Path.GetFileName(d).Split('-')[0], out _))
            .OrderByDescending(d => Version.Parse(Path.GetFileName(d).Split('-')[0]))
            .Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);
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

    /// <summary><paramref name="a"/>.GetType without throwing when the name is a type forward into an assembly the
    /// runtime cannot load: next to a loaded facade it may find only a reference assembly (BadImageFormatException,
    /// windows-2025: Microsoft.Extensions.Logging.Abstractions from the ASP.NET reference pack) or nothing.</summary>
    static Type? TypeIn(Assembly a, string name)
    {
        try { return a.GetType(name, false); }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException) { return null; }
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
                var t = TypeIn(a, c + suffix);
                if (t != null && t.IsPublic) return t;
                int dot = c.LastIndexOf('.');
                if (dot > 0)
                {
                    t = TypeIn(a, c.Substring(0, dot) + "+" + c.Substring(dot + 1) + suffix);
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

    /// <summary>A closed CLR generic seen from Alice. CLR generics carry no nullable-reference annotation, so
    /// `Dictionary(String, String?)` keeps its declared argument spelling in C# (string?): `d[k] = nil` compiles.</summary>
    public static AType Closed(Type closed, IReadOnlyList<AType> args)
    {
        var t = FromClr(closed);
        if (!args.Any(a => a.Cs.EndsWith('?') && a.Clr is { IsValueType: false }) || t.Cs.IndexOf('<') is not (int lt and > 0)) return t;
        return new AType(t.Kind, t.Cs[..lt] + "<" + string.Join(", ", args.Select(a => a.Cs)) + ">", closed) { Sym = t.Sym, Args = [.. args], Elem = t.Elem };
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
    public static bool IsSpan(Type? t) => t is { IsGenericType: true } && (t.GetGenericTypeDefinition() == typeof(Span<>) || t.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>));

    public static Type? ElementOf(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        if (t == typeof(string)) return typeof(char);
        if (IsSpan(t)) return t.GetGenericArguments()[0];   // spans are not IEnumerable but iterate like arrays
        foreach (var i in t.GetInterfaces().Prepend(t))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return i.GetGenericArguments()[0];
        return null;
    }

    Dictionary<string, List<MethodInfo>>? extensionMethods;

    /// <summary>
    /// Result type of a .NET extension method call (`bytes.Concat(px)`, `counts.GetValueOrDefault(w)`,
    /// `db.blogs.Where(...)`, `acc.Allocate1D(xs)`), with generic arguments inferred from the receiver and the
    /// typed arguments as C# does. Among applicable overloads the one with the most specific receiver wins
    /// (Queryable over Enumerable for an IQueryable); Unknown when that is ambiguous or a type argument stays open.
    /// </summary>
    public AType ExtensionReturn(string name, AType recv, List<AType> args, IReadOnlyList<Type>? typeArgs = null)
    {
        var rt = ClrOf(recv);
        if (rt == null) return AType.Unknown;
        if (extensionMethods == null)
        {
            extensionMethods = new();
            foreach (var a in asms)
            {
                Type?[] types;
                try { types = a.GetExportedTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (var t in types)
                {
                    if (t is not { IsSealed: true, IsAbstract: true } || !t.IsDefined(typeof(ExtensionAttribute), false)) continue;
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.IsDefined(typeof(ExtensionAttribute), false))
                        {
                            if (!extensionMethods.TryGetValue(m.Name, out var l)) extensionMethods[m.Name] = l = new();
                            l.Add(m);
                        }
                }
            }
        }
        if (!extensionMethods.TryGetValue(name, out var all)) return AType.Unknown;
        var fits = new List<(MethodInfo M, Type Recv)>();
        foreach (var m0 in all)
        {
            var ps = m0.GetParameters();
            int req = ps.Count(p => !p.IsOptional && !p.IsDefined(typeof(ParamArrayAttribute))) - 1;
            if (args.Count < req || args.Count > ps.Length - 1) continue;
            var m = m0;
            if (m.IsGenericMethodDefinition)
            {
                var targs = m.GetGenericArguments();
                Type[] closedArgs;
                if (typeArgs != null)
                {
                    if (typeArgs.Count != targs.Length) continue;
                    closedArgs = typeArgs.ToArray();
                }
                else
                {
                    var map = new Dictionary<Type, Type>();
                    MatchClr(ps[0].ParameterType, rt, map);
                    for (int i = 0; i < args.Count; i++) Match(Unexpr(ps[i + 1].ParameterType), args[i], map);
                    if (!targs.All(map.ContainsKey)) continue;
                    closedArgs = targs.Select(g => map[g]).ToArray();
                }
                try { m = m.MakeGenericMethod(closedArgs); }
                catch (ArgumentException) { continue; }
            }
            else if (typeArgs != null) continue;
            var cps = m.GetParameters();
            if (!Receives(cps[0].ParameterType, rt)) continue;
            bool ok = true;
            for (int i = 0; i < args.Count && ok; i++)
            {
                var a = args[i].Kind == "func" ? null : args[i].Clr ?? (args[i].Sym != null ? Proxy(args[i]) : null);
                var p = cps[i + 1].ParameterType;
                if (p.IsByRef) p = p.GetElementType()!;
                if (args[i].Kind == "func") ok = typeof(Delegate).IsAssignableFrom(Unexpr(p));
                else if (a != null) ok = p.IsAssignableFrom(a) || Array.IndexOf(NumericOrder, a) >= 0 && Array.IndexOf(NumericOrder, p) > Array.IndexOf(NumericOrder, a);
                else if (args[i].Kind is "unknown" or "dynamic") ok = false;
            }
            if (ok) fits.Add((m, cps[0].ParameterType));
        }
        // most specific receiver: assignable to every other candidate's receiver parameter
        var best = fits.Where(f => fits.All(g => g.Recv.IsAssignableFrom(f.Recv) || Receives(g.Recv, f.Recv))).ToList();
        // no single most specific one (Span and ReadOnlySpan overloads of IndexOf): fine when they all agree
        var rets = (best.Count > 0 ? best : fits).Select(f => f.M.ReturnType).Distinct().ToList();
        if (rets.Count != 1 || rets[0].ContainsGenericParameters) return AType.Unknown;
        return FromClr(rets[0]);
    }

    /// <summary>
    /// Parameter types of the block passed as <paramref name="m"/>'s last (delegate) parameter: `Par.For(n) { |i| }`
    /// gives Int32, `Par.Map(xs) do |y|` the element type of xs. Generic ones come from the other arguments; a type
    /// that stays open is Unknown, as before.
    /// </summary>
    public static List<AType> BlockParamTypes(MethodInfo? m, List<AType>? args)
    {
        if (m == null || m.GetParameters() is not { Length: > 0 } ps) return [];
        var d = ps[^1].ParameterType;
        if (!d.IsSubclassOf(typeof(Delegate)) || d.GetMethod("Invoke") is not { } invoke) return [];
        var map = new Dictionary<Type, Type>();
        if (m.IsGenericMethodDefinition && args != null)
            for (int i = 0; i < args.Count && i < ps.Length - 1; i++) Match(ps[i].ParameterType, args[i], map);
        Type? Close(Type t)
        {
            if (t.IsGenericParameter) return map.GetValueOrDefault(t);
            if (!t.ContainsGenericParameters) return t;
            if (t.IsArray) return Close(t.GetElementType()!) is { } e ? e.MakeArrayType() : null;
            if (!t.IsGenericType) return null;
            var targs = t.GetGenericArguments().Select(Close).ToArray();
            if (targs.Any(a => a == null)) return null;
            try { return t.GetGenericTypeDefinition().MakeGenericType(targs!); }
            catch (ArgumentException) { return null; }
        }
        return invoke.GetParameters().Select(p => Close(p.ParameterType) is { } c && !c.IsByRef ? FromClr(c) : AType.Unknown).ToList();
    }

    /// <summary>The CLR type an Alice-typed receiver has at run time (its own, a CLR base class, or a proxy).</summary>
    static Type? ClrOf(AType t)
    {
        if (t.Clr != null) return t.Clr;
        if (t.Sym?.Bases.Select(b => b.Clr).FirstOrDefault(b => b != null && !b.IsInterface) is { } clrBase) return clrBase;
        return null;
    }

    /// <summary>`Expression(Func(T, Bool))` parameters take lambdas like `Func(T, Bool)` does.</summary>
    static Type Unexpr(Type p) =>
        p.IsGenericType && !p.IsGenericTypeDefinition && p.GetGenericTypeDefinition() == typeof(System.Linq.Expressions.Expression<>) ? p.GetGenericArguments()[0] : p;

    /// <summary>The receiver converts to the `this` parameter: reference/boxing conversion, or array to span (C# 14).</summary>
    static bool Receives(Type p, Type a)
    {
        if (p.IsAssignableFrom(a)) return true;
        if (a.IsArray && IsSpan(p) && p.GetGenericArguments()[0] == a.GetElementType()) return true;
        return false;
    }

    /// <summary>Binds generic parameters of <paramref name="p"/> from a CLR type (its base classes and interfaces too).</summary>
    static void MatchClr(Type p, Type a, Dictionary<Type, Type> map)
    {
        if (p.IsGenericParameter) { map.TryAdd(p, a); return; }
        if (p.IsArray && a.IsArray) { MatchClr(p.GetElementType()!, a.GetElementType()!, map); return; }
        if (!p.IsGenericType) return;
        var def = p.GetGenericTypeDefinition();
        if (a.IsArray && IsSpan(p)) { MatchClr(p.GetGenericArguments()[0], a.GetElementType()!, map); return; }
        for (var b = a; b != null; b = b.BaseType)
            if (b.IsGenericType && b.GetGenericTypeDefinition() == def) { Pairs(p, b, map); return; }
        foreach (var i in a.GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == def) { Pairs(p, i, map); return; }
    }

    static void Pairs(Type p, Type a, Dictionary<Type, Type> map)
    {
        var pa = p.GetGenericArguments();
        var aa = a.GetGenericArguments();
        for (int i = 0; i < pa.Length && i < aa.Length; i++) MatchClr(pa[i], aa[i], map);
    }
}
