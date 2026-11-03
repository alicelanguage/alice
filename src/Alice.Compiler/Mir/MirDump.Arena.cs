namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the affine arena nodes (Mir/MExpr.Arena.cs).</summary>
public sealed partial class MirDump
{
    string? CoreArena(MExpr e)
    {
        switch (e)
        {
            case AirTermPack p:
                return $"(term-pack {p.Variant} {p.Tag}{(p.Payload != null ? " " + Kind(p.Kind) + " " + E(p.Payload) : "")})";
            case AirTermTag t:
                return $"(term-tag {E(t.Term)})";
            case AirTermPayload p:
                return $"(term-payload {Kind(p.Kind)} {E(p.Term)})";
            case AirArenaAlloc a:
                return $"(arena-alloc {a.Layout.Enum} {a.Variant} {a.Tag}{string.Concat(a.Fields.Select((f, i) => " " + (a.Words != null ? "w" + a.Words[i] + ":" : "") + Kind(a.Kinds[i]) + ":" + E(f)))})";
            case AirArenaTake t:
                return $"(arena-take {t.Layout.Enum}{(t.Frees ? " frees" : "")}{(t.Layout.Proof == SlotProof.Live ? " live" : "")} {E(t.Term)})";
            case AirArenaBorrow b:
                return $"(arena-borrow {b.Layout.Enum}{(b.Layout.Proof == SlotProof.Live ? " live" : "")} {E(b.Term)})";
            case AirArenaDrop d:
                return $"(arena-drop {d.Layout.Enum}{(d.All != 0 ? $" deep {d.Top:x}/{d.All:x}" : "")} {E(d.Term)})";
            case AirSlotWord w:
                return $"(slot-word {w.Word} {Kind(w.Kind)} {E(w.Slot)})";
            case AirArenaRegion:
                return "(arena-region)";
            case AirArenaRegionEnd r:
                return $"(arena-region-end {E(r.Region)})";
            case AirLane:
                return "(lane)";
        }
        return null;
    }

    static string Kind(WordKind k) => k.ToString().ToLowerInvariant();
}
