using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Process isolation (ADR 0024): actor messages must be sendable (AL0230) and actor code may not capture
/// shared mutable state (AL0231), so no two processes ever share a mutable object.
/// </summary>
[Collection("console")]
public class ActorIsolationTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Theory]
    [InlineData("a = Actor(List(Int32)).new(->(m : List(Int32)) { puts m.Count })\n")]
    [InlineData("a = Actor(Array(Int32)).new(->(m : Array(Int32)) { puts m[0] })\n")]
    [InlineData("class Box\n  property v : Int32 = 0\nend\na = Actor(Box).new(->(m : Box) { puts m.v })\n")]
    [InlineData("a = Actor(Int32, System.Text.StringBuilder).new(-> { 0 }, ->(s : Int32, m : System.Text.StringBuilder) { s })\n")]
    [InlineData("record Bag(items : List(Int32))\na = Actor(Bag).new(->(m : Bag) { puts 1 })\n")]
    [InlineData("a = Actor(Tuple(Int32, Array(Int32))).new(->(m : Tuple(Int32, Array(Int32))) { puts 1 })\n")]
    public void Mutable_message_types_are_rejected(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains("not sendable", ex.Message);
        Assert.Equal("AL0230", ex.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("xs = List(Int32).new\na = Actor(Int32).new(->(m : Int32) { puts xs.Count })\n")]
    [InlineData("xs = [1, 2]\na = Actor(Array(Int32), Int32).new(-> { xs }, ->(s : Array(Int32), m : Int32) { s })\n")]
    [InlineData("sb = System.Text.StringBuilder.new\na = Actor(Int32, Int32).new(-> { 0 }, ->(s : Int32, m : Int32) { sb.Length + s })\n")]
    public void Capturing_shared_mutable_state_is_rejected(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains("not isolated", ex.Message);
        Assert.Equal("AL0231", ex.Diagnostics[0].Code);
    }

    [Fact]
    public void Writing_captured_state_from_an_actor_is_still_a_data_race()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("n = 0\na = Actor(Int32, Int32).new(-> { 0 }, ->(s : Int32, m : Int32) { n = m\n  s })\n"));
        Assert.Equal("AL0202", ex.Diagnostics[0].Code);
    }

    public static TheoryData<string, string, string> Programs => new()
    {
        { "record message", "record Point(x : Int32, y : Int32)\nsum = Atomic.new(0)\na = Actor(Point).new(->(m : Point) { sum.add(m.x + m.y) })\na.send(Point.new(1, 2))\na.stop\nputs sum.get", "3" },
        { "string, tuple and struct messages", "struct V\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\nend\nn = Atomic.new(0)\na = Actor(String).new(->(m : String) { n.add(m.size) })\nb = Actor(Tuple(Int32, String)).new(->(m : Tuple(Int32, String)) { n.add(m.Item1) })\nc = Actor(V).new(->(m : V) { n.add(m.x) })\na.send(\"abc\")\nb.send({4, \"x\"})\nc.send(V.new(10))\na.stop\nb.stop\nc.stop\nputs n.get", "17" },
        { "state built inside the actor", "a = Actor(List(Int32), Int32).new(-> { List(Int32).new }, ->(s : List(Int32), m : Int32) {\n  s.Add(m)\n  s\n})\n3.times { |i| a.send(i) }\nputs a.finish.Count", "3" },
        { "actors may hold other actors", "sink = Actor(Int64, Int32).new(-> { 0L }, ->(s : Int64, m : Int32) { s + m })\nfwd = Actor(Int32).new(->(m : Int32) { sink.send(m * 2) })\n5.times { |i| fwd.send(i) }\nfwd.stop\nputs sink.finish", "20" },
        { "immutable captures are fine", "k = 3\nname = \"n\"\nout = Atomic.new(0)\na = Actor(Int32).new(->(m : Int32) { out.add(m * k + name.size) })\na.send(2)\na.stop\nputs out.get", "7" },
        { "supervisor restarts a crashed actor", "sup = Supervisor.one_for_one(3, 10000)\nc = sup.supervise(Actor(Int32, Int32).new(-> { 100 }, ->(s : Int32, m : Int32) {\n  raise \"bad\" if m < 0\n  s + m\n}))\nc.send(5)\nc.send(-1)\nputs c.join.message\nc.send(2)\nputs c.finish\nputs c.restarts", "bad\n102\n1" },
        { "crash isolation and monitors", "ok = Atomic.new(0)\nbad = Actor(Int32).new(->(m : Int32) { raise \"boom\" })\ngood = Actor(Int32).new(->(m : Int32) { ok.add(m) })\nbad.on_exit { |r| ok.add(100) }\nbad.send(1)\ngood.send(1)\nputs bad.join.kind\ngood.stop\nputs ok.get", "crash\n101" },
        { "links and one_for_all", "sup = Supervisor.one_for_all(5, 10000)\na = sup.supervise(Actor(Int32, Int32).new(-> { 0 }, ->(s : Int32, m : Int32) {\n  raise \"x\" if m < 0\n  s + m\n}))\nb = sup.supervise(Actor(Int32, Int32).new(-> { 0 }, ->(s : Int32, m : Int32) { s + m }))\nb.send(9)\na.send(-1)\nwhile b.restarts == 0\n  Thread.Sleep(1)\nend\nb.send(1)\nputs b.finish\nputs sup.restart_count", "1\n1" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Isolated_actor_programs_compile_and_run(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }
}
