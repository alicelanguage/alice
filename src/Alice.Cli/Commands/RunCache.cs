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

    const int TagBytes = 32;
    const UnixFileMode OwnerOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode SharedWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    /// <summary>
    /// The image is loaded and run, so an entry is only used when it carries this install's HMAC (key in `.key`, 0600)
    /// and, on Unix, neither the directory nor the file is writable by other users. Anything else is a miss.
    /// </summary>
    public static byte[]? TryGet(string key, string? dir = null)
    {
        try
        {
            dir ??= DefaultDir;
            var path = Path.Combine(dir, key + ".dll");
            if (!File.Exists(path) || !Trusted(dir) || !Trusted(path)) return null;
            var stored = File.ReadAllBytes(path);
            if (stored.Length < TagBytes || ReadKey(dir) is not { } mac) return null;
            var image = stored[TagBytes..];
            return CryptographicOperations.FixedTimeEquals(stored.AsSpan(0, TagBytes), HMACSHA256.HashData(mac, image)) ? image : null;
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
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else
            {
                Directory.CreateDirectory(dir, OwnerOnlyDir);
                if (File.GetUnixFileMode(dir) != OwnerOnlyDir) File.SetUnixFileMode(dir, OwnerOnlyDir);   // caches from older versions were 0755
            }
            var mac = ReadKey(dir) ?? CreateKey(dir);
            var tmp = Path.Combine(dir, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var f = Create(tmp))
            {
                f.Write(HMACSHA256.HashData(mac, image));
                f.Write(image);
            }
            File.Move(tmp, Path.Combine(dir, key + ".dll"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool Trusted(string path) => OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & SharedWrite) == 0;

    static byte[]? ReadKey(string dir)
    {
        var f = Path.Combine(dir, ".key");
        if (!File.Exists(f) || !Trusted(f)) return null;
        var k = File.ReadAllBytes(f);
        return k.Length == 32 ? k : null;
    }

    static byte[] CreateKey(string dir)
    {
        var k = RandomNumberGenerator.GetBytes(32);
        var tmp = Path.Combine(dir, ".key." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var f = Create(tmp)) f.Write(k);
        // two first runs may race: the first key wins, the loser's images are simply misses later
        try { File.Move(tmp, Path.Combine(dir, ".key"), overwrite: false); }
        catch (IOException) { File.Delete(tmp); return ReadKey(dir) ?? k; }
        return k;
    }

    static FileStream Create(string path) => new(path, OperatingSystem.IsWindows()
        ? new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write }
        : new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerOnlyFile });
}
