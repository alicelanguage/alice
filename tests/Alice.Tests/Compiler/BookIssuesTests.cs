using Alice.Cli.Commands;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Compiler problems the book had to work around (/work/reports/book-compiler-issues.md, items 1-13) and the follow-ups
/// 14-16 (numeric limits in pure code, `rescue A, B` re-parse, `sort_by_key` result names). Each repro is the report's.
/// </summary>
[Collection("console")]
public class BookIssuesTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    public static TheoryData<string, string, string> Programs => new()
    {
        // 2. @[Strict] on a method whose body types: the attribute is compiler-only, not emitted to C#
        { "strict attribute on a method", "@[Strict]\ndef half(xs)\n  xs.size / 2\nend\nputs half([1, 2, 3, 4])\n", "2" },
        // 3. compound assignment to a class_property through the type
        { "class_property +=", "class Counter\n  class_property total : Int32 = 0\n  def self.make : Counter\n    Counter.total += 1\n    Counter.new\n  end\nend\nCounter.make\nCounter.make\nputs Counter.total\n", "2" },
        // 4. `await` starts a command argument
        { "puts await", "async def twice(x : Int32) : Int32\n  await Task.Delay(1)\n  x * 2\nend\nasync def main_async\n  puts await twice(4)\nend\nmain_async.wait\n", "8" },
        // 5. a generic .NET result typed by an Alice type argument is nil-able (`TValue?`): `if x` and `x || d` work
        { "nullable generic .NET result", "using System.Text.Json\nrecord Tea(name : String, cents : Int32)\nloaded = JsonSerializer.Deserialize<Array(Tea)>(\"[{\\\"name\\\":\\\"A\\\",\\\"cents\\\":1}]\")\nif loaded\n  loaded.each { |t| puts t.name }\nend\nx = loaded || Array(Tea).new(0, Tea.new(\"\", 0))\nputs x.size\n", "A\n1" },
        // 6. `raise ... unless n` narrows n for the rest of the block, past further statements
        { "raise guard narrows", "def read_age(text : String) : Int32\n  n = text.to_i?\n  raise ArgumentException.new(\"not a number\") unless n\n  raise ArgumentException.new(\"negative\") if n < 0\n  n\nend\nputs read_age(\"7\")\n", "7" },
        // 8. negative indexes and slices on arrays, lists and strings
        { "negative index", "xs = [5, 3, 8]\nputs xs[-1]\nputs xs[-3]\n", "8\n5" },
        { "array slices", "xs = [5, 3, 8]\nputs xs[1..].to_s\nputs xs[..1].to_s\nputs xs[0...2].to_s\nputs xs[-2..].to_s\nputs xs[1, 5].to_s\nputs xs[7..].to_s\n", "[3, 8]\n[5, 3]\n[5, 3]\n[3, 8]\n[3, 8]\n[]" },
        { "string slices", "s = \"Hello\"\nputs s[1..3]\nputs s[1, 3]\nputs s[-1]\nputs s[-3..]\n", "ell\nell\no\nllo" },
        { "list slices and negative index", "l = List(Int32).new\nl << 1\nl << 2\nl << 3\nputs l[-1]\nputs l[1..].to_s\n", "3\n[2, 3]" },
        { "negative index of a parameter, and as a target", "def last(ys : Array(Int32)) : Int32\n  ys[-1]\nend\nputs last([4, 9])\nzs = [1, 2, 3]\nzs[-1] = 7\nputs zs.to_s\n", "9\n[1, 2, 7]" },
        // 9. a pin inside an array pattern, of a name bound earlier in the same pattern
        { "pin in an array pattern", "def describe(xs : Array(Int32)) : String\n  case xs\n  in [a, ^a] then \"pair of #{a}\"\n  in _ then \"other\"\n  end\nend\nputs describe([4, 4])\nputs describe([4, 5])\n", "pair of 4\nother" },
        { "pin of an outer value in an array pattern", "k = 3\ncase [3, 1]\nin [^k, b] then puts b\nin _ then puts 0\nend\n", "1" },
        // 10. `<<` on List(T) of a type parameter
        { "List(T) << in a generic class", "class Shelf(T)\n  getter items : List(T)\n  def initialize\n    @items = List(T).new\n  end\n  def put(item : T)\n    @items << item\n  end\nend\ns = Shelf(String).new\ns.put(\"Assam\")\nputs s.items\n", "[\"Assam\"]" },
        // 11. a Kernel function inside a reopened .NET type is the Prelude function, not a method of self
        { "format in a reopened .NET type", "class Int32\n  def cents_text : String\n    format(\"%d.%02d\", self / 100, self % 100)\n  end\nend\nputs 1205.cents_text\n", "12.05" },
        // 12. String#upcase is proven pure
        { "pure def with upcase", "pure def shout(s : String) : String = s.upcase + \"!\"\nputs shout(\"tea\")\n", "TEA!" },
        // 14. numeric limits are constants: pure
        { "pure def with Int32::MAX", "pure def cap(x : Int32) : Int32 = x > Int32::MAX - 1 ? Int32::MIN : x\nputs cap(3)\n", "3" },
        // 15. `rescue A, B => e`: each catch is parsed from the same tokens; a negative literal stays -1
        { "rescue A, B with a negative literal", "def parse(s : String) : Int32\n  begin\n    Int32.Parse(s)\n  rescue FormatException, OverflowException => e\n    -1\n  end\nend\nputs parse(\"12\")\nputs parse(\"x\")\nputs parse(\"99999999999\")\n", "12\n-1\n-1" },
        // 16. the (keys, values) result of sort_by_key: element names win over the named-tuple helpers
        { "sort_by_key keys and values", "keys = [3_u32, 1_u32, 2_u32]\nvals = [\"c\", \"a\", \"b\"]\ns = keys.sort_by_key(vals)\nputs s.keys.to_s\nputs s.values.to_s\n", "[1, 2, 3]\n[\"a\", \"b\", \"c\"]" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Book_repro_compiles_and_runs(string name, string src, string expected)
    {
        Assert.True(expected == Run(src), name + ": " + Run(src));
    }

    // 1. the agents block and the guide header
    [Fact]
    public void Agents_block_and_guide_header_are_current()
    {
        string agents = GuideCommand.Text("agents");
        Assert.DoesNotContain("no `rescue` modifier", agents);
        Assert.Contains("Hash(String, Int32).new(0)", agents);
        string guide = GuideCommand.Text();
        Assert.StartsWith("# Alice guide\n\nFor Alice " + Alice.Cli.App.Version + ".", guide);
        Assert.DoesNotContain("{{version}}", guide);
        Assert.Equal("-1", Run("x = (Int32.Parse(\"q\") rescue -1)\nputs x\n"));   // the modifier the old row denied
    }

    // 7. a lowering error that names its code keeps it
    [Fact]
    public void Capturing_native_callback_is_reported_as_AL0215()
    {
        const string src = "@[Link(\"libc.so.6\")]\nlib LibC\n  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void\nend\nxs = [5, 3, 9, 1]\ndir = -1\nLibC.qsort(xs, 4_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { (a[0] <=> b[0]) * dir })\n";
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal("AL0215", d.Code);
        Assert.StartsWith("a callback passed to native code cannot capture variables", d.Message);
        Assert.Equal(7, d.Line);
    }

    // 13. every code has one row in docs/errors.md
    [Fact]
    public void Error_codes_are_documented_once()
    {
        var rows = File.ReadAllLines(Path.Combine(Root(), "docs", "errors.md"))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\| (AL\d{4}) \|")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        Assert.Empty(rows.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Contains("AL0220", rows);   // strict mode (live)
        Assert.Contains("AL0221", rows);   // unassigned field (live)
        Assert.Contains("AL0240", rows);   // affine payload (planned, ADR 0037)
    }

    // 14. Int32::MAX in a rule
    [Fact]
    public void A_rule_using_numeric_limits_is_proven()
    {
        var asm = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile("rule below_max(x in 0..3)\n  x < Int32::MAX && x > Int32::MIN\nend\n")], "rules", exe: false));
        var r = (System.Runtime.CompilerServices.ITuple)asm.GetType("Program")!.GetMethod("__prove_below_max")!.Invoke(null, null)!;
        Assert.Equal(4L, (long)r[0]!);
        Assert.Null(r[1]);
    }

    // README: the range pipeline is one fused serial loop; an array pipeline of 65536+ elements is a parallel reduce,
    // and `alice explain` says so (the `__par_` helper counts for its method)
    [Fact]
    public void Explain_reports_parallel_pipelines_and_the_readme_does_not_overclaim()
    {
        var range = Explain.Report(AliceCompiler.Transpile("def total : Int64\n  (1..1_000_000).map { |i| i.to_i64 * i }.select { |x| x % 2 == 0 }.sum\nend\nputs total\n"));
        Assert.DoesNotContain(range["total"], f => f.Contains("parallel"));
        var array = Explain.Report(AliceCompiler.Transpile("def total(xs : Array(Int64)) : Int64\n  xs.map { |i| i * i }.select { |x| x % 2 == 0 }.sum\nend\nputs total(Array(Int64).new(10) { |i| i.to_i64 })\n"));
        Assert.Contains(array["total"], f => f.Contains("parallel loop"));
        Assert.DoesNotContain(array.Keys, k => k.StartsWith("__"));
        var top = Explain.Report(AliceCompiler.Transpile("xs = Array(Int64).new(10) { |i| i.to_i64 }\nputs xs.map { |i| i * i }.sum\n"));
        Assert.Contains(top["Main"], f => f.Contains("parallel loop"));
        Assert.False(top.ContainsKey("main"));
        Assert.DoesNotContain("fused, parallel", File.ReadAllText(Path.Combine(Root(), "README.md")));
    }
}
