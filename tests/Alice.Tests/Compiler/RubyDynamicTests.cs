using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>More Ruby: case/in guards, alternatives and hash patterns; define_method; Struct.new; method_missing.</summary>
[Collection("console")]
public class RubyDynamicTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        { "guards and alternatives", "def kind(n : Int32) : String\n  case n\n  in 0 then \"zero\"\n  in 1 | 2 | 3 then \"small\"\n  in x if x < 0 then \"neg #{x}\"\n  in x then \"big #{x}\"\n  end\nend\nputs kind(0)\nputs kind(2)\nputs kind(-5)\nputs kind(40)", "zero\nsmall\nneg -5\nbig 40" },
        { "guard with destructuring", "enum Shape\n  Circle(r : Float64)\n  Sq(s : Float64)\nend\ndef d(x : Shape) : String\n  case x\n  in Shape::Circle(r) if r > 10.0 then \"huge circle\"\n  in Shape::Circle(r) then \"circle #{r}\"\n  in Shape::Sq(s) then \"square #{s}\"\n  end\nend\nputs d(Shape::Circle(20.0))\nputs d(Shape::Circle(2.0))\nputs d(Shape::Sq(3.0))", "huge circle\ncircle 2\nsquare 3" },
        { "hash patterns", "def greet(h : Dictionary(String, String)) : String\n  case h\n  in {\"lang\" => \"pt\", \"name\" => n} then \"Olá #{n}\"\n  in {\"name\" => n} then \"Hello #{n}\"\n  in _ then \"?\"\n  end\nend\nputs greet({\"name\" => \"Ana\", \"lang\" => \"pt\"})\nputs greet({\"name\" => \"Bo\"})\nputs greet({\"x\" => \"y\"})", "Olá Ana\nHello Bo\n?" },
        { "define_method", "class Greeter\n  define_method :hello do |name|\n    \"hello #{name}\"\n  end\n  define_method(:twice) { |x| x * 2 }\nend\ng = Greeter.new\nputs g.hello(\"zed\")\nputs g.twice(21)", "hello zed\n42" },
        { "Struct.new", "Point = Struct.new(:x, :y)\np = Point.new(1, 2)\nputs p.x + p.y\nputs p == Point.new(1, 2)", "3\ntrue" },
        { "method_missing", "class Ghost\n  def method_missing(name : String, args : Array(Object)) : String\n    \"#{name}(#{args.size})\"\n  end\n  def real : String\n    \"real\"\n  end\nend\ng = Ghost.new\nputs g.real\nputs g.anything\nputs g.call_me(1, 2)", "real\nanything(0)\ncall_me(2)" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }
}

[Collection("console")]
public class PinPatternTests
{
    [Fact]
    public void Caret_pins_an_existing_value()
        => Assert.Equal("same\nother 4", AliceCompiler.Run("def f(v : Int32, k : Int32) : String\n  case v\n  in ^k then \"same\"\n  in x then \"other #{x}\"\n  end\nend\nputs f(3, 3)\nputs f(4, 3)").Trim().ReplaceLineEndings("\n"));
}
