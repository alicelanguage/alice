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
    public void Heredoc_bodies_are_kept_verbatim()
    {
        const string src = "def f\nx = <<EOT\n  keep\n\n\n spaces\nEOT\ny = <<-'RAW'\n#{x}\n   RAW\nputs x\nend\n";
        const string expected = "def f\n  x = <<EOT\n  keep\n\n\n spaces\nEOT\n  y = <<-'RAW'\n#{x}\n   RAW\n  puts x\nend\n";
        Assert.Equal(expected, Formatter.Format(src));
        Assert.Equal(expected, Formatter.Format(expected));
    }

    [Fact]
    public void Percent_literal_contents_are_not_code()
        => Assert.Equal("xs = %w{a b}\nif x\n  y = %q(end {)\nend\n", Formatter.Format("xs = %w{a b}\nif x\ny = %q(end {)\nend\n"));

    /// <summary>Sources using the newer syntax: formatting must keep them parsing to the same tree (and be idempotent).</summary>
    public static TheoryData<string> RoundTrip => new()
    {
        "class A\nprivate\ndef priv : Int32\n1\nend\npublic\ndef name=(v : String)\n@n = v\nend\ndef -@ : A\nself\nend\ndef []=(i : Int32, v : Int32)\nend\nprivate :priv\ngetter? ok : Bool = true\ngetter a, b : Int32\nclass_getter count : Int32 = 0\nend\n",
        "def m : Int32\nraise \"x\"\nrescue ArgumentException, FormatException => e\n0\nrescue => e\nraise\nelse\n1\nensure\nputs \"e\"\nend\n",
        "k = 0\nbegin\nk += 1\nend while k < 3\nk += 1 until k > 9\nr = loop do\nbreak k if k > 3\nend\nx = Int32.Parse(\"z\") rescue 0\n",
        "puts [1].map { _1 * 2 }\nputs [1].map { it + 1 }\nputs [1].inject(:+)\nputs xs[1..], xs[..2]\ncase 3\nwhen 4.. then 1\nend\n",
        "s = <<~'EOT'\n  raw #{x}\n  EOT\nt = <<EOT\nplain\nEOT\nputs %q(a), %Q[b #{1}], 'ab', \"a\" \"b\", \"\\u{1F600}\"\nputs __FILE__, __LINE__, __method__\n",
    };

    [Theory, MemberData(nameof(RoundTrip))]
    public void Format_then_parse_round_trips(string src)
    {
        string once = Formatter.Format(src);
        Assert.Equal(once, Formatter.Format(once));
        Assert.Equal(Dump(Parser.ParseFile(src, "rt.al")), Dump(Parser.ParseFile(once, "rt.al")));
    }

    /// <summary>The tree without positions (and without mixin source text, which carries indentation).</summary>
    static string Dump(object? n)
    {
        var sb = new System.Text.StringBuilder();
        void Go(object? x)
        {
            switch (x)
            {
                case null: sb.Append("null"); return;
                case string s: sb.Append('"').Append(s).Append('"'); return;
                case System.Collections.IEnumerable e:
                    sb.Append('[');
                    foreach (var y in e) { Go(y); sb.Append(','); }
                    sb.Append(']');
                    return;
                case Node or Alice.Compiler.Syntax.RuleDomain:
                    sb.Append(x.GetType().Name).Append('{');
                    foreach (var f in x.GetType().GetFields().Where(f => f.Name is not ("Line" or "Col" or "SourceText" or "Sym" or "Res") && f.FieldType.Namespace != "Alice.Compiler.Semantics"))
                    {
                        sb.Append(f.Name).Append('=');
                        Go(f.GetValue(x));
                        sb.Append(';');
                    }
                    sb.Append('}');
                    return;
                default:
                    if (x.GetType().IsPrimitive || x is Enum) sb.Append(x);
                    else if (x is System.Runtime.CompilerServices.ITuple t) for (int i = 0; i < t.Length; i++) { Go(t[i]); sb.Append('|'); }
                    else sb.Append(x.GetType().Name);
                    return;
            }
        }
        Go(n);
        return sb.ToString();
    }

    [Fact]
    public void Indents_resource_blocks_and_interface_default_methods()
    {
        const string messy = "async def go : Int32\nawait using r = R.new\nusing f = F.new\nputs 1\nend\nend\n1\nend\ninterface IG\ndef name : String\ndef greet : String\n\"hi\"\nend\nend\nunsafe def p : Int32\n1\nend\n";
        const string clean = "async def go : Int32\n  await using r = R.new\n    using f = F.new\n      puts 1\n    end\n  end\n  1\nend\ninterface IG\n  def name : String\n  def greet : String\n    \"hi\"\n  end\nend\nunsafe def p : Int32\n  1\nend\n";
        Assert.Equal(clean, Formatter.Format(messy));
        Assert.Equal(clean, Formatter.Format(clean));
    }
}
