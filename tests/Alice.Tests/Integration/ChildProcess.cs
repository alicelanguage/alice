using System.Diagnostics;
using System.Text;

namespace Alice.Tests.Integration;

/// <summary>
/// Runs a child `dotnet`/`alice`/`node` process for an integration test without hanging. Reading stdout to the end
/// and then stderr deadlocks when the child fills stderr first, and MSBuild node reuse or the Roslyn compiler server
/// inherit the redirected pipes and keep them open after `dotnet build` exits, so ReadToEnd never returned (the suite
/// hung in SdkPackageTests and SampleTests). Both streams are read asynchronously, build servers are turned off for
/// the child, and the process exit (not the pipes' end) ends the wait.
/// </summary>
static class ChildProcess
{
    public static (int Code, string Output) Run(ProcessStartInfo psi, int timeoutMinutes = 20)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["UseSharedCompilation"] = "false";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        var output = new StringBuilder();
        using var outDone = new ManualResetEventSlim();
        using var errDone = new ManualResetEventSlim();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) outDone.Set(); else lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) errDone.Set(); else lock (output) output.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes)))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            lock (output) return (-1, $"timed out after {timeoutMinutes} min: {psi.FileName} {string.Join(" ", psi.ArgumentList)}\n{output}");
        }
        // a grandchild that still holds a pipe must not block the test: give the readers a moment, then go on
        outDone.Wait(TimeSpan.FromSeconds(10));
        errDone.Wait(TimeSpan.FromSeconds(10));
        lock (output) return (p.ExitCode, output.ToString());
    }
}
