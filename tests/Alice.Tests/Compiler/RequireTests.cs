using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>`require` and `require_relative` load a sibling file (paths relative to the requiring file, `.al` optional).</summary>
[Collection("console")]
public class RequireTests
{
    [Theory]
    [InlineData("require \"lib/util\"")]
    [InlineData("require_relative \"lib/util\"")]
    [InlineData("  require_relative \"./lib/util.al\"")]
    public void A_required_sibling_file_is_loaded_and_runs(string line)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-require-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        File.WriteAllText(Path.Combine(dir, "lib", "util.al"), "def twice(x : Int32) : Int32\n  x * 2\nend\n");
        var main = Path.Combine(dir, "main.al");
        File.WriteAllText(main, line + "\nputs twice(21)\n");
        var files = AliceCompiler.LoadWithRequires([main]);
        Assert.Equal(2, files.Count);
        Assert.EndsWith(Path.Combine("lib", "util.al"), files[1].Path);
        var cs = AliceCompiler.TranspileAll(files);
        var asm = AliceCompiler.Load(AliceCompiler.Emit(cs, "req", exe: true));
        Assert.Equal("42", AliceCompiler.Invoke(asm, []).Trim());
    }

    [Fact]
    public void A_missing_require_relative_file_is_reported_at_its_line()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-require-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var main = Path.Combine(dir, "main.al");
        File.WriteAllText(main, "puts 1\nrequire_relative \"nope\"\n");
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.LoadWithRequires([main]));
        Assert.Contains(":2:1: cannot find 'nope'", ex.Message);
    }
}
