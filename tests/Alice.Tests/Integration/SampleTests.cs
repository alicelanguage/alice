using System.Diagnostics;

namespace Alice.Tests.Integration;

/// <summary>Two-way interop through MSBuild: C# consumes an Alice project and Alice consumes a C# project.</summary>
[Trait("Category", "Integration")]
[Collection("msbuild")]   // MSBuild runs on the same repository projects (obj/) must not overlap
public class SampleTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static string Dotnet(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.Combine(Root(), "samples") };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        var (code, o) = ChildProcess.Run(psi);   // async reads, no build servers: the suite never hangs on a pipe
        Assert.True(code == 0, o);
        return o;
    }

    [Fact]
    public void Interop_samples_build_and_run()
    {
        // the Alice workspace generates its MSBuild projects; C# then references AliceLib like any project
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", Path.Combine(Root(), "samples")]));
        Dotnet("build", "CSharpApp/CSharpApp.csproj");
        var cs = Dotnet("run", "--no-build", "--project", "CSharpApp/CSharpApp.csproj");
        Assert.Contains("distance=5", cs);
        Assert.Contains("pattern-matched an Alice sum type from C#", cs);
        var al = Dotnet("run", "--no-build", "--project", "AliceApp/.alice/AliceApp.alproj");
        Assert.Contains("median from C#: 3", al);
        Assert.Contains("C# counter: 45", al);
        Assert.Contains("distance: 10", al);
    }

    [Fact]
    public void Entity_framework_example_translates_alice_lambdas_to_sql()
    {
        // a DbContext subclass written in Alice; EF translates the Where/Select/Count lambdas (expression trees),
        // and the paren-less `ToQueryString` resolves to EF's extension method
        var dir = Path.Combine(Root(), "examples", "data", "efcore");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));
        var output = Dotnet("run", "--no-build", "--project", Path.Combine(dir, ".alice", "efcore.alproj"));
        Assert.Contains("rated above 3: alice, carol", output);
        Assert.Contains("names starting with b: 1", output);
        Assert.Contains("WHERE \"b\".\"rating\" > 3", output);
    }

    [Fact]
    public void Gpu_kernel_written_in_alice_runs_through_ilgpu()
    {
        // ILGPU compiles the Alice module function passed as `->Kernels.saxpy` (a method group, not a lambda) and runs
        // it on the preferred device: a GPU (CUDA/OpenCL) when present, else ILGPU's CPU accelerator
        var dir = Path.Combine(Root(), "examples", "gpu", "ilgpu");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));
        var output = Dotnet("run", "--no-build", "--project", Path.Combine(dir, ".alice", "ilgpu_saxpy.alproj"));
        Assert.Contains("y[0]=1.0 y[10]=21.0 y[1023]=2047.0", output);
        Assert.Contains("gpu for: y[0]=1.0 y[10]=21.0 y[4095]=8191.0", output);   // the loop compiled to a kernel
        Assert.Contains("gpu sum: 9999900000", output);                          // a reduction through atomic adds
        Assert.Contains("gpu region: 50.0 50.0", output);                        // 51 launches on device-resident arrays
    }

    // ---- domain samples (ROADMAP-1.0 stream H): each builds, runs headless, and its `alice test` specs pass ----

    /// <summary>`alice ARGS` in a child process run from <paramref name="dir"/> (`alice test` reads ./test and ./src).</summary>
    static string AliceCli(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir };
        psi.ArgumentList.Add(typeof(Alice.Cli.App).Assembly.Location);
        psi.ArgumentList.Add("-q");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        var (code, o) = ChildProcess.Run(psi);
        Assert.True(code == 0, o);
        return o;
    }

    /// <summary>Runs a built Alice program (`dotnet bin/Debug/net10.0/NAME.dll ARGS`) and returns exit code and output.</summary>
    static (int Code, string Output) RunBuilt(string dir, string name, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir };
        psi.ArgumentList.Add(Path.Combine(dir, "bin", "Debug", "net10.0", name + ".dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        return ChildProcess.Run(psi, timeoutMinutes: 5);
    }

    static string Built(string dir, string name, params string[] args)
    {
        AliceCli(dir, "build");   // a child process: an in-process build of the ASP.NET workspace member hung the run
        var (code, o) = RunBuilt(dir, name, args);
        Assert.True(code == 0, o);
        return o.Replace("\r", "");
    }

    [Fact]
    public void Netchat_tcp_server_and_clients_talk_through_channels()
    {
        // one hub task owns the room, one task per connection; the demo runs a server and two clients over loopback
        var dir = Path.Combine(Root(), "samples", "netchat");
        var o = Built(dir, "netchat", "demo");
        Assert.Contains("bob   <- * set a name first: /nick NAME\nbob   <- * bob joined\nbob   <- <alice> hi bob\n" +
                        "bob   <- * online: alice, bob\nalice <- * bye\nbob   <- * alice left\ndemo done", o);
        Assert.Contains("6 examples, 0 failures", AliceCli(dir, "test"));
    }

    [Fact]
    public void Web_api_with_di_ef_core_websocket_and_web_application_factory_checks()
    {
        // routes, JSON bodies, DI, EF Core in memory and a WebSocket echo, checked in process by WebApplicationFactory
        // (the echo against a real Kestrel process); the pricing and catalog libraries have their own specs
        var dir = Path.Combine(Root(), "examples", "web", "aspnet");
        var o = Built(Path.Combine(dir, "apitests"), "ApiTests");
        Assert.Contains("PASS create product", o);
        Assert.Contains("PASS websocket echo", o);
        Assert.Contains("14 passed, 0 failed", o);
        Assert.Contains("3 examples, 0 failures", AliceCli(Path.Combine(dir, "pricing"), "test"));
        Assert.Contains("5 examples, 0 failures", AliceCli(Path.Combine(dir, "catalog"), "test"));
    }

    [Fact]
    public void Avalonia_notes_app_mvvm_two_views_and_saved_settings_headless()
    {
        // the real windows, driven headless: typing goes through two-way bindings, buttons run view-model commands,
        // and a second start reads the settings file written by the first
        var dir = Path.Combine(Root(), "examples", "ui", "avalonia");
        var settings = Path.Combine(Path.GetTempPath(), "alice-notes-" + Guid.NewGuid().ToString("N"));
        var o = Built(dir, "avalonia_notes", "--headless", settings);
        Assert.Contains("title: Notes for friend\nnotes view: 2 items, \"2 notes\"\n" +
                        "settings view: saved=true, title: Notes for Alice\ncount visible: false\n" +
                        "after restart: title: Notes for Alice, name box: Alice", o);
        Assert.Contains("5 examples, 0 failures", AliceCli(dir, "test"));
    }

    [Fact]
    public void Game_samples_build_and_their_rules_are_tested_headless()
    {
        // MonoGame DesktopGL and raylib need a display to open a window: here they build, their game logic runs
        // under `alice test`, Pong plays 60 s without a window, and its RULES.al proof is current
        var samples = Path.Combine(Root(), "samples");
        foreach (var game in new[] { "BounceGame", "Breakout", "celeste", "raylib-pong" })
            Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", Path.Combine(samples, game)]));
        Assert.Contains("0 failures", AliceCli(Path.Combine(samples, "Breakout"), "test"));
        Assert.Contains("0 failures", AliceCli(Path.Combine(samples, "celeste"), "test"));
        var pong = Path.Combine(samples, "raylib-pong");
        Assert.Contains("7 examples, 0 failures", AliceCli(pong, "test"));
        AliceCli(pong, "prove", "--check", "RULES.al");
        var (code, o) = RunBuilt(pong, "raylib_pong", "--simulate", "3600");
        Assert.True(code == 0, o);
        Assert.Contains("after 3600 frames: left 0, right 2, paddle hits 32", o);
    }

    [Fact]
    public void Grep_cli_globs_scans_in_parallel_and_follows_grep_exit_codes()
    {
        var dir = Path.Combine(Root(), "samples", "alice-grep");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));
        var (code, o) = RunBuilt(dir, "alice-grep", "-n", "def self.walk", "src/**/*.al");
        Assert.True(code == 0, o);
        Assert.Matches(@"src/glob\.al:\d+:  def self\.walk\(dir : String\) : Array\(String\)", o);   // file:line:text
        Assert.Equal(1, RunBuilt(dir, "alice-grep", "no such text anywhere", "src").Code);
        Assert.Equal(2, RunBuilt(dir, "alice-grep", "-z", "x").Code);
        Assert.Equal(2, RunBuilt(dir, "alice-grep", "(", "src").Code);
        Assert.Contains("6 examples, 0 failures", AliceCli(dir, "test"));
    }

    [Fact]
    public void Data_pipeline_reads_csv_aggregates_and_writes_json()
    {
        var dir = Path.Combine(Root(), "examples", "data", "pipeline");
        var o = Built(dir, "pipeline");
        Assert.Contains("\"revenueCents\": 29325", o);
        Assert.Contains("\"topProduct\": \"Assam tea\"", o);
        Assert.Contains("\"region\": \"West\",\n      \"orders\": 3,\n      \"items\": 15,\n      \"revenueCents\": 13050", o);
        Assert.Contains("7 examples, 0 failures", AliceCli(dir, "test"));
    }

    [Fact]
    public void Lowlevel_sample_calls_libc_runs_a_native_kernel_and_walks_pointers()
    {
        if (!OperatingSystem.IsLinux()) return;   // the sample binds libc.so.6
        var dir = Path.Combine(Root(), "samples", "lowlevel");
        var o = Built(dir, "lowlevel");
        Assert.Contains("qsort: 3 7 19 25 42", o);
        Assert.Contains("env_length(HOME) == strlen: true", o);
        Assert.Contains("malloc'd squares 0..999: 332833500", o);
        Assert.Contains("dot = 600000.0 (managed 600000.0)", o);   // native when clang built the library, else managed
        Assert.Contains("index tree: 100000 nodes, height 39, in order: true", o);
        Assert.Contains("6 examples, 0 failures", AliceCli(dir, "test"));
    }
}
