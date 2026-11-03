using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Alice.Core.Json;

/// <summary>
/// Byte classification of one 64-byte block into bit masks, one implementation per instruction-set tier. Every tier
/// gives identical masks: whitespace is `table[b &amp; 15] == b` and operators are `table[b &amp; 15] == (b | 0x20)`
/// (simdjson's `lookup16`; bytes >= 0x80 never match), so `0x0C` and `0x1A` classify as operators and stage 2
/// rejects them by value.
/// </summary>
static class StructuralIndexTiers
{
    /// <summary>A tier: classifies the 64 bytes at <paramref name="p"/> into masks (bit i = byte i).</summary>
    public interface IClassifier
    {
        static abstract void Classify(ref byte p, out ulong quote, out ulong backslash, out ulong op, out ulong space, out ulong control);
    }

    static ReadOnlySpan<byte> SpaceTable => [(byte)' ', 100, 100, 100, 17, 100, 113, 2, 100, (byte)'\t', (byte)'\n', 112, 100, (byte)'\r', 100, 100];
    static ReadOnlySpan<byte> OpTable => [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)':', (byte)'{', (byte)',', (byte)'}', 0, 0];

    // Vector constants written as Create(...) of literals so the JIT embeds them (no static-field loads).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> Space128() => Vector128.Create((byte)' ', 100, 100, 100, 17, 100, 113, 2, 100, (byte)'\t', (byte)'\n', 112, 100, (byte)'\r', 100, 100);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> Op128() => Vector128.Create(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)':', (byte)'{', (byte)',', (byte)'}', 0, 0);

    /// <summary>AVX-512BW: one load, two vpshufb, five compares into mask registers.</summary>
    public struct Avx512 : IClassifier
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Classify(ref byte p, out ulong quote, out ulong backslash, out ulong op, out ulong space, out ulong control)
        {
            var v = Vector512.LoadUnsafe(ref p);
            var s4 = Vector256.Create(Space128(), Space128());
            var o4 = Vector256.Create(Op128(), Op128());
            quote = Vector512.Equals(v, Vector512.Create((byte)'"')).ExtractMostSignificantBits();
            backslash = Vector512.Equals(v, Vector512.Create((byte)'\\')).ExtractMostSignificantBits();
            space = Vector512.Equals(v, Avx512BW.Shuffle(Vector512.Create(s4, s4), v)).ExtractMostSignificantBits();
            op = Vector512.Equals(v | Vector512.Create((byte)0x20), Avx512BW.Shuffle(Vector512.Create(o4, o4), v)).ExtractMostSignificantBits();
            control = Vector512.LessThan(v, Vector512.Create((byte)0x20)).ExtractMostSignificantBits();
        }
    }

    /// <summary>AVX2: two 32-byte halves.</summary>
    public struct Avx2 : IClassifier
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Classify(ref byte p, out ulong quote, out ulong backslash, out ulong op, out ulong space, out ulong control)
        {
            var a = Vector256.LoadUnsafe(ref p);
            var b = Vector256.LoadUnsafe(ref p, 32);
            var st = Vector256.Create(Space128(), Space128());
            var ot = Vector256.Create(Op128(), Op128());
            var c20 = Vector256.Create((byte)0x20);
            quote = Join(Vector256.Equals(a, Vector256.Create((byte)'"')), Vector256.Equals(b, Vector256.Create((byte)'"')));
            backslash = Join(Vector256.Equals(a, Vector256.Create((byte)'\\')), Vector256.Equals(b, Vector256.Create((byte)'\\')));
            space = Join(Vector256.Equals(a, System.Runtime.Intrinsics.X86.Avx2.Shuffle(st, a)), Vector256.Equals(b, System.Runtime.Intrinsics.X86.Avx2.Shuffle(st, b)));
            op = Join(Vector256.Equals(a | c20, System.Runtime.Intrinsics.X86.Avx2.Shuffle(ot, a)), Vector256.Equals(b | c20, System.Runtime.Intrinsics.X86.Avx2.Shuffle(ot, b)));
            control = Join(Vector256.LessThan(a, c20), Vector256.LessThan(b, c20));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Join(Vector256<byte> lo, Vector256<byte> hi) => lo.ExtractMostSignificantBits() | ((ulong)hi.ExtractMostSignificantBits() << 32);
    }

    /// <summary>Any 128-bit SIMD (SSE2, NEON, wasm): compares only, four 16-byte quarters.</summary>
    public struct V128 : IClassifier
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Classify(ref byte p, out ulong quote, out ulong backslash, out ulong op, out ulong space, out ulong control)
        {
            quote = backslash = op = space = control = 0;
            for (int q = 0; q < 4; q++)
            {
                var v = Vector128.LoadUnsafe(ref p, (nuint)(16 * q));
                int sh = 16 * q;
                quote |= (ulong)Vector128.Equals(v, Vector128.Create((byte)'"')).ExtractMostSignificantBits() << sh;
                backslash |= (ulong)Vector128.Equals(v, Vector128.Create((byte)'\\')).ExtractMostSignificantBits() << sh;
                space |= (ulong)(Vector128.Equals(v, Vector128.Create((byte)' ')) | Vector128.Equals(v, Vector128.Create((byte)'\t'))
                               | Vector128.Equals(v, Vector128.Create((byte)'\n')) | Vector128.Equals(v, Vector128.Create((byte)'\r'))).ExtractMostSignificantBits() << sh;
                var c = v | Vector128.Create((byte)0x20);
                op |= (ulong)(Vector128.Equals(c, Vector128.Create((byte)'{')) | Vector128.Equals(c, Vector128.Create((byte)'}'))
                            | Vector128.Equals(c, Vector128.Create((byte)':')) | Vector128.Equals(c, Vector128.Create((byte)','))).ExtractMostSignificantBits() << sh;
                control |= (ulong)Vector128.LessThan(v, Vector128.Create((byte)0x20)).ExtractMostSignificantBits() << sh;
            }
        }
    }

    /// <summary>No SIMD: one byte at a time, same classes.</summary>
    public struct Scalar : IClassifier
    {
        public static void Classify(ref byte p, out ulong quote, out ulong backslash, out ulong op, out ulong space, out ulong control)
        {
            quote = backslash = op = space = control = 0;
            for (int i = 0; i < 64; i++)
            {
                byte b = Unsafe.Add(ref p, i);
                ulong bit = 1UL << i;
                if (b == '"') quote |= bit;
                else if (b == '\\') backslash |= bit;
                if (b < 0x80 && SpaceTable[b & 15] == b) space |= bit;
                if (b < 0x80 && OpTable[b & 15] == (b | 0x20)) op |= bit;
                if (b < 0x20) control |= bit;
            }
        }
    }
}
