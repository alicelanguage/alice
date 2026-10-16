using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Ruby's core mixins on user classes (Comparable, Enumerable) and Ruby 3 `case ... in` patterns.</summary>
[Collection("console")]
public class RubyMixinTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    const string Version = """
        class Version
          include Comparable
          getter major : Int32
          getter minor : Int32
          def initialize(@major : Int32, @minor : Int32)
          end
          def <=>(o : Version) : Int32
            major == o.major ? minor <=> o.minor : major <=> o.major
          end
          def to_s : String
            "#{major}.#{minor}"
          end
        end
        a = Version.new(1, 2)
        b = Version.new(1, 10)
        puts a < b
        puts b >= a
        puts a == Version.new(1, 2)
        puts Version.new(1, 5).between?(a, b)
        puts [b, a, Version.new(0, 9)].sort.join(" ")
        puts [a, b].max
        puts Version.new(3, 0).clamp(a, b)
        """;

    const string Bag = """
        class Bag
          include Enumerable
          def initialize
            @items = [] of Int32
          end
          def add(x : Int32)
            @items.Add(x)
          end
          def each(&block : Int32 ->)
            for x in @items
              yield x
            end
          end
        end
        b = Bag.new
        b.add(3)
        b.add(1)
        b.add(2)
        puts b.map { |x| x * 10 }.join(",")
        puts b.select { |x| x > 1 }.size
        puts b.sum
        puts b.sort.join(",")
        puts b.include?(2)
        puts b.min
        total = 0
        for x in b
          total += x
        end
        puts total
        """;

    public static TheoryData<string, string, string> Programs => new()
    {
        { "Comparable", Version, "true\ntrue\ntrue\ntrue\n0.9 1.2 1.10\n1.10\n1.10" },
        { "Enumerable", Bag, "30,10,20\n2\n6\n1,2,3\ntrue\n1\n6" },
        { "case in with variants", "enum Shape\n  Circle(r : Float64)\n  Sq(s : Float64)\nend\ndef area(x : Shape) : Float64\n  case x\n  in Shape::Circle(r) then 3.0 * r * r\n  in Shape::Sq(s) then s * s\n  end\nend\nputs area(Shape::Sq(4.0))", "16" },
        { "array patterns", "def describe(xs : Array(Int32)) : String\n  case xs\n  in [] then \"empty\"\n  in [a] then \"one #{a}\"\n  in [a, b] then \"two #{a + b}\"\n  in [first, *rest] then \"many #{first}+#{rest.size}\"\n  end\nend\nputs describe(Array(Int32).new(0))\nputs describe([7])\nputs describe([1, 2])\nputs describe([5, 6, 7, 8])", "empty\none 7\ntwo 3\nmany 5+3" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }
}
