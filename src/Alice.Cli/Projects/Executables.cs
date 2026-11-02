using System.ComponentModel;

namespace Alice.Cli.Projects;

/// <summary>
/// Finds helper programs (dotnet, node, clang) on PATH. A bare name passed to Process.Start is looked up in the
/// working directory before PATH (and the app directory on Windows), so an untrusted checkout could ship its own `dotnet`.
/// Only absolute PATH entries are searched; empty, `.` and relative entries are skipped.
/// </summary>
public static class Executables
{
    /// <summary>Absolute path of <paramref name="name"/>; throws Win32Exception (like Process.Start) when it is not on PATH.</summary>
    public static string Resolve(string name, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        string file = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? name + ".exe" : name;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir = entry.Trim('"');
            if (!Path.IsPathFullyQualified(dir)) continue;
            string candidate = Path.Combine(dir, file);
            if (IsExecutable(candidate)) return candidate;
        }
        // `dotnet` installed by the script lives in DOTNET_ROOT, which the shim exports when it is not on PATH
        if (name == "dotnet" && Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && Path.IsPathFullyQualified(root)
            && Path.Combine(root, file) is var d && IsExecutable(d)) return d;
        throw new Win32Exception(2, $"{name} was not found on PATH");
    }

    static bool IsExecutable(string f)
    {
        if (!File.Exists(f)) return false;
        if (OperatingSystem.IsWindows()) return true;
        const UnixFileMode anyExec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(f) & anyExec) != 0;
    }
}
