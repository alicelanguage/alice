using Alice.Cli.Projects;

namespace Alice.Tests.Tooling;

/// <summary>Package builds type-check once: MSBuild's csc, whose errors are reported in Alice terms; `alice gen` no
/// longer runs a second, cold in-process Roslyn compile (2.4 of 3.7 s on a one-file edit).</summary>
[Collection("console")]
public class IncrementalGenTests
{
    [Fact]
    public void Csc_errors_at_alice_lines_are_reported_as_alice_diagnostics()
    {
        var d = DotNet.ParseLine("/p/src/main.al(3,5): error CS1061: 'int' does not contain a definition for 'frob' and no accessible extension method 'frob' accepting a first argument of type 'int' could be found [/p/.alice/p.alproj]");
        Assert.NotNull(d);
        Assert.Equal("AL0302", d!.Code);
        Assert.Equal("/p/src/main.al", d.File);
        Assert.Equal((3, 5), (d.Line, d.Col));
        Assert.Contains("frob", d.Message);
        Assert.DoesNotContain("accessible extension", d.Message);
    }

    [Fact]
    public void Other_msbuild_diagnostics_pass_through()
    {
        var d = DotNet.ParseLine("/p/x.csproj(1,1): error MSB4025: The project file could not be loaded.");
        Assert.Equal("MSB4025", d!.Code);
        Assert.Null(DotNet.ParseLine("  p -> /p/bin/p.dll"));
    }

    [Fact]
    public void Gen_without_check_leaves_type_errors_to_csc()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "main.al");
            File.WriteAllText(src, "x = 1\nputs x.frob\n");
            Assert.Equal(1, Alice.Cli.App.Run(["--lang", "en", "gen", "-o", Path.Combine(dir, "a"), src]));
            Assert.Equal(0, Alice.Cli.App.Run(["--lang", "en", "gen", "--no-check", "-o", Path.Combine(dir, "b"), src]));
            Assert.True(File.Exists(Path.Combine(dir, "b", "main.al.g.cs")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>`#line` maps lines only: errors that come from the generated C# point at the name they quote in the Alice line.</summary>
public class AliceColumnTests
{
    [Theory]
    [InlineData("AL0302", "undefined method 'frob' for Int32", 14, "  x.frob", 5)]
    [InlineData("AL0302", "undefined method 'to_widget' for String", 30, "  s.to_widget(1)", 5)]
    [InlineData("AL0302", "undefined method 'ToWidget' for String", 30, "  s.to_widget(1)", 5)]   // .NET-cased name
    [InlineData("AL0300", "something odd", 40, "    y = 2", 5)]                                 // no name: first token
    [InlineData("AL0101", "expected `end`", 3, "  if x", 3)]                                    // Alice's own columns stay
    public void Csharp_derived_errors_point_at_the_alice_token(string code, string msg, int col, string line, int expected)
        => Assert.Equal(expected, Alice.Cli.Ui.DiagnosticRenderer.AliceColumn(code, msg, col, line));
}
