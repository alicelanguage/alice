namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the SIMD/stack AIR nodes (Mir/MExpr.Vector.cs).</summary>
public sealed partial class MirDump
{
    string? CoreVector(MExpr e) => e switch
    {
        AirVectorOp v => $"(v.{v.Op} {v.Vec.ToCs()}{L(v.Args)})",
        AirVectorLoad ld => $"(v.load {ld.Vec.ToCs()} {E(ld.Array)} {E(ld.Index)})",
        AirVectorStore st => $"(v.store {st.Vec.ToCs()} {E(st.Value)} {E(st.Array)} {E(st.Index)})",
        AirVectorReduce r => $"(v.reduce {r.Op} {r.Vec.ToCs()} {E(r.Arg)}{(r.CastTo != null ? " as " + r.CastTo.ToCs() : "")})",
        AirVectorFits f => $"(v.fits {f.Form} {f.Vec.ToCs()} {E(f.Index)} {E(f.End)}{(f.EndInclusive ? " inclusive" : "")})",
        AirStackAlloc sa => $"(stackalloc {sa.Elem.ToCs()} {E(sa.Count)})",
        _ => null,
    };
}
