using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;

namespace Alice.Cli.Commands;

public static class VersionCommand
{
    public static int Run()
    {
        AnsiConsole.MarkupLine($"[bold magenta]{Markup.Escape(T("tagline", App.Version))}[/]");
        var t = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumn("").AddColumn("");
        t.AddRow("alice", App.Version);
        t.AddRow(".NET", RuntimeInformation.FrameworkDescription);
        t.AddRow("OS / arch", $"{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        t.AddRow("cores", Environment.ProcessorCount.ToString());
        string simd = $"Vector<T> = {Vector<byte>.Count * 8} bit" +
            (Avx512F.IsSupported ? ", AVX-512" : "") + (Avx2.IsSupported ? ", AVX2" : "") + (AdvSimd.IsSupported ? ", NEON" : "");
        t.AddRow("SIMD", simd);
        t.AddRow("language", Language);
        AnsiConsole.Write(t);
        return 0;
    }
}
