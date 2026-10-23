using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Broad C#/.NET feature compatibility from Alice code, plus Ruby-style runtime reflection.</summary>
[Collection("console")]
public class CSharpCompatTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        { "events", "timer = System.Timers.Timer.new(1000.0)\nfired = Atomic.new(0)\ntimer.Elapsed += ->(s : Object?, e : System.Timers.ElapsedEventArgs) { fired.add(1) }\ntimer.Dispose\nputs fired.get", "0" },
        { "event handler from block and variable, unsubscribe", "class Src\n  def fire\n    @log.Add(\"x\")\n  end\n  def initialize(@log : List(String))\n  end\nend\nlog = [] of String\nn = Atomic.new(0)\nh = ->(s : Object?, e : EventArgs) { n.add(1) }\nsrc = System.ComponentModel.Component.new\nsrc.Disposed += h\nsrc.Disposed += ->(s : Object?, e : EventArgs) { n.add(10) }\nsrc.Disposed -= h\nsrc.Dispose\nputs n.get", "10" },
        { "await each over IAsyncEnumerable", "async def total : Int32\n  sum = 0\n  ch = System.Threading.Channels.Channel.CreateUnbounded<Int32>()\n  ch.Writer.TryWrite(2)\n  ch.Writer.TryWrite(5)\n  ch.Writer.Complete\n  for await x in ch.Reader.ReadAllAsync\n    sum += x\n  end\n  sum\nend\nputs total.wait", "7" },
        { "delegates Func and Action", "f = ->(x : Int32) { x * 3 }\ng : Func(Int32, Int32) = f\nacc = [] of Int32\na : Action(Int32) = ->(x : Int32) { acc.Add(x) }\na.Invoke(g.Invoke(2))\nputs acc[0]", "6" },
        { "nullable value types", "x : Int32? = nil\nputs x.nil?\ny = x || 7\nputs y\nx = 3\nputs x.not_nil! + 1", "true\n7\n4" },
        { "tuples and deconstruction", "def pair : {Int32, String}\n  {1, \"one\"}\nend\nn, s = pair\nputs \"#{n}=#{s}\"\nt = pair\nputs t.Item2", "1=one\none" },
        { "flags enum", "f = System.IO.FileAttributes::ReadOnly | System.IO.FileAttributes::Hidden\nputs f.HasFlag(System.IO.FileAttributes::Hidden)\nputs (f & System.IO.FileAttributes::ReadOnly) == System.IO.FileAttributes::ReadOnly", "true\ntrue" },
        { "indexers and params", "d = Dictionary(String, List(Int32)).new\nd[\"a\"] = List(Int32).new\nd[\"a\"].Add(5)\nputs d[\"a\"][0]\nputs String.Join(\"-\", \"x\", \"y\", \"z\")\nputs Math.Max(1, 2)", "5\nx-y-z\n2" },
        { "out and ref", "ok = Int32.TryParse(\"42\", out n)\nputs ok\nputs n + 1\nd = Dictionary(String, Int32).new\nd[\"k\"] = 9\nputs d.TryGetValue(\"k\", out v) ? v : -1", "true\n43\n9" },
        { "IDisposable using", "class Res < IDisposable\n  getter log : List(String)\n  def initialize(@log : List(String))\n  end\n  def Dispose\n    @log.Add(\"disposed\")\n  end\nend\nlog = [] of String\nusing r = Res.new(log)\n  log.Add(\"working\")\nend\nputs log.join(\",\")", "working,disposed" },
        { "struct and interface generics", "interface IArea\n  def area : Float64\nend\nstruct Sq < IArea\n  getter s : Float64\n  def initialize(@s : Float64)\n  end\n  def area : Float64\n    s * s\n  end\nend\ndef total(xs : Array(T)) : Float64 forall T where T : IArea\n  xs.sum { |x| x.area }\nend\nputs total([Sq.new(2.0), Sq.new(3.0)])", "13.0" },
        { "spans and stackalloc-free APIs", "s = \"hello world\"\nsp = s.AsSpan(6)\nputs sp.ToString()\nputs MemoryExtensions.IndexOf(s.AsSpan(), 'w')", "world\n6" },
        { "date time guid", "d = DateTime.new(2024, 1, 31)\nputs d.AddDays(1).Month\nputs TimeSpan.FromMinutes(90).TotalHours\nputs Guid.Empty.ToString().size", "2\n1.5\n36" },
        { "file io", "p = System.IO.Path.GetTempFileName()\nFile.WriteAllText(p, \"abc\")\nputs File.ReadAllText(p).upcase\nFile.Delete(p)\nputs File.Exists(p)", "ABC\nfalse" },
        { "linq query methods", "xs = [5, 3, 8, 1]\nputs xs.Where(->(x : Int32) { x > 2 }).OrderBy(->(x : Int32) { x }).Select(->(x : Int32) { x * 10 }).First()", "30" },
        { "typeof and reflection", "t = typeof(String)\nputs t.Name\nputs typeof(List(Int32)).IsGenericType", "String\ntrue" },
        { "async Task.WhenAll", "async def work(n : Int32) : Int32\n  await Task.Delay(1)\n  n * 2\nend\nasync def both : Int32\n  a = work(1)\n  b = work(2)\n  await Task.WhenAll(a, b)\n  a.Result + b.Result\nend\nputs both.wait", "6" },
        { "ruby instance variables", "class P\n  def initialize\n    @x = 1\n    @name = \"p\"\n  end\nend\np = P.new\nputs p.instance_variable_get(\"@x\")\np.instance_variable_set(\"@x\", 5)\nputs p.instance_variable_get(\"@x\")\nputs p.instance_variables.join(\",\")", "1\n5\n@x,@name" },
        { "snake_case .NET instance members", "sw = System.Diagnostics.Stopwatch.start_new\nputs sw.elapsed.total_milliseconds >= 0.0\nsw.stop\nputs sw.is_running\nsb = System.Text.StringBuilder.new\nsb.append(\"ab\").append_line(\"c\")\nputs sb.to_string.trim\nputs sb.length\nsb.capacity = 64\nputs sb.capacity\nputs Environment.processor_count > 0\nl = List(Int32).new\nl.add(3)\nputs l.count", "true\nfalse\nabc\n4\n64\ntrue\n1" },
        { "predicate names map to .NET Is/Contains", "d = Dictionary(String, Int32).new\nd[\"k\"] = 1\nputs d.contains_key?(\"k\")\nputs d.contains_key?(\"z\")\nputs System.IO.DirectoryInfo.new(\"/\").exists?\nputs Uri.new(\"http://a.b/c\").absolute_uri?", "true\nfalse\ntrue\ntrue" },
        { "attributes on record parameters", "using System.Text.Json\nusing System.Text.Json.Serialization\nrecord Span(start : Int32, @[JsonPropertyName(\"end\")] stop : Int32)\nputs JsonSerializer.Serialize(Span.new(1, 5))\nback = JsonSerializer.Deserialize(\"{\\\"start\\\":2,\\\"end\\\":9}\", typeof(Span)).not_nil!.as(Span)\nputs back.stop", "{\"start\":1,\"end\":5}\n9" },
        { "iterator methods", "def nums(n : Int32) : IEnumerable(Int32)\n  i = 0\n  while true\n    return if i == n\n    yield i * i\n    i += 1\n  end\nend\nputs nums(4).Sum\nputs nums(1_000_000_000).Take(3).ToList().join(\",\")\nputs nums(0).Any", "14\n0,1,4\nfalse" },
        { "async iterator", "async def ticks : IAsyncEnumerable(Int32)\n  yield 1\n  await Task.Yield\n  yield 2\nend\nasync def total : Int32\n  t = 0\n  for await x in ticks\n    t += x\n  end\n  t\nend\nputs total.wait", "3" },
        { "class implementing IEnumerable via iterator", "class Trio < IEnumerable(Int32)\n  def GetEnumerator : IEnumerator(Int32)\n    yield 1\n    yield 2\n    yield 3\n  end\nend\nputs Trio.new.Sum\nputs Trio.new.Select(->(x : Int32) { x * 2 }).Last", "6\n6" },
        { "ruby block yield still works", "def twice(&b : Int32 ->)\n  yield 1\n  yield 2\nend\nt = 0\ntwice { |x| t += x }\nputs t", "3" },
        { "nullable generic arguments", "d = Dictionary(String, String?).new\nd[\"a\"] = nil\nd[\"b\"] = \"x\"\nputs d[\"a\"].nil?\nl = List(String?).new\nl.Add(nil)\nputs l.Count\nputs (d[\"b\"] || \"\").size", "true\n1\n1" },
        { "declaring events", "class Btn\n  event clicked : EventHandler\n  def click\n    clicked&.Invoke(self, EventArgs.Empty)\n  end\nend\nb = Btn.new\nn = Atomic.new(0)\nb.click\nb.clicked += ->(s : Object?, e : EventArgs) { n.add(1) }\nb.click\nb.click\nputs n.get", "2" },
        { "declaring delegate types", "delegate Op(a : Int32, b : Int32) : Int32\ndef apply(f : Op, x : Int32) : Int32\n  f.Invoke(x, x)\nend\nputs apply(->(a : Int32, b : Int32) { a * b }, 7)", "49" },
        { "interface default methods", "interface IGreet\n  def name : String\n  def greet : String\n    \"hi \" + name\n  end\nend\nclass G < IGreet\n  def name : String\n    \"bo\"\n  end\nend\nputs G.new.as(IGreet).greet", "hi bo" },
        { "ruby methods and public_send", "class Q\n  def hi : String\n    \"hi\"\n  end\n  def add(a : Int32, b : Int32) : Int32\n    a + b\n  end\nend\nq = Q.new\nputs q.methods.includes?(\"hi\")\nputs q.public_send(\"add\", 2, 3)\nputs q.is_a?(Object)", "true\n5\ntrue" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }

    [Fact]
    public void Nullable_generic_values_must_be_checked()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Run("d = Dictionary(String, String?).new\nd[\"a\"] = \"x\"\nputs d[\"a\"].size\n"));
        Assert.Contains(ex.Diagnostics, d => d.Code == "AL0310");
    }
}
