using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>End-to-end: Alice source -> compiled .NET assembly -> stdout.</summary>
[Collection("console")]
public class RunTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        { "puts", "puts \"hello\"", "hello" },
        { "interpolation", "name = \"Alice\"\nputs \"hi #{name}, #{1 + 2}\"", "hi Alice, 3" },
        { "locals and reassignment", "x = 1\nx = x + 41\nputs x", "42" },
        { "compound assign", "x = 10\nx += 5\nx *= 2\nputs x", "30" },
        { "power and float", "puts 2 ** 10\nputs 7 / 2.0", "1024\n3.5" },
        { "if elsif else", "x = 5\nif x > 10\n  puts \"big\"\nelsif x > 3\n  puts \"mid\"\nelse\n  puts \"small\"\nend", "mid" },
        { "unless and postfix", "x = 1\nunless x == 2\n  puts \"a\"\nend\nputs \"b\" if x == 1\nputs \"c\" unless x == 1", "a\nb" },
        { "while", "i = 0\nwhile i < 3\n  puts i\n  i += 1\nend", "0\n1\n2" },
        { "until", "i = 3\nuntil i == 0\n  i -= 1\nend\nputs i", "0" },
        { "exclusive range for", "s = 0\nfor i in 0...5\n  s += i\nend\nputs s", "10" },
        { "inclusive range for", "s = 0\nfor i in 1..5\n  s += i\nend\nputs s", "15" },
        { "for over array", "for w in [\"a\", \"b\"]\n  print w\nend\nputs", "ab" },
        { "next and break", "for i in 0...10\n  next if i % 2 == 0\n  break if i > 5\n  print i\nend\nputs", "135" },
        { "logic words", "a = true\nb = false\nputs(a and not b)\nputs(a && b || !b)", "true\ntrue" },
        { "ternary", "x = 3\nputs(x > 2 ? \"y\" : \"n\")", "y" },
        { "def with types and implicit return", "def add(a : Int32, b : Int32) : Int32\n  a + b\nend\nputs add(2, 3)", "5" },
        { "def void and command call", "def greet(n : String)\n  puts \"hey #{n}\"\nend\ngreet \"bob\"", "hey bob" },
        { "default param", "def inc(x : Int32, by : Int32 = 1) : Int32\n  x + by\nend\nputs inc(1)\nputs inc(1, 5)", "2\n6" },
        { "explicit return", "def sign(x : Int32) : Int32\n  return -1 if x < 0\n  1\nend\nputs sign(-4)", "-1" },
        { "recursion", "def fib(n : Int64) : Int64\n  n < 2 ? n : fib(n - 1) + fib(n - 2)\nend\nputs fib(30)", "832040" },
        { "class with initialize ivars and methods",
          "class Point\n  getter x : Int32\n  getter y : Int32\n  def initialize(@x : Int32, @y : Int32)\n  end\n  def sum : Int32\n    @x + @y\n  end\n  def to_s : String\n    \"(#{x}, #{y})\"\n  end\nend\np = Point.new(3, 4)\nputs p.sum\nputs p",
          "7\n(3, 4)" },
        { "property setter", "class Box\n  property v : Int32 = 1\nend\nb = Box.new\nb.v = 9\nputs b.v", "9" },
        { "inheritance and super", "class A\n  def hi : String\n    \"A\"\n  end\nend\nclass B < A\n  def hi : String\n    super + \"B\"\n  end\nend\nputs B.new.hi", "AB" },
        { "struct", "struct V\n  getter a : Float64\n  def initialize(@a : Float64)\n  end\nend\nputs V.new(1.5).a", "1.5" },
        { "module static", "module Util\n  def self.twice(x : Int32) : Int32\n    x * 2\n  end\nend\nputs Util.twice(21)", "42" },
        { "generic type syntax", "xs = List(Int32).new\nxs.Add(4)\nxs.Add(5)\nputs xs.Count\nd = Dictionary(String, Int32).new\nd[\"k\"] = 7\nputs d[\"k\"]", "2\n7" },
        { "dotnet interop", "using System.Text\nsb = StringBuilder.new\nsb.Append(\"ab\").Append(1)\nputs sb.ToString()\nputs Math.Max(3, 9)\nputs \"abc\".ToUpper()", "ab1\n9\nABC" },
        { "blocks each map select", "xs = [1, 2, 3, 4]\nxs.each { |x| print x }\nputs\nputs xs.map { |x| x * x }.sum\nputs xs.select { |x| x.even? }.size", "1234\n30\n2" },
        { "do block and times", "3.times do |i|\n  print i\nend\nputs", "012" },
        { "lambda", "sq = ->(x : Int32) { x * x }\nputs sq.call(7)\nputs sq.(3)", "49\n9" },
        { "case when", "def k(x : Int32) : String\n  case x\n  when 1, 2\n    \"low\"\n  when 3\n    \"three\"\n  else\n    \"other\"\n  end\nend\nputs k(2)\nputs k(3)\nputs k(9)", "low\nthree\nother" },
        { "begin rescue ensure", "begin\n  raise \"boom\"\nrescue e : Exception\n  puts e.Message\nensure\n  puts \"done\"\nend", "boom\ndone" },
        { "nil check", "s : String? = nil\nputs s.nil?\ns = \"x\"\nputs s.nil?", "true\nfalse" },
        { "array index and size", "a = [10, 20, 30]\na[1] = 5\nputs a[1] + a.size", "8" },
        { "typed new array", "a = Array(Float64).new(4)\na[0] = 2.5\nputs a.size\nputs a[0]", "4\n2.5" },
        { "predicate and bang names", "def ok?(x : Int32) : Bool\n  x > 0\nend\nputs ok?(1)", "true" },
        { "namespace and entrypoint", "namespace Demo\n  class Calc\n    def self.go : Int32\n      7\n    end\n  end\nend\nputs Demo::Calc.go", "7" },
        { "runtime simd and sparse", "using Alice.Runtime\nxs = [1.0, 2.0, 3.0]\nputs Simd.Dot(xs, xs)\nputs xs.sum", "14\n6" },
        { "full dotnet stdlib", "using System.Text.RegularExpressions\nusing System.Text.Json\nputs Regex.IsMatch(\"abc123\", \"[0-9]+\")\nputs JsonSerializer.Serialize([1, 2])\nputs [3, 1, 2].OrderBy(->(x : Int32) { x }).First()\nputs Path.Combine(\"a\", \"b\")\nputs DateTime.UnixEpoch.Year", "true\n[1,2]\n1\na/b\n1970" },
        { "comments", "# a comment\nx = 1 # trailing\nputs x", "1" },
        { "interface and attribute", "interface IShape\n  def area : Float64\nend\n@[Obsolete(\"old\")]\nclass Sq < IShape\n  def initialize(@s : Float64)\n  end\n  def area : Float64\n    @s * @s\n  end\nend\nputs Sq.new(3.0).area", "9" },
        { "generic def", "def first<T>(xs : Array(T)) : T\n  xs[0]\nend\nputs first([\"q\", \"r\"])", "q" },
        { "string methods", "puts \"abc\".size\nputs 42.to_s + \"!\"\nputs \"12\".to_i + 1", "3\n42!\n13" },
        { "parallel map", "r = Par.Map([1, 2, 3], ->(x : Int32) { x * 2 })\nputs r.sum\nputs [1, 2, 3].par_map { |x| x + 1 }.sum", "12\n9" },
        { "spawn and channel", "ch = Channel(Int32).new\nspawn do\n  for i in 1..10\n    ch.send(i)\n  end\n  ch.close\nend\ntotal = 0\nch.each { |v| total += v }\nputs total", "55" },
        { "actor", "sum = 0\na = Actor(Int32).new(->(m : Int32) { sum += m })\nfor i in 1..100\n  a.send(i)\nend\na.stop\nputs sum", "5050" },
        { "spawn returns value", "t = spawn { 6 * 7 }\nputs t.wait", "42" },
        { "enum", "enum Color\n  Red\n  Green = 5\nend\nputs Color::Green", "Green" },
        { "native lib", "@[Link(\"libm.so.6\")]\nlib LibM\n  fun cos(x : Float64) : Float64\nend\nputs LibM.cos(0.0)", "1" },
        { "hash literal", "h = {\"a\" => 1, \"b\" => 2}\nputs h[\"b\"]\nfor k, v in h\n  print k, v\nend\nputs", "2\na1b2" },
        { "untyped generic param", "def ident(x)\n  puts x\nend\nident 5\nident \"s\"", "5\ns" },
        { "operator overload", "struct V2\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\n  def +(o : V2) : V2\n    V2.new(@x + o.x)\n  end\nend\nputs (V2.new(1) + V2.new(2)).x", "3" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }

    [Fact]
    public void Null_dereference_is_a_compile_error()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Run("s : String? = nil\nputs s.Length\n"));
        Assert.Contains("CS8602", ex.Message);
    }

    [Fact]
    public void Syntax_error_reports_line()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("x = 1\nif x\n  puts x\n"));
        Assert.Contains("3:", ex.Message);
        Assert.Contains("end", ex.Message);
    }

    [Fact]
    public void Dotnet_errors_map_to_alice_lines()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Run("x = 1\ny = x.NoSuchMember\n"));
        Assert.Contains("main.al(2", ex.Message);
    }
}
