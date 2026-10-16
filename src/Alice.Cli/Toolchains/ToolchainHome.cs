using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alice.Cli.Toolchains;

/// <summary>
/// rustup-style layout under `ALICE_HOME` (default `~/.alice`):
/// `toolchains/VERSION/` complete release directories, `bin/alice` shim to the default, `settings.json`.
/// A project pins a version with an `alice-toolchain` file; `ALICE_TOOLCHAIN` or `alice +VERSION` override it.
/// </summary>
public sealed class ToolchainHome(string root)
{
    public const string OverrideFile = "alice-toolchain";

    public static ToolchainHome Current() => new(Environment.GetEnvironmentVariable("ALICE_HOME") is { Length: > 0 } h
        ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alice"));

    public string Root { get; } = root;
    public string ToolchainsDir => Path.Combine(Root, "toolchains");
    public string BinDir => Path.Combine(Root, "bin");
    public string ShimPath => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "alice.cmd" : "alice");
    string SettingsPath => Path.Combine(Root, "settings.json");

    public string ToolchainDir(string version) => Path.Combine(ToolchainsDir, version);
    public string ExePath(string version) => Path.Combine(ToolchainDir(version), Platform.ExeName);

    public List<ToolchainVersion> Installed() =>
        !Directory.Exists(ToolchainsDir) ? new()
        : Directory.GetDirectories(ToolchainsDir)
            .Where(d => File.Exists(Path.Combine(d, Platform.ExeName)))
            .Select(d => ToolchainVersion.TryParse(Path.GetFileName(d))).OfType<ToolchainVersion>()
            .OrderDescending().ToList();

    public bool IsInstalled(string version) => File.Exists(ExePath(version));

    public string? Default
    {
        get => Read()["default"]?.GetValue<string>();
        set
        {
            var s = Read();
            if (value == null) s.Remove("default"); else s["default"] = value;
            Write(s);
            if (value != null) WriteShim(value); else if (File.Exists(ShimPath)) File.Delete(ShimPath);
        }
    }

    JsonObject Read()
    {
        try { return File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    void Write(JsonObject s)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(SettingsPath, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The `bin/alice` shim always runs the default toolchain; that binary re-dispatches to project overrides.</summary>
    void WriteShim(string version)
    {
        Directory.CreateDirectory(BinDir);
        string exe = ExePath(version);
        if (OperatingSystem.IsWindows())
            File.WriteAllText(ShimPath, $"@echo off\r\n\"{exe}\" %*\r\n");
        else
        {
            File.WriteAllText(ShimPath, Shim.Replace("{exe}", exe));
            File.SetUnixFileMode(ShimPath, (UnixFileMode)0b111_101_101);
        }
    }

    /// <summary>Unix shim (same as install.sh writes): sets DOTNET_ROOT from the dotnet on PATH when unset.</summary>
    const string Shim = @"#!/bin/sh
# Alice shim: the apphost only searches default .NET locations, so point it at the dotnet on PATH.
if [ -z ""${DOTNET_ROOT:-}"" ]; then
  if [ -x ""$HOME/.dotnet/dotnet"" ]; then DOTNET_ROOT=""$HOME/.dotnet""
  elif d=""$(command -v dotnet 2>/dev/null)""; then
    while [ -L ""$d"" ]; do l=""$(readlink ""$d"")""; case ""$l"" in /*) d=""$l"" ;; *) d=""$(dirname ""$d"")/$l"" ;; esac; done
    DOTNET_ROOT=""$(cd ""$(dirname ""$d"")"" && pwd)""
  fi
  [ -n ""${DOTNET_ROOT:-}"" ] && export DOTNET_ROOT
fi
exec ""{exe}"" ""$@""
";

    /// <summary>Which toolchain applies here: env > nearest `alice-toolchain` file > default.</summary>
    public (string Version, string Source) Resolve(string dir, string? env)
    {
        if (!string.IsNullOrWhiteSpace(env)) return (env.Trim(), "ALICE_TOOLCHAIN");
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, OverrideFile);
            if (File.Exists(f) && File.ReadAllText(f).Trim() is { Length: > 0 } v) return (v, $"{OverrideFile} file ({f})");
        }
        return (Default ?? App.Version, Default != null ? "default" : "running binary");
    }

    public void Uninstall(string version)
    {
        var dir = ToolchainDir(version);
        if (!Directory.Exists(dir)) throw new ToolchainException($"toolchain {version} is not installed", "alice toolchain list");
        Directory.Delete(dir, true);
        if (Default == version) Default = Installed().FirstOrDefault()?.ToString();
    }
}
