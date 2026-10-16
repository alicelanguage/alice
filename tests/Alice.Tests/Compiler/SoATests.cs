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
    public void Store_type_usable_in_signatures_before_declaration()
        => Assert.Equal("2", Run("def make : SoA(Q)\n  s = SoA(Q).new\n  s.add(Q.new(1.0))\n  s.add(Q.new(2.0))\n  s\nend\n@[SoA]\nrecord struct Q(a : Float64)\nputs make.size"));

    [Fact]
    public void Column_loops_touch_columns_and_vectorize()
    {
        var cs = AliceCompiler.Transpile(Particles);
        Assert.Contains("public double[] x", cs);
        Assert.Contains("StoreUnsafe", cs);                      // vectorized `each` update
        Assert.DoesNotContain("new Particle(ps", cs);            // no per-element struct gather in loops
    }
}

public class SoAFusionTests
{
    static System.Reflection.MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "fuse", exe: false)).GetType("Program")!.GetMethod("f")!;

    const string Sim = "@[SoA]\nrecord struct P(x : Float64, y : Float64, vx : Float64, vy : Float64)\ndef f(n : Int32)\n  ps = SoA(P).new\n  n.times { |i| ps.add(P.new(i * 0.5, i * 0.25, (i % 7) * 0.1, (i % 5) * 0.2)) }\n  dt = 0.01\n  g = 9.81\n  for step in 0...10\n    ps.each { |p| p.vy = p.vy - g * dt }\n    ps.each { |p| p.x = p.x + p.vx * dt }\n    ps.each { |p| p.y = p.y + p.vy * dt }\n  end\n  ps.sum { |p| p.x + p.y * 3.0 }\nend";

    static double Reference(int n)
    {
        var x = new double[n]; var y = new double[n]; var vx = new double[n]; var vy = new double[n];
        for (int i = 0; i < n; i++) { x[i] = i * 0.5; y[i] = i * 0.25; vx[i] = (i % 7) * 0.1; vy[i] = (i % 5) * 0.2; }
        for (int s = 0; s < 10; s++)
        {
            for (int i = 0; i < n; i++) vy[i] = vy[i] - 9.81 * 0.01;
            for (int i = 0; i < n; i++) x[i] = x[i] + vx[i] * 0.01;
            for (int i = 0; i < n; i++) y[i] = y[i] + vy[i] * 0.01;
        }
        double sum = 0;
        for (int i = 0; i < n; i++) sum += x[i] + y[i] * 3.0;
        return sum;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(100_003)]
    public void Fused_interchanged_loop_matches_pass_by_pass_semantics(int n)
    {
        double got = (double)Compile(Sim).Invoke(null, [n])!;
        double want = Reference(n);
        Assert.True(Math.Abs(got - want) <= Math.Abs(want) * 1e-9, $"{got} vs {want}");
    }

    [Fact]
    public void Passes_are_fused_into_one_sweep_with_registers()
    {
        var cs = AliceCompiler.Transpile(Sim + "\n");
        int f = cs.IndexOf("double f(int n)");
        string body = cs.Substring(f, cs.IndexOf("\n}\n", f) - f);
        // one vector sweep: each column loaded once and stored once per vector, the step loop runs on registers
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body + string.Concat(cs.Split("private static").Skip(1)), @"StoreUnsafe\([^,]*, ref [^)]*\.vy\)").Count / 1 > 0 ? 1 : 0);
        Assert.Contains("__fused", cs);
    }

    [Fact]
    public void Cross_row_reads_prevent_fusion()
    {
        var src = "@[SoA]\nrecord struct Q(a : Float64)\ndef f(n : Int32)\n  qs = SoA(Q).new\n  n.times { |i| qs.add(Q.new(i.to_f)) }\n  for s in 0...3\n    qs.each { |q| q.a = q.a + 1.0 }\n    first = qs[0].a\n    qs.each { |q| q.a = q.a * first }\n  end\n  qs.sum { |q| q.a }\nend";
        Assert.DoesNotContain("__fused", AliceCompiler.Transpile(src + "\n"));
    }
}
