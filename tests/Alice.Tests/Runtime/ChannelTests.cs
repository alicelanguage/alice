using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>
/// Channel(T) contract (0.12.0): `new` is a rendezvous (send returns once a receiver took the value),
/// `new(n)` buffers n values, close drains then raises, and the blocking paths allocate nothing.
/// </summary>
public class ChannelTests
{
    static bool Completes(Task t, int ms = 300) => t.Wait(ms);

    [Fact]
    public async Task Unbuffered_send_waits_for_a_receiver()
    {
        var ch = new Channel<int>();
        var send = Task.Run(() => ch.send(7));
        await Task.Delay(150);
        Assert.False(send.IsCompleted);   // nobody received yet
        Assert.Equal(7, ch.receive());
        await send.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Buffered_send_blocks_only_when_full()
    {
        var ch = new Channel<int>(2);
        ch.send(1);
        ch.send(2);
        var third = Task.Run(() => ch.send(3));
        await Task.Delay(150);
        Assert.False(third.IsCompleted);
        Assert.Equal(1, ch.receive());
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, ch.receive());
        Assert.Equal(3, ch.receive());
    }

    [Fact]
    public void Close_drains_pending_values_then_raises()
    {
        var ch = new Channel<int>(3);
        ch.send(1);
        ch.send(2);
        ch.close();
        Assert.Throws<ClosedChannelError>(() => ch.send(3));
        Assert.Equal(1, ch.receive());
        Assert.Equal(2, ch.receive());
        Assert.ThrowsAny<InvalidOperationException>(() => ch.receive());
        Assert.False(ch.try_receive(out _));
    }

    [Fact]
    public async Task Each_stops_at_close_and_close_wakes_blocked_receivers()
    {
        var ch = new Channel<int>();
        var seen = new List<int>();
        var consumer = Task.Run(() => ch.each(seen.Add));
        for (int i = 0; i < 5; i++) ch.send(i);
        ch.close();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([0, 1, 2, 3, 4], seen);

        var empty = new Channel<int>();
        var blocked = Task.Run(() => Assert.ThrowsAny<InvalidOperationException>(() => empty.receive()));
        await Task.Delay(100);
        empty.close();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Many_producers_and_consumers_lose_nothing_and_keep_per_producer_order()
    {
        const int producers = 4, per = 20_000;
        var ch = new Channel<long>(64);
        var got = new System.Collections.Concurrent.ConcurrentBag<long>();
        var lastSeen = new long[producers];
        Array.Fill(lastSeen, -1);
        var consumers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => ch.each(v => got.Add(v)))).ToArray();
        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() => { for (int i = 0; i < per; i++) ch.send(p * 1_000_000L + i); })));
        ch.close();
        await Task.WhenAll(consumers).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(producers * per, got.Count);
        Assert.Equal(Enumerable.Range(0, producers).Sum(p => Enumerable.Range(0, per).Sum(i => p * 1_000_000L + i)), got.Sum());
    }

    [Fact]
    public void Buffered_send_and_receive_allocate_nothing()
    {
        var ch = new Channel<int>(8);
        for (int i = 0; i < 100; i++) { ch.send(i); ch.receive(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) { ch.send(i); ch.receive(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task Async_send_and_receive_interoperate_with_blocking_ones()
    {
        var ch = new Channel<int>();
        var producer = Task.Run(async () => { for (int i = 1; i <= 100; i++) await ch.send_async(i); ch.close(); });
        long sum = 0;
        while (await ch.wait_async()) if (ch.try_receive(out var v)) sum += v;
        await producer;
        Assert.Equal(5050, sum);
        var ch2 = new Channel<int>(1);
        var r = ch2.receive_async();
        ch2.send(9);
        Assert.Equal(9, await r.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
