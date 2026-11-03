namespace Alice.Compiler.Mir;

// Affine arena nodes (ADR 0037, IL shapes ADR 0040 section 2). An arena value is a 64-bit term word (`ulong` in both
// backends): bit 63 packed flag, bits 56-62 variant tag, bits 32-55 lane, bits 0-31 payload or slot index. A slot
// is a ValueTuple of `ulong` words, one word per field of the enum's widest slot variant (`ulong` itself for one
// word). Children are ordinary expressions: Map hands every one of them to the MIR passes.

/// <summary>How one variant field is stored in a 64-bit word (a slot word or a packed payload).</summary>
public enum WordKind { Term, U64, I64, U32, I32, U16, I16, U8, I8, Bool, Char, F32, F64 }

/// <summary>Whether a slot access may drop the stale check `i &lt; Len` (ADR 0040 `SlotProof`).</summary>
public enum SlotProof { Unknown, Live }

/// <summary>The slab of one arena enum, shared by every node of that enum.</summary>
public sealed class AirSlab
{
    /// <summary>The Alice enum name (dumps).</summary>
    public string Enum = "";
    /// <summary>Number of 64-bit words per slot (1..7).</summary>
    public int Words;
    /// <summary>Slot terms are valid wherever they are read (no region that a term could outlive).</summary>
    public SlotProof Proof = SlotProof.Live;
    /// <summary>Bit k: slot word k holds a term (a variant that leaves it unused stores <see cref="PackedNil"/>).</summary>
    public uint TermWords;

    /// <summary>A packed term with tag 0 and payload 0: what an unused term word holds (a recursive Drop skips it).</summary>
    public const ulong PackedNil = 1UL << 63;

    /// <summary>The slot type as C#: `ulong` or `(ulong, ulong, ...)`.</summary>
    public string SlotCs => Words == 1 ? "ulong" : "(" + string.Join(", ", Enumerable.Repeat("ulong", Words)) + ")";

    /// <summary>The C# member of word <paramref name="i"/> of a slot value.</summary>
    public string Item(int i) => Words == 1 ? "" : ".Item" + (i + 1);

    public static WordKind? KindOf(string cs) => cs switch
    {
        "ulong" => WordKind.U64,
        "long" => WordKind.I64,
        "uint" => WordKind.U32,
        "int" => WordKind.I32,
        "ushort" => WordKind.U16,
        "short" => WordKind.I16,
        "byte" => WordKind.U8,
        "sbyte" => WordKind.I8,
        "bool" => WordKind.Bool,
        "char" => WordKind.Char,
        "float" => WordKind.F32,
        "double" => WordKind.F64,
        _ => null,
    };

    /// <summary>Fits a packed payload (at most 32 bits).</summary>
    public static bool Narrow(WordKind k) => k is not (WordKind.Term or WordKind.U64 or WordKind.I64 or WordKind.F64);

    public static string Cs(WordKind k) => k switch
    {
        WordKind.Term or WordKind.U64 => "ulong",
        WordKind.I64 => "long",
        WordKind.U32 => "uint",
        WordKind.I32 => "int",
        WordKind.U16 => "ushort",
        WordKind.I16 => "short",
        WordKind.U8 => "byte",
        WordKind.I8 => "sbyte",
        WordKind.Bool => "bool",
        WordKind.Char => "char",
        WordKind.F32 => "float",
        _ => "double",
    };

    /// <summary>The packed flag of a term word.</summary>
    public const ulong PackedBit = 1UL << 63;

    /// <summary>The high bits of a packed term of variant <paramref name="tag"/>.</summary>
    public static ulong PackedHead(int tag) => PackedBit | (ulong)(uint)tag << 56;
}

/// <summary>Base of the arena nodes: every child is rewritable.</summary>
public abstract class AirArenaExpr : AirParExpr
{
}

/// <summary>`AirTermPack(variant, payload)`: a packed variant as a term word, `1&lt;&lt;63 | tag&lt;&lt;56 | payload`
/// (payload at most 32 bits; null for a variant without fields).</summary>
public sealed class AirTermPack : AirArenaExpr
{
    public string Variant = "";
    public int Tag;
    public MExpr? Payload;
    public WordKind Kind = WordKind.U32;

    public override IEnumerable<MExpr> Children() => Payload != null ? [Payload] : [];
    public override void Map(Func<MExpr, MExpr> f) { if (Payload != null) Payload = f(Payload); }
}

/// <summary>`AirTermTag(term)`: the top byte of a term word as an Int32, packed flag (0x80) plus tag, so packed and
/// slot variants dispatch through one key.</summary>
public sealed class AirTermTag : AirArenaExpr
{
    public MExpr Term = null!;

