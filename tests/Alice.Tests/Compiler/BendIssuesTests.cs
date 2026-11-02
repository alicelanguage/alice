using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Compiler issues found while porting the bend2 workloads (benchmarks/bend2/README.md "Compiler work queued", and
/// bench2's NOTES): each repro must compile and print what the workaround printed.
/// </summary>
[Collection("console")]
public class BendIssuesTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        { "compound wrapping assignment", "x = 1_u32\nx &+= 4_294_967_295_u32\nputs x\ny = 3_u32\ny &-= 4_u32\nputs y\nz = 65536_u32\nz &*= 65536_u32\nputs z\n", "0\n4294967295\n0" },
        { "UInt32 literal first assigned in an if body", "def f(m : Int32) : UInt32\n  if m > 1\n    w = 7_u32\n    return w\n  end\n  0_u32\nend\nputs f(3)\nputs f(0)\n", "7\n0" },
        { "enum constructor as an argument of another", "enum T\n  L(v : UInt32)\n  N(l : T, r : T)\nend\ndef s(t : T) : UInt32\n  case t\n  when T::L(v) then v\n  when T::N(l, r) then s(l) &+ s(r)\n  end\nend\na = 2_u32\nputs s(T::N(T::L(a), T::L(a)))\nputs s(T::N(T::N(T::L(1_u32), T::L(a)), T::L(a)))\n", "4\n5" },
        { "nullary variant as an argument", "enum M\n  Free\n  Node(l : M, r : M)\nend\ndef n(m : M) : Int32\n  case m\n  when M::Free then 1\n  when M::Node(l, r) then n(l) + n(r)\n  end\nend\nr = M::Node(M::Free, M::Free)\nputs n(M::Node(M::Free, r))\n", "3" },
        { "top-level UInt32 array constant", "RK = [256_u32, 5394_u32]\ndef k(i : Int32) : UInt32\n  RK[i] &+ 1_u32\nend\nputs k(1)\n", "5395" },
        { "UInt32 shifted by UInt32", "x = 256_u32\ns = 4_u32\nputs x >> s\nputs x << s\n", "16\n4096" },
        { "local initialised with a nullary variant has the enum type", "enum M\n  Free\n  Node(l : M, r : M)\nend\nr = M::Free\nf : M = M::Free\nr = M::Node(r, f)\nputs r.is_a?(M::Node)\n", "true" },
        { "val with a constant-style name", "val CUT = 3\ndef deep(d : Int32) : Int32\n  return 1 if d == CUT\n  deep(d - 1) + 1\nend\nputs deep(5)\n", "3" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Bend_repros_compile_and_run(string name, string src, string expected)
    {
        Assert.True(expected == Run(src), name);
    }

    [Fact]
    public void String_to_u32_says_what_is_missing()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("puts \"7\".to_u32\n"));
        Assert.Contains("to_u32", ex.Message);
        Assert.Contains("String", ex.Message);
        Assert.DoesNotContain("type parameter", ex.Message);
        Assert.DoesNotContain("INumber", ex.Message);
    }

    [Fact]
    public void A_local_named_nil_is_a_reserved_word_error()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("nil = 3\nputs 1\n"));
        Assert.Contains("reserved word", ex.Message);
        Assert.Equal(1, ex.Diagnostics[0].Line);
    }
}
