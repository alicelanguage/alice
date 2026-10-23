using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// Exceptions raised in an Alice callback called from C. Unwinding through C frames aborts the process, so the
/// callback stores the exception and returns a zero value; the `fun` wrapper rethrows it once the C call returns.
/// Only the first exception of a C call is kept.
/// </summary>
public static class NativeCallback
{
    [ThreadStatic] static ExceptionDispatchInfo? pending;

    public static void Fail(Exception e) => pending ??= ExceptionDispatchInfo.Capture(e);

    public static void Rethrow()
    {
        var p = pending;
        if (p == null) return;
        pending = null;
        p.Throw();
    }
}
