using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Alice.Tests;

/// <summary>
/// End-of-run guard for the test host: when the process exits, every child process a test left running (and its
/// descendants) is named on stderr (shown in the <c>dotnet test</c> output) and killed, so the run ends at once.
/// </summary>
/// <remarks>
/// It cannot fail the run: once every test has reported, vstest ignores the host's exit code and even a
/// <c>FailFast</c> here (both tried on the forge with a test that leaves <c>sleep 600</c> running). Tests must await
/// and kill what they start (<c>ChildProcess.Run</c> kills the tree on timeout). Linux only (it walks <c>/proc</c>);
/// zombies are ignored. A lingering foreground thread is not covered: the runtime waits for those before raising
/// <c>ProcessExit</c>, and the blame hang timeout still names that case.
/// </remarks>
static class AssemblyFixture
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (OperatingSystem.IsLinux()) AppDomain.CurrentDomain.ProcessExit += (_, _) => KillLeftovers();
    }

    static void KillLeftovers()
    {
        try
        {
            var left = Descendants(Environment.ProcessId);
            foreach (var (pid, cmd) in left)
                Console.Error.WriteLine($"Alice.Tests: child process {pid} still running after the last test (killed): {cmd}");
            foreach (var (pid, _) in left)
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
        }
        catch (Exception e) { Console.Error.WriteLine($"Alice.Tests: leftover-process check failed: {e.Message}"); }
    }

    /// <summary>Live (non-zombie) descendants of <paramref name="root"/> from <c>/proc/*/stat</c>.</summary>
    internal static List<(int Pid, string Cmd)> Descendants(int root)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(dir, "stat")); } catch { continue; }
            int close = stat.LastIndexOf(')');   // comm may contain spaces and parentheses
            if (close < 0) continue;
            var f = stat[(close + 2)..].Split(' ');   // f[0] state, f[1] ppid
            if (f.Length < 2 || f[0] == "Z" || !int.TryParse(f[1], out int ppid)) continue;
            (children.TryGetValue(ppid, out var l) ? l : children[ppid] = new()).Add(pid);
        }
        var result = new List<(int, string)>();
        var todo = new Stack<int>();
        todo.Push(root);
        while (todo.Count > 0)
            if (children.TryGetValue(todo.Pop(), out var kids))
                foreach (var k in kids)
                {
                    string cmd;
                    try { cmd = File.ReadAllText($"/proc/{k}/cmdline").Replace('\0', ' ').Trim(); } catch { cmd = "?"; }
                    result.Add((k, cmd));
                    todo.Push(k);
                }
        return result;
    }
}
