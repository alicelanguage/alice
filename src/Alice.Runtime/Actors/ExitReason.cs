namespace Alice.Runtime;

/// <summary>
/// Why a process stopped (BEAM exit reasons). `normal` exits do not propagate along links and are not
/// restarted by a supervisor; every other kind does both.
/// </summary>
public sealed class ExitReason
{
    /// <summary>normal, crash, killed, shutdown, linked or max_restarts.</summary>
    public string kind { get; }
    public string message { get; }
    /// <summary>The exception that crashed the process (kind crash), or the root crash of a linked exit.</summary>
    public Exception? error { get; }

    ExitReason(string kind, string message, Exception? error = null)
    {
        this.kind = kind;
        this.message = message;
        this.error = error;
    }

    public bool IsNormal => kind == "normal";

    public static readonly ExitReason Normal = new("normal", "normal");
    public static readonly ExitReason Killed = new("killed", "killed");
    public static readonly ExitReason Shutdown = new("shutdown", "shutdown");

    public static ExitReason Crash(Exception e) => new("crash", e.Message, e);
    public static ExitReason Linked(long from, ExitReason cause) => new("linked", $"linked process {from} exited: {cause.message}", cause.error);
    public static ExitReason MaxRestarts(int max, int ms) => new("max_restarts", $"more than {max} restarts in {ms} ms");

    public override string ToString() => kind == message ? kind : $"{kind}: {message}";
}
