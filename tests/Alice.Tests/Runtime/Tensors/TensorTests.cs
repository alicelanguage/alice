using Alice.Runtime;

namespace Alice.Tests.Runtime.Tensors;

public class TensorTests
{
    [Fact]
    public void Allocate_is_zeroed_aligned_and_row_major()
    {
        using var t = Tensor<float>.Allocate(3, 5);
        Assert.Equal(0, t.Address % Tensor<float>.Alignment);
        Assert.Equal([3, 5], t.Shape.ToArray());
        Assert.Equal([5, 1], t.Strides.ToArray());
        Assert.Equal(15, t.Length);
        Assert.True(t.IsContiguous);
        Assert.All(t.Span.ToArray(), v => Assert.Equal(0f, v));
        t[2, 3] = 7f;
        Assert.Equal(7f, t.Span[13]);
        Assert.Equal(7f, t.Row(2)[3]);
    }

    [Fact]
    public void Views_share_memory_without_copy()
    {
        using var t = Tensor<float>.FromSpan(Enumerable.Range(0, 12).Select(i => (float)i).ToArray(), 3, 4);
        var s = t.Slice(1, 2);
        Assert.Equal([2, 4], s.Shape.ToArray());
        Assert.Equal(4f, s[0, 0]);
        s[0, 0] = 100f;
        Assert.Equal(100f, t[1, 0]);

        var tr = t.Transpose();
        Assert.Equal([4, 3], tr.Shape.ToArray());
        Assert.False(tr.IsContiguous);
        Assert.Equal(t[2, 1], tr[1, 2]);

        var r = t.Reshape(2, 6);
        Assert.Equal(t[1, 2], r[1, 0]);

        var n = t.Narrow(1, 1, 2);
        Assert.Equal([3, 2], n.Shape.ToArray());
        Assert.Equal(t[2, 2], n[2, 1]);
        Assert.Throws<InvalidOperationException>(() => tr.Span.Length);
    }

    [Fact]
    public void Rank3_indexer_and_bounds()
    {
        using var t = Tensor<int>.Allocate(2, 3, 4);
        t[1, 2, 3] = 9;
        Assert.Equal(9, t.Span[1 * 12 + 2 * 4 + 3]);
        Assert.Throws<IndexOutOfRangeException>(() => t[2, 0, 0]);
    }

    [Fact]
    public void BFloat16_and_Half_convert_spans()
    {
        Assert.Equal(1.5f, (float)(BFloat16)1.5f);
        Assert.Equal(-2f, (float)(BFloat16)(-2f));
        Assert.True(MathF.Abs((float)(BFloat16)MathF.PI - MathF.PI) < 0.02f);
        Assert.True(float.IsNaN((float)(BFloat16)float.NaN));
        var src = Reference.Random(77, 1, 10);
        var bf = new BFloat16[77];
        var back = new float[77];
        BFloat16.Convert(src, bf);
        BFloat16.Convert(bf, back);
        Reference.Close(src, back, 1f / 128);

        var h = new Half[77];
        var hb = new float[77];
        HalfConvert.Convert(src, h);
        HalfConvert.Convert(h, hb);
        Reference.Close(src, hb, 1f / 1024);
    }

    [Fact]
    public void Arena_rents_aligned_scratch_and_resets()
    {
        using var arena = new TensorArena(1 << 16);
        var a = arena.Rent<float>(10);
        var b = arena.Rent<float>(10);
        a[0] = 1; b[0] = 2;
        Assert.Equal(1f, a[0]);
        Assert.Equal(128, arena.Used);
        arena.Reset();
        Assert.Equal(0, arena.Used);
        Assert.Throws<InsufficientMemoryException>(() => arena.Rent<float>(1 << 20).Length);
    }
}
