using Alice.Cli.Telemetry;

namespace Alice.Tests.Tooling;

/// <summary>Opt-in CLI telemetry (ADR 0031): consent states, env overrides, CI detection, sanitization, update prompt.</summary>
public class TelemetryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-tel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static Func<string, string?> Env(params (string, string)[] vars) =>
        k => vars.FirstOrDefault(v => v.Item1 == k).Item2;

    [Fact]
    public void MissingFileIsUnsetAndNothingIsSent()
    {
        var c = ConsentStore.Load(dir);
        Assert.Equal(ConsentState.Unset, c.State);
        Assert.False(TelemetryPolicy.Enabled(c, Env()));
    }

    [Fact]
    public void GrantedRoundTripsWithPolicyVersionAndInstallId()
    {
        var c = ConsentStore.Load(dir).With(ConsentState.Granted);
        ConsentStore.Save(dir, c);
        var back = ConsentStore.Load(dir);
        Assert.Equal(ConsentState.Granted, back.State);
        Assert.Equal(TelemetryPolicy.PolicyVersion, back.PolicyVersion);
        Assert.Matches("^[0-9a-f]{32}$", back.InstallId);
        Assert.True(TelemetryPolicy.Enabled(back, Env()));
        Assert.Contains("state = \"granted\"", File.ReadAllText(Path.Combine(dir, "telemetry.toml")));
    }

    [Fact]
    public void DeniedNeverSends()
    {
        var c = ConsentStore.Load(dir).With(ConsentState.Denied);
        Assert.False(TelemetryPolicy.Enabled(c, Env()));
    }

    [Fact]
    public void GrantUnderOlderPolicyDoesNotSend()
    {
        var c = new Consent(ConsentState.Granted, TelemetryPolicy.PolicyVersion - 1, "ab", null);
        Assert.False(TelemetryPolicy.Enabled(c, Env()));
    }

    [Theory]
    [InlineData("ALICE_TELEMETRY_OPTOUT", "1")]
    [InlineData("ALICE_TELEMETRY_OPTOUT", "true")]
    [InlineData("DO_NOT_TRACK", "1")]
    public void EnvOptOutBeatsGrant(string key, string value)
    {
        var c = ConsentStore.Load(dir).With(ConsentState.Granted);
        Assert.False(TelemetryPolicy.Enabled(c, Env((key, value))));
        Assert.True(TelemetryPolicy.EnvOptOut(Env((key, value))));
    }

    [Fact]
    public void DoNotTrackZeroIsNotAnOptOut() => Assert.False(TelemetryPolicy.EnvOptOut(Env(("DO_NOT_TRACK", "0"))));

    [Theory]
    [InlineData("CI")]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("GITLAB_CI")]
    [InlineData("TF_BUILD")]
    [InlineData("BUILDKITE")]
    [InlineData("JENKINS_URL")]
    public void CiIsDetected(string key) => Assert.True(TelemetryPolicy.IsCi(Env((key, "true"))));

    [Fact]
    public void NoCiVarsIsNotCi() => Assert.False(TelemetryPolicy.IsCi(Env(("HOME", "/x"))));

    [Fact]
    public void PromptsOnlyWhenUnsetOnTtyOutsideCi()
    {
        var unset = ConsentStore.Load(dir);
        Assert.True(TelemetryPolicy.ShouldPrompt(unset, Env(), interactive: true));
        Assert.False(TelemetryPolicy.ShouldPrompt(unset, Env(), interactive: false));
        Assert.False(TelemetryPolicy.ShouldPrompt(unset, Env(("CI", "1")), interactive: true));
        Assert.False(TelemetryPolicy.ShouldPrompt(unset, Env(("DO_NOT_TRACK", "1")), interactive: true));
        Assert.False(TelemetryPolicy.ShouldPrompt(unset.With(ConsentState.Granted), Env(), interactive: true));
        Assert.False(TelemetryPolicy.ShouldPrompt(unset.With(ConsentState.Denied), Env(), interactive: true));
    }

    [Fact]
    public void SelfUpdateRespectsStoredAnswerAndAsksWhenUnset()
    {
        int asked = 0;
        bool Ask() { asked++; return true; }
        ConsentStore.Save(dir, ConsentStore.Load(dir).With(ConsentState.Denied));
        Assert.Equal(ConsentState.Denied, TelemetryPolicy.AfterUpdate(dir, Env(), true, Ask));
        Assert.Equal(0, asked);

        File.Delete(Path.Combine(dir, "telemetry.toml"));
        Assert.Equal(ConsentState.Unset, TelemetryPolicy.AfterUpdate(dir, Env(("CI", "1")), true, Ask));
        Assert.Equal(0, asked);
        Assert.Equal(ConsentState.Granted, TelemetryPolicy.AfterUpdate(dir, Env(), true, Ask));
        Assert.Equal(1, asked);
        Assert.Equal(ConsentState.Granted, ConsentStore.Load(dir).State);
        Assert.Equal(ConsentState.Granted, TelemetryPolicy.AfterUpdate(dir, Env(), true, Ask));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void DecliningThePromptIsStoredAsDenied()
    {
        TelemetryPolicy.AfterUpdate(dir, Env(), true, () => false);
        Assert.Equal(ConsentState.Denied, ConsentStore.Load(dir).State);
    }

    [Fact]
    public void ResetRegeneratesInstallId()
    {
        var a = ConsentStore.Load(dir).With(ConsentState.Granted);
        Assert.NotEqual(a.InstallId, a.ResetId().InstallId);
    }

    [Fact]
    public void StackIsSanitized()
    {
        var raw = "System.InvalidOperationException: secret message from /home/bob/app/main.al\n" +
                  "   at Alice.Compiler.Binder.Bind(Node n) in /home/bob/src/alice/Binder.cs:line 42\n" +
                  "   at Alice.Cli.App.Run(String[] argv) in C:\\Users\\bob\\alice\\App.cs:line 7\n" +
                  "   at MyUserCode.Foo()";
        var s = Sanitizer.Stack(raw);
        Assert.DoesNotContain("bob", s);
        Assert.DoesNotContain("secret", s);
        Assert.DoesNotContain("main.al", s);
        Assert.DoesNotContain("MyUserCode", s);
        Assert.Contains("at Alice.Compiler.Binder.Bind(Node n)", s);
        Assert.Contains("at Alice.Cli.App.Run(String[] argv)", s);
    }

    [Fact]
    public void ErrorCodesAreCountedWithoutText()
    {
        var counts = Sanitizer.Codes(["AL0202", "AL0202", "CS0103", "/home/bob/x.al: boom"]);
        Assert.Equal(2, counts["AL0202"]);
        Assert.Equal(1, counts["CS0103"]);
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public void EventJsonHasOnlyDocumentedFields()
    {
        var e = TelemetryEvent.Create("build", "0.13.2", "abc", 1, TimeSpan.FromMilliseconds(12),
            crash: null, codes: new() { ["AL0202"] = 1 }, phases: null);
        var json = e.ToJson();
        Assert.Contains("\"command\":\"build\"", json);
        Assert.Contains("\"exit_code\":1", json);
        Assert.Contains("\"AL0202\":1", json);
        Assert.DoesNotContain(Environment.UserName, json.Replace("\"install_id\":\"abc\"", ""));
    }

    [Fact]
    public void CommandNameIsWhitelisted()
    {
        Assert.Equal("build", TelemetryEvent.CommandName(["build", "/home/bob/secret.al"]));
        Assert.Equal("run", TelemetryEvent.CommandName(["/home/bob/secret.al"]));
        Assert.Equal("other", TelemetryEvent.CommandName(["bobs-typo"]));
        Assert.Equal("self update", TelemetryEvent.CommandName(["self", "update"]));
    }

    [Fact]
    public void QueueIsCappedAndNoEndpointMeansNothingSent()
    {
        var q = new TelemetryQueue(dir, maxBytes: 600);
        for (int i = 0; i < 50; i++) q.Append(new string('x', 100));
        Assert.True(new FileInfo(Path.Combine(dir, "telemetry-queue.jsonl")).Length <= 600);
        Assert.Null(TelemetrySender.ResolveEndpoint(new Consent(ConsentState.Granted, 1, "a", null), Env()));
        Assert.False(TelemetrySender.ServerLive);
        Assert.Equal("https://x", TelemetrySender.ResolveEndpoint(new Consent(ConsentState.Granted, 1, "a", "https://x"), Env()));
        Assert.Equal("https://y", TelemetrySender.ResolveEndpoint(new Consent(ConsentState.Granted, 1, "a", null), Env(("ALICE_TELEMETRY_ENDPOINT", "https://y"))));
    }
}
