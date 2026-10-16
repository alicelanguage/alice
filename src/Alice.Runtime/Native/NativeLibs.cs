using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>
/// Loader for `@[Native]` functions (compiled to a shared library with clang). Returns null when the library or
/// export is missing; generated code then calls the managed fallback, so native code is always optional.
/// </summary>
public static class NativeLibs
{
    static readonly ConcurrentDictionary<string, nint> Loaded = new();
    static readonly List<string> SearchDirs = new();

    /// <summary>Extra directories to search (the CLI registers its build/temp output here).</summary>
    public static void AddSearchPath(string dir)
    {
        lock (SearchDirs) if (!SearchDirs.Contains(dir)) SearchDirs.Insert(0, dir);
    }

    static string FileName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".dll" : OperatingSystem.IsMacOS() ? "lib" + baseName + ".dylib" : "lib" + baseName + ".so";

    static nint Load(string baseName) => Loaded.GetOrAdd(baseName, static b =>
    {
        var dirs = new List<string>();
        lock (SearchDirs) dirs.AddRange(SearchDirs);
        var env = Environment.GetEnvironmentVariable("ALICE_NATIVE_PATH");
        if (!string.IsNullOrEmpty(env)) dirs.AddRange(env.Split(Path.PathSeparator));
        dirs.Add(AppContext.BaseDirectory);
        foreach (var d in dirs)
            if (NativeLibrary.TryLoad(Path.Combine(d, FileName(b)), out var h)) return h;
        return 0;
    });

    public static unsafe void* Export(string baseName, string symbol)
    {
        var h = Load(baseName);
        return h != 0 && NativeLibrary.TryGetExport(h, symbol, out var p) ? (void*)p : null;
    }

    public static bool IsLoaded(string baseName) => Load(baseName) != 0;
}
