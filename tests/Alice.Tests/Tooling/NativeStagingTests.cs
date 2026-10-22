using Alice.Cli.Commands;
using Alice.Compiler;

namespace Alice.Tests.Tooling;

/// <summary>`alice build file.al --native` stages the sources it compiles: relative `require` paths must still resolve.</summary>
public class NativeStagingTests
{
    [Fact]
    public void Staging_keeps_the_relative_layout_requires_depend_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "alice-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "lib", "math"));
            Directory.CreateDirectory(Path.Combine(root, "bench"));
            File.WriteAllText(Path.Combine(root, "lib", "math", "sq.al"), "def sq(x : Int32) : Int32\n  x * x\nend\n");
            File.WriteAllText(Path.Combine(root, "bench", "main.al"), "require \"../lib/math/sq\"\nputs sq(7)\n");
            var files = AliceCompiler.LoadWithRequires([Path.Combine(root, "bench", "main.al")]);
            var work = Path.Combine(root, "out", "obj", "stage");
            CompileCommands.StageNativeSources(files, work);
            // the staged entry still finds its require, and compiles
            var staged = AliceCompiler.LoadWithRequires(Directory.GetFiles(work, "main.al", SearchOption.AllDirectories));
            Assert.Equal(2, staged.Count);
            Assert.Contains(AliceCompiler.TranspileAll(staged, []), cs => cs.Contains("sq("));
        }
        finally { Directory.Delete(root, true); }
    }
}
