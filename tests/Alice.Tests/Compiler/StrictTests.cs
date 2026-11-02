using Alice.Compiler;
using Spectre.Console;

namespace Alice.Tests.Compiler;

/// <summary>
/// Strict typing (AL0220): `[build] strict = true`, `--strict` or `@[Strict]` make every expression, local, parameter
/// and return that the binder could not type a compile error; `Any` written by the user stays allowed. The IL and
/// wasm backends need every expression typed (backend plan 4.1, F1).
/// </summary>
[Collection("console")]
public class StrictTests
{
    static List<string> Strict(string src) => AliceCompiler.TranspileStrict([new SourceFile("main.al", src)]);

    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    // `*` on a .NET struct: the binder does not resolve `op_Multiply`, C# does (still a binder gap)
    const string Untyped = "v = System.Numerics.Vector2.new(1.0_f32, 2.0_f32)\nw = v * 2.0_f32\nputs w.X\n";

    [Fact]
    public void Off_by_default_an_untyped_expression_still_compiles()
    {
        Assert.Equal("2.0", Run(Untyped));
    }

    [Fact]
    public void Strict_reports_only_the_root_cause_with_its_span_and_what_could_not_be_typed()
    {
        var ex = Assert.Throws<CompileException>(() => Strict(Untyped));
        var d = Assert.Single(ex.Diagnostics);   // not also `w` (typed from it) nor `w.X`
        Assert.Equal("AL0220", d.Code);
        Assert.Equal(("main.al", 2, 5), (d.File, d.Line, d.Col));
        Assert.Contains("`*` on `Vector2` and `Float32` has no static type", d.Message);
        Assert.Contains("declare it `Any`", d.Message);
    }

    [Theory]
    [InlineData("t = System.Timers.Timer.new(10.0)\nt.Elapsed += ->(s : Object?, e : System.Timers.ElapsedEventArgs) { puts 1 }\nputs t.Interval\n", "call `Elapsed` on `Timer` has no static type", 2)]
    [InlineData("def triple(x : Int32) : Int32\n  x * 3\nend\nputs [1, 2].map(->triple).join(\",\")\n", "call `map` on `Array(Int32)` has no static type", 4)]
    public void Each_kind_of_gap_names_what_could_not_be_typed(string src, string what, int line)
    {
        var ex = Assert.Throws<CompileException>(() => Strict(src));
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal(("AL0220", line), (d.Code, d.Line));
        Assert.Contains(what, d.Message);
    }

    [Fact]
    public void Any_written_by_the_user_is_accepted_and_so_is_what_is_computed_from_it()
    {
        const string src = "def len(x : Any)\n  x.Length\nend\nputs len(\"abcd\")\ny = len(\"ab\") + 1\nputs y\n";
        Strict(src);
        Assert.Equal("4\n3", Run(src));
    }

    [Fact]
    public void A_typed_program_emits_the_same_CSharp_with_and_without_strict()
    {
        const string src = "record P(x : Int32, y : Int32)\ndef norm(p : P) : Int32\n  p.x * p.x + p.y * p.y\nend\nps = [P.new(1, 2), P.new(3, 4)]\nputs ps.sum { |p| norm(p) }\nputs ps.Length\n";
        Assert.Equal(AliceCompiler.Transpile(src), Assert.Single(Strict(src)));
    }

