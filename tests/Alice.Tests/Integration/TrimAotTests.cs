using System.Diagnostics;
using Alice.Cli.Projects;

namespace Alice.Tests.Integration;

/// <summary>
/// Generated code plus Alice.Runtime publish with NativeAOT and full trimming without a single trim or AOT warning
/// (IL2xxx / IL3xxx), ADR 0024: an optimization the trimmer cannot see through is lost, or breaks, after publish.
/// Uses the ILCompiler / ILLink packages from the local NuGet cache (offline); the toolchain must be restorable.
/// </summary>
[Trait("Category", "Integration")]
[Collection("msbuild")]
public class TrimAotTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "alice-aot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // forks, parallel loops, pipelines, classes, hashes, string formatting: the runtime paths generated code reaches
    const string Program = """
        class Point
          getter x : Int32
          getter y : Int32
          def initialize(@x : Int32, @y : Int32)
          end
        end

        def fib(n : Int32) : Int64
          if n < 2
            n.to_i64
          else
            a = fib(n - 1)
            b = fib(n - 2)
            a + b
          end
        end

        def squares(n : Int32) : Int64
          out = Array(Int64).new(n)
          for i in 0...n
            k = 0L
            for j in 0...50
              k += j * i
            end
            out[i] = k
          end
          out.sum
        end

        xs = Array(Int64).new(100_000) { |i| i.to_i64 }
        counts = Hash(String, Int32).new
        counts["a"] = 1
        p = Point.new(3, 4)
        puts "#{fib(20)} #{squares(70_000)} #{xs.map { |x| x * 3 }.select { |x| x % 2 == 0 }.sum} #{counts["a"]} #{p.x + p.y}"
        """;

    string Package(string name, string toml)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "alice.toml"), toml);
        File.WriteAllText(Path.Combine(dir, "src", "main.al"), Program);
        return Manifest.Sync(dir);
    }

    static List<string> TrimWarnings(DotNet.Result r) =>
        r.Diagnostics.Where(d => d.Code.StartsWith("IL2") || d.Code.StartsWith("IL3")).Select(d => $"{d.File}({d.Line}): {d.Code} {d.Message}").ToList();

    [Fact]
    public void Native_aot_publish_has_no_trim_or_aot_warnings()
    {
        var proj = Package("native", "[package]\nname = \"native\"\n\n[build]\nnative = true\n");
        string outDir = Path.Combine(root, "native", "out");
        var r = DotNet.Run(["publish", proj, "-c", "Release", "-o", outDir, "-v", "m", "-nologo"]);
        Assert.True(r.ExitCode == 0, string.Join("\n", r.Output.TakeLast(40)));
        Assert.Empty(TrimWarnings(r));
        string exe = Path.Combine(outDir, OperatingSystem.IsWindows() ? "native.exe" : "native");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal("6765 3001207125000 7499850000 1 7", output.Trim());
    }

    [Fact]
    public void Wasm_release_is_fully_trimmed_without_warnings_and_runs_serially()
    {
        var proj = Package("web", "[package]\nname = \"web\"\nkind = \"wasm\"\n");
        Assert.Contains("<AliceSequential>true</AliceSequential>", File.ReadAllText(proj));
        string dist = Alice.Cli.Commands.WasmCommands.Dist(proj);
        var args = Alice.Cli.Commands.WasmCommands.PublishArgs(proj, dist, release: true, aotWorkload: false);
        args[args.IndexOf("q")] = "m";
        var r = DotNet.Run(args);
        Assert.True(r.ExitCode == 0, string.Join("\n", r.Output.TakeLast(40)));
        Assert.Empty(TrimWarnings(r));
        string gen = Directory.GetFiles(Path.Combine(root, "web", "obj"), "main.al.g.cs", SearchOption.AllDirectories).Single(f => f.Contains("Release"));
        Assert.DoesNotContain("Alice.Runtime.Par.", File.ReadAllText(gen));   // no thread pool in the browser
        Assert.Empty(Directory.GetFiles(Path.Combine(dist, "wwwroot", "_framework"), "icudt*"));   // invariant globalization
        string runner = Path.Combine(root, "web", "run.mjs");
        File.WriteAllText(runner, WasmTemplates.NodeRunner(Path.Combine(dist, "wwwroot", "_framework", "dotnet.js")));
        try
        {
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(runner);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Contains("6765 3001207125000 7499850000 1 7", output);
        }
        catch (System.ComponentModel.Win32Exception) { }   // no Node: the build checks above still ran
    }

    [Fact]
    public void Ready_to_run_build_publishes_precompiled_code()
    {
        var proj = Package("r2r", "[package]\nname = \"r2r\"\n\n[build]\nready_to_run = true\n");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", Path.GetDirectoryName(Path.GetDirectoryName(proj))!, "--release"]));
        Assert.True(File.Exists(Path.Combine(root, "r2r", "bin", "r2r", "r2r.dll")));
    }
}
