using System.Diagnostics;
using System.Text.RegularExpressions;
using Alice.Compiler;

namespace Alice.Cli.Projects;

/// <summary>Drives `dotnet` (MSBuild/NuGet) and turns its output into Alice diagnostics.</summary>
public static class DotNet
{
    static readonly Regex MsBuildDiag = new(@"^\s*(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\): (?<sev>error|warning) (?<code>\w+): (?<msg>.*?)(?: \[[^\]]*\])?$");

    public sealed record Result(int ExitCode, List<Diagnostic> Diagnostics, List<string> Output);

    /// <summary>One MSBuild diagnostic line. A csc error at an Alice line (the generated C# carries `#line`) is
    /// translated to its Alice diagnostic: package builds type-check once, in csc, not again inside `alice gen`.</summary>
    public static Diagnostic? ParseLine(string line)
    {
        var m = MsBuildDiag.Match(line);
        if (!m.Success) return null;
        string file = m.Groups["file"].Value.Trim(), code = m.Groups["code"].Value, msg = m.Groups["msg"].Value;
        if (code.StartsWith("CS") && file.EndsWith(".al")) (code, msg) = Alice.Compiler.Diagnostics.CSharpErrors.Translate(code, msg);
        return new Diagnostic(m.Groups["sev"].Value, code, file, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), msg);
    }

    public static Result Run(IEnumerable<string> args, Action<string>? onLine = null, bool inherit = false)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = !inherit, RedirectStandardError = !inherit, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["AliceFromCli"] = "true";   // MSBuild property: skip rebuilding the running alice.dll (Alice.targets)   // csc messages are matched in English (CSharpErrors)
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
                if (ParseLine(line) is { } d && seen.Add(d.File + d.Line + d.Code + d.Message)) diags.Add(d);
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
