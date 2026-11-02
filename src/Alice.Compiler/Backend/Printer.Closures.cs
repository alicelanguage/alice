using System.Text;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>
/// A C# spelling of the closure-conversion nodes (Mir/MExpr.Closures.cs), for debugging the IL backend only: the
/// compiler-reserved names (`&lt;&gt;c`, `&lt;M&gt;b__0`) do not compile, and the C# backend never meets these nodes.
/// </summary>
public sealed partial class Printer
{
    static string? EClosures(MExpr e)
    {
        switch (e)
        {
            case AirDelegateCreate dc:
                {
                    var m = dc.Method;
                    if (m.Owner == null) return $"({ClosureHolder}.{dc.CachedField} ??= {ClosureHolder}.{m.Name})";
                    if (dc.ByRefTarget) return $"/* ref */ {E(dc.Target!)}.{m.Name}";
                    return $"{E(dc.Target!)}.{m.Name}";
                }
            case AirDisplayNew dn:
                {
                    var d = dn.Display;
                    if (d.Hoisted || d.Fields.Count == 0) return $"new {d.Name}()";
                    return $"new {d.Name} {{ " + string.Join(", ", d.Fields.Select(f => f.Field + " = " + (f.ByRef ? "ref " : "") + E(f.Source))) + " }";
                }
            case AirDisplayField df:
                return E(df.Display) + "." + df.Field;
        }
        return null;
    }

    const string ClosureHolder = "<>c";

    /// <summary>C# declarations of the display types and the `&lt;&gt;c` holder of a converted body (debugging only).</summary>
    public static string ClosureDeclarations(IEnumerable<AirDisplayType> displays, IEnumerable<AirClosureMethod> statics)
    {
        var sb = new StringBuilder();
        var st = statics.ToList();
        if (st.Count > 0)
        {
            sb.Append("sealed class ").Append(ClosureHolder).Append("\n{\n");
            foreach (var m in st)
            {
                sb.Append("    public static Delegate? <>9__").Append(m.Name).Append(";\n");
                Method(sb, m, "    public static ");
            }
            sb.Append("}\n");
        }
        foreach (var d in displays)
        {
            sb.Append(d.Kind == AirDisplayKind.Class ? "sealed class " : d.ByRefLike ? "ref struct " : "struct ").Append(d.Name)
                .Append(d.Role switch
                {
                    AirKernelRole.Range => " : global::Alice.Runtime.IParKernel",
                    AirKernelRole.Reduce => " : global::Alice.Runtime.IParKernel<T>",
                    AirKernelRole.Fork => " : global::Alice.Runtime.IForkBranch<T>",
                    _ => "",
                }).Append("\n{\n");
            foreach (var f in d.Fields)
                sb.Append("    public ").Append(f.ByRef ? "ref " : "").Append(f.TypeCs ?? f.Type?.ToCs() ?? "var").Append(' ').Append(f.Field).Append(";\n");
            foreach (var m in d.Methods) Method(sb, m, m.Static ? "    public static " : "    public ");
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    static void Method(StringBuilder sb, AirClosureMethod m, string head)
    {
        sb.Append(head).Append(m.Name).Append('(').Append(string.Join(", ", m.Params)).Append(')');
        if (m.ExprBody != null)
        {
            sb.Append(" => ").Append(E(m.ExprBody)).Append(";\n");
            return;
        }
        var pr = new Printer("", 1);
        pr.Line("{");
        pr.ind++;
        pr.Stmts(m.Body ?? []);
        pr.ind--;
        pr.Line("}");
        sb.Append('\n').Append(pr.sb);
    }
}
