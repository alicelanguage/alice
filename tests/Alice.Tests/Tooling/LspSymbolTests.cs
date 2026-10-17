using System.Text.Json.Nodes;
using Alice.Cli.Lsp;

namespace Alice.Tests.Tooling;

public class LspSymbolTests
{
    [Theory]
    [InlineData("Enumerable", "def each(&block : Int32 ->)\n    yield 1\n  end", "each", "GetEnumerator")]
    [InlineData("Comparable", "def <=>(other : A) : Int32 = 0", "<=>", "CompareTo")]
    public void Symbols_exclude_generated_mixin_members_and_have_contained_name_ranges(string mixin, string method, string declaredName, string generatedName)
    {
        var doc = new Document("file:///tmp/alice-symbols.al", $$"""
            class A
              include {{mixin}}
              {{method}}
            end
            """, 1);
        Analyzer.Quick(doc, Project.For(doc.Path));

        var symbols = Features.Symbols(doc);
        var type = Assert.Single(symbols)!;
        var members = type["children"]!.AsArray();
        Assert.Contains(members, m => m!["name"]!.GetValue<string>() == declaredName);
        foreach (var symbol in Flatten(symbols)) AssertValidRanges(doc, symbol);
        Assert.DoesNotContain(members, m => m!["name"]!.GetValue<string>() == generatedName);
    }

    [Theory]
    [InlineData("class A\nend", "A")]
    [InlineData("def f(\n", null)]
    public void Symbols_use_current_text_when_the_previous_ast_has_longer_names_and_more_lines(string text, string? expectedName)
    {
        var doc = new Document("file:///tmp/alice-symbols-edit.al", "\nclass VeryLongTypeName\n  def very_long_method_name = 1\nend\n", 1);
        Analyzer.Parse(doc);
        doc.Apply(new JsonObject { ["text"] = text });
        doc.Version++;

        var symbols = Features.Symbols(doc);
        foreach (var symbol in Flatten(symbols)) AssertValidRanges(doc, symbol);
        if (expectedName == null) Assert.Empty(symbols);
        else Assert.Equal(expectedName, Assert.Single(symbols)!["name"]!.GetValue<string>());
    }

    static IEnumerable<JsonNode> Flatten(JsonArray symbols)
    {
        foreach (var symbol in symbols)
        {
            yield return symbol!;
            if (symbol!["children"] is JsonArray children)
                foreach (var child in Flatten(children)) yield return child;
        }
    }

    static void AssertValidRanges(Document doc, JsonNode symbol)
    {
        static (int Line, int Character) Position(JsonNode p) => (p["line"]!.GetValue<int>(), p["character"]!.GetValue<int>());
        var start = Position(symbol["range"]!["start"]!);
        var end = Position(symbol["range"]!["end"]!);
        var selectedStart = Position(symbol["selectionRange"]!["start"]!);
        var selectedEnd = Position(symbol["selectionRange"]!["end"]!);
        Assert.True(start.CompareTo(selectedStart) <= 0 && selectedEnd.CompareTo(end) <= 0,
            $"{symbol["name"]}: selectionRange must be contained in range: {symbol}");
        Assert.True(selectedStart.CompareTo(selectedEnd) <= 0);
        foreach (var position in new[] { start, end, selectedStart, selectedEnd })
        {
            Assert.InRange(position.Line, 0, doc.Text.Count(c => c == '\n'));
            Assert.InRange(position.Character, 0, doc.Line(position.Line).Length);
        }
        Assert.Equal(symbol["name"]!.GetValue<string>(),
            doc.Text[doc.Offset(selectedStart.Line, selectedStart.Character)..doc.Offset(selectedEnd.Line, selectedEnd.Character)]);
    }
}
