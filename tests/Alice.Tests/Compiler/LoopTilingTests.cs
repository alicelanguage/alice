using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Polyhedral-lite loop optimization (ADR 0025): affine dependence analysis of counted nests over arrays, loop
/// interchange for stride-1 inner access, and cache tiling, each only when the dependences prove it legal. The
/// transformed nest runs behind a runtime guard (in bounds, no aliasing); otherwise the original nest runs.
/// </summary>
[Collection("console")]
public class LoopTilingTests
{
    const string MatMul = """
        def matmul(a : Array(Float64), b : Array(Float64), c : Array(Float64), n : Int32)
          for i in 0...n
            for j in 0...n
              for k in 0...n
                c[i * n + j] += a[i * n + k] * b[k * n + j]
              end
            end
          end
        end
        """;

    const string Transpose = """
        def transpose(a : Array(Float64), t : Array(Float64), n : Int32)
          for i in 0...n
            for j in 0...n
              t[j * n + i] = a[i * n + j]
            end
          end
        end
        """;

    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Fact]
    public void Matmul_is_interchanged_to_ikj_and_tiled()
    {
        var cs = AliceCompiler.Transpile(MatMul);
        Assert.Contains("for (int i_1_t = 0;", cs);
        Assert.Contains("for (int k_3_t = 0;", cs);
        Assert.Contains("for (int j_2_t = 0;", cs);
        // point loops in i, k, j order: j innermost, stride 1 on b and c
        int i = cs.IndexOf("for (int i_1 = i_1_t;"), k = cs.IndexOf("for (int k_3 = k_3_t;"), j = cs.IndexOf("for (int j_2 = j_2_t;");
        Assert.True(i > 0 && k > i && j > k, cs);
        Assert.Contains("ReferenceEquals(c, a)", cs);
        // the original nest stays as the fallback
        Assert.Contains("for (int k_3 = 0; k_3 < n;", cs);
    }

    [Fact]
    public void Transpose_is_tiled()
    {
        var cs = AliceCompiler.Transpile(Transpose);
        Assert.Contains("for (int i_1_t = 0;", cs);
        Assert.Contains("for (int j_2 = j_2_t;", cs);
    }

    [Fact]
    public void A_carried_dependence_with_negative_direction_blocks_interchange_and_tiling()
    {
        // a[i][j] depends on a[i-1][j+1]: distance (1, -1). Interchange and tiling are both illegal.
        var cs = AliceCompiler.Transpile("""
            def f(a : Array(Float64), n : Int32)
              for i in 1...n
                for j in 0...n - 1
                  a[i * n + j] = a[(i - 1) * n + j + 1] * 0.5
                end
              end
            end
            """);
        Assert.DoesNotContain("_t = ", cs);
    }

    [Fact]
    public void Integer_arithmetic_that_may_overflow_is_not_reordered()
    {
        var cs = AliceCompiler.Transpile("""
            def f(a : Array(Int32), b : Array(Int32), c : Array(Int32), n : Int32)
              for i in 0...n
                for j in 0...n
                  for k in 0...n
                    c[i * n + j] += a[i * n + k] * b[k * n + j]
                  end
                end
              end
            end
            """);
        Assert.DoesNotContain("_t = ", cs);
    }

    [Fact]
    public void A_two_deep_jacobi_stencil_is_not_rewritten()
    {
        // every access is already stride 1 in j and there is no reuse across a third loop: nothing to gain
        var cs = AliceCompiler.Transpile("""
            def f(a : Array(Float64), o : Array(Float64), n : Int32)
              for i in 1...n - 1
                for j in 1...n - 1
                  o[i * n + j] = 0.25 * a[(i - 1) * n + j] + 0.25 * a[(i + 1) * n + j] + 0.25 * a[i * n + j - 1] + 0.25 * a[i * n + j + 1]
                end
              end
            end
            """);
        Assert.DoesNotContain("_t = ", cs);
    }

    [Fact]
    public void Column_walk_is_interchanged_without_tiling()
    {
        var cs = AliceCompiler.Transpile("""
            def f(a : Array(Float64), o : Array(Float64), n : Int32)
              for j in 0...n
                for i in 0...n
                  o[i * n + j] = a[i * n + j] * 2.0
                end
              end
            end
            """);
        int i = cs.IndexOf("for (int i_2 = 0;"), j = cs.IndexOf("for (int j_1 = 0;");
        Assert.True(i > 0 && j > i, cs);   // first (fast) version: i outer, j inner
        Assert.DoesNotContain("_t = ", cs);
    }

    static string Program(string kernel, string call) => kernel + "\n" + """
        def naive(a : Array(Float64), b : Array(Float64), c : Array(Float64), n : Int32)
          i = 0
          while i < n
            j = 0
            while j < n
              k = 0
              while k < n
                c[i * n + j] += a[i * n + k] * b[k * n + j]
                k += 1
              end
              j += 1
            end
            i += 1
          end
        end

        def fill(r : System.Random, n : Int32) : Array(Float64)
          xs = Array(Float64).new(n)
          i = 0
          while i < n
            xs[i] = r.NextDouble - 0.5
            i += 1
          end
          xs
        end

        r = System.Random.new(7)
        ok = true
        [1, 5, 37, 64, 70, 130].each do |n|
        """ + "\n" + call + "\nend\nputs ok\n";

    [Fact]
    public void Tiled_matmul_matches_the_untiled_result_bit_for_bit()
    {
        var src = Program(MatMul, """
              a = fill(r, n * n)
              b = fill(r, n * n)
              c1 = fill(r, n * n)
              c2 = Array(Float64).new(n * n)
              i = 0
              while i < n * n
                c2[i] = c1[i]
                i += 1
              end
              matmul(a, b, c1, n)
              naive(a, b, c2, n)
              i = 0
              while i < n * n
                ok = false if System.BitConverter.DoubleToInt64Bits(c1[i]) != System.BitConverter.DoubleToInt64Bits(c2[i])
                i += 1
              end
            """);
        Assert.Equal("true", Run(src));
    }

    [Fact]
    public void Aliased_matmul_runs_the_original_nest()
    {
        var src = Program(MatMul, """
              a1 = fill(r, n * n)
              a2 = Array(Float64).new(n * n)
              i = 0
              while i < n * n
                a2[i] = a1[i]
                i += 1
              end
              same = a1   # AL0217 rejects matmul(a1, a1, a1); the runtime guard still sees one array
              matmul(a1, a1, same, n)
              same2 = a2
              naive(a2, a2, same2, n)
              i = 0
              while i < n * n
                ok = false if System.BitConverter.DoubleToInt64Bits(a1[i]) != System.BitConverter.DoubleToInt64Bits(a2[i])
                i += 1
              end
            """);
        Assert.Equal("true", Run(src));
    }

    [Fact]
    public void Tiled_transpose_matches_and_out_of_bounds_still_raises()
    {
        var src = Program(Transpose, """
              a = fill(r, n * n)
              t = Array(Float64).new(n * n)
              transpose(a, t, n)
              i = 0
              while i < n
                j = 0
                while j < n
                  ok = false if t[j * n + i] != a[i * n + j]
                  j += 1
                end
                i += 1
              end
            """) + """
            caught = false
            begin
              transpose(Array(Float64).new(4), Array(Float64).new(9), 3)
            rescue e : System.IndexOutOfRangeException
              caught = true
            end
            puts caught
            """;
        Assert.Equal("true\ntrue", Run(src));
    }
}
