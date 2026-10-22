using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>
/// The chunk scheduler behind Par.Range / Par.Reduce: every index runs exactly once, partials combine in index
/// order, the first exception reaches the caller unwrapped, and nested parallel loops cannot starve the pool
/// because the calling thread always works through the chunks itself.
/// </summary>
public class ParSchedulerTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 1)]
    [InlineData(1_000_003, 8192)]
    public void Range_covers_every_index_once(int n, int grain)
    {
        var hits = new int[n];
        Par.Range(n, (lo, hi) => { for (int i = lo; i < hi; i++) Interlocked.Increment(ref hits[i]); }, grain);
        Assert.All(hits, h => Assert.Equal(1, h));
    }

    [Fact]
    public void Reduce_combines_partials_in_index_order()
    {
        string s = Par.Reduce(100_000, (lo, hi) => lo.ToString() + ",", (a, b) => a + b, 1000);
        var starts = s.TrimEnd(',').Split(',').Select(int.Parse).ToList();
        Assert.Equal(starts.OrderBy(x => x), starts);
        Assert.Equal(0, starts[0]);
    }

    [Fact]
    public void First_exception_propagates_unwrapped()
        => Assert.Throws<InvalidOperationException>(() => Par.Range(100, (lo, hi) => { if (lo > 10) throw new InvalidOperationException(); }, 1));

    [Fact]
    public void Nested_ranges_complete()
    {
        long total = 0;
        Par.Range(64, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
                Interlocked.Add(ref total, Par.Reduce(10_000, (a, b) => (long)(b - a), (x, y) => x + y, 100));
        }, 1);
        Assert.Equal(64L * 10_000, total);
    }

    [Fact]
    public void Range_allocates_one_job()
    {
        Action<int, int> body = static (lo, hi) => { };
        Par.Range(1 << 20, body, 8192);   // warm the pool and JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        Par.Range(1 << 20, body, 8192);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before <= 128, "Par.Range should allocate at most its job object");
    }
}
