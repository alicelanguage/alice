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

    [Fact]
    public void Cache_is_owner_only_and_rejects_tampered_or_shared_images()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string key = RunCache.Key(Files("puts 4\n"), "x", []);
            RunCache.Put(key, [1, 2, 3], dir);
            var image = Directory.GetFiles(dir, key + "*").Single();
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(dir));
                Assert.Equal((UnixFileMode)0b110_000_000, File.GetUnixFileMode(image));
            }
            // a modified image is a miss, never loaded
            var bytes = File.ReadAllBytes(image);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(image, bytes);
            Assert.Null(RunCache.TryGet(key, dir));
            // a planted image without the per-install tag is a miss too
            File.WriteAllBytes(image, [1, 2, 3]);
            Assert.Null(RunCache.TryGet(key, dir));

            RunCache.Put(key, [5, 6], dir);
            Assert.Equal([5, 6], RunCache.TryGet(key, dir));
            if (!OperatingSystem.IsWindows())
            {
                // a directory other users can write to is not trusted
                File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_111);
                Assert.Null(RunCache.TryGet(key, dir));
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
