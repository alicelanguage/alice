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
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, o);
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
    public void Avalonia_window_with_alice_code_behind()
    {
        // MainWindow.axaml + an Alice class deriving Window: the XAML loads, FindControl finds the named TextBlock
        // (an extension method called with implicit self), and the click handler updates it. Headless: no display
        var dir = Path.Combine(Root(), "examples", "ui", "avalonia");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));
        Assert.Contains("clicked 2", Dotnet("run", "--no-build", "--project", Path.Combine(dir, ".alice", "avalonia_counter.alproj")));
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
}
