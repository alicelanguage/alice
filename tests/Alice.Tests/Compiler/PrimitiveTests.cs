using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Numeric primitives: exact semantics so benchmark code needs no BCL workarounds.</summary>
[Collection("console")]
public class PrimitiveTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Theory]
    [InlineData("puts((-5).abs)", "5")]
    [InlineData("puts(-5.abs)", "5")]
    [InlineData("puts(-7.5.abs)", "7.5")]
    [InlineData("x = -3\nputs x.abs", "3")]
    [InlineData("puts(-2 ** 2)", "-4")]
    public void Negative_literal_receivers_keep_their_sign(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts(-7 / 2)", "-4")]
    [InlineData("puts(7 / -2)", "-4")]
    [InlineData("puts(7 / 2)", "3")]
    [InlineData("puts(-7 % 3)", "2")]
    [InlineData("puts(7 % -3)", "-2")]
    [InlineData("puts(-7_i64 / 2_i64)", "-4")]
    [InlineData("puts(-7_i64 % 3_i64)", "2")]
    [InlineData("puts(-7.5 % 2.0)", "0.5")]
    [InlineData("x = -7\nx /= 2\nputs x", "-4")]
    [InlineData("x = -7\nx %= 3\nputs x", "2")]
    [InlineData("a = [-7]\na[0] %= 3\nputs a[0]", "2")]
    [InlineData("puts(-7.tdiv(2))", "-3")]
    [InlineData("puts(-7.remainder(3))", "-1")]
    [InlineData("q, r = -7.divmod(2)\nputs q * 2 + r", "-7")]
    public void Signed_division_is_floored_like_divmod(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Fact]
    public void Integer_power_with_negative_exponent_raises()
        => Assert.ThrowsAny<Exception>(() => Run("e = -1\nputs 2 ** e"));

    [Theory]
    [InlineData("puts 255.to_u8", "255")]
    [InlineData("puts 300.to_u8!", "44")]
    [InlineData("puts(-1.to_u32!)", "4294967295")]
    [InlineData("puts 2.9.to_i32", "2")]
    [InlineData("puts(-2.9.to_i32)", "-2")]
    [InlineData("puts 3_000_000_000_i64.to_i32!", "-1294967296")]
    [InlineData("puts 7.to_f64 / 2", "3.5")]
    [InlineData("puts 200_u8.to_i64 + 1", "201")]
    public void Sized_conversions_are_checked_or_wrapping(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 300.to_u8")]
    [InlineData("puts 3_000_000_000_i64.to_i32")]
    [InlineData("puts Float64::NAN.to_i32")]
    [InlineData("puts 1e300.to_i64")]
    public void Out_of_range_conversions_raise(string src)
        => Assert.ThrowsAny<Exception>(() => Run(src));

    [Theory]
    [InlineData("puts 255.popcount", "8")]
    [InlineData("puts 8.trailing_zeros_count", "3")]
    [InlineData("puts 1.leading_zeros_count", "31")]
    [InlineData("puts 1_i64.leading_zeros_count", "63")]
    [InlineData("puts 5.bit_length", "3")]
    [InlineData("puts 0.bit_length", "0")]
    [InlineData("puts (6 ^ 3) | (1 << 4) & 0xFF", "21")]
    [InlineData("puts 7.fdiv(2)", "3.5")]
    [InlineData("puts(-5.sign)", "-1")]
    [InlineData("puts 0.sign", "0")]
    [InlineData("puts 2.5.sign", "1")]
    [InlineData("puts Int32::MAX", "2147483647")]
    [InlineData("puts UInt8::MAX", "255")]
    [InlineData("puts Int64::MIN", "-9223372036854775808")]
    [InlineData("puts Math.sqrt(16.0)", "4.0")]
    [InlineData("puts Math.atan2(0.0, 1.0)", "0.0")]
    public void Integer_bits_and_math_helpers(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 2.5.round", "3.0")]
    [InlineData("puts(-2.5.round)", "-3.0")]
    [InlineData("puts 1.005.round(2)", "1.01")]
    [InlineData("puts 0.29.floor(2)", "0.29")]
    [InlineData("puts 0.57.ceil(2)", "0.57")]
    [InlineData("puts 2.675.round(2)", "2.68")]
    public void Rounding_is_half_away_from_zero_on_the_printed_digits(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 2.0", "2.0")]
    [InlineData("puts 0.1 + 0.2", "0.30000000000000004")]
    [InlineData("puts 1e-7", "1.0e-07")]
    [InlineData("puts 0.0001", "0.0001")]
    [InlineData("puts 1e15", "1000000000000000.0")]
    [InlineData("puts 1e16", "1.0e+16")]
    [InlineData("puts 1.5e300", "1.5e+300")]
    [InlineData("puts(-0.0)", "-0.0")]
    [InlineData("puts 1.0 / 0.0", "Infinity")]
    [InlineData("puts(-1.0 / 0.0)", "-Infinity")]
    [InlineData("puts 0.0 / 0.0", "NaN")]
    [InlineData("puts 1.5_f32", "1.5")]
    [InlineData("puts 0.1_f32", "0.1")]
    [InlineData("puts \"x=#{2.0} y=#{0.5_f32}\"", "x=2.0 y=0.5")]
    [InlineData("puts 3.0.to_s", "3.0")]
    [InlineData("puts [1.0, 2.5].inspect", "[1.0, 2.5]")]
    [InlineData("puts [1.0, 2.5]", "[1.0, 2.5]")]
    public void Floats_print_shortest_round_trip_with_a_decimal_point(string src, string expected)
        => Assert.Equal(expected, Run(src));

    // ---- edge cases: the language must neither crash nor print a wrong value ----

    [Theory]
    [InlineData("puts 5e-324", "5.0e-324")]                         // smallest subnormal
    [InlineData("puts 2.2250738585072014e-308", "2.2250738585072014e-308")]
    [InlineData("puts 1.7976931348623157e308", "1.7976931348623157e+308")]
    [InlineData("puts 1.7976931348623157e308 * 10.0", "Infinity")]
    [InlineData("puts 9007199254740993.0", "9007199254740992.0")]   // 2^53 + 1 is not representable
    [InlineData("puts 123456789012345.6", "123456789012345.6")]
    [InlineData("puts 0.1 * 3", "0.30000000000000004")]
    [InlineData("puts(0.1 + 0.2 == 0.3)", "false")]
    [InlineData("puts 100.0", "100.0")]
    [InlineData("puts 1.0e-5", "1.0e-05")]
    [InlineData("puts 0.001", "0.001")]
    [InlineData("puts 12345678901234567.0", "1.2345678901234568e+16")]
    [InlineData("puts 3.4028235e38_f32", "3.4028235e+38")]
    [InlineData("puts 1.0e-45_f32", "1.0e-45")]
    [InlineData("puts 16777217.0_f32", "16777216.0")]
    public void Float_extremes_print_exactly(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("n = 0.0 / 0.0\nputs(n == n)", "false")]
    [InlineData("n = 0.0 / 0.0\nputs(n != n)", "true")]
    [InlineData("n = 0.0 / 0.0\nputs n.nan?", "true")]
    [InlineData("n = 0.0 / 0.0\nputs [n, 1.0].max.nan?", "true")]
    [InlineData("puts(0.0 == -0.0)", "true")]
    [InlineData("puts(1.0 / -0.0)", "-Infinity")]
    [InlineData("puts(-0.0 * 5.0)", "-0.0")]
    [InlineData("i = 1.0 / 0.0\nputs i - i", "NaN")]
    [InlineData("i = 1.0 / 0.0\nputs i.infinite?", "true")]
    [InlineData("puts 5.0 % (1.0 / 0.0)", "5.0")]
    [InlineData("puts(-5.0 % 3.0)", "1.0")]
    [InlineData("puts 5.0 % -3.0", "-1.0")]
    [InlineData("puts 0.0.sqrt", "0.0")]
    [InlineData("x = -1.0\nputs x.sqrt.nan?", "true")]
    [InlineData("puts 2.0 ** 1024", "Infinity")]
    [InlineData("puts 2.0 ** -1", "0.5")]
    [InlineData("puts 0.0 ** 0", "1.0")]
    public void Nan_infinity_and_signed_zero_follow_ieee754(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 0.5.round", "1.0")]
    [InlineData("puts 1.5.round", "2.0")]
    [InlineData("puts(-0.5.round)", "-1.0")]
    [InlineData("puts 0.49999999999999994.round", "0.0")]           // classic floor(x + 0.5) bug
    [InlineData("puts 4503599627370497.0.round", "4503599627370497.0")]
    [InlineData("puts 1e20.round(2)", "1.0e+20")]
    [InlineData("puts 1.23456.round(20)", "1.23456")]
    [InlineData("puts 1234.5678.round(-2)", "1200.0")]
    [InlineData("puts(-1.2345.floor(2))", "-1.24")]
    [InlineData("puts(-1.2345.ceil(2))", "-1.23")]
    [InlineData("puts(-0.001.round(2))", "-0.0")]
    [InlineData("puts (0.0 / 0.0).round(2).nan?", "true")]
    [InlineData("puts 2.5.floor", "2.0")]
    [InlineData("puts(-2.5.floor)", "-3.0")]
    [InlineData("puts(-2.5.ceil)", "-2.0")]
    [InlineData("puts(-2.5.truncate)", "-2.0")]
    public void Rounding_edges(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 2147483647.5.to_i64", "2147483647")]
    [InlineData("puts(-2147483648.9.to_i32)", "-2147483648")]
    [InlineData("puts 255.9.to_u8", "255")]
    [InlineData("puts(-0.9.to_u8)", "0")]
    [InlineData("puts 1e300.to_i32!", "2147483647")]               // wrapping conversions saturate floats
    [InlineData("puts (0.0 / 0.0).to_i32!", "0")]
    [InlineData("puts 9007199254740993_i64.to_f64", "9007199254740992.0")]
    [InlineData("puts Int64::MAX.to_f64", "9.223372036854776e+18")]
    [InlineData("puts 1e39.to_f32", "Infinity")]
    [InlineData("puts UInt64::MAX", "18446744073709551615")]
    [InlineData("puts Int32::MIN.to_i64.abs", "2147483648")]
    public void Conversion_boundaries(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 2.1474836485e9.to_i32")]
    [InlineData("puts(-1.to_u8)")]
    [InlineData("puts (1.0 / 0.0).to_i64")]
    [InlineData("x = Int32::MAX\nputs x + 1")]                       // runtime overflow is checked
    [InlineData("x = Int64::MIN\nputs x - 1")]
    [InlineData("x = Int32::MIN\nputs x.abs")]
    [InlineData("x = Int32::MIN\ny = -1\nputs x / y")]
    [InlineData("x = Int32::MIN\ny = -1\nputs x % y")]
    [InlineData("x = 0\nputs 1 / x")]
    [InlineData("x = 0\nputs 1 % x")]
    [InlineData("puts 3 ** 40")]
    [InlineData("e = 64\nputs 2_i64 ** e")]
    public void Integer_traps_raise_instead_of_wrapping(string src)
        => Assert.ThrowsAny<Exception>(() => Run(src));

    [Theory]
    [InlineData("puts Int32::MIN / 2", "-1073741824")]
    [InlineData("puts(-1 / 2)", "-1")]
    [InlineData("puts(1 / -2)", "-1")]
    [InlineData("puts(-1 % 2)", "1")]
    [InlineData("puts(-6 / 3)", "-2")]
    [InlineData("puts(-6 % 3)", "0")]
    [InlineData("puts 2 ** 30", "1073741824")]
    [InlineData("puts 2_i64 ** 62", "4611686018427387904")]
    [InlineData("puts(-2 ** 3)", "-8")]
    [InlineData("puts 0 ** 0", "1")]
    [InlineData("x = -1\nputs x.bit_length", "0")]
    [InlineData("x = -256\nputs x.bit_length", "8")]
    [InlineData("x = -1\nputs x.popcount", "32")]
    [InlineData("puts 0.trailing_zeros_count", "32")]
    [InlineData("puts 255_u8.leading_zeros_count", "0")]
    [InlineData("puts 7_u32 / 2_u32", "3")]
    [InlineData("puts 7_u32 % 2_u32", "1")]
    [InlineData("puts 1 << 31", "-2147483648")]
    [InlineData("puts(-8 >> 1)", "-4")]
    public void Integer_edges(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts \"1.5\".to_f + 1", "2.5")]
    [InlineData("puts \"  42 \".to_i", "42")]
    [InlineData("puts \"1e3\".to_f", "1000.0")]
    [InlineData("puts \"-0\".to_f", "-0.0")]
    [InlineData("puts \"Infinity\".to_f", "Infinity")]
    [InlineData("puts 2.0.to_s + \"!\"", "2.0!")]
    [InlineData("puts \"#{-0.0}|#{1.0 / 0.0}|#{1e-7}\"", "-0.0|Infinity|1.0e-07")]
    [InlineData("puts [0.1, -0.0, 1e100].inspect", "[0.1, -0.0, 1.0e+100]")]
    [InlineData("p 2.0", "2.0")]
    [InlineData("print 2.0\nputs", "2.0")]
    [InlineData("h = {\"a\" => 1.0}\nputs h[\"a\"]", "1.0")]
    public void Float_text_round_trips(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("x = Int32::MAX\nputs x &+ 1", "-2147483648")]
    [InlineData("x = Int64::MIN\nputs x &- 1", "9223372036854775807")]
    [InlineData("x = 1_664_525_i64 * 4_000_000_000_i64\nputs x &* 1_000_000_000_000", "4266489396032700416")]
    [InlineData("h = 17\nfor c in [1, 2, 3]\n  h = h &* 31 &+ c\nend\nputs h", "507473")]
    [InlineData("puts 2 &+ 3 &* 4", "14")]
    public void Wrapping_operators(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 1.5.finite?", "true")]
    [InlineData("puts (1.0 / 0.0).finite?", "false")]
    [InlineData("puts (0.0 / 0.0).finite?", "false")]
    public void Finite_predicate(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts 0xFFFF_FFFF_i64", "4294967295")]
    [InlineData("puts 0xFF_u8", "255")]
    [InlineData("puts 0xAB_CD", "43981")]
    [InlineData("puts 0b1010_1010", "170")]
    [InlineData("puts 0x1_f32", "1.0")]
    public void Underscores_in_hex_and_binary_literals(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("puts [3, 1, 2].min", "1")]
    [InlineData("puts [3, 1, 2].max", "3")]
    [InlineData("puts [4].min", "4")]
    [InlineData("puts [1, 2.5].max", "2.5")]
    [InlineData("puts [1_i64, 3].max", "3")]
    [InlineData("n = 0.0 / 0.0\nputs [n, 1.0].min.nan?", "true")]
    [InlineData("i = 0\ndef bump(i : Array(Int32)) : Int32\n  i[0] += 1\n  i[0]\nend\nc = [0]\nputs [bump(c), bump(c), bump(c)].min\nputs c[0]", "1\n3")]
    public void Literal_array_min_max_without_allocation(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("w = \"a\"\nr = case w\nwhen \"a\" then [1, 2]\nelse raise \"bad\"\nend\nputs r.size")]
    [InlineData("w = 1\nr = if w == 1\n  \"one\"\nelse\n  raise \"bad\"\nend\nputs r.size")]
    [InlineData("def f(w : Int32)\n  case w\n  when 1 then 10\n  else raise \"bad\"\n  end\nend\nputs f(1) + 1")]
    public void Raising_branches_do_not_make_a_value_dynamic(string src)
    {
        var cs = AliceCompiler.Transpile(src);
        Assert.DoesNotContain("dynamic", cs);
        Run(src);
    }

    [Fact]
    public void Exclusive_loop_counters_skip_the_overflow_check()
    {
        var cs = AliceCompiler.Transpile("def f(n : Int32) : Int64\n  s = 0_i64\n  for i in 0...n\n    s += i\n  end\n  s\nend\n");
        Assert.Contains("i_1 = unchecked(i_1 + 1)", cs);   // i < n <= Int32::MAX, so i + 1 cannot overflow
    }

    [Fact]
    public void Reassigned_or_inclusive_loop_counters_keep_the_checked_step()
    {
        Assert.DoesNotContain("unchecked", AliceCompiler.Transpile("for i in 0...3\n  i = i + 10\n  puts i\nend\n"));
        // inclusive range up to MAX: the checked ++ traps instead of wrapping into an endless loop
        Assert.ThrowsAny<Exception>(() => Run("x = Int32::MAX - 2\nc = 0\nfor i in x..Int32::MAX\n  c += 1\nend\nputs c"));
    }

    [Fact]
    public async Task Loop_counter_takes_the_wider_bound_type()
    {
        const string src = "m = 2_147_483_649_i64\nc = 0\nfor k in 2_147_483_646...m\n  c += 1\nend\nputs c";
        Assert.Contains("for (long k_", AliceCompiler.Transpile(src));
        var output = await Task.Run(() => Run(src)).WaitAsync(TimeSpan.FromSeconds(20));   // an int counter would never reach m
        Assert.Equal("3", output);
    }

    [Theory]
    [InlineData("1.step(10, 3) { |i| print i, \" \" }\nputs", "1 4 7 10")]
    [InlineData("10.step(1, -4) { |i| print i, \" \" }\nputs", "10 6 2")]
    [InlineData("s = 2\n0.step(5, s) { |i| print i, \" \" }\nputs", "0 2 4")]          // non-literal step: runtime path
    [InlineData("n = 3_000_000_000_i64\nc = 0\n(n - 2).step(n, 1) { |i| c += 1 }\nputs c", "3")]
    [InlineData("q, r = (-7).divmod(2)\nputs q\nputs r", "-4\n1")]
    [InlineData("q, r = 7_i64.divmod(-2_i64)\nputs q\nputs r", "-4\n-1")]
    public void Step_and_divmod_edges(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Fact]
    public void Zero_step_raises()
        => Assert.ThrowsAny<Exception>(() => Run("s = 0\n0.step(5, s) { |i| puts i }"));

    [Theory]
    [InlineData("x = Array(Int32).new(3, 7)\nputs x.inspect", "[7, 7, 7]")]
    [InlineData("y = Array(Float64).new(2, 0.5)\nputs y.inspect", "[0.5, 0.5]")]
    [InlineData("z = Array(Int32).new(2)\nputs z.inspect", "[0, 0]")]
    [InlineData("s = Array(String).new(2, \"a\")\nputs s.inspect", "[\"a\", \"a\"]")]
    [InlineData("n = 4\nb = Array(Bool).new(n, true)\nputs b.inspect", "[true, true, true, true]")]
    public void Array_new_fills_with_the_given_value(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("x = Array(Int32).new(4) { |i| i * i }\nputs x.inspect", "[0, 1, 4, 9]")]
    [InlineData("g = Array(Array(Int32)).new(2) { Array(Int32).new(1, 5) }\nputs g[1].inspect", "[5]")]
    [InlineData("g = Array(Array(Int32)).new(2) { Array(Int32).new(1) }\ng[0][0] = 3\nputs g[1][0]", "0")]   // one array per element, not shared
    public void Array_new_with_a_block_builds_each_element(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Fact]
    public void Widening_conversions_are_plain_casts()
    {
        var cs = AliceCompiler.Transpile("def f(x : Int32, b : UInt8, l : Int64) : Float64\n  x.to_i64.to_f + b.to_i + l.to_f + x.to_f32\nend\n");
        Assert.Contains("(long)x", cs);
        Assert.Contains("(double)", cs);
        Assert.DoesNotContain(".to_i64()", cs);
        Assert.DoesNotContain(".to_f()", cs);
        Assert.Contains("to_i32", AliceCompiler.Transpile("def g(l : Int64) : Int32\n  l.to_i32\nend\n"));   // narrowing keeps its check
    }

    [Fact]
    public void Exact_power_forms_skip_math_pow()
    {
        var cs = AliceCompiler.Transpile("def f(n : Int32, x : Float64) : Float64\n  2.0 ** n + x ** 2 + x ** 3\nend\n");
        Assert.Contains("ScaleB(1.0, n)", cs);
        Assert.Contains("x * x", cs);
        Assert.Contains("pow(x, 3)", cs);   // x * x * x rounds twice: keeps Math.Pow
    }

    [Theory]
    [InlineData("for n in [-1080, -1074, -1022, -3, 0, 5, 1023, 1024]\n  print (2.0 ** n).to_s, \" \"\nend\nputs", "0.0 5.0e-324 2.2250738585072014e-308 0.125 1.0 32.0 8.98846567431158e+307 Infinity")]
    [InlineData("x = -1.5\nputs x ** 2\ny = 1e200\nputs y ** 2", "2.25\nInfinity")]
    public void Exact_power_forms_match_math_pow(string src, string expected)
        => Assert.Equal(expected, Run(src));
}
