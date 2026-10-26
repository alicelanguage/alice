using System.Runtime.InteropServices;
using Alice.Runtime;

namespace Alice.Tests.Runtime.Tensors;

public class MappedSpanTests
{
    [Fact]
    public void Maps_file_regions_as_tensors_and_quantized_matrices_without_copy()
    {
        int rows = 8, cols = 64;
        var w = Reference.Random(rows * cols, 40);
        using var q = QuantizedMatrix.Quantize(w, rows, cols, QuantFormat.Int8Block);
        var path = Path.GetTempFileName();
        try
        {
            using (var f = File.Create(path))
            {
                f.Write(new byte[16]);
                f.Write(MemoryMarshal.AsBytes(w.AsSpan()));
                f.Write(q.Bytes);
            }
            using var m = MappedFile.Open(path);
            Assert.Equal(16 + w.Length * 4 + q.Bytes.Length, m.Length);
            Assert.Equal(w, m.Span<float>(16, w.Length).ToArray());
            var t = m.Tensor<float>(16, rows, cols);
            Assert.Equal(w[3 * cols + 5], t[3, 5]);
            var mq = m.Quantized(16 + w.Length * 4, rows, cols, QuantFormat.Int8Block);
            var x = Reference.Random(cols, 41);
            var y1 = new float[rows]; var y2 = new float[rows];
            Nn.Gemv(q, x, y1);
            Nn.Gemv(mq, x, y2);
            Assert.Equal(y1, y2);
            Assert.Throws<ArgumentOutOfRangeException>(() => m.Span<float>(m.Length - 2, 1).Length);
        }
        finally { File.Delete(path); }
    }
}
