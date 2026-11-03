using System.Diagnostics;
using Alice.Compiler;
using Alice.Compiler.Backend.Wasm;
using Xunit.Abstractions;

namespace Alice.Tests.Backends;

/// <summary>
/// E1-E2: the native wasm backend (AirToWasm, ADR 0034, `runtime = "alice"`). Programs compile to a wasm32-wasi
/// module, run under Node (node:wasi) and must print exactly what the C# backend prints (the oracle). Skipped
/// without Node on PATH.
/// </summary>
[Collection("console")]
public class WasmTests(ITestOutputHelper output)
{
    static readonly string? Node = Which("node");

    static string? Which(string exe)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(d, OperatingSystem.IsWindows() ? exe + ".exe" : exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static byte[] Wasm(IReadOnlyList<SourceFile> files, WasmOptions? options = null)
    {
        var res = AirToWasm.Compile(files, options);
        Assert.True(res.Ok, res.Stats + "\n" + string.Join("\n", res.Errors.Select(e => e.Message)));
        return res.Wasm!;
    }

    /// <summary>The module under Node; stdout, with the exit code appended when it is not 0.</summary>
    static string RunNode(byte[] wasm, params string[] args)
    {
        var dir = Directory.CreateTempSubdirectory("alice-wasmtest-").FullName;
        try
        {
            var file = Path.Combine(dir, "m.wasm");
            File.WriteAllBytes(file, wasm);
            File.WriteAllText(Path.Combine(dir, "m.mjs"), Wasi.NodeRunner(file));
            var psi = new ProcessStartInfo(Node!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add(Path.Combine(dir, "m.mjs"));
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(120_000), "node timed out");
            Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {err.Result}");
            return o.Replace("\r\n", "\n");
        }
        finally { Directory.Delete(dir, true); }
    }

    static string RunCs(IReadOnlyList<SourceFile> files, params string[] args) =>
        AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit(AliceCompiler.TranspileAll(files), "cs", exe: true)), args).Replace("\r\n", "\n");

    void Differential(IReadOnlyList<SourceFile> files, params string[] args)
    {
        if (Node == null) return;
        var wasm = Wasm(files);
        string cs = RunCs(files, args);
        string w = RunNode(wasm, args);
        Assert.Equal(cs, w);
        output.WriteLine($"{wasm.Length} bytes\n{w}");
    }

    static List<SourceFile> Src(string src) => [new SourceFile("t.al", src)];

    [Fact]
    public void Hello_is_small_and_valid()
    {
        var wasm = Wasm(Src("puts \"hello\"\n"));
        Assert.Equal([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00], wasm[..8]);
        Assert.True(wasm.Length < 16 * 1024, $"hello is {wasm.Length} bytes");
        if (Node != null) Assert.Equal("hello\n", RunNode(wasm));
    }

    [Theory, MemberData(nameof(AirToIlTests.Programs), MemberType = typeof(AirToIlTests))]
    public void Programs_run_identically_to_csharp(string name, string src)
    {
        _ = name;
        Differential(Src(src));
    }

    [Theory]
    [InlineData("benchmarks/bend2/alice/nbody.al", "6")]
    [InlineData("benchmarks/bend2/alice/queens.al", "8")]
    [InlineData("benchmarks/bend2/alice/editdist.al", "6")]
    [InlineData("benchmarks/bend2/alice/bfs.al", "6")]
    [InlineData("benchmarks/bend2/alice/gameoflife.al", "6")]
    [InlineData("benchmarks/bend2/alice/hashmap.al", "6")]
    [InlineData("benchmarks/bend2/alice/kmeans.al", "6")]
    [InlineData("benchmarks/bend2/alice/mandelbrot.al", "6")]
    [InlineData("benchmarks/bend2/alice/merkle.al", "6")]
    [InlineData("benchmarks/bend2/alice/raytrace.al", "6")]
    [InlineData("benchmarks/bend2/alice/symreg.al", "6")]
    [InlineData("benchmarks/bend2/alice/terrain.al", "6")]
    [InlineData("benchmarks/bend2/alice/tree-bitonic.al", "6")]
    [InlineData("benchmarks/bend2/alice/tree-radix.al", "6")]
    [InlineData("benchmarks/bend2/idiomatic/symreg.al", "6")]
    [InlineData("benchmarks/bend2/idiomatic/tree-bitonic.al", "6")]
    [InlineData("benchmarks/bend2/idiomatic/tree-matmul.al", "6")]
    [InlineData("benchmarks/bend2/idiomatic/tree-radix.al", "6")]
    public void Benchmarks_run_identically_to_csharp(string file, string arg) =>
        Differential(AliceCompiler.LoadWithRequires([Path.Combine(Root(), file)]), arg);

    /// <summary>Arena enums (ADR 0037) as linear-memory slab terms and as heap records print the same as C#, and the
    /// arena module really uses the slabs.</summary>
    [Theory]
    [InlineData("benchmarks/bend2/alice/tree-radix.al", "6")]
    [InlineData("benchmarks/bend2/alice/merkle.al", "6")]
    [InlineData("benchmarks/bend2/alice/tree-bitonic.al", "6")]
    [InlineData("benchmarks/bend2/alice/symreg.al", "6")]
    public void Arena_and_records_agree_with_csharp(string file, string arg)
    {
        if (Node == null) return;
        var files = AliceCompiler.LoadWithRequires([Path.Combine(Root(), file)]);
        string cs = RunCs(files, arg);
        var arena = Wasm(files, new WasmOptions { Arena = true });
        var records = Wasm(files, new WasmOptions { Arena = false });
        Assert.Contains("arena_alloc", System.Text.Encoding.UTF8.GetString(arena));
        Assert.DoesNotContain("arena_alloc", System.Text.Encoding.UTF8.GetString(records));
        Assert.Equal(cs, RunNode(arena, arg));
        Assert.Equal(cs, RunNode(records, arg));
    }

    /// <summary>Coverage over benchmarks/ (`WASM_CORPUS=1`; reports, never fails): files that compile, and the
    /// unsupported node kinds ranked.</summary>
    [Fact]
    public void Corpus_coverage()
    {
        if (Environment.GetEnvironmentVariable("WASM_CORPUS") != "1") return;
        var root = Path.Combine(Root(), "benchmarks");
        var blocked = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int ok = 0, all = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*.al", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            all++;
            string verdict;
            try
            {
                var res = AirToWasm.Compile(AliceCompiler.LoadWithRequires([f]));
                if (res.Ok) { ok++; verdict = "ok " + res.Wasm!.Length; }
                else
                {
                    foreach (var k in res.Stats.Unsupported) blocked[k.Key] = blocked.GetValueOrDefault(k.Key) + k.Value;
                    verdict = "no " + string.Join(" | ", res.Errors.Take(3).Select(e => e.Message));
                }
            }
            catch (Exception e) { verdict = "crash " + e.GetType().Name + ": " + e.Message.Split((char)10)[0]; blocked["crash"] = blocked.GetValueOrDefault("crash") + 1; }
            output.WriteLine(Path.GetRelativePath(root, f) + ": " + verdict);
        }
        output.WriteLine($"compiled {ok}/{all}");
        foreach (var k in blocked.OrderByDescending(k => k.Value)) output.WriteLine($"  {k.Value,4} {k.Key}");
    }
}
