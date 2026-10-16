using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Alice.Cli.Toolchains;

/// <summary>Downloads a release archive, verifies its SHA-256, extracts it atomically into `toolchains/VERSION`.</summary>
public sealed class Installer(ToolchainHome home, IReleaseSource source)
{
    public Action<string, string>? Progress { get; init; }

    public ToolchainVersion Install(string spec, string rid, bool makeDefault = true, bool force = false)
    {
        var release = ReleaseFeed.Pick(source.List(), spec)
            ?? throw new ToolchainException($"no release matches '{spec}'", "alice toolchain available");
        string version = release.Version.ToString();
        if (home.IsInstalled(version) && !force)
        {
            if (makeDefault || home.Default == null) home.Default = version;
            return release.Version;
        }
        var asset = release.Asset(rid) ?? throw new ToolchainException($"release {version} has no {rid} archive", "supported: linux-x64, win-x64, osx-x64, osx-arm64");
        var sum = release.Checksum(rid) ?? throw new ToolchainException($"release {version} has no checksum for {rid}");

        Directory.CreateDirectory(home.ToolchainsDir);
        string tmp = Path.Combine(home.ToolchainsDir, $".tmp-{version}-{Guid.NewGuid():N}");
        string archive = tmp + (asset.Name.EndsWith(".zip") ? ".zip" : ".tar.gz");
        try
        {
            Progress?.Invoke("Downloading", asset.Size > 0 ? $"{asset.Name} ({asset.Size / 1048576.0:0.0} MB)" : asset.Name);
            using (var input = source.Open(asset.Url))
            using (var output = File.Create(archive)) input.CopyTo(output);

            string expected;
            using (var s = source.Open(sum.Url))
            using (var r = new StreamReader(s)) expected = r.ReadToEnd().Trim().Split(' ', '\t')[0].ToLowerInvariant();
            string actual;
            using (var f = File.OpenRead(archive)) actual = Convert.ToHexStringLower(SHA256.HashData(f));
            if (expected != actual) throw new ToolchainException($"checksum mismatch for {asset.Name}: expected {expected}, got {actual}", "the download was corrupted or tampered with; retry");

            Progress?.Invoke("Verified", "sha256 " + actual[..16] + "…");
            Progress?.Invoke("Extracting", version);
            Directory.CreateDirectory(tmp);
            if (archive.EndsWith(".zip")) ZipFile.ExtractToDirectory(archive, tmp);
            else
            {
                using var fs = File.OpenRead(archive);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, tmp, overwriteFiles: true);
            }
            var exe = Path.Combine(tmp, Platform.ExeName);
            if (!File.Exists(exe)) throw new ToolchainException($"{asset.Name} does not contain {Platform.ExeName}");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            var dest = home.ToolchainDir(version);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(tmp, dest);
            if (makeDefault || home.Default == null) home.Default = version;
            return release.Version;
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
