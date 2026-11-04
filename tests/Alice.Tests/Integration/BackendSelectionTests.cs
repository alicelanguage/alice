using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Alice.Tests.Integration;

/// <summary>
/// ADR 0033: `alice run` and `alice build` compile loose files through the IL backend by default; `--backend csharp`
/// and ALICE_BACKEND=csharp select the C# printer + Roslyn. 1.0.0/1.0.1 resolved the backend and dropped the result,
/// so every command compiled through C# (benchmarks/bend2 measured C# while the docs said IL).
/// </summary>
[Trait("Category", "Integration")]
public class BackendSelectionTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-backend-" + Guid.NewGuid().ToString("N"));

    public BackendSelectionTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string AliceDll => typeof(Alice.Cli.App).Assembly.Location;

    (int Code, string Output) Alice(Dictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        psi.ArgumentList.Add(AliceDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["ALICE_NO_CACHE"] = "1";
        psi.Environment.Remove("ALICE_BACKEND");
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        return ChildProcess.Run(psi);
    }

    /// <summary>Roslyn marks every C# 11+ module with RefSafetyRulesAttribute; the IL backend does not.</summary>
    static bool BuiltByRoslyn(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        return md.TypeDefinitions.Any(h => md.GetString(md.GetTypeDefinition(h).Name) == "RefSafetyRulesAttribute")
            || md.TypeReferences.Any(h => md.GetString(md.GetTypeReference(h).Name) == "RefSafetyRulesAttribute");
    }

    string Build(string tag, Dictionary<string, string>? env, params string[] flags)
    {
        File.WriteAllText(Path.Combine(dir, "main.al"), "puts 6 * 7\n");
        string output = Path.Combine(dir, tag, "main");
        var (code, log) = Alice(env, ["-q", "build", .. flags, "main.al", "-o", output]);
        Assert.True(code == 0, log);
        var (run, printed) = ChildProcess.Run(new ProcessStartInfo("dotnet", output + ".dll") { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, run);
        Assert.Equal("42", printed.Trim());
        return output;
    }

    [Fact]
    public void Build_defaults_to_the_il_backend()
    {
        var o = Build("default", null);
        Assert.False(BuiltByRoslyn(o + ".dll"));
        Assert.True(File.Exists(o + ".pdb"));   // the IL backend writes its portable pdb beside the image
    }

    [Fact]
    public void Build_backend_csharp_uses_roslyn()
    {
        var o = Build("cs", null, "--backend", "csharp");
        Assert.True(BuiltByRoslyn(o + ".dll"));
        Assert.False(File.Exists(o + ".pdb"));
    }

    [Fact]
    public void Build_honours_ALICE_BACKEND()
    {
        Assert.True(BuiltByRoslyn(Build("env-cs", new() { ["ALICE_BACKEND"] = "csharp" }) + ".dll"));
        Assert.False(BuiltByRoslyn(Build("env-il", new() { ["ALICE_BACKEND"] = "il" }) + ".dll"));
        // the flag wins over the environment
        Assert.False(BuiltByRoslyn(Build("flag-il", new() { ["ALICE_BACKEND"] = "csharp" }, "--backend", "il") + ".dll"));
    }

    [Fact]
    public void Run_reports_the_backend_it_compiled_with()
    {
        File.WriteAllText(Path.Combine(dir, "main.al"), "puts 6 * 7\n");
        var (code, output) = Alice(null, "run", "main.al");
        Assert.True(code == 0, output);
        Assert.Contains("main (il, ", output);
        Assert.Contains("42", output);
        (code, output) = Alice(null, "run", "--backend", "csharp", "main.al");
        Assert.True(code == 0, output);
        Assert.Contains("main (csharp, ", output);
        (code, output) = Alice(new() { ["ALICE_BACKEND"] = "csharp" }, "run", "main.al");
        Assert.Contains("main (csharp, ", output);
    }

    [Fact]
    public void A_program_the_il_backend_cannot_compile_uses_csharp_with_a_note()
    {
        // a C callback parameter (`delegate* unmanaged`) has no IL lowering yet (examples/interop/libc.al): the C#
        // image runs, and says so (never called here, so any OS)
        File.WriteAllText(Path.Combine(dir, "main.al"), """
            @[Link("libc.so.6")]
            lib LibC
              fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void
            end
            puts 42
            """);
        var (code, output) = Alice(null, "run", "main.al");
        Assert.True(code == 0, output);
        Assert.Contains("note: the il backend does not support", output);
        Assert.Contains("compiled with the csharp backend", output);
        Assert.Contains("main (csharp, ", output);
        Assert.Contains("42", output);
    }

    [Fact]
    public void An_unknown_backend_is_a_usage_error()
    {
        File.WriteAllText(Path.Combine(dir, "main.al"), "puts 1\n");
        var (code, output) = Alice(null, "build", "--backend", "roslyn", "main.al");
        Assert.NotEqual(0, code);
        Assert.Contains("unknown backend `roslyn`", output);
    }
}
