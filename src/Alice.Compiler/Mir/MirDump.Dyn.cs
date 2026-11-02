using Alice.Runtime;

namespace Alice.Compiler.Mir;

/// <summary>Dump form of dynamic sites (Mir/MExpr.Dyn.cs): `(dyn.Kind#site name target args)`; the name is the member,
/// the operator or the target type, absent for indexers and `.()`; `?` marks a safe call.</summary>
public sealed partial class MirDump
{
    string? CoreDyn(MExpr e)
    {
        if (e is not AirDynCall d) return null;
        string what = d.Kind switch
        {
            DynKind.BinaryOp or DynKind.UnaryOp => d.Op.ToString(),
            DynKind.Convert or DynKind.Cast => d.To?.ToCs() ?? "?",
            _ => d.Name,
        };
        return "(dyn." + d.Kind + "#" + d.SiteId + (d.Safe ? "?" : "") + (what.Length > 0 ? " " + what : "")
            + (d.Target != null ? " " + E(d.Target) : "") + L(d.Args) + ")";
    }
}