    [Fact]
    public void Strict_attribute_checks_only_the_class_it_is_on()
    {
        const string loose = "class Loose\n  def scale(v : System.Numerics.Vector2)\n    v * 3.0_f32\n  end\nend\n";
        const string strict = "@[Strict]\nclass Calc\n  def scale(v : System.Numerics.Vector2) : Float32\n    (v * 2.0_f32).X\n  end\nend\n";
        AliceCompiler.Transpile(loose);
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(loose + strict));
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal(("AL0220", 9), (d.Code, d.Line));
    }

    // ---- binder gaps closed for strict mode (the C# of programs that were already typed does not change)

    [Theory]
    [InlineData("enum Shape\n  Circle(r : Float64)\n  Rect(w : Float64, h : Float64)\nend\ndef area(s : Shape) : Float64\n  case s\n  when Shape::Circle(r) then 3.0 * r * r\n  when Shape::Rect(w, _) then w\n  end\nend\nputs area(Shape::Circle.new(2.0))\n", "12.0")]
    [InlineData("record B(x : Int32)\nclass Wall\n  def initialize(@bricks : Array(B))\n  end\n  def count : Int32\n    @bricks.Length\n  end\nend\nputs Wall.new([B.new(1), B.new(2)]).count\n", "2")]
    [InlineData("head = [1_u8, 2_u8]\nall = head.Concat([3_u8]).ToArray\nputs all.Length\n", "3")]
    [InlineData("d = Dictionary(String, Int32).new\nd[\"a\"] = 4\nputs d.GetValueOrDefault(\"a\") + d.GetValueOrDefault(\"b\") + 1\n", "5")]
    [InlineData("i = ARGV.IndexOf(\"x\")\nputs i + ARGV.Length\n", "-1")]
    [InlineData("MASK = [1, 2, 3]\nputs MASK.Length\n", "3")]
    [InlineData("xs = Array(Float32).new(4) { |k| k.to_f32 }\nc = Atomic.new(0)\nPar.For(10) { |i| c.add(i) }\nr = Par.Map([1, 5].ToArray) do |v|\n  v * 2\nend\nputs xs.Length + c.get + r.sum\n", "61")]
    public void Former_binder_gaps_are_typed(string src, string expected)
    {
        Strict(src);
        Assert.Equal(expected, Run(src));
    }

    [Fact]
    public void Prelude_test_dsl_calls_are_typed()
    {
        Strict("describe \"m\" do\n  it \"adds\" do\n    expect(1 + 1).to eq(2)\n    expect([1, 2]).to have_size(2)\n  end\n  xit \"later\" do\n  end\nend\n");
    }

    // ---- the corpus

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void The_benchmarks_alice_corpus_compiles_under_strict()
    {
        var failures = new List<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "benchmarks", "alice"), "*.al").Order())
        {
            try { AliceCompiler.TranspileStrict(AliceCompiler.LoadWithRequires([f])); }
            catch (CompileException e) { failures.Add($"{Path.GetFileName(f)}: {e.Diagnostics[0]}"); }
        }
        Assert.Empty(failures);
    }

    // ---- flags

    [Fact]
    public void Build_strict_in_alice_toml_turns_it_on_for_alice_gen()
    {
        static string Sync(string toml)
        {
            var dir = Path.Combine(Path.GetTempPath(), "alice-strict-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "alice.toml"), toml);
            return File.ReadAllText(Alice.Cli.Projects.Manifest.Sync(dir));
        }
        Assert.DoesNotContain("AliceStrict", Sync("[package]\nname = \"app\"\n"));
        Assert.Contains("<AliceStrict>true</AliceStrict>", Sync("[package]\nname = \"app\"\n\n[build]\nstrict = true\n"));
        var targets = File.ReadAllText(Path.Combine(Root(), "src", "Alice.Sdk", "Alice.targets"));
        Assert.Contains("'$(AliceStrict)' == 'true'\">$(_AliceGenCheckArg) --strict", targets);
    }

    [Fact]
    public void Strict_flag_on_check_run_build_and_emit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-strict-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.al");
        File.WriteAllText(file, Untyped);
        static (int Code, string Out) Cli(params string[] args)
        {
            var w = new StringWriter();
            var saved = AnsiConsole.Console;
            var savedErr = Console.Error;
            var savedOut = Console.Out;
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w), ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
            Console.SetError(w);
            Console.SetOut(w);
            try { return (Alice.Cli.App.Run(["--lang", "en", .. args]), w.ToString()); }
            finally { AnsiConsole.Console = saved; Console.SetError(savedErr); Console.SetOut(savedOut); }
        }
        Assert.Equal(0, Cli("check", file).Code);
        foreach (var cmd in new[] { "check", "run", "emit", "build" })
        {
            var (code, output) = Cli(cmd, "--strict", file);
            Assert.True(code == 1, $"{cmd}: {code}\n{output}");
            Assert.Contains("AL0220", output);
        }
    }
}
