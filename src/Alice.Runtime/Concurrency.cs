using System.Threading.Channels;

namespace Alice.Runtime;

/// <summary>Crystal-style typed channel over System.Threading.Channels. Unbuffered-by-default is emulated by capacity 1.</summary>
public sealed class Channel<T>
{
    readonly System.Threading.Channels.Channel<T> _ch;

    public Channel() : this(0) { }

    public Channel(int capacity)
    {
        _ch = capacity <= 0
            ? System.Threading.Channels.Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false })
            : System.Threading.Channels.Channel.CreateBounded<T>(capacity);
    }

    public void send(T v)
    {
        if (!_ch.Writer.TryWrite(v)) _ch.Writer.WriteAsync(v).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Blocks until a value arrives. Throws InvalidOperationException when closed and drained.</summary>
    public T receive()
    {
        if (_ch.Reader.TryRead(out var v)) return v;
        return _ch.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
    }

    public bool try_receive(out T v) => _ch.Reader.TryRead(out v!);

    public void close() => _ch.Writer.TryComplete();

    public void each(Action<T> f)
    {
        while (_ch.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (_ch.Reader.TryRead(out var v)) f(v);
    }
}

/// <summary>Elixir-style actor: private mailbox, one consumer task, messages processed in order.</summary>
public sealed class Actor<T>
{
    readonly Channel<T> _mailbox = new();
    readonly Task _loop;

    public Actor(Action<T> handler) => _loop = Task.Run(() => _mailbox.each(handler));

    public void send(T msg) => _mailbox.send(msg);

    /// <summary>Stops accepting messages and waits until the mailbox drains.</summary>
    public void stop()
    {
        _mailbox.close();
        _loop.GetAwaiter().GetResult();
    }
}

public static partial class Tasks
{
    public static void await_all(params Task[] ts) => Task.WaitAll(ts);
}
