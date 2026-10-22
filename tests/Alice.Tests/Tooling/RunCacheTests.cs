using Alice.Cli.Commands;
using Alice.Compiler;

namespace Alice.Tests.Tooling;

/// <summary>`alice run` keeps the Roslyn image of an unchanged program: the second run skips the compile.</summary>
public class RunCacheTests
{
    static List<SourceFile> Files(string text) => [new SourceFile("/tmp/x.al", text)];

    [Fact]
    public void Key_depends_on_sources_name_and_references()
    {
        string k = RunCache.Key(Files("puts 1\n"), "x", []);
        Assert.Equal(k, RunCache.Key(Files("puts 1\n"), "x", []));
        Assert.NotEqual(k, RunCache.Key(Files("puts 2\n"), "x", []));
        Assert.NotEqual(k, RunCache.Key(Files("puts 1\n"), "y", []));
        Assert.NotEqual(k, RunCache.Key(Files("puts 1\n"), "x", ["/nonexistent/Lib.dll"]));
    }

    [Fact]
    public void Stored_image_comes_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string key = RunCache.Key(Files("puts 3\n"), "x", []);
            Assert.Null(RunCache.TryGet(key, dir));
            RunCache.Put(key, [1, 2, 3], dir);
            Assert.Equal([1, 2, 3], RunCache.TryGet(key, dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
