namespace Alice.Cli.Telemetry;

/// <summary>Who may send and when to ask. Default is off: only an explicit grant under the current policy sends, and env opt-outs always win.</summary>
public static class TelemetryPolicy
{
    /// <summary>Bump when the set of collected fields changes; older grants then stop sending until re-asked.</summary>
    public const int PolicyVersion = 1;

    public const string Prompt =
        "Help improve Alice? With your permission the alice CLI sends anonymous usage data: the command name (never its " +
        "arguments), Alice/.NET version, OS and architecture, exit code, duration, the type and sanitized stack of compiler " +
        "crashes, and counts of error codes such as AL0202. No source code, file names, paths, user names or IP addresses are " +
        "stored, and your compiled programs never send anything. Change it any time with `alice telemetry off|on`, or set " +
        "ALICE_TELEMETRY_OPTOUT=1. Details: docs/telemetry.md.";

    static readonly string[] CiVars =
        ["CI", "CONTINUOUS_INTEGRATION", "BUILD_NUMBER", "RUN_ID", "GITHUB_ACTIONS", "GITLAB_CI", "TF_BUILD", "BUILDKITE", "CIRCLECI",
         "TRAVIS", "APPVEYOR", "JENKINS_URL", "TEAMCITY_VERSION", "BITBUCKET_BUILD_NUMBER", "CODEBUILD_BUILD_ID", "DRONE", "SEMAPHORE"];

    static bool Truthy(string? s) => s is { Length: > 0 } && s != "0" && !s.Equals("false", StringComparison.OrdinalIgnoreCase);

    public static bool IsCi(Func<string, string?> env) => CiVars.Any(v => Truthy(env(v)));

    public static bool EnvOptOut(Func<string, string?> env) => Truthy(env("ALICE_TELEMETRY_OPTOUT")) || Truthy(env("DO_NOT_TRACK"));

    public static bool Enabled(Consent c, Func<string, string?> env) =>
        !EnvOptOut(env) && c.State == ConsentState.Granted && c.PolicyVersion == PolicyVersion;

    /// <summary>Ask only when no current answer exists, on an interactive terminal, outside CI, and not opted out by env.</summary>
    public static bool ShouldPrompt(Consent c, Func<string, string?> env, bool interactive) =>
        interactive && !IsCi(env) && !EnvOptOut(env) && (c.State == ConsentState.Unset || c.PolicyVersion != PolicyVersion);

    /// <summary>
    /// After `alice self update` (and once on an interactive command): a stored answer under the current policy is kept
    /// silently; otherwise, when prompting is allowed, asks and stores the answer. Returns the effective state.
    /// </summary>
    public static ConsentState AfterUpdate(string dir, Func<string, string?> env, bool interactive, Func<bool> ask)
    {
        var c = ConsentStore.Load(dir);
        if (!ShouldPrompt(c, env, interactive)) return c.PolicyVersion == PolicyVersion ? c.State : ConsentState.Unset;
        bool yes;
        try { yes = ask(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return ConsentState.Unset; }
        var answered = c.With(yes ? ConsentState.Granted : ConsentState.Denied);
        try { ConsentStore.Save(dir, answered); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return answered.State;
    }
}
