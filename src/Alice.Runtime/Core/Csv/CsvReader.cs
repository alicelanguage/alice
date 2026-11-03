using System.Buffers;
using System.Buffers.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace Alice.Core.Csv;

/// <summary>Dialect of a <see cref="CsvReader"/> / <see cref="CsvWriter"/>: RFC 4180 by default.</summary>
/// <param name="Separator">Field separator byte (`,`; `\t` for TSV, `;` for European spreadsheets).</param>
/// <param name="Quote">Quote byte (`"`); a doubled quote inside a quoted field is one quote.</param>
public readonly record struct CsvOptions(byte Separator = (byte)',', byte Quote = (byte)'"');

/// <summary>
/// RFC 4180 reader over UTF-8 bytes, built like simdjson's stage 1: each 64-byte block is classified with SIMD into
/// bit masks of separators, newlines and quotes; a prefix-XOR of the quote mask (carried across blocks) gives the
/// in-quote region; rows are then cut at the remaining structural bits. Fields are zero-copy views
/// (<see cref="CsvField"/>); the field-offset buffer is pooled, so reading a row allocates 0 bytes.
/// Line ends may be LF or CRLF; quoted fields may contain separators and newlines.
/// </summary>
public ref struct CsvReader
{
    readonly ReadOnlySpan<byte> data;
    readonly byte sep, quote;
    int rowStart;          // start of the next row
    long blockBase;        // offset of the block whose bits are in `mask`
    ulong mask;            // unconsumed structural bits of the block at blockBase
    ulong lineEnds;        // the LF bits among them
    ulong inQuoteCarry;    // all ones when the previous block ended inside quotes
    int[] ends;            // field end offsets of the current row
    int fieldCount;
    bool done;

    /// <summary>A reader over <paramref name="utf8"/> (which must outlive it). Dispose returns the pooled buffer.</summary>
    public CsvReader(ReadOnlySpan<byte> utf8, CsvOptions options = default)
    {
        data = utf8;
        sep = options.Separator == 0 ? (byte)',' : options.Separator;
        quote = options.Quote == 0 ? (byte)'"' : options.Quote;
        if (sep is (byte)'\n' or (byte)'\r' || sep == quote) throw new ArgumentException("invalid CSV dialect", nameof(options));
        // a UTF-8 byte order mark is not data
        rowStart = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? 3 : 0;
        blockBase = -64;
        mask = 0;
        inQuoteCarry = 0;
        ends = ArrayPool<int>.Shared.Rent(64);
        fieldCount = 0;
        done = false;
        Current = default;
    }

    /// <summary>The row read by the last successful <see cref="MoveNext"/>.</summary>
    public CsvRow Current { get; private set; }

    /// <summary>Byte offset of the next row.</summary>
    public readonly long Position => rowStart;

    /// <summary>foreach support.</summary>
    public readonly CsvReader GetEnumerator() => this;

    /// <summary>Reads the next row into <paramref name="row"/>; false at the end of the input.</summary>
    public bool TryReadRow(out CsvRow row)
    {
        bool ok = MoveNext();
        row = Current;
        return ok;
    }

    /// <summary>Advances to the next row. An empty line is a row with one empty field (as Ruby's CSV gives []: check
    /// <see cref="CsvRow.IsBlank"/>).</summary>
    public bool MoveNext()
    {
        if (done || rowStart >= data.Length) { done = true; return false; }
        int start = rowStart, fc = 0;
        int[] e = ends;
        while (true)
        {
            if (mask == 0)
            {
                blockBase += 64;
                if (blockBase >= data.Length)
                {
                    // the last row has no line end
                    if (fc == e.Length) e = Grow(e, fc);
                    e[fc++] = data.Length;
                    rowStart = data.Length;
                    break;
                }
                mask = Classify((int)blockBase, out lineEnds);
                // drop structural bits before the row start (only after a byte order mark)
                if (blockBase < start) mask &= ulong.MaxValue << (int)(start - blockBase);
                continue;
            }
            int k = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            if (fc == e.Length) e = Grow(e, fc);
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(e), fc++) = (int)blockBase + k;
            if ((lineEnds >> k & 1) != 0) { rowStart = (int)blockBase + k + 1; break; }
        }
        ends = e;
        fieldCount = fc;
        Current = new CsvRow(data, start, ends.AsSpan(0, fieldCount), quote);
        return true;
    }

    static int[] Grow(int[] e, int count)
    {
        var n = ArrayPool<int>.Shared.Rent(e.Length * 2);
        e.AsSpan(0, count).CopyTo(n);
        ArrayPool<int>.Shared.Return(e);
        return n;
    }

    /// <summary>Structural bits (separators and LF outside quotes) of the 64-byte block at <paramref name="at"/>.</summary>
    ulong Classify(int at, out ulong lf)
    {
        ulong q, s, n;
        if (at + 64 <= data.Length) Masks(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), at), out q, out s, out n);
        else
        {
            Span<byte> tail = stackalloc byte[64];
            tail.Clear();
            data[at..].CopyTo(tail);
            Masks(ref MemoryMarshal.GetReference(tail), out q, out s, out n);
        }
        ulong inside = PrefixXor(q) ^ inQuoteCarry;
        inQuoteCarry = (ulong)((long)inside >> 63);
        lf = n & ~inside;
        return (s | n) & ~inside;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Masks(ref byte p, out ulong q, out ulong s, out ulong n)
    {
        if (Vector512.IsHardwareAccelerated)
        {
            var v = Vector512.LoadUnsafe(ref p);
            q = Vector512.Equals(v, Vector512.Create(quote)).ExtractMostSignificantBits();
            s = Vector512.Equals(v, Vector512.Create(sep)).ExtractMostSignificantBits();
            n = Vector512.Equals(v, Vector512.Create((byte)'\n')).ExtractMostSignificantBits();
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            var a = Vector256.LoadUnsafe(ref p);
            var b = Vector256.LoadUnsafe(ref p, 32);
            var vq = Vector256.Create(quote);
            var vs = Vector256.Create(sep);
            var vn = Vector256.Create((byte)'\n');
            q = Vector256.Equals(a, vq).ExtractMostSignificantBits() | (ulong)Vector256.Equals(b, vq).ExtractMostSignificantBits() << 32;
            s = Vector256.Equals(a, vs).ExtractMostSignificantBits() | (ulong)Vector256.Equals(b, vs).ExtractMostSignificantBits() << 32;
            n = Vector256.Equals(a, vn).ExtractMostSignificantBits() | (ulong)Vector256.Equals(b, vn).ExtractMostSignificantBits() << 32;
        }
        else
        {
            q = s = n = 0;
            for (int i = 0; i < 64; i++)
            {
                byte c = Unsafe.Add(ref p, i);
                if (c == quote) q |= 1UL << i;
                else if (c == sep) s |= 1UL << i;
                else if (c == (byte)'\n') n |= 1UL << i;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong PrefixXor(ulong x)
    {
        x ^= x << 1; x ^= x << 2; x ^= x << 4; x ^= x << 8; x ^= x << 16; x ^= x << 32;
        return x;
    }

    /// <summary>Returns the pooled field buffer.</summary>
    public void Dispose()
    {
        if (ends is { Length: > 0 }) ArrayPool<int>.Shared.Return(ends);
        ends = [];
    }
}

/// <summary>One row of a <see cref="CsvReader"/>: a view valid until the next <see cref="CsvReader.MoveNext"/>.</summary>
public readonly ref struct CsvRow
{
    readonly ReadOnlySpan<byte> data;
    readonly ReadOnlySpan<int> ends;
    readonly int start;
    readonly byte quote;

    internal CsvRow(ReadOnlySpan<byte> data, int start, ReadOnlySpan<int> ends, byte quote)
    {
        this.data = data; this.start = start; this.ends = ends; this.quote = quote;
    }

    /// <summary>Number of fields.</summary>
    public int Count => ends.Length;

    /// <summary>True for an empty line (one empty field).</summary>
    public bool IsBlank => ends.Length == 1 && this[0].Raw.IsEmpty && !this[0].IsQuoted;

    /// <summary>Byte offset of the row in the input.</summary>
    public int Offset => start;

    /// <summary>Field <paramref name="index"/>.</summary>
    public CsvField this[int index]
    {
        get
        {
            if ((uint)index >= (uint)ends.Length) throw new ArgumentOutOfRangeException(nameof(index));
            ref int e0 = ref MemoryMarshal.GetReference(ends);
            int s = index == 0 ? start : Unsafe.Add(ref e0, index - 1) + 1;
            int e = Unsafe.Add(ref e0, index);
            ref byte d0 = ref MemoryMarshal.GetReference(data);
            // the line end of the last field: CRLF or LF (or a final CR at the end of the input)
            if (index == ends.Length - 1 && e > s && Unsafe.Add(ref d0, e - 1) == (byte)'\r') e--;
            return new CsvField(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref d0, s), e - s), quote);
        }
    }

    /// <summary>Every field as a string (allocates the array and strings).</summary>
    public string[] ToArray()
    {
        var a = new string[ends.Length];
        for (int i = 0; i < a.Length; i++) a[i] = this[i].ToString();
        return a;
    }
}

