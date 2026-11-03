using System.Runtime.InteropServices;
using System.Text;
using SysProcess = System.Diagnostics.Process;
using StartInfo = System.Diagnostics.ProcessStartInfo;

namespace Alice.Core.Process;

/// <summary>Ruby `Process::Status` (`$?`): how a child process ended.</summary>
/// <param name="ExitCode">Ruby `exitstatus` (on Unix a signal-terminated child reports 128 + signal).</param>
/// <param name="Pid">The child's process id.</param>
public readonly record struct ProcessStatus(int ExitCode, int Pid)
{
    /// <summary>Ruby `success?`.</summary>
    public bool IsSuccess => ExitCode == 0;

    /// <summary>Ruby `to_s`: <c>pid 123 exit 0</c>.</summary>
    public override string ToString() => $"pid {Pid} exit {ExitCode}";
}

/// <summary>Captured output of <see cref="Open3.Capture3"/>.</summary>
/// <param name="Stdout">Standard output.</param>
/// <param name="Stderr">Standard error.</param>
/// <param name="Status">Exit status.</param>
public readonly record struct Capture(string Stdout, string Stderr, ProcessStatus Status);

/// <summary>
/// Ruby's process primitives over <see cref="System.Diagnostics.Process"/>: backticks (<see cref="Capture"/>),
/// `system`, `spawn` + `Process.wait`, and <see cref="Run"/> with input and an output callback. A command string goes
/// through the platform shell (`/bin/sh -c`, `cmd.exe /c`); the program + arguments overloads do not use a shell, so
/// arguments need no quoting (prefer them for untrusted input). <see cref="LastStatus"/> is Ruby's `$?`
/// (per thread).
/// </summary>
public static class Shell
{
    [ThreadStatic] static ProcessStatus last;

    /// <summary>Ruby `$?`: the status of the last child this thread waited for.</summary>
    public static ProcessStatus LastStatus => last;

    static StartInfo ShellInfo(string command)
    {
        var si = OperatingSystem.IsWindows() ? new StartInfo("cmd.exe") : new StartInfo("/bin/sh");
        if (OperatingSystem.IsWindows()) { si.ArgumentList.Add("/c"); si.ArgumentList.Add(command); }
        else { si.ArgumentList.Add("-c"); si.ArgumentList.Add(command); }
        si.UseShellExecute = false;
        return si;
    }

    static StartInfo ArgsInfo(string program, ReadOnlySpan<string> args)
    {
        var si = new StartInfo(program) { UseShellExecute = false };
        foreach (var a in args) si.ArgumentList.Add(a);
        return si;
    }

    /// <summary>Ruby `` `cmd` `` / `%x(cmd)`: runs through the shell and returns standard output (stderr passes
    /// through); sets <see cref="LastStatus"/>.</summary>
    public static string Capture(string command)
    {
        var si = ShellInfo(command);
        si.RedirectStandardOutput = true;
        using var p = SysProcess.Start(si)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        last = new ProcessStatus(p.ExitCode, p.Id);
        return output;
    }

    /// <summary>Ruby `system(cmd)`: runs through the shell with inherited stdio; true when it exits 0. A command
    /// that cannot be started returns false (Ruby returns nil).</summary>
    public static bool System(string command) => Wait(ShellInfo(command));

    /// <summary>Ruby `system(program, *args)`: no shell.</summary>
    public static bool System(string program, params ReadOnlySpan<string> args) => Wait(ArgsInfo(program, args));

    static bool Wait(StartInfo si)
    {
        try
        {
            using var p = SysProcess.Start(si)!;
            p.WaitForExit();
            last = new ProcessStatus(p.ExitCode, p.Id);
            return p.ExitCode == 0;
        }
        catch (global::System.ComponentModel.Win32Exception)
        {
            last = new ProcessStatus(127, 0);
            return false;
        }
    }

    /// <summary>Ruby `spawn(cmd)`: starts through the shell without waiting; returns the pid (see <see cref="Wait(int)"/>).</summary>
    public static int Spawn(string command)
    {
        var p = SysProcess.Start(ShellInfo(command))!;
        Children.Add(p);
        return p.Id;
    }

    /// <summary>Ruby `spawn(program, *args)`: no shell.</summary>
    public static int Spawn(string program, params ReadOnlySpan<string> args)
    {
        var p = SysProcess.Start(ArgsInfo(program, args))!;
        Children.Add(p);
        return p.Id;
    }

    /// <summary>Ruby `Process.wait(pid)`: waits for a child started by <see cref="Spawn(string)"/>.</summary>
    public static ProcessStatus Wait(int pid)
    {
        var p = Children.Take(pid) ?? SysProcess.GetProcessById(pid);
        using (p)
        {
            p.WaitForExit();
            last = new ProcessStatus(p.ExitCode, pid);
            return last;
        }
    }

    /// <summary>Ruby `Process.kill(:TERM, pid)` (entire tree when <paramref name="tree"/>).</summary>
    public static void Kill(int pid, bool tree = false)
    {
        using var p = SysProcess.GetProcessById(pid);
        p.Kill(tree);
    }

