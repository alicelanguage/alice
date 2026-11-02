namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the closure-conversion nodes (Mir/MExpr.Closures.cs). A delegate's method is dumped inline,
/// as the lambda it replaces was; a display's fields are dumped at its creation.</summary>
public sealed partial class MirDump
{
    string? CoreClosures(MExpr e)
    {
        switch (e)
        {
            case AirDelegateCreate dc:
                {
                    var m = dc.Method;
                    string owner = m.Owner?.Name ?? "<>c";
                    string head = "(delegate-create " + owner + "." + m.Name
                        + (dc.CachedField != null ? " cached " + dc.CachedField : "")
                        + (dc.Target != null ? (dc.ByRefTarget ? " ref " : " ") + E(dc.Target) : "")
                        + " (method" + (m.Static ? " static" : "") + (m.Hot ? " hot" : "") + " (" + string.Join(", ", m.Params) + ")";
                    if (m.ExprBody != null) return head + " " + E(m.ExprBody) + "))";
                    var inner = new MirDump { ind = ind + 1 };
                    inner.Stmts(m.Body ?? []);
                    return head + "\n" + inner.sb + new string(' ', ind * 2) + "))";
                }
            case AirDisplayNew dn:
                {
                    var d = dn.Display;
                    string kind = d.Kind == AirDisplayKind.Class ? "class" : d.Role != AirKernelRole.None ? "struct " + d.Role.ToString().ToLowerInvariant() : "struct";
                    if (d.Hoisted) return $"(display-new {kind} {d.Name} hoisted ({string.Join(", ", d.Fields.Select(f => f.Field))}))";
                    return $"(display-new {kind} {d.Name}" + string.Concat(d.Fields.Select(f =>
                        " (" + (f.ByRef ? "ref " : "") + (f.IsThis ? "this " : f.IsLink ? "link " : "") + f.Field + " " + E(f.Source) + ")")) + ")";
                }
            case AirDisplayField df:
                return $"(display-field {E(df.Display)} {df.Field})";
        }
        return null;
    }
}