    public override IEnumerable<MExpr> Children() => [Term];
    public override void Map(Func<MExpr, MExpr> f) => Term = f(Term);
}

/// <summary>The payload field of a packed term (the low 32 bits) in its field type.</summary>
public sealed class AirTermPayload : AirArenaExpr
{
    public MExpr Term = null!;
    public WordKind Kind = WordKind.U32;

    public override IEnumerable<MExpr> Children() => [Term];
    public override void Map(Func<MExpr, MExpr> f) => Term = f(Term);
}

/// <summary>`AirArenaAlloc(type, fields...)`: stores the fields of a slot variant in a slot of this thread's lane (the
/// free list first, else the bump cursor) and yields its term word. <see cref="Fields"/> has one value per variant
/// field, in declaration order; the remaining slot words are zero.</summary>
public sealed class AirArenaAlloc : AirArenaExpr
{
    public AirSlab Layout = null!;
    public string Variant = "";
    public int Tag;
    public List<MExpr> Fields = new();
    public List<WordKind> Kinds = new();
    /// <summary>The slot word of each field (default: field i in word i).</summary>
    public List<int>? Words;

    /// <summary>The value of slot word <paramref name="k"/>: its field index, or -1 (zero, or <see cref="AirSlab.PackedNil"/> for a term word).</summary>
    public int FieldAt(int k) => Words == null ? (k < Fields.Count ? k : -1) : Words.IndexOf(k);

    public override IEnumerable<MExpr> Children() => Fields;
    public override void Map(Func<MExpr, MExpr> f)
    {
        for (int i = 0; i < Fields.Count; i++) Fields[i] = f(Fields[i]);
    }
}

/// <summary>`AirArenaTake(term)`: the slot value of a slot term; with <see cref="Frees"/> the slot is pushed on the free
/// list afterwards when this thread's lane owns it (a foreign lane's node is only read, ADR 0037 rule 3).</summary>
public sealed class AirArenaTake : AirArenaExpr
{
    public AirSlab Layout = null!;
    public MExpr Term = null!;
    public bool Frees = true;

    public override IEnumerable<MExpr> Children() => [Term];
    public override void Map(Func<MExpr, MExpr> f) => Term = f(Term);
}

/// <summary>`AirArenaBorrow(term)`: the slot value of a slot term, nothing freed.</summary>
public sealed class AirArenaBorrow : AirArenaExpr
{
    public AirSlab Layout = null!;
    public MExpr Term = null!;

    public override IEnumerable<MExpr> Children() => [Term];
    public override void Map(Func<MExpr, MExpr> f) => Term = f(Term);
}

/// <summary>Frees the slot of a term without reading it (a Take whose fields are all dead, or an owned value that dies
/// unconsumed). A packed or foreign-lane term is left alone. With <see cref="All"/> non-zero the Drop is recursive
/// (Bend's free of the subtree): the term words in <see cref="Top"/> of this node, and <see cref="All"/> of every
/// node below, are dropped too; words a borrowing arm bound to locals are left out of <see cref="Top"/> (the locals own
/// those children now). Void.</summary>
public sealed class AirArenaDrop : AirArenaExpr
{
    public AirSlab Layout = null!;
    public MExpr Term = null!;
    public uint Top, All;

    public override IEnumerable<MExpr> Children() => [Term];
    public override void Map(Func<MExpr, MExpr> f) => Term = f(Term);
}

/// <summary>Word <see cref="Word"/> of a slot value read by Take/Borrow, converted to its field type.</summary>
public sealed class AirSlotWord : AirArenaExpr
{
    public AirSlab Layout = null!;
    public MExpr Slot = null!;
    public int Word;
    public WordKind Kind;

    public override IEnumerable<MExpr> Children() => [Slot];
    public override void Map(Func<MExpr, MExpr> f) => Slot = f(Slot);
}

/// <summary>`AirArenaRegion`: opens an arena region (`scope ... end`); the lowering stores it in a local and closes it
/// with <see cref="AirArenaRegionEnd"/> in a `finally`, so the reset runs at every exit.</summary>
public sealed class AirArenaRegion : AirArenaExpr
{
    public override IEnumerable<MExpr> Children() => [];
}

/// <summary>Ends the region held in <see cref="Region"/> (a local): every lane cursor goes back to its mark. Void.</summary>
public sealed class AirArenaRegionEnd : AirArenaExpr
{
    public MExpr Region = null!;

    public override IEnumerable<MExpr> Children() => [Region];
    public override void Map(Func<MExpr, MExpr> f) => Region = f(Region);
}

/// <summary>`AirLane()`: this thread's arena lane id (Int32).</summary>
public sealed class AirLane : AirArenaExpr
{
    public override IEnumerable<MExpr> Children() => [];
}
