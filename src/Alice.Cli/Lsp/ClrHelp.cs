using System.Reflection;
using System.Xml;
using System.Xml.Linq;

namespace Alice.Cli.Lsp;

/// <summary>Member signatures and streamed XML documentation, bounded and loaded only on demand.</summary>
public static class ClrHelp
{
    static readonly Dictionary<string, string> Cache = new();
    public static string TypeName(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "(" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ")"
        : type.Name;
    public static string Signature(MemberInfo member) => member switch
    {
        MethodInfo m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.Name + " : " + TypeName(p.ParameterType)))}) : {TypeName(m.ReturnType)}",
        ConstructorInfo c => $"new({string.Join(", ", c.GetParameters().Select(p => p.Name + " : " + TypeName(p.ParameterType)))})",
        PropertyInfo p => p.Name + " : " + TypeName(p.PropertyType),
        FieldInfo f => f.Name + " : " + TypeName(f.FieldType),
        Type t => (t.IsInterface ? "interface " : t.IsValueType ? "struct " : "class ") + TypeName(t),
        _ => member.Name,
    };

    public static string Documentation(MemberInfo member)
    {
        var type = member as Type ?? member.DeclaringType;
        if (type == null) return "";
        var assembly = type.Assembly;
        if (assembly.IsDynamic) return "";
        string location = Alice.Compiler.Semantics.ClrTypes.AssemblyPath(assembly);
        if (location.Length == 0) return "";
        string xml = Path.ChangeExtension(location, ".xml");
        if (!File.Exists(xml))
        {
            var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            var packs = Path.Combine(dotnet, "packs");
            if (Directory.Exists(packs))
                xml = Directory.EnumerateFiles(packs, assembly.GetName().Name + ".xml", SearchOption.AllDirectories).OrderDescending().FirstOrDefault() ?? xml;
        }
        if (!File.Exists(xml)) return "";
        string name = (member is Type ? "T:" : member is MethodBase ? "M:" : member is PropertyInfo ? "P:" : "F:") + (type.FullName ?? type.Name).Replace('+', '.');
        if (member is not Type) name += "." + (member is ConstructorInfo ? "#ctor" : member.Name);
        if (member is MethodInfo { IsGenericMethod: true } generic) name += "``" + generic.GetGenericArguments().Length;
        if (member is MethodBase method && method.GetParameters().Length > 0)
            name += "(" + string.Join(",", method.GetParameters().Select(p => XmlType(p.ParameterType))) + ")";
        string key = xml + ":" + File.GetLastWriteTimeUtc(xml).Ticks + ":" + name;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            string result = "";
            try
            {
                using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "member" || reader.GetAttribute("name") != name) continue;
                    var entry = (XElement)XNode.ReadFrom(reader);
                    foreach (var see in entry.Descendants("see").ToArray()) see.ReplaceWith((see.Attribute("cref")?.Value ?? "").Split(':').Last());
                    foreach (var reference in entry.Descendants("paramref").ToArray()) reference.ReplaceWith(reference.Attribute("name")?.Value ?? "");
                    result = string.Join("\n\n", entry.Elements().Where(e => e.Name.LocalName is "summary" or "returns" or "param").Select(e =>
                        (e.Name.LocalName == "param" ? "`" + e.Attribute("name")?.Value + "`: " : "") + string.Join(' ', e.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
                    break;
                }
            }
            catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException) { }
            if (Cache.Count >= 128) Cache.Clear();
            return Cache[key] = result;
        }
    }
    static string XmlType(Type t) => t.IsByRef ? XmlType(t.GetElementType()!) + "@" : t.IsArray ? XmlType(t.GetElementType()!) + "[]" :
        t.IsGenericParameter ? (t.DeclaringMethod == null ? "`" : "``") + t.GenericParameterPosition :
        t.IsGenericType ? t.GetGenericTypeDefinition().FullName!.Split('`')[0] + "{" + string.Join(",", t.GetGenericArguments().Select(XmlType)) + "}" : t.FullName ?? t.Name;
}
