using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alice.Telemetry.Wire;

/// <summary>A validated event as the collector stores it. Mirrors the JSON written by `TelemetryEvent.ToJson`.</summary>
public sealed record WireEvent(
    int Schema, string InstallId, string Command, string AliceVersion, string Os, string Arch, string Dotnet,
    int ExitCode, long DurationMs, string? CrashType, string? CrashStack,
    IReadOnlyDictionary<string, int> ErrorCodes, IReadOnlyDictionary<string, long>? Phases);

/// <summary>
/// The batch wire format `{"events":[...]}` shared by the CLI and tools/telemetry-server (linked source, no project
/// reference). Validation is strict: unknown fields, wrong types, or values that could carry personal data are rejected.
/// </summary>
public static partial class TelemetryWire
{
    public const int MaxBodyBytes = 512 * 1024;
    public const int MaxEventsPerBatch = 100;
    public const int MaxStackChars = 4000;

    static readonly HashSet<string> EventFields =
        ["schema", "install_id", "command", "alice_version", "os", "arch", "dotnet", "exit_code", "duration_ms", "crash", "error_codes", "phases_ms"];
    static readonly HashSet<string> Os = ["linux", "windows", "macos", "other"];

    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex Id();
    [GeneratedRegex("^[a-z]{1,16}( [a-z-]{1,12})?$")] private static partial Regex Cmd();
    [GeneratedRegex(@"^[0-9A-Za-z.\-]{1,40}$")] private static partial Regex Ver();
    [GeneratedRegex("^[a-z0-9]{1,16}$")] private static partial Regex Arch();
    [GeneratedRegex(@"^(AL|CS)\d{4}$")] private static partial Regex Code();
    [GeneratedRegex("^[a-z_]{1,24}$")] private static partial Regex Phase();
    [GeneratedRegex(@"^(System|Alice|Microsoft)\.[A-Za-z0-9_.`+]{1,200}$|^Exception$")] private static partial Regex CrashType();
    [GeneratedRegex(@"^   at (<external>|(Alice|System|Microsoft|Spectre)\.[^\s(/\\]+\([A-Za-z0-9_.,`\[\]<>&*? ]*\))$")] private static partial Regex Frame();

    public static bool TryParseBatch(ReadOnlySpan<byte> body, out List<WireEvent> events, out string error)
    {
        events = [];
        if (body.Length > MaxBodyBytes) { error = "body too large"; return false; }
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "expected object"; return false; }
            foreach (var p in root.EnumerateObject()) if (p.Name != "events") { error = $"unknown field '{Clip(p.Name)}'"; return false; }
            if (!root.TryGetProperty("events", out var arr) || arr.ValueKind != JsonValueKind.Array) { error = "missing events"; return false; }
            if (arr.GetArrayLength() is 0 or > MaxEventsPerBatch) { error = "events: 1-100 per batch"; return false; }
            foreach (var e in arr.EnumerateArray())
            {
                if (!TryParseEvent(e, out var ev, out error)) return false;
                events.Add(ev!);
            }
            error = "";
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { error = "malformed json"; return false; }
    }

    static string Clip(string s) => s.Length > 32 ? s[..32] : s;

    static bool TryParseEvent(JsonElement e, out WireEvent? ev, out string error)
    {
        ev = null;
        if (e.ValueKind != JsonValueKind.Object) { error = "event must be an object"; return false; }
        foreach (var p in e.EnumerateObject()) if (!EventFields.Contains(p.Name)) { error = $"unknown field '{Clip(p.Name)}'"; return false; }
        string Str(string k, Regex r) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && r.IsMatch(s)
            ? s : throw new FormatException(k);
        try
        {
            int schema = e.GetProperty("schema").GetInt32();
            string id = Str("install_id", Id()), cmd = Str("command", Cmd()), ver = Str("alice_version", Ver()),
                arch = Str("arch", Arch()), dotnet = Str("dotnet", Ver());
            string os = e.GetProperty("os").GetString() is { } o && Os.Contains(o) ? o : throw new FormatException("os");
            int exit = e.GetProperty("exit_code").GetInt32();
            long dur = e.GetProperty("duration_ms").GetInt64();
            if (schema < 1 || dur < 0 || dur > 86_400_000) throw new FormatException("range");
            string? ctype = null, cstack = null;
            if (e.TryGetProperty("crash", out var c))
            {
                foreach (var p in c.EnumerateObject()) if (p.Name is not ("type" or "stack")) throw new FormatException("crash field");
                ctype = c.GetProperty("type").GetString() is { } t && CrashType().IsMatch(t) ? t : throw new FormatException("crash.type");
                cstack = c.GetProperty("stack").GetString() ?? throw new FormatException("crash.stack");
                var lines = cstack.Split('\n');
                if (cstack.Length > MaxStackChars || lines[0] != ctype || lines.Skip(1).Any(l => !Frame().IsMatch(l))) throw new FormatException("crash.stack");
            }
            var codes = new Dictionary<string, int>();
            foreach (var p in e.GetProperty("error_codes").EnumerateObject())
            {
                if (!Code().IsMatch(p.Name) || p.Value.GetInt32() is < 1 or > 100_000) throw new FormatException("error_codes");
                codes[p.Name] = p.Value.GetInt32();
            }
            Dictionary<string, long>? phases = null;
            if (e.TryGetProperty("phases_ms", out var ph))
            {
                phases = [];
                foreach (var p in ph.EnumerateObject())
                {
                    if (!Phase().IsMatch(p.Name) || p.Value.GetInt64() is < 0 or > 86_400_000 || phases.Count >= 16) throw new FormatException("phases_ms");
                    phases[p.Name] = p.Value.GetInt64();
                }
            }
            ev = new WireEvent(schema, id, cmd, ver, os, arch, dotnet, exit, dur, ctype, cstack, codes, phases);
            error = "";
            return true;
        }
        catch (Exception x) when (x is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            error = $"invalid field '{Clip(x.Message)}'";
            return false;
        }
    }
}
