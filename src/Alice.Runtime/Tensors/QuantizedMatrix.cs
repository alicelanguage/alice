using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>
/// Block quantization, 32 values per block, one Half scale per block (ADR 0030).
/// Int8Block: d = max|x| / 127, q = round(x / d), 34 bytes per block (2 scale + 32 sbyte).
/// Int4Block: d = signed max / -8, q in [0, 15] stores x/d + 8, low nibbles hold values 0..15 and
/// high nibbles 16..31, 18 bytes per block (2 scale + 16 bytes). Same byte layout as GGML Q8_0 / Q4_0.
/// </summary>
public enum QuantFormat { Int8Block, Int4Block }

/// <summary>Row-major matrix of quantized blocks, owned (native aligned) or a view over external memory.</summary>
public sealed unsafe class QuantizedMatrix : IDisposable
{
    public const int BlockSize = 32;

    byte* _data;
    readonly bool _owns;
    readonly object? _keepAlive;

    public int Rows { get; }
    public int Columns { get; }
    public QuantFormat Format { get; }
    public int RowBytes { get; }
    public byte* Pointer => _data;
    public ReadOnlySpan<byte> Bytes => new(_data, checked(RowBytes * Rows));

    public static int BlockBytes(QuantFormat f) => f == QuantFormat.Int8Block ? 34 : 18;

    QuantizedMatrix(byte* data, int rows, int cols, QuantFormat fmt, bool owns, object? keepAlive)
    {
        if (cols % BlockSize != 0) throw new ArgumentException($"columns must be a multiple of {BlockSize}");
        _data = data; Rows = rows; Columns = cols; Format = fmt; _owns = owns; _keepAlive = keepAlive;
        RowBytes = cols / BlockSize * BlockBytes(fmt);
    }

    /// <summary>View over already quantized bytes (e.g. a mapped file). <paramref name="keepAlive"/> is held for the view's lifetime.</summary>
    public static QuantizedMatrix Wrap(byte* data, int rows, int cols, QuantFormat fmt, object? keepAlive)
        => new(data, rows, cols, fmt, false, keepAlive);

    public static QuantizedMatrix Quantize(ReadOnlySpan<float> w, int rows, int cols, QuantFormat fmt)
    {
        if (cols % BlockSize != 0) throw new ArgumentException($"columns must be a multiple of {BlockSize}");
        if (w.Length != rows * cols) throw new ArgumentException("weights length does not match rows * cols");
        long bytes = (long)rows * (cols / BlockSize) * BlockBytes(fmt);
        var p = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, bytes), Tensor<byte>.Alignment);
        var m = new QuantizedMatrix(p, rows, cols, fmt, true, null);
        int bb = BlockBytes(fmt);
        for (long b = 0; b < (long)rows * (cols / BlockSize); b++)
        {
            var src = w.Slice((int)(b * BlockSize), BlockSize);
            var dst = p + b * bb;
            if (fmt == QuantFormat.Int8Block) QuantizeInt8(src, dst); else QuantizeInt4(src, dst);
        }
        return m;
    }

    static void QuantizeInt8(ReadOnlySpan<float> x, byte* dst)
    {
        float amax = 0;
        foreach (var v in x) amax = MathF.Max(amax, MathF.Abs(v));
        float d = amax / 127f, id = d != 0 ? 1f / d : 0f;
        *(Half*)dst = (Half)d;
        var q = (sbyte*)(dst + 2);
        for (int i = 0; i < BlockSize; i++) q[i] = (sbyte)Math.Clamp((int)MathF.Round(x[i] * id), -127, 127);
    }

    static void QuantizeInt4(ReadOnlySpan<float> x, byte* dst)
    {
        float amax = 0, max = 0;
        foreach (var v in x) if (MathF.Abs(v) > amax) { amax = MathF.Abs(v); max = v; }
        float d = max / -8f, id = d != 0 ? 1f / d : 0f;
        *(Half*)dst = (Half)d;
        var q = dst + 2;
        for (int i = 0; i < BlockSize / 2; i++)
        {
            int a = Math.Min(15, (int)(x[i] * id + 8.5f)), b = Math.Min(15, (int)(x[i + 16] * id + 8.5f));
            q[i] = (byte)(Math.Max(0, a) | (Math.Max(0, b) << 4));
        }
    }

    public void DequantizeRow(int row, Span<float> dst)
    {
        if ((uint)row >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if (dst.Length < Columns) throw new ArgumentException("destination too short");
        var p = _data + (long)row * RowBytes;
        int bb = BlockBytes(Format);
        for (int b = 0; b < Columns / BlockSize; b++, p += bb)
        {
            float d = (float)*(Half*)p;
            var o = dst.Slice(b * BlockSize, BlockSize);
            if (Format == QuantFormat.Int8Block)
                for (int i = 0; i < BlockSize; i++) o[i] = ((sbyte*)(p + 2))[i] * d;
            else
                for (int i = 0; i < BlockSize / 2; i++)
                {
                    o[i] = ((p[2 + i] & 0x0F) - 8) * d;
                    o[i + 16] = ((p[2 + i] >> 4) - 8) * d;
                }
        }
    }

    /// <summary>Fused dequantize-dot of one row with <paramref name="x"/>.</summary>
    public float Dot(int row, ReadOnlySpan<float> x)
    {
        if ((uint)row >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if (x.Length < Columns) throw new ArgumentException("x too short");
        fixed (float* px = x)
            return Format == QuantFormat.Int8Block
                ? QuantKernels.DotInt8(_data + (long)row * RowBytes, px, Columns / BlockSize)
                : QuantKernels.DotInt4(_data + (long)row * RowBytes, px, Columns / BlockSize);
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~QuantizedMatrix() => Free();

    void Free()
    {
        if (_owns && _data != null) { NativeMemory.AlignedFree(_data); _data = null; }
        GC.KeepAlive(_keepAlive);
    }
}
