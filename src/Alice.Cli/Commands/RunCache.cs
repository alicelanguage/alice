using System.Security.Cryptography;
using System.Text;
using Alice.Compiler;

namespace Alice.Cli.Commands;

/// <summary>
/// Cache of `alice run` images. Transpiling and the Roslyn compile cost about 1.5 s on every run; an unchanged program
/// (same sources, `require`d files, references and compiler build) reuses its IL image instead. The key covers
/// the compiler and runtime builds, so a new Alice never runs a stale image. Programs with `@[Native]` functions
/// and failed compiles are never stored. `ALICE_NO_CACHE=1` turns it off.
/// </summary>
public static class RunCache
{
    public static bool Enabled => Environment.GetEnvironmentVariable("ALICE_NO_CACHE") is not ("1" or "true");

    public static string DefaultDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "alice", "run");

    public static string Key(List<SourceFile> files, string name, List<string> refs)
    {
        var sb = new StringBuilder();
        sb.Append(typeof(AliceCompiler).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        sb.Append(typeof(Alice.Runtime.Prelude).Assembly.ManifestModule.ModuleVersionId).Append('\n');
        sb.Append(Environment.Version).Append('\n').Append(name).Append('\n');
        foreach (var r in refs)
            sb.Append("ref ").Append(r).Append(' ').Append(File.Exists(r) ? File.GetLastWriteTimeUtc(r).Ticks : 0).Append('\n');
        foreach (var f in files)
            sb.Append("file ").Append(f.Path).Append(' ').Append(f.LibraryOnly).Append(' ').Append(f.Text.Length).Append('\n').Append(f.Text).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static byte[]? TryGet(string key, string? dir = null)
    {
        try
        {
            var path = Path.Combine(dir ?? DefaultDir, key + ".dll");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes through a temporary file and a rename, so a concurrent run never reads half an image.</summary>
    public static void Put(string key, byte[] image, string? dir = null)
    {
        try
        {
            dir ??= DefaultDir;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(tmp, image);
            File.Move(tmp, Path.Combine(dir, key + ".dll"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
