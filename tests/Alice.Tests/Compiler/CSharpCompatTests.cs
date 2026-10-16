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
        { "delegates Func and Action", "f = ->(x : Int32) { x * 3 }\ng : Func(Int32, Int32) = f\nacc = [] of Int32\na : Action(Int32) = ->(x : Int32) { acc.Add(x) }\na.Invoke(g.Invoke(2))\nputs acc[0]", "6" },
        { "nullable value types", "x : Int32? = nil\nputs x.nil?\ny = x || 7\nputs y\nx = 3\nputs x.not_nil! + 1", "true\n7\n4" },
        { "tuples and deconstruction", "def pair : {Int32, String}\n  {1, \"one\"}\nend\nn, s = pair\nputs \"#{n}=#{s}\"\nt = pair\nputs t.Item2", "1=one\none" },
        { "flags enum", "f = System.IO.FileAttributes::ReadOnly | System.IO.FileAttributes::Hidden\nputs f.HasFlag(System.IO.FileAttributes::Hidden)\nputs (f & System.IO.FileAttributes::ReadOnly) == System.IO.FileAttributes::ReadOnly", "true\ntrue" },
        { "indexers and params", "d = Dictionary(String, List(Int32)).new\nd[\"a\"] = List(Int32).new\nd[\"a\"].Add(5)\nputs d[\"a\"][0]\nputs String.Join(\"-\", \"x\", \"y\", \"z\")\nputs Math.Max(1, 2)", "5\nx-y-z\n2" },
        { "out and ref", "ok = Int32.TryParse(\"42\", out n)\nputs ok\nputs n + 1\nd = Dictionary(String, Int32).new\nd[\"k\"] = 9\nputs d.TryGetValue(\"k\", out v) ? v : -1", "true\n43\n9" },
        { "IDisposable using", "class Res < IDisposable\n  getter log : List(String)\n  def initialize(@log : List(String))\n  end\n  def Dispose\n    @log.Add(\"disposed\")\n  end\nend\nlog = [] of String\nusing r = Res.new(log)\n  log.Add(\"working\")\nend\nputs log.join(\",\")", "working,disposed" },
        { "struct and interface generics", "interface IArea\n  def area : Float64\nend\nstruct Sq < IArea\n  getter s : Float64\n  def initialize(@s : Float64)\n  end\n  def area : Float64\n    s * s\n  end\nend\ndef total(xs : Array(T)) : Float64 forall T where T : IArea\n  xs.sum { |x| x.area }\nend\nputs total([Sq.new(2.0), Sq.new(3.0)])", "13" },
        { "spans and stackalloc-free APIs", "s = \"hello world\"\nsp = s.AsSpan(6)\nputs sp.ToString()\nputs MemoryExtensions.IndexOf(s.AsSpan(), 'w')", "world\n6" },
        { "date time guid", "d = DateTime.new(2024, 1, 31)\nputs d.AddDays(1).Month\nputs TimeSpan.FromMinutes(90).TotalHours\nputs Guid.Empty.ToString().size", "2\n1.5\n36" },
        { "file io", "p = System.IO.Path.GetTempFileName()\nFile.WriteAllText(p, \"abc\")\nputs File.ReadAllText(p).upcase\nFile.Delete(p)\nputs File.Exists(p)", "ABC\nfalse" },
        { "linq query methods", "xs = [5, 3, 8, 1]\nputs xs.Where(->(x : Int32) { x > 2 }).OrderBy(->(x : Int32) { x }).Select(->(x : Int32) { x * 10 }).First()", "30" },
        { "typeof and reflection", "t = typeof(String)\nputs t.Name\nputs typeof(List(Int32)).IsGenericType", "String\ntrue" },
        { "async Task.WhenAll", "async def work(n : Int32) : Int32\n  await Task.Delay(1)\n  n * 2\nend\nasync def both : Int32\n  a = work(1)\n  b = work(2)\n  await Task.WhenAll(a, b)\n  a.Result + b.Result\nend\nputs both.wait", "6" },
        { "ruby instance variables", "class P\n  def initialize\n    @x = 1\n    @name = \"p\"\n  end\nend\np = P.new\nputs p.instance_variable_get(\"@x\")\np.instance_variable_set(\"@x\", 5)\nputs p.instance_variable_get(\"@x\")\nputs p.instance_variables.join(\",\")", "1\n5\n@x,@name" },
        { "ruby methods and public_send", "class Q\n  def hi : String\n    \"hi\"\n  end\n  def add(a : Int32, b : Int32) : Int32\n    a + b\n  end\nend\nq = Q.new\nputs q.methods.includes?(\"hi\")\nputs q.public_send(\"add\", 2, 3)\nputs q.is_a?(Object)", "true\n5\ntrue" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }
}
