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
        { "await using", "class R < IAsyncDisposable\n  getter log : List(String)\n  def initialize(@log : List(String))\n  end\n  def DisposeAsync : ValueTask\n    @log.Add(\"closed\")\n    ValueTask.CompletedTask\n  end\nend\nasync def go(log : List(String)) : Int32\n  await using r = R.new(log)\n    log.Add(\"in\")\n  end\n  log.Count\nend\nlog = [] of String\nputs go(log).wait\nputs log.join(\",\")", "2\nin,closed" },
        { "struct field initializers and explicit layout", "@[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind::Explicit)]\nstruct U\n  @[System.Runtime.InteropServices.FieldOffset(0)]\n  property i : Int32 = 7\nend\nstruct Cfg\n  property port : Int32 = 80\n  property host : String = \"localhost\"\nend\nputs U.new.i\nc = Cfg.new\nputs \"#{c.host}:#{c.port}\"", "7\nlocalhost:80" },
        { "span iteration", "def f(xs : ReadOnlySpan(Int32)) : Int32\n  t = 0\n  xs.each { |x| t += x }\n  xs.each_with_index { |x, i| t += x * i }\n  t + xs.sum { |x| x * 2 } + xs.count { |x| x > 1 }\nend\ndef g(xs : Span(Int32))\n  xs.each_with_index { |x, i| xs[i] = x * 10 }\nend\narr = [1, 2, 3]\nputs f(arr.AsSpan)\ng(arr.AsSpan)\nputs arr.join(\",\")", "28\n10,20,30" },
        { "method references", "def sq(x : Int32) : Int32\n  x * x\nend\nclass Sh\n  def initialize(@k : Int32)\n  end\n  def scale(x : Int32) : Int32\n    x * @k\n  end\nend\nmodule M\n  def self.add(a : Int32, b : Int32) : Int32\n    a + b\n  end\nend\nf = ->sq(Int32)\ng = ->sq\ns = Sh.new(10)\nh = ->s.scale\nd : Func(Int32, Int32) = ->sq\nadd = ->M.add\nputs \"#{f.call(4)} #{g.call(5)} #{h.call(3)} #{d.Invoke(6)} #{add.call(2, 3)}\"\nputs [1, 2, 3].map(->sq).join(\",\")", "16 25 30 36 5\n1,4,9" },
        { "lock statement", "gate = System.Threading.Lock.new\nn = 0\nlock gate\n  n += 1\nend\no = Object.new\nlock o\n  puts \"in #{n}\"\nend", "in 1" },
        { "nil checks by truthiness", "def find(k : String) : String?\n  k == \"a\" ? \"alpha\" : nil\nend\ns = find(\"a\")\nif s\n  puts s.size\nend\nt = find(\"z\")\nputs \"none\" unless t\nputs !t\nn : Int32? = 4\nputs(n ? \"has n\" : \"no n\")\nb : Bool? = nil\nputs(b ? \"yes\" : \"no\")\nq = find(\"a\")\nwhile q\n  puts q\n  q = nil\nend", "5\nnone\ntrue\nhas n\nno\nalpha" },
        { "assignment in conditions", "def find(k : String) : String?\n  k == \"a\" ? \"alpha\" : nil\nend\nif v = find(\"a\")\n  puts v.size\nend\nputs v\nif w = find(\"z\")\n  puts \"never\"\nelse\n  puts \"no w\"\nend\nq = Queue(Int32).new\nq.Enqueue(1)\nq.Enqueue(2)\ndef pop(q : Queue(Int32)) : Int32?\n  q.Count > 0 ? q.Dequeue : nil\nend\ntotal = 0\nwhile x = pop(q)\n  total += x\nend\nputs total", "5\nalpha\nno w\n3" },
        { "nullable value narrowing", "def half(n : Int32?) : Int32\n  return -1 unless n\n  n / 2\nend\nm : Int32? = 9\nif m\n  puts m + 1\nend\nputs half(8)\nputs half(nil)\nf : Float64? = 1.5\nputs(f ? f * 2.0 : 0.0)", "10\n4\n-1\n3.0" },
        { "named tuples", "def stats(xs : Array(Int32)) : {min: Int32, max: Int32}\n  {min: xs.min, max: xs.max}\nend\nt = {name: \"ada\", age: 36}\nputs t[:name]\nputs t.age + 1\ns = stats([4, 1, 9])\nputs \"#{s.min}..#{s.max}\"\nputs s.Item2", "ada\n37\n1..9\n9" },
        { "lambdas as blocks", "def sq(x : Int32) : Int32\n  x * x\nend\nf = ->(x : Int32) { x + 100 }\nputs [1, 2, 3].map(&->sq).join(\",\")\nputs [1, 2, 3].map(&f).join(\",\")\nputs [1, 2, 3, 4].select(&->(x : Int32) { x.even? }).join(\",\")\nt = 0\n[5, 6].each(&->(x : Int32) { t += x })\nputs t", "1,4,9\n101,102,103\n2,4\n11" },
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

    [Theory]
    [InlineData("f = ->nope\n", "no method `nope`")]
    [InlineData("def k(x : Int32) : Int32\n  x\nend\ndef k(x : String) : String\n  x\nend\nf = ->k\n", "give the parameter types")]
    public void Method_reference_errors_name_the_fix(string src, string message)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains(message, ex.Message + string.Join("", ex.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void Lock_does_not_silence_the_race_checker()
    {
        // `lock` cannot prove every writer takes the same lock: shared Alice data still needs Mutex(T) (AL0202)
        var src = "gate = Object.new\nlist = List(Int32).new\nt = spawn { lock gate\n  list.Add(1)\nend }\nt.wait\n";
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains("data race", ex.Message + string.Join("", ex.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void Narrowing_stops_where_the_local_is_reassigned()
    {
        // `n = nil` inside the branch: n stays Int32? there (reading n.Value would throw); `n + 1` is lifted arithmetic
        var src = "n : Int32? = 1\nif n\n  n = nil\n  puts n + 1\nend\n";
        Assert.DoesNotContain(".Value", AliceCompiler.Transpile(src));
        Assert.Contains("n.Value + 1", AliceCompiler.Transpile("n : Int32? = 1\nif n\n  puts n + 1\nend\n"));
    }
}
