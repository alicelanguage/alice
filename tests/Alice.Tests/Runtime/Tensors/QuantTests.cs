using Alice.Runtime;

namespace Alice.Tests.Runtime.Tensors;

public class QuantTests
{
    [Fact]
    public void Block_layouts()
    {
        Assert.Equal(34, QuantizedMatrix.BlockBytes(QuantFormat.Int8Block));
        Assert.Equal(18, QuantizedMatrix.BlockBytes(QuantFormat.Int4Block));
        Assert.Equal(32, QuantizedMatrix.BlockSize);
    }

    [Theory]
    [InlineData(QuantFormat.Int8Block, 1f / 127)]
    [InlineData(QuantFormat.Int4Block, 1f / 7)]
    public void Roundtrip_error_is_bounded_by_block_scale(QuantFormat fmt, float bound)
    {
        var w = Reference.Random(4 * 128, 3);
        using var q = QuantizedMatrix.Quantize(w, 4, 128, fmt);
        var row = new float[128];
        for (int r = 0; r < 4; r++)
        {
            q.DequantizeRow(r, row);
            for (int c = 0; c < 128; c++)
                Assert.True(MathF.Abs(row[c] - w[r * 128 + c]) <= bound + 1e-6f, $"{fmt} [{r},{c}] {row[c]} vs {w[r * 128 + c]}");
        }
    }

    [Theory]
    [InlineData(QuantFormat.Int8Block, 64, 256)]
    [InlineData(QuantFormat.Int4Block, 64, 256)]
    [InlineData(QuantFormat.Int8Block, 300, 4096)]
    [InlineData(QuantFormat.Int4Block, 300, 4096)]
    public void Fused_dequant_gemv_equals_reference(QuantFormat fmt, int rows, int cols)
    {
        var w = Reference.Random(rows * cols, 5);
        var x = Reference.Random(cols, 6);
        using var q = QuantizedMatrix.Quantize(w, rows, cols, fmt);
        var deq = new float[rows * cols];
        for (int r = 0; r < rows; r++) q.DequantizeRow(r, deq.AsSpan(r * cols, cols));
        var expected = Reference.Gemv(deq, x, rows, cols);
        var y = new float[rows];
        Nn.Gemv(q, x, y);
        Reference.Close(expected, y, 1e-4f);
        Assert.Equal(expected[7], q.Dot(7, x), 1e-2f);
    }

    [Fact]
    public void Columns_must_be_block_multiples()
        => Assert.Throws<ArgumentException>(() => QuantizedMatrix.Quantize(new float[40], 1, 40, QuantFormat.Int8Block));
}
