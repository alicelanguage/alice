using System.Collections.Concurrent;

namespace Alice.Runtime;

/// <summary>
/// Elixir-style actor: private mailbox, one logical consumer, messages handled in order. Built on
/// <see cref="ProcessCore"/>: an idle actor holds no thread (thousands are cheap), a crashing handler kills
/// only this actor, and it can be linked, monitored and supervised. The compiler only admits sendable
/// message types and handlers that capture no shared mutable state (AL0230, AL0231).
/// </summary>
public sealed class Actor<T> : ProcessCore
{
    readonly ConcurrentQueue<T> mailbox = new();   // lock-free; its ring segment is reused while the consumer keeps up
    readonly Action<T> handler;

    public Actor(Action<T> handler)
    {
        this.handler = handler;
    }

    public void send(T msg)
    {
        if (!Admit()) return;
        mailbox.Enqueue(msg);
        Schedule();
    }

    protected override bool HandleOne()
    {
        if (!mailbox.TryDequeue(out var m)) return false;
        handler(m);
        return true;
    }

    protected override bool HasMessages => !mailbox.IsEmpty;
    protected override void ClearMailbox() { while (mailbox.TryDequeue(out _)) { } }
}

/// <summary>
/// GenServer-style actor with isolated state: `init` builds the state inside the actor (and again on every
/// supervisor restart), and the handler folds each message into the next state. Nothing outside can reach
/// the state; `finish` drains, stops and hands the final state back (ownership moves out of the dead actor).
/// </summary>
public sealed class Actor<S, T> : ProcessCore
{
    readonly ConcurrentQueue<T> mailbox = new();
    readonly Func<S> init;
    readonly Func<S, T, S> handler;
    S state;

    public Actor(Func<S> init, Func<S, T, S> handler)
    {
        this.init = init;
        this.handler = handler;
        state = init();
    }

    public void send(T msg)
    {
        if (!Admit()) return;
        mailbox.Enqueue(msg);
        Schedule();
    }

    protected override bool HandleOne()
    {
        if (!mailbox.TryDequeue(out var m)) return false;
        state = handler(state, m);
        return true;
    }

    protected override bool HasMessages => !mailbox.IsEmpty;
    protected override void ClearMailbox() { while (mailbox.TryDequeue(out _)) { } }
    protected override void Reinit() => state = init();

    /// <summary>Drains the mailbox, stops, and returns the final state.</summary>
    public S finish()
    {
        stop();
        return state;
    }
}