/// <summary>One field: zero-copy bytes with its quotes removed; <see cref="HasEscapes"/> tells whether doubled quotes
/// still need decoding (<see cref="CopyTo"/>, <see cref="ToString"/> decode them).</summary>
public readonly ref struct CsvField
{
    readonly byte quote;

    /// <summary>The bytes between the quotes (or the whole field when unquoted). Escapes are not decoded.</summary>
    public ReadOnlySpan<byte> Raw { get; }

    /// <summary>The field was quoted.</summary>
    public bool IsQuoted { get; }

    internal CsvField(ReadOnlySpan<byte> field, byte quote)
    {
        this.quote = quote;
        if (field.Length >= 2 && field[0] == quote && field[^1] == quote) { Raw = field[1..^1]; IsQuoted = true; }
        else if (field.Length == 1 && field[0] == quote) { Raw = default; IsQuoted = true; }
        else { Raw = field; IsQuoted = false; }
    }

    /// <summary>The quoted text contains a doubled quote.</summary>
    public bool HasEscapes => IsQuoted && Raw.IndexOf(quote) >= 0;

    /// <summary>Length of the decoded field in bytes.</summary>
    public int DecodedLength => HasEscapes ? Raw.Length - Raw.Count(quote) / 2 : Raw.Length;

    /// <summary>Writes the decoded bytes into <paramref name="destination"/>; returns the count.</summary>
    public int CopyTo(Span<byte> destination)
    {
        if (!HasEscapes) { Raw.CopyTo(destination); return Raw.Length; }
        int w = 0;
        var r = Raw;
        for (int i = 0; i < r.Length; i++)
        {
            destination[w++] = r[i];
            if (r[i] == quote && i + 1 < r.Length && r[i + 1] == quote) i++;
        }
        return w;
    }

    /// <summary>The decoded field as a string.</summary>
    public override string ToString()
    {
        if (!HasEscapes) return Encoding.UTF8.GetString(Raw);
        Span<byte> tmp = Raw.Length <= 512 ? stackalloc byte[Raw.Length] : new byte[Raw.Length];
        return Encoding.UTF8.GetString(tmp[..CopyTo(tmp)]);
    }

    /// <summary>Byte equality with an unescaped value.</summary>
    public bool Equals(ReadOnlySpan<byte> utf8) => !HasEscapes ? Raw.SequenceEqual(utf8) : ToString() == Encoding.UTF8.GetString(utf8);

    /// <summary>Parses an integer (no allocation).</summary>
    public bool TryGetInt64(out long value) => Utf8Parser.TryParse(Raw, out value, out int n) && n == Raw.Length;

    /// <summary>Parses a float (invariant; no allocation).</summary>
    public bool TryGetDouble(out double value) => double.TryParse(Raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>Parses an integer; throws <see cref="FormatException"/> when the field is not one.</summary>
    public long GetInt64() => TryGetInt64(out long v) ? v : throw new FormatException($"CSV field is not an integer: \"{ToString()}\"");

    /// <summary>Parses a float; throws <see cref="FormatException"/> when the field is not one.</summary>
    public double GetDouble() => TryGetDouble(out double v) ? v : throw new FormatException($"CSV field is not a number: \"{ToString()}\"");
}
