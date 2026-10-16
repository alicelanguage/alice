using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Type-system features: generics + constraints, sum types, records, monkey patching, duck typing.</summary>
[Collection("console")]
public class LanguageTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    const string Tree = """
        class Node(T) where T : IComparable(T)
          getter value : T
          property left : Node(T)? = nil
          property right : Node(T)? = nil

          def initialize(@value : T)
          end

          def insert(v : T)
            if v.CompareTo(value) < 0
              l = left
              if l.nil?
                @left = Node(T).new(v)
              else
                l.insert(v)
              end
            else
              r = right
              if r.nil?
                @right = Node(T).new(v)
              else
                r.insert(v)
              end
            end
          end

          def walk(out : List(T))
            left&.walk(out)
            out.Add(value)
            right&.walk(out)
          end
        end

        root = Node(Int32).new(5)
        for v in [3, 8, 1, 4, 9]
          root.insert(v)
        end
        acc = [] of Int32
        root.walk(acc)
        puts acc.join(",")
        """;

    const string Shapes = """
        enum Shape
          Circle(r : Float64)
          Rect(w : Float64, h : Float64)
          Empty
        end

        def area(s : Shape) : Float64
          case s
          when Shape::Circle(r) then 3.0 * r * r
          when Shape::Rect(w, h) then w * h
          when Shape::Empty then 0.0
          end
        end

        shapes = [Shape::Circle(1.0), Shape::Rect(2.0, 3.0), Shape::Empty]
        puts shapes.sum { |s| area(s) }
        puts Shape::Rect(1.0, 2.0) == Shape::Rect(1.0, 2.0)
        """;

    public static TheoryData<string, string, string> Programs => new()
    {
        { "generic binary tree", Tree, "1,3,4,5,8,9" },
        { "generic class", "class Box(T)\n  getter item : T\n  def initialize(@item : T)\n  end\nend\nputs Box(String).new(\"hi\").item.upcase", "HI" },
        { "generic method constraint", "def biggest(a : T, b : T) : T forall T where T : IComparable(T)\n  a.CompareTo(b) >= 0 ? a : b\nend\nputs biggest(3, 7)\nputs biggest(\"a\", \"b\")", "7\nb" },
        { "sum type match", Shapes, "9\ntrue" },
        { "record", "record Point(x : Int32, y : Int32)\np = Point.new(1, 2)\nputs p.x + p.y\nputs p == Point.new(1, 2)\nputs p", "3\ntrue\nPoint { x = 1, y = 2 }" },
        { "record struct with method", "record struct Vec(x : Float64, y : Float64)\n  def len : Float64\n    Math.Sqrt(x * x + y * y)\n  end\nend\nputs Vec.new(3.0, 4.0).len", "5" },
        { "monkey patch String", "class String\n  def shout : String\n    upcase + \"!\"\n  end\nend\nputs \"hey\".shout", "HEY!" },
        { "monkey patch Int32", "struct Int32\n  def double : Int32\n    self * 2\n  end\nend\nputs 21.double", "42" },
        { "reopen alice class", "class Dog\n  def name : String\n    \"rex\"\n  end\nend\nclass Dog\n  def bark : String\n    name + \" woof\"\n  end\nend\nputs Dog.new.bark", "rex woof" },
        { "override protected .NET virtual", "class MyStream < System.IO.MemoryStream\n  def Dispose(disposing : Bool)\n    puts \"disposed\"\n    super\n  end\nend\ns = MyStream.new\ns.Dispose", "disposed" },
        { "implicit members of .NET base", "class MyList < List(Int32)\n  def total : Int32\n    Add(5)\n    Add(6)\n    Count + Capacity * 0\n  end\nend\nputs MyList.new.total", "2" },
        { "array of mixed .NET subclasses unifies to base", "xs = [System.IO.MemoryStream.new, System.IO.BufferedStream.new(System.IO.MemoryStream.new)]\nputs xs.size\nputs xs.GetType().Name", "2\nStream[]" },
        { "member named like its type (Color Color)", "class Holder < System.IO.StringWriter\n  def go : Int32\n    Encoding.WebName.Length + NewLine.Length\n  end\nend\nputs Holder.new.go", "7" },
        { "float32 conversions", "x = 2.7f32\nputs x.to_i\nputs x.to_f > 2.6", "2\ntrue" },
        { "generic .NET method return inference", "def k(xs : Array(Int32))\n  Par.Map(xs, ->(x : Int32) { x * 2 }).sum\nend\nputs k([1, 2])\nputs Array.IndexOf([5, 6], 6).even?", "6\nfalse" },
        { "declared return keeps an untyped tail", "def k(xs : Array(Int32)) : Int32\n  ys = Par.Map(xs, ->(x : Int32) { x * 2 })\n  ys.sum\nend\nputs k([1, 2])", "6" },
        { "duck typing is monomorphized", "def len(x)\n  x.Length\nend\nputs len(\"abcd\")\nputs len([1, 2])", "4\n2" },
        { "duck typed arithmetic", "def twice(x)\n  x * 2\nend\nputs twice(2)\nputs twice(1.5)", "4\n3" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }

    [Fact]
    public void Non_exhaustive_match_is_a_compile_error()
    {
        var src = "enum E\n  A(x : Int32)\n  B\nend\ndef f(e : E) : Int32\n  case e\n  when E::A(x) then x\n  end\nend\n";
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains("non-exhaustive", ex.Message);
        Assert.Contains("B", ex.Message);
    }

    [Fact]
    public void Duck_typing_emits_typed_specializations_not_dynamic()
    {
        var cs = AliceCompiler.Transpile("def len(x)\n  x.Length\nend\nputs len(\"abcd\")\n");
        Assert.Contains("int len(string x)", cs);
        Assert.DoesNotContain("dynamic", cs);
    }

    [Fact]
    public void Leaf_classes_are_sealed_and_locals_skip_init()
    {
        var cs = AliceCompiler.Transpile("class A\nend\nclass B < A\nend\n");
        Assert.Contains("public sealed partial class B", cs);
        Assert.Contains("public partial class A", cs);
        Assert.Contains("[module: global::System.Runtime.CompilerServices.SkipLocalsInit]", cs);
    }
}
