using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

public class FormatterTests
{
    const string Messy = "class Point\n      getter x : Int32\n  def initialize(@x : Int32)\nend\n    def show   \n  if x > 0\n puts \"pos #{x} # not a comment\"\n      else\n  puts \"end of if\"\n   end\n  [1, 2].each do |v|\nputs v\n     end\n  xs = [1, 2].map { |v|\n v * 2 }\n    puts \"x\" if x > 1\n  end\n\n\n\nend\n";

    const string Clean = "class Point\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\n  def show\n    if x > 0\n      puts \"pos #{x} # not a comment\"\n    else\n      puts \"end of if\"\n    end\n    [1, 2].each do |v|\n      puts v\n    end\n    xs = [1, 2].map { |v|\n      v * 2 }\n    puts \"x\" if x > 1\n  end\n\nend\n";

    [Fact]
    public void Reindents_by_block_structure() => Assert.Equal(Clean, Formatter.Format(Messy));

    [Fact]
    public void Is_idempotent() => Assert.Equal(Clean, Formatter.Format(Formatter.Format(Messy)));

    [Fact]
    public void Case_when_and_rescue_align_with_opener()
    {
        const string src = "case x\n    when 1 then 2\n  else\n 3\nend\nbegin\nrisky\n rescue e : Exception\n puts e\n ensure\n done\n  end\n";
        const string expected = "case x\nwhen 1 then 2\nelse\n  3\nend\nbegin\n  risky\nrescue e : Exception\n  puts e\nensure\n  done\nend\n";
        Assert.Equal(expected, Formatter.Format(src));
    }
}
