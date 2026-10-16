using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Rust-like data-race freedom: shared mutation from concurrent code is a compile error.</summary>
[Collection("console")]
public class RaceSafetyTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Theory]
    [InlineData("total = 0\nPar.For(100) { |i| total += i }\n")]
    [InlineData("total = 0\n[1, 2].par_each { |i| total += i }\n")]
    [InlineData("xs = List(Int32).new\nPar.For(100) { |i| xs.Add(i) }\n")]
    [InlineData("xs = List(Int32).new\nt = spawn { xs.Add(1) }\nt.wait\n")]
    [InlineData("acc = [0, 0]\nPar.For(2) { |i| acc[0] = i }\n")]
    [InlineData("n = 0\nt = Task.Run(->() { n = 1 })\nt.Wait()\n")]
    [InlineData("sum = 0\na = Actor(Int32).new(->(m : Int32) { sum += m })\n")]
    [InlineData("class Counter\n  getter n : Int32 = 0\n  def bump\n    @n += 1\n  end\nend\nc = Counter.new\nPar.For(10) { |i| c.bump }\n")]
    [InlineData("class Box\n  @v : Int32 = 0\n  def run\n    t = spawn { @v = 5 }\n    t.wait\n  end\nend\n")]
    [InlineData("x = 0\nPar.For(10) { |i| Volatile.Write(ref x, i) }\n")]
    public void Shared_mutation_in_concurrent_code_is_rejected(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains("data race", ex.Message);
        Assert.Equal("AL0202", ex.Diagnostics[0].Code);
    }

    public static TheoryData<string, string, string> SafePrograms => new()
    {
        { "disjoint index writes", "out = Array(Int32).new(100)\nPar.For(100) { |i| out[i] = i * 2 }\nputs out.sum", "9900" },
        { "mutex", "m = Mutex(List(Int32)).new(List(Int32).new)\nPar.For(100) { |i| m.lock { |l| l.Add(i) } }\nputs m.lock { |l| l.Count }", "100" },
        { "atomic", "c = Atomic.new(0)\nPar.For(1000) { |i| c.add(1) }\nputs c.get", "1000" },
        { "concurrent collection", "bag = System.Collections.Concurrent.ConcurrentBag(Int32).new\nPar.For(50) { |i| bag.Add(i) }\nputs bag.Count", "50" },
        { "interlocked by ref", "sum = 0L\nPar.For(100) { |i| Interlocked.Add(ref sum, i) }\nputs sum", "4950" },
        { "reads are shared freely", "xs = [1, 2, 3]\nc = Atomic.new(0)\nPar.For(3) { |i| c.add(xs[i]) }\nputs c.get", "6" },
        { "channel from tasks", "ch = Channel(Int32).new\nt = spawn { 5.times { |i| ch.send(i) }\n  ch.close }\ntotal = 0\nch.each { |v| total += v }\nt.wait\nputs total", "10" },
        { "actor with atomic state", "seen = Atomic.new(0)\na = Actor(Int32).new(->(m : Int32) { seen.add(m) })\n10.times { |i| a.send(i) }\na.stop\nputs seen.get", "45" },
    };

    [Theory, MemberData(nameof(SafePrograms))]
    public void Safe_concurrency_compiles_and_runs(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }
}
