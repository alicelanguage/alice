namespace Alice.Runtime;

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
