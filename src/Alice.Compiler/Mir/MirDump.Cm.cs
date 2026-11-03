namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the Connection-Machine nodes (Mir/MExpr.Cm.cs).</summary>
public sealed partial class MirDump
{
    static string CmFlags(AirCmExpr c) => " " + c.Elem.ToCs() + (c.Ordered ? "" : " chunked");

    string? CoreCm(MExpr e)
    {
        switch (e)
        {
            case AirScan s:
                return $"(cm-scan {s.Op.ToString().ToLowerInvariant()} {(s.Inclusive ? "inclusive" : "exclusive")}{CmFlags(s)}{(s.Fused ? " fused" : "")} {E(s.Src)} {E(s.Dst)} {E(s.Count)})";
            case AirSegmented g:
                return $"(cm-segmented {g.Op.ToString().ToLowerInvariant()}{CmFlags(g)} {E(g.Src)} {E(g.Flags)} {E(g.Dst)} {E(g.Count)})";
            case AirCombine c:
                return $"(cm-combine {(c.Vals == null ? "histogram" : "scatter-add")}{CmFlags(c)} idx {c.IdxElem.ToCs()}{(c.Fused ? " fused" : "")} {E(c.Target)} {E(c.Idx)}{(c.Vals != null ? " " + E(c.Vals) : "")} {E(c.Count)})";
            case AirCompact k:
                return $"(cm-compact{CmFlags(k)} {E(k.Src)} {E(k.Flags)} {E(k.Dst)} {E(k.Count)})";
            case AirPermute p:
                return $"(cm-permute{CmFlags(p)} proof {p.Proof} {E(p.Src)} {E(p.Perm)} {E(p.Dst)} {E(p.Count)})";
            case AirCmDistinct d:
                return $"(cm-distinct {E(d.W)} {E(d.R)})";
        }
        return null;
    }
}
