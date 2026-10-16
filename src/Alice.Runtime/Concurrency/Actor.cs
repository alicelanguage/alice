namespace Alice.Runtime;

/// <summary>
/// Elixir-style actor: private mailbox, one logical consumer, messages handled in order.
/// The consumer is an async loop, so an idle actor holds no thread (thousands of actors are cheap).
/// </summary>
public sealed class Actor<T>
{
    readonly System.Threading.Channels.Channel<T> _mailbox =
        System.Threading.Channels.Channel.CreateUnbounded<T>(new() { SingleReader = true, SingleWriter = false });
    readonly Task _loop;

    public Actor(Action<T> handler) => _loop = Run(handler);

    async Task Run(Action<T> handler)
    {
        await Task.Yield();
        var reader = _mailbox.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
            while (reader.TryRead(out var msg)) handler(msg);
    }

    public void send(T msg) => _mailbox.Writer.TryWrite(msg);

    /// <summary>Stops accepting messages and waits until the mailbox drains.</summary>
    public void stop()
    {
        _mailbox.Writer.TryComplete();
        _loop.GetAwaiter().GetResult();
    }

    public Task stop_async()
    {
        _mailbox.Writer.TryComplete();
        return _loop;
    }
}
