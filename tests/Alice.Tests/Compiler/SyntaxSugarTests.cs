using Alice.Compiler;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

/// <summary>
/// Ruby/Crystal syntax the parser lowers to existing constructs (language-gaps.md, syntax layer): string escapes and
/// literal forms, heredocs, operator symbols, implicit block params, setters, definable operators, visibility
/// sections, accessor macros, loop/break values, modifier loops, rescue forms, open ranges, compile-time names.
/// </summary>
[Collection("console")]
public class SyntaxSugarTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        // strings
        { "unicode escapes", "puts \"\\u{1F600}\".size\nputs \"\\u{48 49}\\u0021\"", "2\nHI!" },
        { "octal hex and space escapes", "puts \"\\101\\x42\\s|\\e\".size\nputs \"\\101\\x42\"", "5\nAB" },
        { "unknown escape is the char", "puts \"a\\qb\"", "aqb" },
        { "char escapes", "puts '\\n' == '\\u{A}'\nputs '\\u{41}'\nputs '\\101'\nputs '\\''", "true\nA\nA\n'" },
        { "single-quoted string", "puts 'single #{no}'\nputs 'it\\'s'.size", "single #{no}\n4" },
        { "adjacent literals", "puts \"a\" \"b\" 'c'\nx = \"con\" \\\n  \"cat\"\nputs x", "abc\nconcat" },
        { "percent strings", "x = 2\nputs %q(raw #{x} (nested))\nputs %Q[interp #{x + 1}]\nputs %(paren #{x})\nputs %q{a\\}b}", "raw #{x} (nested)\ninterp 3\nparen 2\na}b" },
        { "percent word arrays", "puts %w{a b c}.join(\"-\")\nputs %i<x y>.join(\",\")\nputs %w|p q|.size", "a-b-c\nx,y\n2" },
        { "raw dash heredoc", "x = 1\ns = <<-'EOT'\n  #{x}\n  EOT\nputs s.strip", "#{x}" },
        { "plain heredoc", "a = <<EOT\n  plain #{1 + 1}\nEOT\nputs a", "plain 2" },
        { "raw heredoc", "x = 2\nc = <<~'EOT'\n  raw #{x} \\n\n  EOT\nputs c", "raw #{x} \\n" },
        { "heredoc keeps quotes", "s = <<~EOT\n  say \"hi\" #{\"there\"}\n  EOT\nputs s", "say \"hi\" there" },
        // symbols, blocks
        { "operator symbols", "puts [1, 2, 3].inject(:+)\nputs [2, 3].reduce(1, :*)\nputs :a <=> :b\nputs :+", "6\n6\n-1\n+" },
        { "numbered block params", "puts [1, 2, 3].map { _1 * 2 }.join(\",\")\nputs [3, 1, 2].sort { _2 <=> _1 }.join(\",\")", "2,4,6\n3,2,1" },
        { "it block param", "puts [1, 2, 3].map { it * 3 }.join(\",\")\nputs [[1, 2], [3]].map { |xs| xs.map { it + 1 }.sum }.join(\",\")", "3,6,9\n5,4" },
        // definitions
        { "setter def", "class A\n  def name=(v : String)\n    @n = v\n  end\n  def name : String\n    @n\n  end\nend\na = A.new\na.name = \"z\"\na.name += \"y\"\nputs a.name", "zy" },
        { "setter beside getter", "class B\n  getter v : Int32 = 0\n  def v=(x : Int32)\n    @v = x * 2\n  end\nend\nb = B.new\nb.v = 4\nputs b.v", "8" },
        { "unary and shift operators", "class V\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\n  def -@ : V\n    V.new(-x)\n  end\n  def <<(n : Int32) : V\n    V.new(x + n)\n  end\nend\nputs (-V.new(3)).x\nv = V.new(1)\nv << 5\nputs (v << 2 << 3).x", "-3\n6" },
        { "index setter operator", "class Grid\n  def initialize\n    @d = Hash(Int32, Int32).new\n  end\n  def [](i : Int32) : Int32\n    @d[i]\n  end\n  def []=(i : Int32, v : Int32)\n    @d[i] = v\n  end\nend\ng = Grid.new\ng[1] = 5\nputs g[1]", "5" },
        { "visibility section", "class A\n  def pub : Int32\n    priv + prot\n  end\n  private\n  def priv : Int32\n    1\n  end\n  protected\n  def prot : Int32\n    2\n  end\n  public\n  def other : Int32\n    3\n  end\nend\nputs A.new.pub + A.new.other", "6" },
        { "private symbol forms", "class A\n  def priv : Int32\n    1\n  end\n  private :priv\n  def self.s : Int32\n    4\n  end\n  private_class_method :s\n  def go : Int32\n    priv + A.s\n  end\nend\nputs A.new.go", "5" },
        { "crystal accessors", "class A\n  getter x : Int32 = 1\n  getter? ok : Bool = true\n  property? flag : Bool = false\n  getter a, b : Int32\n  def initialize\n    @a = 2\n    @b = 3\n  end\nend\na = A.new\na.flag = true\nputs a.x, a.ok?, a.flag?, a.a + a.b", "1\ntrue\ntrue\n5" },
        { "getter bang", "class A\n  getter! maybe : String\n  def set\n    @maybe = \"there\"\n  end\nend\na = A.new\nputs a.maybe?.nil?\na.set\nputs a.maybe", "true\nthere" },
        { "field writer shorthand and property bang", "class A\n  property! v : Int32\n  getter n : String = \"\"\n  def n=(@n : String)\n  end\nend\na = A.new\na.n = \"q\"\na.v = 4\nputs a.n, a.v, a.v?", "q\n4\n4" },
        { "class setter", "class C\n  class_setter level : Int32 = 1\n  def self.show : Int32\n    @@level\n  end\nend\nC.level = 9\nputs C.show", "9" },
        { "docs exception example", "def load(path : String) : String\n  File.ReadAllText(path)\nrescue FileNotFoundException, DirectoryNotFoundException => e\n  \"missing\"\nrescue e : IOException | UnauthorizedAccessException\n  raise\nrescue => e\n  \"\"\nensure\n  puts \"done\"\nend\nputs load(\"/nonexistent/x.txt\")", "done\nmissing" },
        { "class accessors", "class A\n  class_getter count : Int32 = 0\n  class_property name : String = \"A\"\n  def self.bump\n    @@count += 1\n  end\nend\nA.bump\nA.name = \"B\"\nputs A.count, A.name", "1\nB" },
        // control flow
        { "loop value", "i = 0\nr = loop do\n  i += 1\n  break i * 10 if i == 3\nend\nputs r", "30" },
        { "break without value keeps working", "i = 0\nloop do\n  i += 1\n  break if i > 2\nend\nwhile true\n  break i\nend\nputs i", "3" },
        { "begin end while", "k = 0\nbegin\n  k += 1\nend while k < 3\nputs k\nj = 10\nbegin\n  j += 1\nend until true\nputs j", "3\n11" },
        { "modifier loops", "k = 0\nk += 1 while k < 5\nputs k\nk += 1 until k >= 8\nputs k", "5\n8" },
        // exceptions
        { "rescue arrow forms", "begin\n  raise \"a\"\nrescue => e\n  puts e.message\nend\nbegin\n  raise ArgumentException.new(\"b\")\nrescue ArgumentException => e\n  puts e.message\nend", "a\nb" },
        { "rescue several types", "def f(i : Int32)\n  raise ArgumentException.new(\"arg\") if i == 0\n  raise FormatException.new(\"fmt\") if i == 1\n  raise \"other\"\nrescue ArgumentException, FormatException => e\n  puts \"multi #{e.message}\"\nrescue e : InvalidOperationException | NotSupportedException\n  puts \"never\"\nrescue\n  puts \"rest\"\nend\nf(0)\nf(1)\nf(2)", "multi arg\nmulti fmt\nrest" },
        { "rescue else ensure", "begin\n  puts \"ok\"\nrescue\n  puts \"r\"\nelse\n  puts \"else\"\nensure\n  puts \"ens\"\nend\nbegin\n  raise \"x\"\nrescue\n  puts \"r\"\nelse\n  puts \"no\"\nend", "ok\nelse\nens\nr" },
        { "def level rescue and ensure", "def m(x : Int32) : Int32\n  raise \"boom\" if x < 0\n  x\nrescue e : Exception\n  puts \"rescued #{e.message}\"\n  0\nensure\n  puts \"ensure\"\nend\nputs m(5)\nputs m(-1)", "ensure\n5\nrescued boom\nensure\n0" },
        { "do block rescue", "[1].each do |x|\n  raise \"in block\"\nrescue e : Exception\n  puts e.message\nend", "in block" },
        { "bare raise re-raises", "begin\n  begin\n    raise \"inner\"\n  rescue\n    raise\n  end\nrescue e\n  puts \"reraised #{e.message}\"\nend", "reraised inner" },
        { "raise class and message", "begin\n  raise ArgumentException, \"two-arg\"\nrescue e : ArgumentException\n  puts e.message\nend", "two-arg" },
        { "rescue modifier", "x = Int32.Parse(\"zz\") rescue 0\nputs x\nputs (raise \"x\" rescue \"rescued\")\nInt32.Parse(\"q\") rescue nil\nputs \"after\"", "0\nrescued\nafter" },
        // ranges, names
        { "open ranges", "puts (1..).first(3).join(\",\")\ncase 7\nwhen ..3 then puts \"low\"\nwhen 4.. then puts \"high\"\nend\ncase 2\nin ..3 then puts \"in low\"\nelse puts \"no\"\nend", "1,2,3\nhigh\nin low" },
        { "compile-time names", "def who\n  __method__\nend\nputs who\nputs __LINE__\nputs __FILE__.ends_with?(\".al\")\nputs __DIR__.size > 0", "who\n5\ntrue\ntrue" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }

    [Fact]
    public void Defining_case_equality_compiles()
        => Assert.Equal("ok", Run("class Sevens\n  def ===(v : Int32) : Bool\n    v == 7\n  end\nend\ns = Sevens.new\nputs \"ok\""));

    [Fact]
    public void Open_index_ranges_lower_to_negative_ends()
    {
        var f = Parser.ParseFile("xs[1..]\nxs[..2]\nxs[...2]\n", "t.al");
        var ranges = f.Items.OfType<StmtItem>().Select(s => (RangeExpr)((IndexExpr)((ExprStmt)s.Stmt).Expr).Args[0].Value!).ToList();
        Assert.Equal(("1", "-1", false), (((LitExpr)ranges[0].A).Text, ((LitExpr)ranges[0].B).Text, ranges[0].Excl));
        Assert.Equal(("0", "2", false), (((LitExpr)ranges[1].A).Text, ((LitExpr)ranges[1].B).Text, ranges[1].Excl));
        Assert.Equal(("0", "2", true), (((LitExpr)ranges[2].A).Text, ((LitExpr)ranges[2].B).Text, ranges[2].Excl));
    }

    [Theory]
    [InlineData("puts _1", "outside a block")]
    [InlineData("puts [1].map { it + _1 }", "also uses `it`")]
    [InlineData("puts [1, 2].each { |x| break x if x == 2 }", "inside a block")]
    [InlineData("puts(loop do\n  break 1\nend)", "used as a value")]
    [InlineData("i = 0\nwhile true\n  break i + 1\nend", "break` value")]
    [InlineData("raise", "outside a rescue")]
    [InlineData("x = (..5)", "beginless range")]
    [InlineData("begin\n  1\nelse\n  2\nend", "without a rescue")]
    [InlineData("x = begin\n  1\nrescue\n  2\nelse\n  3\nend", "used as a value")]
    [InlineData("class A\n  private :nope\nend", "no method `nope`")]
    [InlineData("c = '\\u{1F600}'", "does not fit in a Char")]
    [InlineData("puts \"\\u{110000}\"", "invalid unicode escape")]
    public void Unsupported_forms_are_clean_parse_errors(string src, string message)
    {
        var e = Assert.Throws<CompileException>(() => Parser.ParseFile(src, "bad.al"));
        Assert.Contains(message, e.Message);
        Assert.All(e.Diagnostics, d => Assert.Equal("bad.al", d.File));
    }
}
