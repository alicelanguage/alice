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

    [Theory]
    [InlineData("record struct V(x : Float64)\ndef d : Float64\nx\nend\nend\n", "record struct V(x : Float64)\n  def d : Float64\n    x\n  end\nend\n")]
    [InlineData("record P(a : Int32)\nputs 1\n", "record P(a : Int32)\nputs 1\n")]
    [InlineData("def f(x : Int32)\nreturn if x < 0\nreturn unless x > 9\nputs x\nend\n", "def f(x : Int32)\n  return if x < 0\n  return unless x > 9\n  puts x\nend\n")]
    public void Records_and_bare_postfix_return(string src, string expected) => Assert.Equal(expected, Formatter.Format(src));

    [Fact]
    public void Indents_resource_blocks_and_interface_default_methods()
    {
        const string messy = "async def go : Int32\nawait using r = R.new\nusing f = F.new\nputs 1\nend\nend\n1\nend\ninterface IG\ndef name : String\ndef greet : String\n\"hi\"\nend\nend\nunsafe def p : Int32\n1\nend\n";
        const string clean = "async def go : Int32\n  await using r = R.new\n    using f = F.new\n      puts 1\n    end\n  end\n  1\nend\ninterface IG\n  def name : String\n  def greet : String\n    \"hi\"\n  end\nend\nunsafe def p : Int32\n  1\nend\n";
        Assert.Equal(clean, Formatter.Format(messy));
        Assert.Equal(clean, Formatter.Format(clean));
    }
}
