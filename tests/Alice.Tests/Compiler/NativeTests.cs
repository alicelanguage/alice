using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>`@[Native]` functions: compiled to C with clang -O3, called through function pointers, managed fallback.</summary>
public class NativeTests
{
    const string Src = """
        @[Native]
        def dot(a : Array(Float64), b : Array(Float64)) : Float64
          s = 0.0
          for i in 0...a.size
            s += a[i] * b[i]
          end
          s
        end

        @[Native]
        def collatz_steps(n : Int64) : Int32
          steps = 0
          x = n
          while x != 1
            if x % 2 == 0
              x = x / 2
            else
              x = 3 * x + 1
            end
            steps += 1
          end
          steps
        end

        @[Native]
        def scale_add(xs : Array(Float64), k : Float64)
          for i in 0...xs.size
            xs[i] = xs[i] * k + Math.Sqrt(4.0)
          end
        end

        @[Native]
        def hyp(x : Float64, y : Float64) : Float64 = Math.Sqrt(sq(x) + sq(y))

        @[Native]
        def sq(v : Float64) : Float64 = v * v
        """;

    static (Type prog, string? dir) Build(bool withNative)
    {
        var name = "nat" + Guid.NewGuid().ToString("N")[..8];
        var r = AliceCompiler.CompileNative(Src, name, withNative);
        var asm = AliceCompiler.Load(r.Image);
        return (asm.GetType("Program")!, r.NativeDir);
    }

    static void Check(Type prog)
    {
        double[] a = [1, 2, 3], b = [4, 5, 6];
        Assert.Equal(32.0, prog.GetMethod("dot")!.Invoke(null, [a, b]));
        Assert.Equal(111, prog.GetMethod("collatz_steps")!.Invoke(null, [27L]));
        var xs = new[] { 1.0, 2.0 };
        prog.GetMethod("scale_add")!.Invoke(null, [xs, 3.0]);
        Assert.Equal(new[] { 5.0, 8.0 }, xs);
        Assert.Equal(5.0, prog.GetMethod("hyp")!.Invoke(null, [3.0, 4.0]));
    }

    [Fact]
    public void Native_library_is_built_and_used()
    {
        var (prog, dir) = Build(withNative: true);
        Assert.NotNull(dir);
        Check(prog);
        Assert.True((bool)prog.GetMethod("__alice_native_loaded", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!);
    }

    [Fact]
    public void Without_the_library_the_managed_fallback_gives_the_same_results()
    {
        var (prog, _) = Build(withNative: false);
        Check(prog);
        Assert.False((bool)prog.GetMethod("__alice_native_loaded", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!);
    }

    [Fact]
    public void Unsupported_constructs_are_clear_errors()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("@[Native]\ndef f(s : String) : Int32\n  s.size\nend\n"));
        Assert.Contains("@[Native]", ex.Message);
    }
}
