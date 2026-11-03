using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Alice.Core.Json;

/// <summary>
/// Stage 1 (simdjson, Langdale and Lemire 2019): one pass over 64-byte blocks that finds the structural characters
/// (`{}[]:,`, the opening quote of each string and the first byte of each number/literal) outside strings, and
/// rejects unescaped control characters and invalid escapes inside strings. Strings are tracked with the
/// odd-backslash-run trick and a prefix-xor of the quote mask (carry-less multiply). Byte classes come from in-lane
/// nibble shuffles (vpshufb) on AVX-512BW / AVX2, compares on any 128-bit SIMD, a scalar loop otherwise
/// (<see cref="StructuralIndexTiers"/>). UTF-8 validity is checked separately with <c>Utf8.IsValid</c>.
/// </summary>
static class StructuralIndex
{
    /// <summary>Instruction set used for byte classification (tests force each tier).</summary>
    internal enum Tier { Auto, Avx512, Avx2, Vector128, Scalar }

    /// <summary>Forced tier (tests, benchmarks; env `ALICE_JSON_TIER=avx512|avx2|vector128|scalar`); Auto = best available.</summary>
    internal static Tier Force = Enum.TryParse<Tier>(Environment.GetEnvironmentVariable("ALICE_JSON_TIER"), true, out var t) ? t : Tier.Auto;

    /// <summary>The tier <see cref="Build"/> uses on this machine.</summary>
    internal static Tier Best =>
        Avx512BW.IsSupported && Vector512.IsHardwareAccelerated ? Tier.Avx512
        : Avx2.IsSupported ? Tier.Avx2
        : Vector128.IsHardwareAccelerated ? Tier.Vector128
        : Tier.Scalar;

    /// <summary>Capacity <see cref="Build"/> needs for <paramref name="length"/> input bytes (sentinel + unrolled stores).</summary>
    public static int Capacity(int length) => length + 24;

    /// <summary>
    /// Writes the offsets of the structural characters of <paramref name="input"/> to <paramref name="positions"/>
    /// (at least <see cref="Capacity"/> entries) followed by the sentinel <c>input.Length</c>; returns their count
    /// (sentinel excluded). Throws <see cref="JsonParseException"/> for unclosed strings, control characters in
    /// strings and invalid escapes.
    /// </summary>
    public static int Build(ReadOnlySpan<byte> input, Span<uint> positions) => Build(input, positions, default);

    /// <summary>As <see cref="Build(ReadOnlySpan{byte}, Span{uint})"/>, also copying the input to
    /// <paramref name="copyTo"/> (empty: no copy) in the same pass.</summary>
    public static int Build(ReadOnlySpan<byte> input, Span<uint> positions, Span<byte> copyTo)
    {
        if (positions.Length < Capacity(input.Length)) throw new ArgumentException("index buffer too small", nameof(positions));
        bool copy = !copyTo.IsEmpty;
        if (copy && copyTo.Length < input.Length) throw new ArgumentException("copy buffer too small", nameof(copyTo));
        ref byte cdst = ref MemoryMarshal.GetReference(copyTo);
        var tier = Force == Tier.Auto ? Best : Force;
        return tier switch
        {
            Tier.Avx512 => Build<StructuralIndexTiers.Avx512>(input, positions, ref cdst, copy),
            Tier.Avx2 => Build<StructuralIndexTiers.Avx2>(input, positions, ref cdst, copy),
            Tier.Vector128 => Build<StructuralIndexTiers.V128>(input, positions, ref cdst, copy),
            _ => Build<StructuralIndexTiers.Scalar>(input, positions, ref cdst, copy),
        };
    }

    struct State
    {
        public ulong PrevEscaped;   // 1 when the first byte of the next block is escaped
        public ulong PrevInString;  // all ones when the previous block ended inside a string
        public ulong PrevScalar;    // 1 when the previous block ended inside a scalar
    }

    const ulong OddBits = 0xAAAAAAAAAAAAAAAAUL;

