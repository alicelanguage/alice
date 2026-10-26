using System.Security.Cryptography;

namespace Alice.Cli.Telemetry;

public enum ConsentState { Unset, Granted, Denied }

/// <summary>The stored answer (telemetry.toml). <paramref name="Endpoint"/> overrides the build's endpoint; <paramref name="Perf"/> opts into compile-phase timings.</summary>
public sealed record Consent(ConsentState State, int PolicyVersion, string InstallId, string? Endpoint, bool Perf = false)
{
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Records an answer under the current policy version.</summary>
    public Consent With(ConsentState s) => this with { State = s, PolicyVersion = TelemetryPolicy.PolicyVersion };

    /// <summary>A fresh random install id (unlinkable from the previous one).</summary>
    public Consent ResetId() => this with { InstallId = NewId() };
}

/// <summary>Reads and writes `telemetry.toml` in the user config dir. A missing or unreadable file is Unset.</summary>
public static class ConsentStore
{
    public const string FileName = "telemetry.toml";

    /// <summary>`$ALICE_CONFIG_DIR`, else `%APPDATA%\alice` on Windows, else `$XDG_CONFIG_HOME/alice` or `~/.config/alice`.</summary>
    public static string ConfigDir(Func<string, string?> env)
    {
        if (env("ALICE_CONFIG_DIR") is { Length: > 0 } d) return d;
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "alice");
        if (env("XDG_CONFIG_HOME") is { Length: > 0 } x) return Path.Combine(x, "alice");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "alice");
    }

    public static Consent Load(string dir)
    {
        var state = ConsentState.Unset; int policy = 0; string? id = null, endpoint = null; bool perf = false;
        try
        {
            foreach (var raw in File.ReadAllLines(Path.Combine(dir, FileName)))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith('#') || eq < 0) continue;
                string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim().Trim('"');
                switch (k)
                {
                    case "state": state = v switch { "granted" => ConsentState.Granted, "denied" => ConsentState.Denied, _ => ConsentState.Unset }; break;
                    case "policy_version": int.TryParse(v, out policy); break;
                    case "install_id": if (v.Length == 32 && v.All(Uri.IsHexDigit)) id = v; break;
                    case "endpoint": endpoint = v.Length > 0 ? v : null; break;
                    case "perf": perf = v == "true"; break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new Consent(state, policy, id ?? Consent.NewId(), endpoint, perf);
    }

    public static void Save(string dir, Consent c)
    {
        Directory.CreateDirectory(dir);
        var state = c.State switch { ConsentState.Granted => "granted", ConsentState.Denied => "denied", _ => "unset" };
        var text = "# Alice CLI telemetry consent. See docs/telemetry.md. `alice telemetry off` or ALICE_TELEMETRY_OPTOUT=1 disables it.\n" +
                   $"state = \"{state}\"\npolicy_version = {c.PolicyVersion}\ninstall_id = \"{c.InstallId}\"\nperf = {(c.Perf ? "true" : "false")}\n" +
                   (c.Endpoint is { } e ? $"endpoint = \"{e}\"\n" : "");
        File.WriteAllText(Path.Combine(dir, FileName), text);
    }
}
