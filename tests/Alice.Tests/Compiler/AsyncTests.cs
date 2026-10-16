using Alice.Compiler;

namespace Alice.Tests.Compiler;

[Collection("console")]
public class AsyncTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Fact]
    public void Async_def_and_await()
    {
        var src = "async def twice(x : Int32) : Int32\n  await Task.Delay(1)\n  x * 2\nend\nasync def main_async\n  a = await twice(4)\n  b = await twice(a)\n  puts b\nend\nmain_async.wait";
        Assert.Equal("16", Run(src));
    }

    [Fact]
    public void Async_return_is_inferred()
    {
        var cs = AliceCompiler.Transpile("async def f(x : Int32)\n  await Task.Yield\n  x + 1\nend\n");
        Assert.Contains("async global::System.Threading.Tasks.Task<int> f(", cs);
    }

    [Fact]
    public void Async_channels_do_not_block()
    {
        var src = "ch = Channel(Int32).new\nasync def produce(c : Channel(Int32))\n  for i in 1..100\n    await c.send_async(i)\n  end\n  c.close\nend\nasync def consume(c : Channel(Int32)) : Int32\n  total = 0\n  while await c.wait_async\n    total += c.receive\n  end\n  total\nend\np = produce(ch)\nputs consume(ch).wait\np.wait";
        Assert.Equal("5050", Run(src));
    }

    [Fact]
    public void Many_actors_do_not_hold_threads()
    {
        var src = "sum = 0L\nactors = List(Actor(Int32)).new\n2000.times { |i| actors.Add(Actor(Int32).new(->(m : Int32) { Interlocked.Add(ref sum, m) })) }\nactors.each { |a| a.send(1) }\nactors.each { |a| a.stop }\nputs sum";
        Assert.Equal("2000", Run(src));
    }
}
