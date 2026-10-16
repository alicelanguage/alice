using System.Reflection;
using System.Text;
using Alice.Compiler.Diagnostics;
using Alice.Runtime;

namespace Alice.Cli.Repl;

/// <summary>`?name` help mode: keywords, Prelude functions, .NET types and members, with Alice type names.</summary>
public static class ReplHelp
{
    static readonly Dictionary<string, string> Keywords = new()
    {
        ["def"] = "def name(arg : Type) : Ret ... end   — method; `def name(x) = expr` for one-liners",
        ["class"] = "class Name < Base ... end   — reference type; getter/property/setter declare fields",
        ["struct"] = "struct Name ... end   — value type (record struct Name(x : T) for positional)",
        ["module"] = "module Name ... end   — namespace for `def self.` functions and mixins (include/extend)",
        ["describe"] = "describe(String name, Action body)   — group specs: describe \"X\" do it \"..\" do expect(a).to eq(b) end end",
        ["spawn"] = "spawn { ... }   — run concurrently (Task); data races are compile errors (AL0202)",
        ["case"] = "case x when 1 then ... in [a, b] then ... else ... end   — values, types, patterns",
        ["require"] = "require \"./file\"   — load another Alice file",
        ["using"] = "using System.Text   — import a .NET namespace",
    };

    public static readonly string[] CommonTypes = ["Int32", "Int64", "Float64", "Float32", "Bool", "String", "Char", "Array", "List", "Dictionary", "HashSet", "Math", "Console", "File", "Path", "DateTime", "TimeSpan", "Guid", "Task", "Channel", "Par", "Simd"];

    static readonly Dictionary<string, Type> AliceTypes = new()
    {
        ["Int32"] = typeof(int),
        ["Int64"] = typeof(long),
        ["Float64"] = typeof(double),
        ["Float32"] = typeof(float),
        ["Bool"] = typeof(bool),
        ["String"] = typeof(string),
        ["Char"] = typeof(char),
        ["Object"] = typeof(object),
        ["List"] = typeof(List<>),
        ["Dictionary"] = typeof(Dictionary<,>),
        ["HashSet"] = typeof(HashSet<>),
        ["Array"] = typeof(Array),
    };

    static readonly string[] Namespaces = ["System", "System.IO", "System.Linq", "System.Text", "System.Collections.Generic", "System.Threading.Tasks", "Alice.Runtime"];

    public static Type? ResolveType(string name)
    {
        if (AliceTypes.TryGetValue(name, out var t)) return t;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Append(typeof(Prelude).Assembly))
        {
            if (asm.IsDynamic) continue;
            foreach (var ns in Namespaces.Prepend(""))
                if (asm.GetType(ns.Length > 0 ? ns + "." + name : name) is { IsPublic: true } found) return found;
        }
        return Type.GetType("System." + name);
    }

    public static string Lookup(string q)
    {
        q = q.Trim();
        var sb = new StringBuilder();
        if (Keywords.TryGetValue(q, out var kw)) sb.AppendLine(kw);
        int dot = q.LastIndexOf('.');
        if (dot > 0 && ResolveType(q[..dot]) is { } owner)
        {
            foreach (var m in owner.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(m => m.Name == q[(dot + 1)..]))
                sb.AppendLine($"{q[..dot]}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType)))}) : {Name(m.ReturnType)}");
            foreach (var p in owner.GetProperties().Where(p => p.Name == q[(dot + 1)..]))
                sb.AppendLine($"{q[..dot]}.{p.Name} : {Name(p.PropertyType)}");
        }
        else if (ResolveType(q) is { } t)
        {
            string kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class";
            sb.AppendLine($"{kind} {q}   ({t.FullName})");
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property && !m.Name.Contains('_')).Select(m => m.Name).Distinct().Order().Take(40);
            sb.AppendLine("  " + string.Join("  ", members));
        }
        string mname = q.EndsWith("?") ? q[..^1] + "_q" : q;
        foreach (var m in typeof(Prelude).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == mname).Take(8))
        {
            var ps = m.GetParameters().Where(p => p.ParameterType != typeof(string) || !p.IsDefined(typeof(System.Runtime.CompilerServices.CallerFilePathAttribute)))
                .Where(p => !p.IsDefined(typeof(System.Runtime.CompilerServices.CallerLineNumberAttribute)) && !p.IsDefined(typeof(System.Runtime.CompilerServices.CallerFilePathAttribute)));
            string recv = m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute)) ? Name(m.GetParameters()[0].ParameterType) + "#" : "";
            var shown = recv.Length > 0 ? ps.Skip(1) : ps;
            sb.AppendLine($"{recv}{q}({string.Join(", ", shown.Select(p => $"{Name(p.ParameterType)} {p.Name}"))}) : {Name(m.ReturnType)}");
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : $"no help for '{q}' (try ?String, ?Math.Sqrt, ?puts, ?def)";
    }

    static string Name(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        string cs = t.IsGenericType
            ? t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">"
            : t.IsArray ? Name(t.GetElementType()!) + "[]" : t == typeof(void) ? "void" : Prims(t) ?? t.Name;
        return AliceNames.Type(cs);
    }

    static string? Prims(Type t) =>
        t == typeof(int) ? "int" : t == typeof(long) ? "long" : t == typeof(double) ? "double" : t == typeof(float) ? "float" : t == typeof(bool) ? "bool"
        : t == typeof(string) ? "string" : t == typeof(char) ? "char" : t == typeof(object) ? "object" : null;
}
