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

public class NativeSpecializationTests
{
    [Fact]
    public void Small_and_large_inputs_and_aliasing_all_match_managed()
    {
        var src = "@[Native]\ndef axpy(a : Float64, x : Array(Float64), y : Array(Float64))\n  for i in 0...x.size\n    y[i] = a * x[i] + y[i]\n  end\nend\n";
        var nat = AliceCompiler.Load(AliceCompiler.CompileNative(src, "sp" + Guid.NewGuid().ToString("N")[..8], true).Image).GetType("Program")!.GetMethod("axpy")!;
        foreach (int n in new[] { 0, 1, 5, 64, 65, 1000 })
        {
            var x = Enumerable.Range(0, n).Select(i => i * 0.5).ToArray();
            var y = Enumerable.Range(0, n).Select(i => 1.0 + i).ToArray();
            var expect = x.Zip(y, (a, b) => Math.FusedMultiplyAdd(2.0, a, b)).ToArray();
            nat.Invoke(null, [2.0, x, y]);
            for (int i = 0; i < n; i++) Assert.Equal(expect[i], y[i], 12);
        }
        var same = new[] { 1.0, 2.0, 3.0 };
        nat.Invoke(null, [2.0, same, same]);     // aliased: managed fallback, y[i] = 2*y[i] + y[i]
        Assert.Equal(new[] { 3.0, 6.0, 9.0 }, same);
    }
}

public class NativeCallOverheadTests
{
    [Fact]
    public void Leaf_native_functions_skip_the_gc_transition_loops_do_not()
    {
        var cs = AliceCompiler.Transpile("@[Native]\ndef hyp(a : Float64, b : Float64) : Float64\n  Math.Sqrt(a * a + b * b)\nend\n@[Native]\ndef total(xs : Array(Float64)) : Float64\n  s = 0.0\n  for i in 0...xs.size\n    s += xs[i]\n  end\n  s\nend\n@[Native(fast)]\ndef total2(xs : Array(Float64)) : Float64\n  s = 0.0\n  for i in 0...xs.size\n    s += xs[i]\n  end\n  s\nend\n");
        Assert.Contains("delegate* unmanaged[SuppressGCTransition]<double, double, double> __np_hyp", cs);
        Assert.Contains("delegate* unmanaged<double*, int, double> __np_total ", cs);
        Assert.Contains("delegate* unmanaged[SuppressGCTransition]<double*, int, double> __np_total2", cs);
    }

    [Fact]
    public void Lib_funs_fast_attribute_and_one_byte_bools()
    {
        var cs = AliceCompiler.Transpile("lib LibX\n  @[Fast]\n  fun add1(x : Int32) : Int32\n  fun flag(b : Bool) : Bool\nend\n");
        Assert.Contains("[global::System.Runtime.InteropServices.SuppressGCTransition]", cs);
        // blittable: a C bool crosses as one byte, so the call needs no marshalling stub and the JIT can inline it
        Assert.Contains("private static extern byte __raw_flag(byte b);", cs);
        Assert.Contains("public static bool flag(bool b) => __raw_flag(b ? (byte)1 : (byte)0) != 0;", cs);
        Assert.DoesNotContain("MarshalAs", cs);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, "SuppressGCTransition"));
    }

    [Fact]
    public void Fast_bool_funs_stay_blittable()
    {
        // SuppressGCTransition needs a blittable signature: a marshalled bool would fail at the first call
        var cs = AliceCompiler.Transpile("lib LibX\n  @[Fast]\n  fun flag(b : Bool) : Bool\nend\n");
        Assert.Matches(@"SuppressGCTransition\]\s*private static extern byte __raw_flag\(byte b\);", cs);
    }
}

/// <summary>`@[Native]` C keeps Alice's safety: bounds checks, overflow traps, collision-free generated names.</summary>
public class NativeSafetyTests
{
    static string C(string src) => AliceCompiler.TranspileFull([new SourceFile("main.al", src)], null, "t").C!;

    [Fact]
    public void Array_accesses_are_bounds_checked()
    {
        var c = C("@[Native]\ndef get(a : Array(Int32), xs : Array(Int32), i : Int32) : Int32\n  a[i] + xs[i]\nend\n");
        Assert.Contains("xs[alice_ix(i, xs__len)]", c);
        Assert.Contains("a[alice_ix(i, alice__N)]", c);   // the specialized worker checks against its constant length
    }

    [Fact]
    public void Integer_arithmetic_traps_on_overflow()
    {
        var c = C("@[Native]\ndef f(a : Int32, b : Int64) : Int64\n  x = a * a\n  x += 1\n  b + x - (-b)\nend\n");
        Assert.Contains("alice_mul_i32(a, a)", c);
        Assert.Contains("alice_add_i32(l_x, 1)", c);
        Assert.Contains("__builtin_add_overflow", c);
        Assert.DoesNotContain("(a * a)", c);
    }

    [Fact]
    public void Length_substitution_only_touches_the_driver_array()
    {
        // `a__len` is a suffix of `ba__len`: only the whole identifier may become the specialized constant
        var c = C("@[Native]\ndef f(a : Array(Int32), ba : Array(Int32)) : Int32\n  a.size * 100 + ba.size\nend\n");
        Assert.Contains("alice_add_i32(alice_mul_i32(alice__N, 100), ba__len)", c);
    }

    [Fact]
    public void Parameter_names_never_collide_with_the_generated_driver()
    {
        var src = "@[Native]\ndef f(a : Array(Int32), r : Int32, k : Int32) : Int32\n  a[0] + r * 10 + k\nend\n";
        var nat = AliceCompiler.Load(AliceCompiler.CompileNative(src, "nc" + Guid.NewGuid().ToString("N")[..8], true).Image).GetType("Program")!.GetMethod("f")!;
        Assert.True((bool)nat.DeclaringType!.GetMethod("__alice_native_loaded")!.Invoke(null, null)!);   // the C ran, not the managed fallback
        Assert.Equal(1 + 70 + 3, nat.Invoke(null, [new[] { 1, 2 }, 7, 3]));
        var ba = "@[Native]\ndef g(a : Array(Int32), ba : Array(Int32)) : Int32\n  a.size * 100 + ba.size\nend\n";
        var g = AliceCompiler.Load(AliceCompiler.CompileNative(ba, "nb" + Guid.NewGuid().ToString("N")[..8], true).Image).GetType("Program")!.GetMethod("g")!;
        Assert.Equal(205, g.Invoke(null, [new int[2], new int[5]]));
    }
}