    /// <summary>`Process.run(program, args, input:, output:)`: runs without a shell, writes <paramref name="input"/>
    /// to stdin, streams stdout lines to <paramref name="output"/> (inherited when null), waits.</summary>
    public static ProcessStatus Run(string program, string[] args, string? input = null, Action<string>? output = null, string? workingDirectory = null)
    {
        var si = ArgsInfo(program, args);
        si.RedirectStandardInput = input is not null;
        si.RedirectStandardOutput = output is not null;
        if (workingDirectory is not null) si.WorkingDirectory = workingDirectory;
        using var p = SysProcess.Start(si)!;
        if (output is not null)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
            p.BeginOutputReadLine();
        }
        if (input is not null)
        {
            p.StandardInput.Write(input);
            p.StandardInput.Close();
        }
        p.WaitForExit();
        last = new ProcessStatus(p.ExitCode, p.Id);
        return last;
    }

    // children started by Spawn, kept so Wait can read their exit code (lazy holder: no eager static state)
    static class Children
    {
        static readonly Dictionary<int, SysProcess> map = [];
        internal static void Add(SysProcess p) { lock (map) map[p.Id] = p; }
        internal static SysProcess? Take(int pid) { lock (map) return map.Remove(pid, out var p) ? p : null; }
    }
}

/// <summary>Ruby's `Open3`: run a command and capture its streams.</summary>
public static class Open3
{
    /// <summary>Ruby `Open3.capture3(cmd, stdin_data:)`: stdout, stderr and status (through the shell).</summary>
    public static Capture Capture3(string command, string? stdinData = null)
    {
        var si = OperatingSystem.IsWindows() ? new StartInfo("cmd.exe") : new StartInfo("/bin/sh");
        si.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        si.ArgumentList.Add(command);
        return Run(si, stdinData);
    }

    /// <summary>Ruby `Open3.capture3(program, *args)` without a shell.</summary>
    public static Capture Capture3(string program, string[] args, string? stdinData = null)
    {
        var si = new StartInfo(program);
        foreach (var a in args) si.ArgumentList.Add(a);
        return Run(si, stdinData);
    }

    /// <summary>Ruby `Open3.capture2e`: stdout and stderr interleaved into one string (order across the two
    /// streams is by arrival).</summary>
    public static (string Output, ProcessStatus Status) Capture2e(string command)
    {
        var si = OperatingSystem.IsWindows() ? new StartInfo("cmd.exe") : new StartInfo("/bin/sh");
        si.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        si.ArgumentList.Add(command);
        si.UseShellExecute = false;
        si.RedirectStandardOutput = si.RedirectStandardError = true;
        var sb = new StringBuilder();
        using var p = SysProcess.Start(si)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.Append(e.Data).Append('\n'); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.Append(e.Data).Append('\n'); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return (sb.ToString(), new ProcessStatus(p.ExitCode, p.Id));
    }

    static Capture Run(StartInfo si, string? stdin)
    {
        si.UseShellExecute = false;
        si.RedirectStandardOutput = si.RedirectStandardError = true;
        si.RedirectStandardInput = stdin is not null;
        using var p = SysProcess.Start(si)!;
        var err = p.StandardError.ReadToEndAsync();
        if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new Capture(output, err.GetAwaiter().GetResult(), new ProcessStatus(p.ExitCode, p.Id));
    }
}

/// <summary>Ruby `trap("INT") { }` and `at_exit { }` over <see cref="PosixSignalRegistration"/>.</summary>
public static class Signals
{
    /// <summary>Ruby `trap(signal) { }`: runs <paramref name="handler"/> on INT, TERM, HUP or QUIT (names with or
    /// without "SIG"); the default action (terminating) is cancelled. "EXIT" registers an exit handler. Dispose the
    /// result to restore the default.</summary>
    public static IDisposable Trap(string signal, Action handler)
    {
        string name = signal.StartsWith("SIG", StringComparison.OrdinalIgnoreCase) ? signal[3..] : signal;
        if (name.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
        {
            EventHandler h = (_, _) => handler();
            AppDomain.CurrentDomain.ProcessExit += h;
            return new Undo(() => AppDomain.CurrentDomain.ProcessExit -= h);
        }
        PosixSignal sig = name.ToUpperInvariant() switch
        {
            "INT" => PosixSignal.SIGINT,
            "TERM" => PosixSignal.SIGTERM,
            "HUP" => PosixSignal.SIGHUP,
            "QUIT" => PosixSignal.SIGQUIT,
            _ => throw new ArgumentException($"unsupported signal {signal} (INT, TERM, HUP, QUIT, EXIT)", nameof(signal)),
        };
        return PosixSignalRegistration.Create(sig, ctx => { ctx.Cancel = true; handler(); });
    }

    sealed class Undo(Action a) : IDisposable { public void Dispose() => a(); }
}
