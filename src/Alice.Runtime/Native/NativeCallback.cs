using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// Exceptions raised in an Alice callback called from C. Unwinding through C frames aborts the process, so the
/// callback stores the exception and returns a zero value; every `fun` of a `lib` that declares any `Callback`
/// parameter brackets its C call with <see cref="Enter"/> and <see cref="Rethrow(int)"/>, so the exception
/// resurfaces from the C call during which it was raised (also when C invokes a stored callback from a `fun` that
/// takes none). When several callbacks raise during one C call (qsort calls its comparator many times), the first
/// exception is rethrown unchanged, so `rescue` by type still matches, and every later one is kept, in raise order,
/// in its <see cref="Exception.Data"/> under <see cref="SuppressedKey"/> (nothing is dropped).
/// A callback that raises on a thread with no such C call in progress (a thread C created, or a `fun` of a lib that
/// declares no callbacks) has no Alice frame to rethrow into: the process fails fast with the exception instead of
/// losing it or reporting it from a later, unrelated call.
/// </summary>
public static class NativeCallback
{
    [ThreadStatic] static ExceptionDispatchInfo? pending;
    [ThreadStatic] static List<Exception>? more;
    [ThreadStatic] static int depth;   // callback-capable C calls in progress on this thread

    /// <summary><see cref="Exception.Data"/> key of the exceptions raised after the first one during the same C call
    /// (an <see cref="Exception"/>[]).</summary>
    public const string SuppressedKey = "Alice.NativeCallback.Suppressed";

    /// <summary>Right before a callback-capable C call: returns the depth <see cref="Rethrow(int)"/> restores.</summary>
    public static int Enter() => depth++;

    /// <summary>The catch block of every callback trampoline.</summary>
    public static void Fail(Exception e)
    {
        if (depth == 0)
            Environment.FailFast($"an Alice callback raised {e.GetType().FullName} on a thread with no Alice call into C in progress (a thread created by C, or a C function of a lib without Callback parameters); it cannot be rethrown: {e.Message}", e);
        if (pending == null) pending = ExceptionDispatchInfo.Capture(e);
        else (more ??= []).Add(e);
    }

    /// <summary>Right after the C call: restores the depth from <see cref="Enter"/>, then rethrows what the callbacks raised.</summary>
    public static void Rethrow(int saved)
    {
        depth = saved;
        Rethrow();
    }

    /// <summary>Rethrows the first exception callbacks raised during the C call, the later ones attached under
    /// <see cref="SuppressedKey"/>.</summary>
    public static void Rethrow()
    {
        var p = pending;
        if (p == null) return;
        pending = null;
        var m = more;
        if (m is { Count: > 0 })
        {
            more = null;
            var data = p.SourceException.Data;
            Exception[] all = data[SuppressedKey] is Exception[] old ? [.. old, .. m] : [.. m];
            data[SuppressedKey] = all;
        }
        p.Throw();
    }
}
