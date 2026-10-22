using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// An owned array comes from `Own(T).Rent(n)`, so the JIT cannot see that its length is `n` and keeps a bounds check
/// on every `buf[k]` in `for k in 0...n` (C#'s `new int[n]` does not have that problem). When `n` is a local or
/// parameter that never changes and the array is never reassigned, the loop bound is rewritten to `buf.Length`,
/// which is the same value and lets the JIT drop the checks.
/// </summary>
public class OwnedBoundsTests
{
    [Fact]
    public void Loop_bound_of_a_parameter_length_becomes_the_owned_array_length()
    {
        var cs = AliceCompiler.Transpile("""
            def f(w : Int32) : Int32
              buf = Array(Int32).new(w)
              for k in 0...w
                buf[k] = k * 3
              end
              s = 0
              for k in 1...w
                s += buf[k] - buf[k - 1]
              end
              s
            end
            """);
        Assert.Contains("< buf.Length;", cs);
        Assert.DoesNotContain("k_1 < w;", cs);
    }

    [Fact]
    public void A_length_that_changes_keeps_its_loop_bound()
    {
        var cs = AliceCompiler.Transpile("""
            def f(w : Int32) : Int32
              buf = Array(Int32).new(w)
              w = w - 1
              for k in 0...w
                buf[k] = k
              end
              buf[0]
            end
            """);
        Assert.DoesNotContain("buf.Length", cs);
    }

    [Fact]
    public void A_reassigned_array_keeps_its_loop_bound()
    {
        var cs = AliceCompiler.Transpile("""
            def f(w : Int32) : Int32
              buf = Array(Int32).new(w)
              buf = Array(Int32).new(w + 1)
              for k in 0...w
                buf[k] = k
              end
              buf[0]
            end
            """);
        Assert.DoesNotContain("buf.Length", cs);
    }

    [Fact]
    public void A_getter_length_keeps_its_loop_bound()
    {
        var cs = AliceCompiler.Transpile("""
            class Box
              getter w : Int32
              def initialize(@w : Int32)
              end
              def go : Int32
                buf = Array(Int32).new(w)
                for k in 0...w
                  buf[k] = k
                end
                buf[0]
              end
            end
            """);
        Assert.DoesNotContain("buf.Length", cs);
    }
}
