using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>The four language properties: typed like Crystal, dynamic like Ruby, safe like Rust, fast like C.</summary>
[Collection("console")]
public class PropertyTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        // typed like Crystal: return + local inference (extension methods only bind on static types)
        { "inferred int return", "def add(a : Int32, b : Int32)\n  a + b\nend\nputs add(2, 3).even?", "false" },
        { "inferred string return", "def greet(n : String)\n  \"hi #{n}\"\nend\nputs greet(\"x\").upcase", "HI X" },
        { "inference across later defs", "def a\n  b + 1\nend\ndef b\n  41\nend\nputs a.even?", "true" },
        { "local inference", "def avg(xs : Array(Float64))\n  s = 0.0\n  for x in xs\n    s += x\n  end\n  s / xs.size\nend\nputs avg([1.0, 4.0]).floor", "2.0" },
        { "inferred method in class", "class C\n  getter v : Int32 = 4\n  def twice\n    v * 2\n  end\nend\nputs C.new.twice.even?", "true" },
        // dynamic like Ruby
        { "Any is dynamic", "def len(x : Any)\n  x.Length\nend\nputs len(\"abcd\")\nputs len([1, 2])", "4\n2" },
        { "symbols", "puts :foo\nh = {:a => 1}\nputs h[:a]", "foo\n1" },
        { "send and respond_to", "puts \"abc\".respond_to?(:ToUpper)\nputs \"abc\".respond_to?(:nope)\nputs \"abc\".send(:ToUpper)\nputs \"a-b\".send(:Replace, \"-\", \"+\")", "true\nfalse\nABC\na+b" },
        { "yield to block", "def twice(&block : Int32 ->)\n  yield 1\n  yield 2\nend\ntwice { |x| print x }\nputs", "12" },
        { "yield with value", "def apply(x : Int32, &f : Int32 -> Int32) : Int32\n  yield x\nend\nputs apply(3) { |v| v * 10 }", "30" },
        { "if expression", "x = 5\ny = if x > 3\n  \"big\"\nelse\n  \"small\"\nend\nputs y", "big" },
        { "case expression", "x = 5\nz = case x\nwhen 5 then 1\nelse 0\nend\nputs z + 1", "2" },
        // safe like Rust (allowed forms)
        { "val is readable", "val k = 3\nputs k * 2", "6" },
        { "spawn may use channels", "ch = Channel(Int32).new(1)\nt = spawn { ch.send(5) }\nt.wait\nputs ch.receive", "5" },
        // fast like C
        { "inline attribute", "@[Inline]\ndef sq(x : Int32) : Int32\n  x * x\nend\nputs sq(9)", "81" },
        { "stack struct", "@[Stack]\nstruct P\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\nend\np = P.new(7)\nputs p.x", "7" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }

    [Theory]
    [InlineData("def add(a : Int32, b : Int32)\n  a + b\nend\n", "public static int add(")]
    [InlineData("def f\n  1.5\nend\n", "public static double f(")]
    [InlineData("def s(n : Int32)\n  \"#{n}\"\nend\n", "public static string s(")]
    [InlineData("@[Inline]\ndef sq(x : Int32) : Int32\n  x * x\nend\n", "MethodImplOptions.AggressiveInlining")]
    [InlineData("@[Stack]\nstruct P\nend\n", "public ref partial struct P")]
    public void Lowering_is_statically_typed(string src, string expected)
        => Assert.Contains(expected, AliceCompiler.Transpile(src));

    [Theory]
    [InlineData("val x = 1\nx = 2\n", "immutable")]
    [InlineData("total = 0\nt = spawn { total += 1 }\nt.wait\n", "data race")]
    [InlineData("total = 0\nspawn do\n  total = 5\nend\n", "data race")]
    public void Safety_violations_are_compile_errors(string src, string expected)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains(expected, ex.Message);
    }
}
