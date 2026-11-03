using Alice.Fuzz;

namespace Alice.Tests.Fuzz;

/// <summary>The fuzz minimizer (ddmin over lines, then blocks, then single lines) and the shrinking guards.</summary>
public class MinimizerTests
{
    [Fact]
    public void Ddmin_keeps_exactly_the_lines_the_failure_needs()
    {
        var lines = Enumerable.Range(0, 60).Select(i => "x" + i + " = " + i).ToList();
        string src = string.Join('\n', lines) + "\n";
        var min = Minimizer.Minimize(src, s => s.Contains("x7 = 7") && s.Contains("x41 = 41"));
        Assert.Equal("x7 = 7\nx41 = 41\n", min);
    }

    [Fact]
    public void Whole_blocks_are_removed_through_their_end()
    {
        string src = "a = 1\ndef f(x : Int32) : Int32\n  if x > 0\n    x\n  else\n    0\n  end\nend\nputs a\n";
        // the failure needs `puts a` and a parseable program: a block may only go as a whole
        var min = Minimizer.Minimize(src, s => s.Contains("puts a") && s.Split('\n').Count(l => l.TrimStart().StartsWith("end")) == s.Split('\n').Count(l => l.TrimStart().StartsWith("def ") || l.TrimStart().StartsWith("if ")));
        Assert.Equal("puts a\n", min);
    }

    [Fact]
    public void A_finding_is_written_with_its_divergence_as_a_header()
    {
        var f = new Finding("diff-il:stdout", "C# backend:\n1\nIL backend:\n2", "puts 1\n");
        var text = f.ToFile();
        Assert.StartsWith("# fuzz finding diff-il-stdout-", text);
        Assert.Contains("# IL backend:", text);
        Assert.EndsWith("\nputs 1\n", text);
        Assert.Equal(f.Id, (f with { Detail = "other" }).Id);   // the id hashes the program, not the detail
    }

    [Fact]
    public void Shrinking_guards_keep_loops_terminating_and_effects_strippable()
    {
        Assert.True(Shrink.Terminates("  j2 = 0\n  while j2 < 3\n    x = 1\n    j2 += 1\n  end\n"));
        Assert.False(Shrink.Terminates("  j2 = 0\n  while j2 < 3\n    x = 1\n  end\n"));
        Assert.False(Shrink.Terminates("  while j2 < 3\n    j2 += 1\n  end\n"));
        Assert.Equal("def f(n : Int32) : Int32\n  for i in 0...n\n  end\nend\n",
            Shrink.WithoutEffects("safe pure def f(n : Int32) : Int32\n  par for i in 0...n\n  end\nend\n"));
        Assert.Equal("  a, b = f(1), g(2)\n", Shrink.WithoutEffects("  par a, b = f(1), g(2)\n"));
    }

    [Fact]
    public void Known_issues_match_by_category_detail_and_source()
    {
        var f = new Finding("roslyn-reject", "error AL0300: The operation overflows at compile time in checked mode", "x = 1\n");
        Assert.Equal("D1-constant-overflow", Known.Match(f));
        Assert.Null(Known.Match(f with { Category = "rejected:AL0210" }));
        var pure = new Finding("rejected:AL0210", "`k` is not pure", "pure def k(n : Int32) : Int32\n  case n\n  when 1..4\n    1\n  else\n    0\n  end\nend\n");
        Assert.Equal("F4-pure-range-pattern", Known.Match(pure));
        Assert.Null(Known.Match(pure with { Source = "pure def k(n : Int32) : Int32\n  puts n\nend\n" }));
    }
}
