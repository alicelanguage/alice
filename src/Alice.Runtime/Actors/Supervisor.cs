namespace Alice.Runtime;

/// <summary>
/// OTP-style supervisor (ADR 0028). Children are restarted in place when they exit abnormally (crash, kill,
/// linked exit); a normal exit (`stop`) is final (transient children). `one_for_one` restarts only the
/// failed child; `one_for_all` shuts the siblings down and restarts every child. More than `max_restarts`
/// restarts within `within_ms` and the supervisor gives up: it exits with max_restarts, shutting its
/// children down, which its own supervisor (supervision trees nest) sees as a failure.
/// Supervisors trap exits: a linked process dying does not kill them.
/// </summary>
public sealed class Supervisor : ProcessCore
{
    readonly bool all;
    readonly int max;
    readonly int window;
    readonly List<ProcessCore> kids = new();
    readonly Queue<long> recent = new();
    readonly object sgate = new();

    Supervisor(bool all, int maxRestarts, int withinMs)
    {
        this.all = all;
        max = maxRestarts;
        window = withinMs;
    }

    public static Supervisor one_for_one(int max_restarts = 3, int within_ms = 5000) => new(false, max_restarts, within_ms);
    public static Supervisor one_for_all(int max_restarts = 3, int within_ms = 5000) => new(true, max_restarts, within_ms);

    public string strategy => all ? "one_for_all" : "one_for_one";
    public int children { get { lock (sgate) return kids.Count; } }
    /// <summary>Restarts performed (a one_for_all round counts once).</summary>
    public int restart_count { get; private set; }

    /// <summary>Starts supervising `child` and returns it.</summary>
    public P supervise<P>(P child) where P : ProcessCore
    {
        if (ReferenceEquals(child, this)) throw new ArgumentException("a supervisor cannot supervise itself");
        lock (sgate)
        {
            if (child.parent != null) throw new InvalidOperationException($"process {child.id} already has a supervisor");
            child.parent = this;
            kids.Add(child);
        }
        if (!child.IsAlive) ChildExited(child, child.exit_reason ?? ExitReason.Killed);
        return child;
    }

    internal void ChildExited(ProcessCore child, ExitReason reason)
    {
        lock (sgate)
        {
            if (reason.IsNormal)
            {
                kids.Remove(child);   // transient: a normal exit is final
                child.parent = null;
                return;
            }
            // already back (a one_for_all round restarted it while its own exit was in flight)
            if (!IsAlive || child.IsAlive || reason.kind == "shutdown") return;
            long now = Environment.TickCount64;
            while (recent.Count > 0 && now - recent.Peek() > window) recent.Dequeue();
            if (recent.Count >= max)
            {
                Terminate(ExitReason.MaxRestarts(max, window));
                return;
            }
            recent.Enqueue(now);
            restart_count++;
            if (!all)
            {
                child.Restart();
                return;
            }
            foreach (var k in kids) if (k != child) k.Terminate(ExitReason.Shutdown);
            foreach (var k in kids) k.Restart();
        }
    }

    internal override void ExitSignal(ProcessCore from, ExitReason reason) { }   // trap exits

    protected override void OnTerminated(ExitReason reason)
    {
        lock (sgate)
            for (int i = kids.Count - 1; i >= 0; i--) kids[i].Terminate(ExitReason.Shutdown);
    }

    protected override void Reinit()
    {
        lock (sgate)
        {
            recent.Clear();
            foreach (var k in kids) if (!k.IsAlive) k.Restart();
        }
    }

    protected override bool HandleOne() => false;
    protected override bool HasMessages => false;
    protected override void ClearMailbox() { }
}
