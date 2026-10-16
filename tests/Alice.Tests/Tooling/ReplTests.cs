using Alice.Cli.Repl;
using Spectre.Console;
using System.Reflection;

namespace Alice.Tests.Tooling;

[Collection("console")]
public class ReplTests
{
    static string CaptureGuidance(Action render)
    {
        using var output = new StringWriter();
        var saved = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
        });
        try
        {
            render();
            return output.ToString();
        }
        finally { AnsiConsole.Console = saved; }
    }

    [Fact]
    public void Banner_renders_the_package_mode_shortcut()
    {
        var output = CaptureGuidance(() => typeof(ReplCommand)
            .GetMethod("Banner", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null));
        Assert.Contains("Alice", output);
        Assert.Contains("] packages", output);
        Assert.Contains("Ctrl-D exits", output);
    }

    [Fact]
    public void Help_renders_the_package_mode_examples()
    {
        var output = CaptureGuidance(() => typeof(ReplCommand)
            .GetMethod("Command", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new ReplSession(), ":help"]));
        Assert.Contains("]add Newtonsoft.Json", output);
        Assert.Contains("]remove X", output);
        Assert.Contains("]list", output);
        Assert.Contains("]search json", output);
    }

    [Fact]
    public void Values_definitions_ans_and_muted_replay()
    {
        var s = new ReplSession();
        Assert.Equal("2", s.Eval("x = 2").Value);
        Assert.Equal("42", s.Eval("x * 21").Value);
        var p = s.Eval("puts \"hi\"");
        Assert.Equal("hi" + Environment.NewLine, p.Output);
        Assert.Null(p.Value);
        Assert.Equal("", s.Eval("1").Output);                       // earlier `puts` is replayed silently
        Assert.Null(s.Eval("def sq(n : Int32) : Int32\n  n * n\nend").Error);
        Assert.Equal("49", s.Eval("sq(7)").Value);
        Assert.Equal("2", s.Eval("1 + 1").Value);
        Assert.Equal("20", s.Eval("ans * 10").Value);
        Assert.Equal("[1, 2, 3]", s.Eval("[1, 2, 3]").Value);
        Assert.Equal("\"str\"", s.Eval("\"str\"").Value);
    }

    [Fact]
    public void Errors_do_not_break_the_session()
    {
        var s = new ReplSession();
        s.Eval("x = 5");
        var e = s.Eval("nope_name + 1");
        Assert.NotNull(e.Error);
        Assert.Contains("AL0303", e.Error);
        Assert.Contains("repl[2]:1", e.Error);
        var r = s.Eval("raise \"boom\"");
        Assert.Contains("boom", r.Error);
        Assert.Equal("5", s.Eval("x").Value);
    }

    [Fact]
    public void Redefinition_replaces_and_reset_clears()
    {
        var s = new ReplSession();
        s.Eval("def f : Int32\n  1\nend");
        s.Eval("def f : Int32\n  2\nend");
        Assert.Equal("2", s.Eval("f").Value);
        s.Eval("class Pt\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\nend");
        Assert.Equal("3", s.Eval("Pt.new(3).x").Value);
        s.Reset();
        Assert.NotNull(s.Eval("f").Error);
    }

    [Theory]
    [InlineData("x = 1", true)]
    [InlineData("def f", false)]
    [InlineData("def f\nend", true)]
    [InlineData("class A\n  def f\n  end", false)]
    [InlineData("xs.each do |x|", false)]
    [InlineData("xs.each { |x| puts x }", true)]
    [InlineData("puts \"abc", false)]
    [InlineData("xs = [1,", false)]
    [InlineData("if x > 1", false)]
    [InlineData("y = x if x > 1", true)]
    [InlineData("s = <<-TXT\nhello", false)]
    [InlineData("def sq(n) = n * n", true)]
    public void Detects_incomplete_input(string src, bool complete) => Assert.Equal(complete, ReplSession.IsComplete(src));

    [Theory]
    [InlineData("puts", "puts")]
    [InlineData("Math.Sqrt", "Sqrt(Float64) : Float64")]
    [InlineData("String", "class String")]
    [InlineData("describe", "describe(String name")]
    [InlineData("each", "each")]
    public void Help_mode_lookup(string q, string expected) => Assert.Contains(expected, ReplHelp.Lookup(q));

    [Fact]
    public void Completion_offers_session_names_keywords_and_members()
    {
        var s = new ReplSession();
        s.Eval("counter = 1");
        Assert.Contains("counter", s.Complete("cou"));
        Assert.Contains("class", s.Complete("cla"));
        Assert.Contains("Math.Sqrt", s.Complete("Math.Sq"));
    }
}
