using System.Diagnostics;
using System.Text.RegularExpressions;
using Alice.Compiler;

namespace Alice.Cli.Projects;

/// <summary>Drives `dotnet` (MSBuild/NuGet) and turns its output into Alice diagnostics.</summary>
public static class DotNet
{
    static readonly Regex MsBuildDiag = new(@"^\s*(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\): (?<sev>error|warning) (?<code>\w+): (?<msg>.*?)(?: \[[^\]]*\])?$");

    public sealed record Result(int ExitCode, List<Diagnostic> Diagnostics, List<string> Output);

    public static Result Run(IEnumerable<string> args, Action<string>? onLine = null, bool inherit = false)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = !inherit, RedirectStandardError = !inherit, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi)!;
        var diags = new List<Diagnostic>();
        var seen = new HashSet<string>();
        var output = new List<string>();
        void Handle(string? line)
        {
            if (line == null) return;
            lock (output)
            {
                output.Add(line);
                var m = MsBuildDiag.Match(line);
                if (m.Success && seen.Add(m.Groups["file"].Value + m.Groups["line"].Value + m.Groups["code"].Value + m.Groups["msg"].Value))
                    diags.Add(new Diagnostic(m.Groups["sev"].Value, m.Groups["code"].Value, m.Groups["file"].Value.Trim(),
                        int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), m.Groups["msg"].Value));
                onLine?.Invoke(line);
            }
        }
        if (!inherit)
        {
            p.OutputDataReceived += (_, e) => Handle(e.Data);
            p.ErrorDataReceived += (_, e) => Handle(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        p.WaitForExit();
        return new Result(p.ExitCode, diags, output);
    }
}
