using System.Globalization;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>
/// C# for the affine arena nodes (Mir/MExpr.Arena.cs, ADR 0037): calls of the public runtime API
/// (`Arena&lt;TSlot&gt;.Alloc/Take/Borrow`, `Term.Pack`) on `ulong` term words, so the C# backend runs the same
/// arena program as the IL backend, which inlines the word arithmetic (Backend/Il/AirToIl.Arena.cs). Every form
/// is an atomic C# operand (parenthesized or a call), as <see cref="Atomic"/> assumes for AIR nodes.
/// </summary>
public sealed partial class Printer
{
    const string Rt = "global::Alice.Runtime.";

    static string Slab(AirSlab l) => Rt + "Arena<" + l.SlotCs + ">";

    static string? EArena(MExpr e)
    {
        switch (e)
        {
            case AirTermPack p:
                if (p.Payload == null) return "0x" + AirSlab.PackedHead(p.Tag).ToString("X16", CultureInfo.InvariantCulture) + "UL";
                return Rt + "Term.Pack(" + p.Tag.ToString(CultureInfo.InvariantCulture) + ", " + ToPayload(E(p.Payload), p.Kind) + ").Raw";
            case AirTermTag t:
                return "unchecked((int)(" + E(t.Term) + " >> 56))";
            case AirTermPayload p:
                return FromWord("unchecked((uint)" + P(p.Term) + ")", p.Kind, payload: true);
            case AirArenaAlloc a:
                {
                    var words = new List<string>();
                    for (int k = 0; k < a.Layout.Words; k++)
                        words.Add(a.FieldAt(k) is int i and >= 0 ? ToWord(E(a.Fields[i]), a.Kinds[i])
                            : (a.Layout.TermWords & (1u << k)) != 0 ? "0x8000000000000000UL" : "0UL");
                    string slot = words.Count == 1 ? words[0] : "(" + string.Join(", ", words) + ")";
                    return Slab(a.Layout) + ".Alloc(" + a.Tag.ToString(CultureInfo.InvariantCulture) + ", " + slot + ").Raw";
                }
            case AirArenaTake t:
                return Slab(t.Layout) + (t.Frees ? ".TakeValue(" : ".BorrowValue(") + E(t.Term) + ")";
            case AirArenaBorrow b:
                return Slab(b.Layout) + ".Borrow(new " + Rt + "Term(" + E(b.Term) + "))";
            case AirArenaDrop d:
                if (d.All != 0) return Slab(d.Layout) + ".DropDeep(" + E(d.Term) + ", " + d.Top.ToString(CultureInfo.InvariantCulture) + "u, " + d.All.ToString(CultureInfo.InvariantCulture) + "u)";
                return Slab(d.Layout) + ".Drop(" + E(d.Term) + ")";
            case AirSlotWord w:
                return FromWord(P(w.Slot) + w.Layout.Item(w.Word), w.Kind, payload: false);
            case AirArenaRegion:
                return Rt + "ArenaRegion.Enter()";
            case AirArenaRegionEnd r:
                return P(r.Region) + ".Dispose()";
            case AirLane:
                return Rt + "ArenaLane.Current";
        }
        return null;
    }

    /// <summary>A field value as a 64-bit slot word.</summary>
    static string ToWord(string v, WordKind k) => k switch
    {
        WordKind.Term or WordKind.U64 => "(" + v + ")",
        WordKind.I64 => "unchecked((ulong)(" + v + "))",
        WordKind.I32 => "unchecked((ulong)(uint)(" + v + "))",
        WordKind.I16 => "unchecked((ulong)(ushort)(" + v + "))",
        WordKind.I8 => "unchecked((ulong)(byte)(" + v + "))",
        WordKind.Bool => "((" + v + ") ? 1UL : 0UL)",
        WordKind.F32 => "((ulong)global::System.BitConverter.SingleToUInt32Bits(" + v + "))",
        WordKind.F64 => "global::System.BitConverter.DoubleToUInt64Bits(" + v + ")",
        _ => "((ulong)(" + v + "))",   // U32 U16 U8 Char
    };

    /// <summary>A field value as the 32-bit payload of a packed term.</summary>
    static string ToPayload(string v, WordKind k) => k switch
    {
        WordKind.U32 => "(" + v + ")",
        WordKind.I32 => "unchecked((uint)(" + v + "))",
        WordKind.I16 => "unchecked((uint)(ushort)(" + v + "))",
        WordKind.I8 => "unchecked((uint)(byte)(" + v + "))",
        WordKind.Bool => "((" + v + ") ? 1u : 0u)",
        WordKind.F32 => "global::System.BitConverter.SingleToUInt32Bits(" + v + ")",
        _ => "((uint)(" + v + "))",   // U16 U8 Char
    };

    /// <summary>A slot word (or a packed payload, already `(uint)`) back in its field type.</summary>
    static string FromWord(string w, WordKind k, bool payload) => k switch
    {
        WordKind.Term or WordKind.U64 => "(" + w + ")",
        WordKind.I64 => "unchecked((long)" + w + ")",
        WordKind.U32 => payload ? "(" + w + ")" : "unchecked((uint)" + w + ")",
        WordKind.I32 => "unchecked((int)(uint)" + w + ")",
        WordKind.U16 => "unchecked((ushort)" + w + ")",
        WordKind.I16 => "unchecked((short)(ushort)" + w + ")",
        WordKind.U8 => "unchecked((byte)" + w + ")",
        WordKind.I8 => "unchecked((sbyte)(byte)" + w + ")",
        WordKind.Bool => "(" + w + " != 0)",
        WordKind.Char => "unchecked((char)" + w + ")",
        WordKind.F32 => "global::System.BitConverter.UInt32BitsToSingle(unchecked((uint)" + w + "))",
        _ => "global::System.BitConverter.UInt64BitsToDouble(" + w + ")",
    };
}
