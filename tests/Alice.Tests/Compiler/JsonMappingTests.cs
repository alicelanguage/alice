using Alice.Compiler;
using Alice.Compiler.Backend.Il;

namespace Alice.Tests.Compiler;

/// <summary>C2.5 (ADR 0036): compiler-generated JSON mapping of Alice records, classes and enums, no reflection.</summary>
[Collection("console")]
public class JsonMappingTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    const string Types = """
        record Point(x : Int32, y : Int32)
        enum Color
          Red
          Green
        end
        enum Shape
          Circle(r : Float64)
          Rect(w : Float64, h : Float64)
          Empty
        end
        class User
          property name : String
          property age : Int64
          property tags : Array(String)
          property home : Point?
          property color : Color
          property scores : Hash(String, Float64)
          def initialize(@name : String, @age : Int64, @tags : Array(String), @home : Point?, @color : Color, @scores : Hash(String, Float64))
          end
        end

        """;

    [Fact]
    public void Record_writes_and_reads_back()
    {
        var src = Types + """
            p = Point.new(1, -2)
            puts to_json(p)
            puts p.to_json
            puts JSON.generate(p)
            q = JSON.parse(Point, "{\"y\": 5, \"zz\": [1, {\"a\": null}], \"x\": 4}")
            puts q.x + q.y
            puts Point.from_json(to_json(p)) == p
            """;
        Assert.Equal("{\"x\":1,\"y\":-2}\n{\"x\":1,\"y\":-2}\n{\"x\":1,\"y\":-2}\n9\ntrue", Run(src));
    }

    [Fact]
    public void Class_with_nested_values_round_trips()
    {
        var src = Types + """
            u = User.new("ada", 36, ["x", "y"], Point.new(3, 4), Color::Green, {"m" => 1.5})
            s = to_json(u)
            puts s
            v = User.from_json(s)
            puts v.name, v.age, v.tags.size, v.home.not_nil!.y, v.color, v.scores["m"]
            w = User.from_json("{\"name\":\"bo\",\"age\":1,\"tags\":[],\"home\":null,\"color\":\"Red\",\"scores\":{}}")
            puts w.home.nil?
            puts to_json(w) == "{\"name\":\"bo\",\"age\":1,\"tags\":[],\"home\":null,\"color\":\"Red\",\"scores\":{}}"
            """;
        Assert.Equal(
            "{\"name\":\"ada\",\"age\":36,\"tags\":[\"x\",\"y\"],\"home\":{\"x\":3,\"y\":4},\"color\":\"Green\",\"scores\":{\"m\":1.5}}\n" +
            "ada\n36\n2\n4\nGreen\n1.5\ntrue\ntrue", Run(src));
    }

    [Fact]
    public void Sum_types_carry_a_type_tag()
    {
        var src = Types + """
            shapes = [Shape::Circle(1.0), Shape::Rect(2.0, 3.0), Shape::Empty]
            s = to_json(shapes)
            puts s
            back = JSON.parse<Array(Shape)>(s)
            puts back.size
            puts back[1] == Shape::Rect(2.0, 3.0)
            puts JSON.parse(Shape, "{\"r\": 2.5, \"type\": \"Circle\"}") == Shape::Circle(2.5)
            """;
        Assert.Equal("[{\"type\":\"Circle\",\"r\":1.0},{\"type\":\"Rect\",\"w\":2.0,\"h\":3.0},{\"type\":\"Empty\"}]\n3\ntrue\ntrue", Run(src));
    }

    [Fact]
    public void Pretty_generate_indents()
    {
        var src = Types + "puts JSON.pretty_generate(Point.new(1, 2))";
        Assert.Equal("{\n  \"x\": 1,\n  \"y\": 2\n}", Run(src));
    }

    [Fact]
    public void Missing_required_fields_and_wrong_variants_raise()
    {
        var src = Types + """
            begin
              Point.from_json("{\"x\": 1}")
            rescue => e
              puts e.message
            end
            begin
              JSON.parse(Shape, "{\"type\": \"Hexagon\"}")
            rescue => e
              puts e.class
            end
            begin
              JSON.parse(Point, "{\"x\": 1, \"y\": \"no\"}")
            rescue => e
              puts e.class
            end
            """;
        var lines = Run(src).Split('\n');
        Assert.StartsWith("missing field \"y\" of Point at byte 0", lines[0]);
        Assert.Equal("Alice.Core.Json.JsonParseException", lines[1]);
        Assert.Equal("Alice.Core.Json.JsonParseException", lines[2]);
    }

    [Fact]
    public void Plain_values_keep_the_prelude_shape()
    {
        // no Alice type involved: the Ruby-shape Prelude writer, unchanged
        Assert.Equal("[1,2.0,\"é\"]\n{\"k\":[true]}", Run("puts to_json([1, 2.0, \"é\"])\nputs to_json({\"k\" => [true]})"));
    }

    [Fact]
    public void Parse_json_returns_json_any()
    {
        Assert.Equal("5\n2", Run("n = parse_json(\"{\\\"a\\\": 5, \\\"b\\\": [1, 2]}\")\nputs n[\"a\"].as_i\nputs n[\"b\"].size"));
    }

    [Fact]
    public void Unmappable_types_are_compile_errors()
    {
        var e1 = Assert.ThrowsAny<Exception>(() => AliceCompiler.Run("x = JSON.parse(Int32, \"1\")"));
        Assert.Contains("needs an Alice record, class or enum", e1.Message);
        Assert.Equal("AL0250", Assert.IsType<CompileException>(e1).Diagnostics[0].Code);
        var e2 = Assert.ThrowsAny<Exception>(() => AliceCompiler.Run("record Bag(items : Set(Int32))\nputs to_json(Bag.new(Set(Int32).new))"));
        Assert.Contains("cannot map Bag to JSON", e2.Message);
    }

    [Fact]
    public void The_il_backend_runs_the_typed_mapping()
    {
        // the readers/writers are typed AIR helpers, so `--backend il` emits them like any other method
        var src = Types + """
            u = User.new("ada", 36, ["x", "y"], Point.new(3, 4), Color::Green, {"m" => 1.5})
            s = to_json(u)
            puts s
            v = User.from_json(s)
            puts v.name, v.age, v.tags.size, v.home.not_nil!.y, v.color, v.scores["m"]
            puts User.from_json("{\"name\":\"bo\",\"age\":1,\"tags\":[],\"home\":null,\"color\":\"Red\",\"scores\":{}}").home.nil?
            shapes = [Shape::Circle(1.0), Shape::Rect(2.0, 3.0), Shape::Empty]
            t = to_json(shapes)
            puts t
            puts JSON.parse<Array(Shape)>(t)[1] == Shape::Rect(2.0, 3.0)
            puts JSON.pretty_generate(Point.new(1, 2))
            begin
              Point.from_json("{\"x\": 1}")
            rescue => e
              puts e.message
            end
            """;
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "jsonil", exe: true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        var errors = Alice.Tests.Backends.AirToIlTests.IlVerify(res.Pe!);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
        string il = AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []).Trim().ReplaceLineEndings("\n");
        Assert.Equal(Run(src), il);
        Assert.StartsWith("{\"name\":\"ada\",\"age\":36,", il);
    }

    [Fact]
    public void A_variant_field_named_type_is_AL0250()
    {
        var e = Assert.ThrowsAny<Exception>(() => AliceCompiler.Run("enum Ev\n  Click(type : String)\nend\nputs to_json(Ev::Click(\"x\"))"));
        Assert.Equal("AL0250", Assert.IsType<CompileException>(e).Diagnostics[0].Code);
        Assert.Contains("variant Ev::Click has a field named `type`, which is the sum-type tag", e.Message);
    }

    [Fact]
    public void Generated_code_uses_no_reflection()
    {
        var cs = AliceCompiler.Transpile(Types + "puts to_json(User.from_json(\"{}\"))", "main.al");
        Assert.Contains("__json_R", cs);
        Assert.Contains("JsonMap.ToJson<User>", cs);
        Assert.DoesNotContain("JsonSerializer", cs);
        Assert.DoesNotContain("GetType()", cs);
    }
}
