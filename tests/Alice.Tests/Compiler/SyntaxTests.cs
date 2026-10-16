using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>More Ruby/Crystal syntax: heredocs, %w, regex literals, &:sym, endless defs, attr_accessor, alias, retry...</summary>
[Collection("console")]
public class SyntaxTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        { "heredoc squiggly", "name = \"Ann\"\ntext = <<~EOS\n  Hello #{name}\n    indented\n  bye\nEOS\nputs text", "Hello Ann\n  indented\nbye" },
        { "heredoc dash", "x = <<-TXT\nraw line\n  TXT\nputs x.strip", "raw line" },
        { "word arrays", "xs = %w[apple banana cherry]\nputs xs.size\nputs xs[1]\nputs %i[a b].join(\",\")", "3\nbanana\na,b" },
        { "symbol to proc", "puts [\"a\", \"b\"].map(&:upcase).join\nputs [1, 2, 3].select(&:even?).size", "AB\n1" },
        { "lambda and proc", "sq = lambda { |x : Int32| x * x }\ninc = proc { |x : Int32| x + 1 }\nputs sq.call(4) + inc.call(1)", "18" },
        { "endless def", "def sq(x : Int32) : Int32 = x * x\ndef hi(n : String) = \"hi #{n}\"\nputs sq(7)\nputs hi(\"z\")", "49\nhi z" },
        { "regex literal and =~", "s = \"order 42 shipped\"\nputs s =~ /\\d+/\nputs s.match?(/SHIP/i)\nputs (\"abc\" =~ /z/).nil?", "6\ntrue\ntrue" },
        { "string format and repeat", "puts \"%05d|%.2f|%s\" % [42, 3.14159, \"ok\"]\nputs \"ab\" * 3\nputs \"=\" * 5", "00042|3.14|ok\nababab\n=====" },
        { "append operator", "xs = [] of Int32\nxs << 1 << 2\nxs << 3\nputs xs.size\nsb = StringBuilder.new\nsb << \"a\" << \"b\"\nputs sb.ToString()", "3\nab" },
        { "array set operations", "a = [1, 2, 3]\nb = [2, 3, 4]\nputs (a + b).size\nputs (a - b).join(\",\")\nputs (a & b).join(\",\")\nputs (a | b).join(\",\")", "6\n1\n2,3\n1,2,3,4" },
        { "attr_accessor ruby style", "class Pt\n  attr_accessor :x, :y\n  attr_reader :tag\n  def initialize(x : Int32, y : Int32)\n    @x = x\n    @y = y\n    @tag = \"pt\"\n  end\nend\np = Pt.new(1, 2)\np.x = 10\nputs p.x + p.y\nputs p.tag", "12\npt" },
        { "inferred instance variables", "class Counter\n  def initialize\n    @count = 0\n    @name = \"c\"\n  end\n  def bump : Int32\n    @count += 1\n    @count\n  end\n  def label : String\n    \"#{@name}#{@count}\"\n  end\nend\nc = Counter.new\nc.bump\nc.bump\nputs c.label", "c2" },
        { "alias", "class Greeter\n  def hello(n : String) : String\n    \"hello #{n}\"\n  end\n  alias hi hello\nend\nputs Greeter.new.hi(\"bo\")", "hello bo" },
        { "retry", "tries = 0\nbegin\n  tries += 1\n  raise \"flaky\" if tries < 3\n  puts \"ok after #{tries}\"\nrescue e : Exception\n  retry\nend", "ok after 3" },
        { "keyword arguments", "def box(w : Int32 = 1, h : Int32 = 1) : Int32\n  w * h\nend\nputs box(h: 5)\nputs box(w: 2, h: 3)", "5\n6" },
        { "multiple return and destructuring", "def minmax(xs : Array(Int32))\n  return xs.min, xs.max\nend\nlo, hi = minmax([3, 9, 1])\nputs \"#{lo}..#{hi}\"", "1..9" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }

    [Fact]
    public void Division_is_not_a_regex()
        => Assert.Equal("2\n4", Run("a = 8\nb = 4\nputs a / b\nputs a/2"));
}
