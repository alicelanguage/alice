using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Errors found while compiling the generated C# are reported in Alice terms: AL codes, Alice type names.</summary>
[Collection("console")]
public class ErrorTranslationTests
{
    static string Error(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Run(src));
        Assert.DoesNotMatch(@"\bCS\d{4}\b", ex.Message);
        Assert.DoesNotContain("global::", ex.Message);
        return ex.Message;
    }

    [Theory]
    [InlineData("class Box\nend\nBox.new.open\n", "AL0302", "undefined method 'open' for Box")]
    [InlineData("x : Int32 = 1\ny : String = \"a\"\nputs x - y\n", "AL0301", "'-' is not defined for Int32 and String")]
    [InlineData("def f(x : Int32) : Int32\n  x\nend\nputs f(\"a\")\n", "AL0305", "argument 1 of f: expected Int32, got String")]
    [InlineData("def f(x : Int32) : Int32\n  x\nend\nputs f(1, 2)\n", "AL0306", "wrong number of arguments for f")]
    [InlineData("s : String? = nil\nputs s.Length\n", "AL0310", "may be nil")]
    [InlineData("xs = List(Int32).new\ny : String = xs[0]\n", "AL0304", "expected String, got Int32")]
    [InlineData("puts Math.Sqrt(\"x\")\n", "AL0305", "expected Float64, got String")]
    public void Translated(string src, string code, string text)
    {
        var msg = Error(src);
        Assert.Contains(code, msg);
        Assert.Contains(text, msg);
    }

    [Theory]
    [InlineData("int", "Int32")]
    [InlineData("double", "Float64")]
    [InlineData("string?", "String?")]
    [InlineData("global::System.Collections.Generic.List<int>", "List(Int32)")]
    [InlineData("System.Collections.Generic.Dictionary<string, global::Foo>", "Dictionary(String, Foo)")]
    [InlineData("int[]", "Array(Int32)")]
    [InlineData("System.Func<int, bool>", "Int32 -> Bool")]
    [InlineData("(int, string)", "{Int32, String}")]
    public void Type_names_read_as_alice(string cs, string alice) =>
        Assert.Equal(alice, Alice.Compiler.Diagnostics.AliceNames.Type(cs));

    [Fact]
    public void Constant_array_literals_infer_their_type() =>
        Assert.Equal("3 b", AliceCompiler.Run("ROWS = [\"a\", \"b\", \"c\"]\nputs \"#{ROWS.size} #{ROWS[1]}\"\n").Trim());
}
