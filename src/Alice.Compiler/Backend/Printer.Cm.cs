using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>
/// C# for the Connection-Machine nodes (Mir/MExpr.Cm.cs, ADR 0039): direct calls into <c>Alice.Runtime.Cm</c> over
/// spans <c>[0, n)</c> of whole arrays, the same calls the IL backend emits (Backend/Il/AirToIl.Cm.cs).
/// </summary>
public sealed partial class Printer
{
    const string CmRt = "global::Alice.Runtime.Cm";

    static string CmSpan(string kind, AirType elem, MExpr arr, MExpr? n) =>
        $"new global::System.{kind}<{elem.ToCs()}>({E(arr)}" + (n != null ? $", 0, {E(n)})" : ")");

    static string CmOpCs(AirCmOp op) => "global::Alice.Runtime.CmOp." + op;

    static string? ECm(MExpr e)
    {
        switch (e)
        {
            case AirScan s:
                return $"{CmRt}.{(s.Inclusive ? "InclusiveScan" : "ExclusiveScan")}<{s.Elem.ToCs()}>({CmSpan("ReadOnlySpan", s.Elem, s.Src, s.Count)}, {CmSpan("Span", s.Elem, s.Dst, s.Count)}, {CmOpCs(s.Op)})";
            case AirSegmented g:
                return $"{CmRt}.SegmentedScan<{g.Elem.ToCs()}>({CmSpan("ReadOnlySpan", g.Elem, g.Src, g.Count)}, {CmSpan("ReadOnlySpan", AirLeaf.Bool, g.Flags, g.Count)}, {CmSpan("Span", g.Elem, g.Dst, g.Count)}, {CmOpCs(g.Op)})";
            case AirCombine c when c.Vals == null:
                return $"{CmRt}.Histogram<{c.Elem.ToCs()}>({CmSpan("ReadOnlySpan", c.IdxElem, c.Idx, c.Count)}, {CmSpan("Span", c.Elem, c.Target, null)})";
            case AirCombine c:
                {
                    string idx = CmSpan("ReadOnlySpan", c.IdxElem, c.Idx, c.Count);
                    if (c.IdxElem.ToCs() != "int") idx = $"global::System.Runtime.InteropServices.MemoryMarshal.Cast<{c.IdxElem.ToCs()}, int>({idx})";
                    return $"{CmRt}.ScatterAdd<{c.Elem.ToCs()}>({CmSpan("Span", c.Elem, c.Target, null)}, {idx}, {CmSpan("ReadOnlySpan", c.Elem, c.Vals!, c.Count)})";
                }
            case AirCompact k:
                return $"{CmRt}.Compact<{k.Elem.ToCs()}>({E(k.Src)}, {CmSpan("ReadOnlySpan", AirLeaf.Bool, k.Flags, k.Count)}, {E(k.Dst)})";
            case AirPermute p:
                return $"{CmRt}.Permute<{p.Elem.ToCs()}>({E(p.Src)}, {CmSpan("ReadOnlySpan", AirLeaf.Int, p.Perm, p.Count)}, {E(p.Dst)})";
            case AirCmDistinct d:
                return "!global::System.Object.ReferenceEquals(" + E(d.W) + ", " + E(d.R) + ")";
        }
        return null;
    }
}
