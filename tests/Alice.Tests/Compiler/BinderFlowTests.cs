using Alice.Compiler;
using Alice.Compiler.Semantics;

namespace Alice.Tests.Compiler;

/// <summary>
/// The binder owns nullable flow (AL0310), use before assignment (AL0309) and constant overflow (AL0300), D1.12 /
/// ADR 0033: the IL backend has no Roslyn to find them. Parity: on each program the binder reports what the C#
/// path's Roslyn translation reports (code and line), and nothing else (Binder.Flow*.cs, Diagnostics/FlowOracle.cs).
/// </summary>
[Collection("console")]
public class BinderFlowTests
{
    const string Pick = "def pick(flag : Bool) : String?\n  flag ? \"abc\" : nil\nend\n";

    static List<(string Code, int Line)> Binder(string src)
    {
        var b = new Binder([Alice.Compiler.Syntax.Parser.ParseFile(src, "main.al")], new ClrTypes(null)) { FlowShadow = true };
        b.Bind();
        return b.FlowErrors.Select(d => (d.Code, d.Line)).Distinct().OrderBy(x => x.Line).ToList();
    }

    static bool FlowDiagnostic(Diagnostic d) =>
        d.Code is "AL0309" or "AL0310" || d.Code == "AL0300" && (d.Message.Contains("overflows at compile time") || d.Message.Contains("Constant value"));

    /// <summary>What Roslyn reports on the generated C# (the C# path keeps the binder in shadow mode).</summary>
    static List<(string Code, int Line)> Roslyn(string src)
    {
        try
        {
            var cs = AliceCompiler.TranspileAll([new SourceFile("main.al", src)]);
            AliceCompiler.Emit(cs, "flow", exe: true);
            return [];
        }
        catch (CompileException e)
        {
            return e.Diagnostics.Where(FlowDiagnostic).Select(d => (d.Code, d.Line)).Distinct().OrderBy(x => x.Line).ToList();
        }
    }

