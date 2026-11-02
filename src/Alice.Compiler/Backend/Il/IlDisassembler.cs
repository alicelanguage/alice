using System.Reflection;
using System.Text;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// A readable dump of an <see cref="IlModule"/> for `alice emit --il`: types with their base, fields, properties and
/// methods; each body as <see cref="IlBuilder.Listing"/> prints it (locals, labels, instructions, EH regions,
/// sequence points as `// file.al:line:col` comments). ilasm-like, not ilasm-exact: operands are symbolic.
/// </summary>
public static class IlDisassembler
{
    public static string Disassemble(IlModule m)
    {
        var sb = new StringBuilder();
        sb.Append(".assembly ").Append(m.Name).Append(" // ").Append(m.Version).Append('\n');
        if (m.EntryPoint != null) sb.Append(".entrypoint ").Append(m.EntryPoint).Append('\n');
        foreach (var t in m.TypeDefOrder()) Type(sb, t);
        return sb.ToString();
    }

    static void Type(StringBuilder sb, IlTypeDef t)
    {
        sb.Append('\n').Append(".class ").Append(Flags(t.Attributes)).Append(' ').Append(Full(t));
        if (t.BaseType != null) sb.Append(" extends ").Append(t.BaseType);
        if (t.Interfaces.Count > 0) sb.Append(" implements ").Append(string.Join(", ", t.Interfaces));
        sb.Append("\n{\n");
        foreach (var f in t.Fields)
            sb.Append("  .field ").Append(Flags(f.Attributes)).Append(' ').Append(f.Type).Append(' ').Append(f.Name)
              .Append(f.Constant != null ? " = " + f.Constant : "").Append('\n');
        foreach (var p in t.Properties)
            sb.Append("  .property ").Append(p.Type).Append(' ').Append(p.Name).Append(p.Getter != null ? " get" : "").Append(p.Setter != null ? " set" : "").Append('\n');
        foreach (var md in t.Methods)
        {
            var sig = md.Signature;
            var names = md.Parameters.ToDictionary(p => p.Sequence, p => p.Name);
            string ps = string.Join(", ", sig.Parameters.Select((p, i) => p + (names.TryGetValue(i + 1, out var n) && n != null ? " " + n : "")));
            sb.Append("  .method ").Append(Flags(md.Attributes)).Append(' ').Append(sig.Return).Append(' ').Append(md.Name).Append('(').Append(ps).Append(')');
            if (md.ImplAttributes != MethodImplAttributes.IL) sb.Append(' ').Append(md.ImplAttributes);
            if (md.Body is { } body)
            {
                sb.Append("\n  {\n");
                foreach (var line in body.Listing().Split('\n', StringSplitOptions.RemoveEmptyEntries)) sb.Append("  ").Append(line).Append('\n');
                sb.Append("  }\n");
            }
            else sb.Append(" // no body\n");
        }
        sb.Append("}\n");
    }

    static string Full(IlTypeDef t) => t.Enclosing != null ? Full(t.Enclosing) + "/" + t.Name : t.Namespace.Length > 0 ? t.Namespace + "." + t.Name : t.Name;

    static string Flags<T>(T flags) where T : Enum => flags.ToString().ToLowerInvariant().Replace(",", "");
}
