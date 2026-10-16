using Alice.Cli.Repl;

namespace Alice.Tests.Tooling;

/// <summary>REPL editing aids: live highlighting, history hints/search, completion menu layout.</summary>
public class ReplEditorTests
{
    [Theory]
    [InlineData("def sq(n : Int32) : Int32 = n * n  # square")]
    [InlineData("puts \"total #{xs.sum} items\"")]
    [InlineData("x = :sym if @count > 0x1F")]
    [InlineData("s = \"unterminated #{a")]
    [InlineData("")]
    public void Highlighting_never_changes_the_visible_text(string src) =>
        Assert.Equal(src, Highlighter.StripAnsi(Highlighter.Highlight(src)));

    [Fact]
    public void Highlighting_colors_each_token_kind()
    {
        string h = Highlighter.Highlight("def f(x) = puts \"hi #{x}\" + 42 # done");
        Assert.Contains(Highlighter.Keyword + "def", h);
        Assert.Contains(Highlighter.Function + "f", h);
        Assert.Contains(Highlighter.String + "\"hi ", h);
        Assert.Contains(Highlighter.Number + "42", h);
        Assert.Contains(Highlighter.Comment + "# done", h);
        Assert.Contains(Highlighter.Type + "Int32", Highlighter.Highlight("x : Int32"));
        Assert.Contains(Highlighter.Symbol + ":ok", Highlighter.Highlight("r = :ok"));
    }

    [Fact]
    public void Matching_bracket_is_found_both_ways_and_ignores_strings()
    {
        Assert.Equal(8, Highlighter.MatchingBracket("f(a, (b)) ", 1));
        Assert.Equal(1, Highlighter.MatchingBracket("f(a, (b))", 8));
        Assert.Equal(-1, Highlighter.MatchingBracket("f(\")\"", 1));
    }

    [Fact]
    public void History_hint_completes_from_the_newest_matching_entry()
    {
        var history = new List<string> { "xs = [1, 2, 3]", "xs.map { |x| x * 2 }", "puts 1" };
        Assert.Equal(".map { |x| x * 2 }", LineEditor.HistoryHint(history, "xs"));
        Assert.Null(LineEditor.HistoryHint(history, ""));
        Assert.Null(LineEditor.HistoryHint(history, "zz"));
    }

    [Fact]
    public void Prefix_history_search_walks_only_matching_entries()
    {
        var history = new List<string> { "a = 1", "b = 2", "a + b", "c" };
        Assert.Equal(2, LineEditor.SearchBack(history, "a", 4));
        Assert.Equal(0, LineEditor.SearchBack(history, "a", 2));
        Assert.Equal(-1, LineEditor.SearchBack(history, "a", 0));
        Assert.Equal(2, LineEditor.SearchBack(history, "b", 4, contains: true));
        Assert.Equal(1, LineEditor.SearchBack(history, "b", 2, contains: true));
    }

    [Fact]
    public void Completion_menu_lays_out_columns_within_the_width()
    {
        var items = Enumerable.Range(0, 20).Select(i => "name" + i).ToList();
        var rows = LineEditor.Columns(items, 40);
        Assert.All(rows, r => Assert.True(r.Length <= 40));
        Assert.Equal(20, rows.Sum(r => r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void Word_motion_skips_identifiers()
    {
        Assert.Equal(4, LineEditor.WordLeft("foo bar", 7));
        Assert.Equal(0, LineEditor.WordLeft("foo bar", 4));
        Assert.Equal(3, LineEditor.WordRight("foo bar", 0));
        Assert.Equal(7, LineEditor.WordRight("foo bar", 3));
    }

    [Fact]
    public void Repl_commands_and_pkg_words_complete()
    {
        var s = new ReplSession();
        Assert.Contains(":load", s.Complete(":lo"));
        Assert.Contains(":time", s.Complete(":t"));
    }
}
