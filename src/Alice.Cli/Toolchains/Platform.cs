using System.Runtime.InteropServices;

namespace Alice.Cli.Toolchains;

/// <summary>Release runtime identifiers and archive names (must match `.github/scripts/package.ps1`).</summary>
public static class Platform
{
    public static string ExeName => OperatingSystem.IsWindows() ? "alice.exe" : "alice";

    public static string CurrentRid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "arm64", _ => "x64" };
        return $"{os}-{arch}";
    }

    public static string ArchiveName(string version, string rid) =>
        $"alice-{version}-{rid}" + (rid.StartsWith("win") ? ".zip" : ".tar.gz");
}
