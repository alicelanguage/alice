using Alice.Compiler;
using Alice.Compiler.Syntax;
using Alice.Fuzz;

namespace Alice.Tests.Fuzz.Regressions;

/// <summary>
/// Parser and formatter bugs found by the fuzzers (tools/fuzz, /work/reports/fuzz-findings.md), minimized. Skipped until
/// fixed; each asserts the correct behavior, so removing the Skip is the fix's test.
/// </summary>
public class ParserRegressionTests
{
    static Exception? ParseError(string src)
    {
        try { Parser.ParseFile(src, "fuzz.al"); return null; }
        catch (Exception e) { return e; }
    }

    [Fact]
    public void P1_unterminated_string_inside_interpolation_is_a_diagnostic()
    {
        var e = ParseError("x = \"a#{\"b\n");
        Assert.IsType<CompileException>(e);
    }

    [Fact]
    public void P2_backslash_at_end_of_file_in_a_string_is_a_diagnostic()
    {
        var e = ParseError("x = \"\\");
        Assert.IsType<CompileException>(e);
    }

    [Fact]
    public void P3_fmt_keeps_multiline_string_contents()
    {
        const string src = "s = \"a\n    end\nb\"\nputs s\n";
        Assert.Null(Oracles.CheckSyntax(src).Finding);
        Assert.Contains("\n    end\n", Formatter.Format(src));
    }

    [Fact]
    public void P4_fmt_is_idempotent_on_a_heredoc_without_body()
    {
        const string src = "<<~EOS\n";
        var once = Formatter.Format(src);
        Assert.Equal(once, Formatter.Format(once));
    }

    [Fact]
    public void P5_record_one_liner_then_def_survives_fmt()
    {
        const string src = "record P(x : Int32)\ndef f(a : Int32, &blk : Int32 -> Int32) : Int32\n  a\nend\n";
        Assert.Null(Oracles.CheckSyntax(src).Finding);
    }

    [Fact]
    public void P6_percent_literal_after_an_operator_parses()
    {
        Assert.Null(ParseError("x = 1 + %w[a b].size\n"));
        Assert.Null(ParseError("x = true ? %w[a] : %w[b]\n"));
    }

    [Fact]
    public void P7_while_do_with_a_call_condition()
    {
        Assert.Null(ParseError("def f\n  1\nend\nwhile f do\n  break\nend\n"));
    }

    [Fact]
    public void P8_incomplete_hex_literal_in_rule_range_is_a_diagnostic()
    {
        var e = ParseError("rule r(y in 0x");
        Assert.IsType<CompileException>(e);
    }

    [Fact]
    public void P9_fmt_with_LINE_is_idempotent_even_if_line_numbers_change()
    {
        const string src = "begin\n  until s =~ /[a-z]*/i\n    puts xs.map { |x4| __LINE__ && (%i(x y z)) }  \n  end\nend\n";
        // The formatted version may have different line numbers, but formatting should be stable
        var once = Formatter.Format(src);
        var twice = Formatter.Format(once);
        Assert.Equal(once, twice);
    }
}