    // The main loop is written out (no helper taking the state by reference) so the JIT keeps the masks and the
    // carried state in registers; Tail handles the last partial block with the same steps through State.
    static int Build<T>(ReadOnlySpan<byte> input, Span<uint> positions, ref byte cdst, bool copy) where T : struct, StructuralIndexTiers.IClassifier
    {
        ulong prevEscaped = 0, prevInString = 0, prevScalar = 0;
        int n = 0, len = input.Length, b = 0;
        ref byte src = ref MemoryMarshal.GetReference(input);
        ref uint dst = ref MemoryMarshal.GetReference(positions);
        for (; b + 64 <= len; b += 64)
        {
            T.Classify(ref Unsafe.Add(ref src, b), out ulong quoteRaw, out ulong bs, out ulong op, out ulong space, out ulong ctrl);
            if (copy) Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref cdst, b), ref Unsafe.Add(ref src, b), 64);
            ulong escaped;
            if (bs == 0)
            {
                escaped = prevEscaped;
                prevEscaped = 0;
            }
            else
            {
                ulong potential = bs & ~prevEscaped;
                ulong code = (((potential << 1) | OddBits) - potential) ^ OddBits;
                escaped = code ^ (bs | prevEscaped);
                prevEscaped = (code & bs) >> 63;
            }
            ulong quote = quoteRaw & ~escaped;
            ulong inString = PrefixXor(quote) ^ prevInString;
            prevInString = (ulong)((long)inString >> 63);
            if (((ctrl | escaped) & inString) != 0) CheckStrings(input, b, ctrl & inString, escaped & inString);
            ulong scalar = ~(space | op | quoteRaw | inString);
            ulong structural = (op & ~inString) | (scalar & ~((scalar << 1) | prevScalar)) | (quote & inString);
            prevScalar = scalar >> 63;
            if (structural != 0) n = Flatten(ref dst, n, (uint)b, structural);
        }
        if (b < len)
        {
            var st = new State { PrevEscaped = prevEscaped, PrevInString = prevInString, PrevScalar = prevScalar };
            n = Tail<T>(input, b, ref dst, n, ref st);
            if (copy) input.Slice(b).CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref cdst, b), len - b));
            prevInString = st.PrevInString;
        }
        if (prevInString != 0) throw new JsonParseException("unterminated string", LastQuote(input));
        positions[n] = (uint)len;
        return n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Tail<T>(ReadOnlySpan<byte> input, int b, ref uint dst, int n, ref State st) where T : struct, StructuralIndexTiers.IClassifier
    {
        Span<byte> tail = stackalloc byte[64];
        tail.Fill((byte)' ');
        input.Slice(b).CopyTo(tail);
        T.Classify(ref MemoryMarshal.GetReference(tail), out ulong quoteRaw, out ulong bs, out ulong op, out ulong space, out ulong ctrl);
        ulong escaped;
        if (bs == 0)
        {
            escaped = st.PrevEscaped;
            st.PrevEscaped = 0;
        }
        else
        {
            ulong potential = bs & ~st.PrevEscaped;
            ulong code = (((potential << 1) | OddBits) - potential) ^ OddBits;
            escaped = code ^ (bs | st.PrevEscaped);
            st.PrevEscaped = (code & bs) >> 63;
        }
        ulong quote = quoteRaw & ~escaped;
        ulong inString = PrefixXor(quote) ^ st.PrevInString;
        st.PrevInString = (ulong)((long)inString >> 63);
        if (((ctrl | escaped) & inString) != 0) CheckStrings(input, b, ctrl & inString, escaped & inString);
        ulong scalar = ~(space | op | quoteRaw | inString);
        ulong structural = (op & ~inString) | (scalar & ~((scalar << 1) | st.PrevScalar)) | (quote & inString);
        st.PrevScalar = scalar >> 63;
        return structural != 0 ? Flatten(ref dst, n, (uint)b, structural) : n;
    }

    /// <summary>Errors inside strings of one block: control characters, invalid escapes (the first one wins).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CheckStrings(ReadOnlySpan<byte> input, int at, ulong ctrl, ulong esc)
    {
        int c = ctrl == 0 ? 64 : BitOperations.TrailingZeroCount(ctrl);
        if (esc != 0) CheckEscapes(input, at, c == 64 ? esc : esc & ((1UL << c) - 1));
        if (ctrl != 0) throw new JsonParseException("control character in string", at + c);
    }

    static void CheckEscapes(ReadOnlySpan<byte> input, int at, ulong esc)
    {
        if (esc == 0) return;
        do
        {
            int k = at + BitOperations.TrailingZeroCount(esc);
            if (k >= input.Length) throw new JsonParseException("unterminated string", k);
            switch (input[k])
            {
                case (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t': break;
                case (byte)'u':
                    if (Strings.Hex4(input, k + 1) < 0) throw new JsonParseException("invalid \\u escape", k - 1);
                    break;
                default: throw new JsonParseException("invalid escape", k - 1);
            }
            esc &= esc - 1;
        } while (esc != 0);
    }

    /// <summary>Prefix xor: bit i = xor of bits 0..i (carry-less multiply by all ones).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong PrefixXor(ulong x)
    {
        if (Pclmulqdq.IsSupported)
            return Pclmulqdq.CarrylessMultiply(Vector128.CreateScalarUnsafe(x), Vector128.Create(ulong.MaxValue), 0).ToScalar();
        x ^= x << 1;
        x ^= x << 2;
        x ^= x << 4;
        x ^= x << 8;
        x ^= x << 16;
        x ^= x << 32;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Flatten(ref uint dst, int n, uint baseIdx, ulong bits)
    {
        int cnt = BitOperations.PopCount(bits);
        ref uint d = ref Unsafe.Add(ref dst, n);
        Unsafe.Add(ref d, 0) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
        Unsafe.Add(ref d, 1) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
        Unsafe.Add(ref d, 2) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
        Unsafe.Add(ref d, 3) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
        if (cnt > 4)
        {
            Unsafe.Add(ref d, 4) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
            Unsafe.Add(ref d, 5) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
            Unsafe.Add(ref d, 6) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
            Unsafe.Add(ref d, 7) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits); bits &= bits - 1;
            if (cnt > 8)
            {
                for (int i = 8; i < cnt; i++)
                {
                    Unsafe.Add(ref d, i) = baseIdx + (uint)BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                }
            }
        }
        return n + cnt;
    }

    static int LastQuote(ReadOnlySpan<byte> input) => Math.Max(0, input.LastIndexOf((byte)'"'));
}
