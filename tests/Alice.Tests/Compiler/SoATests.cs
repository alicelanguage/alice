using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>@[SoA] structs: columnar stores, column-wise loops, vectorized column updates.</summary>
[Collection("console")]
public class SoATests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    const string Particles = """
        @[SoA]
        record struct Particle(x : Float64, v : Float64, m : Float64)

        ps = SoA(Particle).new
        for i in 0...1000
          ps.add(Particle.new(i.to_f, 1.0, 2.0))
        end
        ps.each { |p| p.x += p.v * 0.5 }
        ps.each { |p| p.v = p.v * 2.0 }
        puts ps.size
        puts ps[10].x
        puts ps.sum { |p| p.m * p.v }
        ps.remove_at(0)
        puts ps.size
        puts ps[0].x
        """;

    [Fact]
    public void Store_and_column_loops_are_correct()
        => Assert.Equal("1000\n10.5\n4000\n999\n999.5", Run(Particles));

    [Fact]
    public void Column_loops_touch_columns_and_vectorize()
    {
        var cs = AliceCompiler.Transpile(Particles);
        Assert.Contains("public double[] x", cs);
        Assert.Contains("StoreUnsafe", cs);                      // vectorized `each` update
        Assert.DoesNotContain("new Particle(ps", cs);            // no per-element struct gather in loops
    }
}
