using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>C# for the SIMD/stack AIR nodes (Mir/MExpr.Vector.cs), byte-identical to the text they replace.</summary>
public sealed partial class Printer
{
    const string MemoryMarshal = "global::System.Runtime.InteropServices.MemoryMarshal";

    static bool AtomicVector(MExpr e) => e switch
    {
        AirLeafStandIn l => l.Text is not ['-', ..],   // as MCode (TODO merge: delete with the stand-ins)
        AirVectorOp v => v.Op != AirVecOp.Neg,
        AirVectorLoad or AirVectorStore => true,
        AirVectorReduce r => r.CastTo == null,
        _ => false,
    };

    static string? EVector(MExpr e)
    {
        switch (e)
        {
            case AirLeafStandIn l: return l.Text;   // TODO merge: delete with the stand-ins
            case AirVectorOp v: return VecOp(v);
            case AirVectorLoad ld:
                return $"{Api(ld.Vec)}.LoadUnsafe(ref {MemoryMarshal}.GetArrayDataReference({E(ld.Array)}), (nuint){E(ld.Index)})";
            case AirVectorStore st:
                return $"{Api(st.Vec)}.StoreUnsafe({E(st.Value)}, ref {MemoryMarshal}.GetArrayDataReference({E(st.Array)}), (nuint){E(st.Index)})";
            case AirVectorReduce r:
                return (r.CastTo != null ? "(" + r.CastTo.ToCs() + ")" : "") + $"{Api(r.Vec)}.{r.Op}({E(r.Arg)})";
            case AirVectorFits f:
                {
                    string end = f.EndInclusive ? $"({E(f.End)}) + 1" : E(f.End);
                    string count = f.Vec.ToCs() + ".Count";
                    return f.Form switch
                    {
                        AirFitsForm.Fits => $"{E(f.Index)} <= {end} - {count}",
                        AirFitsForm.Overruns => $"{E(f.Index)} > {end} - {count}",
                        _ => $"{Api(f.Vec)}.IsHardwareAccelerated && {end} - {E(f.Index)} >= {count}",
                    };
                }
            case AirStackAlloc sa:
                return $"stackalloc {sa.Elem.ToCs()}[{E(sa.Count)}]";
        }
        return null;
    }

    static string Api(AirType vec) => ((AirType.ClrRef)vec).Name;

    static string VecOp(AirVectorOp v)
    {
        string ns = Api(v.Vec), vt = v.Vec.ToCs();
        switch (v.Op)
        {
            case AirVecOp.Zero or AirVecOp.One or AirVecOp.Indices or AirVecOp.Count:
                return vt + "." + v.Op;
            case AirVecOp.IsAccelerated:
                return ns + ".IsHardwareAccelerated";
            case AirVecOp.Splat:
                {
                    // the scalar is converted to the lane type: `(T)x` for a name, `(T)(text)` otherwise
                    var x = v.Args[0];
                    string lane = "(" + AirVector.Lane(v.Vec).ToCs() + ")" + (x is AirLocalRef ? E(x) : "(" + E(x) + ")");
                    return AirVector.Width(v.Vec) == 0 ? $"new {vt}({lane})" : $"{ns}.Create({lane})";
                }
            case AirVecOp.Add or AirVecOp.Sub or AirVecOp.Mul or AirVecOp.Div:
                {
                    string op = v.Op switch { AirVecOp.Add => "+", AirVecOp.Sub => "-", AirVecOp.Mul => "*", _ => "/" };
                    return $"({E(v.Args[0])} {op} {E(v.Args[1])})";
                }
            case AirVecOp.Neg:
                return $"-({E(v.Args[0])})";
            case AirVecOp.Sqrt:
                return $"{ns}.{(AirVector.Width(v.Vec) == 0 ? "SquareRoot" : "Sqrt")}({Join(v.Args)})";
            default:
                return $"{ns}.{v.Op}({Join(v.Args)})";
        }
    }
}
