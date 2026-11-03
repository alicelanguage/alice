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

    [Fact(Skip = "fuzz P1: Lexer.Str/Adv index past the end on an unterminated string inside an interpolation (Syntax/Lexer.cs:420)")]
    public void P1_unterminated_string_inside_interpolation_is_a_diagnostic()
    {
        var e = ParseError("x = \"a#{\"b\n");
        Assert.IsType<CompileException>(e);
    }

    [Fact(Skip = "fuzz P2: Lexer.Str reads past the end after a backslash at end of file (Syntax/Lexer.cs:406)")]
    public void P2_backslash_at_end_of_file_in_a_string_is_a_diagnostic()
    {
        var e = ParseError("x = \"\\");
        Assert.IsType<CompileException>(e);
    }

    [Fact(Skip = "fuzz P3: alice fmt re-indents the continuation lines of a multi-line string literal, changing its value (Syntax/Formatter.cs)")]
    public void P3_fmt_keeps_multiline_string_contents()
    {
        const string src = "s = \"a\n    end\nb\"\nputs s\n";
        Assert.Null(Oracles.CheckSyntax(src).Finding);
        Assert.Contains("\n    end\n", Formatter.Format(src));
    }

    [Fact(Skip = "fuzz P4: alice fmt is not idempotent on a heredoc opener without a body (Syntax/Formatter.cs heredoc queue)")]
    public void P4_fmt_is_idempotent_on_a_heredoc_without_body()
    {
        const string src = "<<~EOS\n";
        var once = Formatter.Format(src);
        Assert.Equal(once, Formatter.Format(once));
    }

    [Fact(Skip = "fuzz P5: a one-line `record` followed by a `def` parses differently by indentation; alice fmt indents the def and the output no longer parses (examples/particles.al; Syntax/Parser/Parser.Items.cs TypeItem + Syntax/Formatter.cs)")]
    public void P5_record_one_liner_then_def_survives_fmt()
    {
        const string src = "record P(x : Int32)\ndef f(a : Int32, &blk : Int32 -> Int32) : Int32\n  a\nend\n";
        Assert.Null(Oracles.CheckSyntax(src).Finding);
    }

    [Fact(Skip = "fuzz P6: a percent literal right after a binary operator or `?` is rejected (`unexpected token (found '%')`, Syntax/Lexer.cs operand-start check)")]
    public void P6_percent_literal_after_an_operator_parses()
    {
        Assert.Null(ParseError("x = 1 + %w[a b].size\n"));
        Assert.Null(ParseError("x = true ? %w[a] : %w[b]\n"));
    }

    [Fact(Skip = "fuzz P7: `while m do ... end` with a method call as the condition takes `do` as m's block (Syntax/Parser/Parser.Statements.cs noDo)")]
    public void P7_while_do_with_a_call_condition()
    {
        Assert.Null(ParseError("def f\n  1\nend\nwhile f do\n  break\nend\n"));
    }
}
