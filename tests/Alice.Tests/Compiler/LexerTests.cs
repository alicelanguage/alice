using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Token-level checks for the Ruby/Crystal literal forms (strings, chars, symbols, heredocs, operator method names).</summary>
public class LexerTests
{
    static List<(string Kind, string Text)> Lex(string src) =>
        new Lexer(src, "t.al", 1).Lex().Where(t => t.Kind is not ("nl" or "eof")).Select(t => (t.Kind, t.Text)).ToList();

    [Theory]
    [InlineData(":+", "+")]
    [InlineData(":<=>", "<=>")]
    [InlineData(":[]", "[]")]
    [InlineData(":[]=", "[]=")]
    [InlineData(":<<", "<<")]
    [InlineData(":-@", "-@")]
    [InlineData(":==", "==")]
    public void Operator_symbols(string src, string sym) => Assert.Equal([("sym", sym)], Lex(src));

    [Fact]
    public void Symbol_after_an_operator() => Assert.Equal([("sym", "a"), ("op", "<=>"), ("sym", "b")], Lex(":a <=> :b"));

    [Fact]
    public void Ternary_colon_minus_is_not_a_symbol() => Assert.DoesNotContain(Lex("c ? a :-1"), t => t.Kind == "sym");

    [Theory]
    [InlineData("def []=(i, v)", "[]=")]
    [InlineData("def -@", "-@")]
    [InlineData("def <<(x)", "<<")]
    [InlineData("def ===(x)", "===")]
    [InlineData("def self.+(x)", "+")]
    public void Operator_method_names_are_one_token(string src, string op) => Assert.Contains(("op", op), Lex(src));

    [Theory]
    [InlineData("'a'", "char", "'a'")]
    [InlineData("'\\n'", "char", "'\\n'")]
    [InlineData("'\\u{41}'", "char", "'\\u0041'")]
    [InlineData("'\\101'", "char", "'\\u0041'")]
    [InlineData("'\\x41'", "char", "'\\u0041'")]
    [InlineData("'\"'", "char", "'\\\"'")]
    [InlineData("'ab'", "str", "ab")]
    [InlineData("'#{x}'", "str", "\\#{x}")]
    [InlineData("''", "str", "")]
    public void Single_quotes_are_a_char_or_a_raw_string(string src, string kind, string text) => Assert.Equal([(kind, text)], Lex(src));

    [Theory]
    [InlineData("%q(a #{b} \\n)", "a \\#{b} \\\\n")]
    [InlineData("%Q(a #{\"b\"})", "a #{\"b\"}")]
    [InlineData("%(say \"hi\")", "say \\\"hi\\\"")]
    [InlineData("%q[x [y] z]", "x [y] z")]
    [InlineData("%q|p|", "p")]
    public void Percent_strings(string src, string text) => Assert.Equal([("str", text)], Lex(src));

    [Fact]
    public void Percent_after_an_operand_stays_modulo() => Assert.Equal([("id", "a"), ("op", "%"), ("op", "("), ("id", "b"), ("op", ")")], Lex("a %(b)"));

    [Fact]
    public void Plain_heredoc_needs_its_terminator()
    {
        Assert.Equal([("id", "a"), ("op", "="), ("str", "x\n")], Lex("a = <<EOT\nx\nEOT\n"));
        // no `EOT` line: `<<` is the append operator
        Assert.Equal([("id", "a"), ("op", "<<"), ("const", "EOT")], Lex("a <<EOT\n"));
    }

    [Fact]
    public void Raw_heredoc_escapes_nothing() => Assert.Equal([("str", "\\#{x} \\\\n\n")], Lex("<<~'EOT'\n  #{x} \\n\n  EOT\n"));

    [Theory]
    [InlineData("\\u{1F600}", "\\U0001F600")]
    [InlineData("\\u{41 42}", "\\u0041\\u0042")]
    [InlineData("\\101", "\\u0041")]
    [InlineData("\\0", "\\0")]
    [InlineData("\\x7", "\\u0007")]
    [InlineData("\\e", "\\u001b")]
    [InlineData("\\s", " ")]
    [InlineData("\\q", "q")]
    [InlineData("\\n", "\\n")]
    public void Escapes_become_csharp(string esc, string cs)
    {
        int k = 0;
        Assert.Equal(cs, Lexer.CsEscape(esc, ref k));
        Assert.Equal(esc.Length, k);
    }
}
