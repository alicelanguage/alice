using Alice.Compiler;
using Alice.Compiler.Backend.Il;

namespace Alice.Tests.Backends;

/// <summary>Review round 1 (review-r1.md) IL miscompiles, pinned against the C# backend as oracle.</summary>
[Collection("console")]
public class IlFixR1Tests
{
    static void Differential(string src, string expected)
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "il", true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        var decode = AirToIlTests.DecodeCheck(res.Pe!);
        Assert.True(decode.Count == 0, "IL decode check: " + string.Join(", ", decode));
        var errors = AirToIlTests.IlVerify(res.Pe!);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
        string cs = AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), []);
        string il = AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []);
        Assert.Equal(expected, cs.Replace("\r\n", "\n"));
        Assert.Equal(cs, il);
    }

    // F1: a typed wide literal keeps its type; the op is not demoted to the narrow operand.
    [Fact]
    public void F1_int32_plus_int64_literal_computes_in_int64() =>
        Differential("w = [2147483647][0]\nputs (w + 1_i64)\nputs 2147483647_i64 + w\nputs (w + 1_i64).class\n",
            "2147483648\n4294967294\nSystem.Int64\n");

    [Fact]
    public void F1_uint32_plus_uint64_literal_computes_in_uint64() =>
        Differential("q = [5_u32][0]\nputs q + 4294967295_u64\nputs (q + 4294967295_u64).class\n",
            "4294967300\nSystem.UInt64\n");

    [Fact]
    public void F1_int8_plus_int64_literal_computes_in_int64() =>
        Differential("b = [100_i8][0]\nputs b + 2147483647_i64\nputs (b + 1_i64).class\n",
            "2147483747\nSystem.Int64\n");

    // F2: Float64 constants stay ldc.r8 (an ldc.r4 encoding made `1.0 / 3.0` single precision).
    [Fact]
    public void F2_float64_constants_keep_double_precision() =>
        Differential("puts 1.0 / 3.0\nputs (1.0 / 3.0 == 0.3333333333333333)\na = 1.0 / 3.0\nputs a\n",
            "0.3333333333333333\ntrue\n0.3333333333333333\n");

    // F7: compound assignment through a non-array indexer evaluates receiver and index once.
    [Fact]
    public void F7_compound_indexer_assignment_evaluates_index_once() =>
        Differential("CNT = [0]\ndef idx : Int32\n  CNT[0] += 10\n  1\nend\nxs = [1_i8, 2_i8]\nxs[idx - 1] += 1_i8\nputs xs[0]\nputs CNT[0]\n"
            + "ls = List(Int32).new\nls.add(5)\nls[idx - 1] += 3\nputs ls[0]\nputs CNT[0]\n",
            "2\n10\n8\n20\n");
}
