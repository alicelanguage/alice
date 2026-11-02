namespace Alice.Compiler.Mir;

// AIR nodes of the SoA lowerings (Lowerer.SoA, Lowerer.SoAFusion; ADR 0032, backend plan D0.7b). The vector work
// itself uses the nodes of Mir/MExpr.Vector.cs; leaves are the Core ones (Mir/MExpr.Core.cs).

/// <summary>`store.Count`, the row count of an SoA store held in a local. A leaf like <see cref="AirArrayLength"/>:
/// a loop bound the passes read as leaf text (OwnedBounds matches `End: MCode`), so it derives from MCode.</summary>
public sealed class AirStoreCount : MCode
{
    public string Store { get; }

    public AirStoreCount(string store) : base(store + ".Count")
    {
        Store = store;
        Type = new AirType.Prim(PrimKind.Int);
    }
}
