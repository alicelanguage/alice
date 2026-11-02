using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// One 64-bit word naming an affine arena value (ADR 0037). Layout: bit 63 packed flag; bits 56-62 variant tag
/// (0..127); bits 32-55 lane id (0..2^24-1); bits 0-31 the payload of a packed variant (no fields, or one field of at
/// most 32 bits, which never touches memory) or the slot index of a node in lane <see cref="Lane"/> of
/// <see cref="Arena{TSlot}"/>. Packed terms carry lane 0.
/// </summary>
public readonly struct Term : IEquatable<Term>
{
    /// <summary>Largest variant tag (7 bits).</summary>
    public const int MaxTag = 127;

    /// <summary>Largest lane id (24 bits).</summary>
    public const int MaxLane = (1 << 24) - 1;

    const ulong PackedBit = 1UL << 63;

    /// <summary>The raw word.</summary>
    public readonly ulong Raw;

    /// <summary>Wraps a raw word (no validation; the arena bounds-checks every slot access).</summary>
    public Term(ulong raw) => Raw = raw;

    /// <summary>A packed variant: tag plus a 32-bit payload, no slot.</summary>
    [Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Term Pack(int tag, uint payload)
    {
        if ((uint)tag > MaxTag) ThrowTag(tag);
        return new Term(PackedBit | (ulong)(uint)tag << 56 | payload);
    }

    /// <summary>A slot variant: tag, owning lane and slot index.</summary>
    [Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Term Node(int tag, int lane, uint slot)
    {
        if ((uint)tag > MaxTag) ThrowTag(tag);
        if ((uint)lane > MaxLane) throw new ArgumentOutOfRangeException(nameof(lane), lane, $"lane must be 0..{MaxLane}");
        return new Term((ulong)(uint)tag << 56 | (ulong)(uint)lane << 32 | slot);
    }

    /// <summary>Splits a packed term into tag and payload; throws for a slot term.</summary>
    [Pure]
    public static void Unpack(Term term, out int tag, out uint payload)
    {
        if (!term.IsPacked) throw new ArgumentException($"{term} is a slot term, not a packed one", nameof(term));
        tag = term.Tag;
        payload = term.Payload;
    }

    /// <summary>Whether the value lives in the word itself (no slot).</summary>
    public bool IsPacked => (long)Raw < 0;

    /// <summary>Variant tag, 0..127.</summary>
    public int Tag => (int)(Raw >> 56) & MaxTag;

    /// <summary>Lane that owns the slot (0 for packed terms).</summary>
    public int Lane => (int)(Raw >> 32) & MaxLane;

    /// <summary>Slot index in the owning lane (meaningful for slot terms).</summary>
    public uint Slot => (uint)Raw;

    /// <summary>Payload of a packed term (the low 32 bits).</summary>
    public uint Payload => (uint)Raw;

    public bool Equals(Term other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Term t && t.Raw == Raw;
    public override int GetHashCode() => Raw.GetHashCode();
    public static bool operator ==(Term a, Term b) => a.Raw == b.Raw;
    public static bool operator !=(Term a, Term b) => a.Raw != b.Raw;

    public override string ToString() =>
        IsPacked ? $"Term(packed tag={Tag} payload={Payload})" : $"Term(tag={Tag} lane={Lane} slot={Slot})";

    static void ThrowTag(int tag) => throw new ArgumentOutOfRangeException(nameof(tag), tag, $"variant tag must be 0..{MaxTag}");
}