    public static TheoryData<string, string> Programs => new()
    {
        { "if narrows", "s = pick(true)\nif s\n  puts s.size\nend\n" },
        { "else of if", "s = pick(true)\nif s\n  puts 1\nelse\n  puts s.size\nend\n" },
        { "unless/else", "s = pick(true)\nunless s\n  puts 0\nelse\n  puts s.size\nend\n" },
        { "return unless", "def f(s : String?) : Int32\n  return 0 unless s\n  s.size\nend\nputs f(pick(true))\n" },
        { "raise unless", "def f(s : String?) : Int32\n  raise \"x\" unless s\n  s.size\nend\nputs f(pick(true))\n" },
        { "nil? or", "s = pick(true)\nif s.nil? || s.size == 0\n  puts 0\nelse\n  puts s.size\nend\n" },
        { "nil? or, wrong branch", "s = pick(true)\nif s.nil? || s.size == 0\n  puts s.size\nend\n" },
        { "coalesce", "s = pick(true)\nt = s || \"d\"\nputs t.size\n" },
        { "or-assign", "s = pick(true)\ns ||= \"e\"\nputs s.size\n" },
        { "x = x || d", "s = pick(true)\ns = s || \"d\"\nputs s.size\n" },
        { "not_nil!", "s = pick(true)\nputs s.not_nil!.size\n" },
        { "safe call", "s = pick(true)\nputs s&.size\n" },
        { "dereference", "s = pick(true)\nputs s.size\n" },
        { "reported once", "s = pick(true)\nputs s.size\nputs s.size\n" },
        { "loop resets", "s = pick(true)\ni = 0\nwhile i < 3\n  if s\n    puts s.size\n  end\n  s = nil\n  i += 1\nend\n" },
        { "loop back edge", "s = \"a\"\ni = 0\nwhile i < 3\n  puts s.size\n  s = pick(false)\n  i += 1\nend\n" },
        { "for back edge", "s = \"a\"\nfor i in 0..3\n  puts s.size\n  s = pick(false)\nend\n" },
        { "for stays non-nil", "s = \"a\"\nfor i in 0..3\n  puts s.size\n  s = \"b\"\nend\n" },
        { "loop do break if", "s = pick(true)\nloop do\n  break if s\n  s = \"x\"\nend\nputs s.size\n" },
        { "case when nil", "x = pick(true)\ncase x\nwhen nil\n  puts 1\nelse\n  puts x.size\nend\n" },
        { "case when literal", "x = pick(true)\ncase x\nwhen \"abc\"\n  puts x.size\nend\n" },
        { "case when type", "x : Object? = pick(true)\ncase x\nwhen String\n  puts 1\nelse\n  puts x.to_s\nend\n" },
        { "argument of an Alice method", "def g(s : String) : Int32\n  s.size\nend\nputs g(pick(true))\n" },
        { "nilable parameter", "def g(s : String?) : Int32\n  s ? s.size : 0\nend\nputs g(pick(true))\n" },
        { "tail return", "def g(s : String?) : String\n  s\nend\nputs g(pick(true))\n" },
        { "return", "def g(s : String?) : String\n  return s\nend\nputs g(pick(true))\n" },
        { "typed local", "x : String = pick(true)\nputs x\n" },
        { "typed local nil", "x : String = nil\nputs x\n" },
        { "nilable local", "x : String? = nil\nputs x.size\n" },
        { "nilable local assigned", "x : String? = nil\nx = \"a\"\nputs x.size\n" },
        { "nilable field", "class P\n  @name : String?\n  def initialize\n  end\n  def len : Int32\n    @name.size\n  end\nend\nputs P.new.len\n" },
        { "nilable field checked", "class P\n  @name : String?\n  def initialize\n  end\n  def len : Int32\n    if @name\n      return @name.size\n    end\n    0\n  end\nend\nputs P.new.len\n" },
        { "store into a field", "class P\n  @name : String = \"a\"\n  def set(s : String?)\n    @name = s\n  end\nend\nP.new.set(nil)\n" },
        { "captured by a lambda", "s = pick(true)\nf = ->() { s.size }\nputs f.call\n" },
        { "lambda under a check", "s = pick(true)\nif s\n  f = ->() { s.size }\n  puts f.call\nend\n" },
        { "block", "s = pick(true)\n[1, 2].each { |x| puts s.size }\n" },
        { "ternary", "s = pick(true)\nputs(s ? s.size : 0)\nputs(s.nil? ? 0 : s.size)\n" },
        { "ternary wrong arm", "s = pick(true)\nputs(s ? 0 : s.size)\n" },
        { "rescue sees the try states", "s = pick(true)\nbegin\n  s = \"a\"\n  puts s.size\nrescue e\n  puts s.size\nend\n" },
        { "== nil / != nil", "s = pick(true)\nif s != nil\n  puts s.size\nend\nif s == nil\n  puts 0\nelse\n  puts s.size\nend\n" },
        { "nilable array index", "a : Array(String)? = nil\nputs a[0]\n" },
        { "nilable element", "a = [pick(true), pick(false)]\nputs a[0].size\n" },
        { "nilable dictionary value", "d = Dictionary(String, String?).new\nd[\"a\"] = \"x\"\nputs d[\"a\"].size\n" },
        { "store nil as a nilable value", "d = Dictionary(String, String?).new\nd[\"a\"] = nil\nputs d[\"a\"].nil?\n" },
        { "interpolation", "s = pick(true)\nputs \"v=#{s}\"\n" },
        { "elsif", "s = pick(true)\nt = pick(false)\nif s\n  puts s.size\nelsif t\n  puts t.size\nelse\n  puts 0\nend\n" },
        { "next unless", "xs = [pick(true), pick(false)]\nfor x in xs\n  next unless x\n  puts x.size\nend\n" },
        { "break unless", "xs = [pick(true), pick(false)]\nfor x in xs\n  break unless x\n  puts x.size\nend\n" },
        { "while x = f", "def rd(i : Int32) : String?\n  i < 3 ? \"x\" : nil\nend\ni = 0\nwhile line = rd(i)\n  puts line.size\n  i += 1\nend\n" },
        { "if x = f", "if x = pick(true)\n  puts x.size\nend\n" },
        { "modifier unless nil?", "s = pick(true)\nputs s.size unless s.nil?\n" },
        { "modifier if", "s = pick(true)\nputs s.size if s\n" },
        { "property path", "class Q\n  property name : String? = nil\nend\nq = Q.new\nif q.name\n  puts q.name.size\nend\nputs q.name.size\n" },
        { "tail of an if", "def g(f : Bool) : String\n  if f\n    pick(f)\n  else\n    \"a\"\n  end\nend\nputs g(true)\n" },
        { "unsigned constant overflow", "z = 0_u32 - 1_u32\nputs z\n" },
        { "int constant overflow", "z = 2147483647 + 1\nputs z\n" },
        { "wrapping constant", "z = 2147483647 &+ 1\nputs z\n" },
        { "negated minimum", "z = -(-2147483648)\nputs z\n" },
        { "typed literal out of range", "g = 200_i8\nputs g\n" },
        { "unchecked region", "unchecked\n  m = 2147483647 + 1\n  puts m\nend\n" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Binder_reports_what_the_csharp_path_reports(string name, string body)
    {
        _ = name;
        var src = Pick + body;
        Assert.Equal(Roslyn(src), Binder(src));
    }

    [Theory]
    [InlineData("def len(s : String) : Int32\n  s.size\nend\ns = pick(false)\nputs len(s)\n", "AL0310")]
    [InlineData("class P\n  getter name : String = \"n\"\nend\nq : P? = nil\nputs q.name.size\n", "AL0310")]
    [InlineData("z = 0_u32 - 1_u32\nputs z\n", "AL0300")]
    public void Il_backend_rejects_what_the_csharp_backend_rejects(string body, string code)
    {
        var ex = Assert.Throws<CompileException>(() => Alice.Compiler.Backend.Il.AirToIl.Compile([new SourceFile("main.al", Pick + body)], "flow"));
        Assert.Equal(code, ex.Diagnostics[0].Code);
    }

    [Fact]
    public void Il_backend_accepts_checked_nil_flow()
    {
        var res = Alice.Compiler.Backend.Il.AirToIl.Compile([new SourceFile("main.al", Pick + "s = pick(true)\nif s\n  puts s.size\nend\nt = s || \"d\"\nputs t.size\n")], "flow");
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
    }
}
