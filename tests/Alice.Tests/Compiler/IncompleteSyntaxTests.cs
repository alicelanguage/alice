using Alice.Compiler;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

public class IncompleteSyntaxTests
{
    [Theory]
    [InlineData("def f(\n")]
    [InlineData("record Point(\n")]
    [InlineData("class Box(T\n")]
    [InlineData("enum Shape\nCircle(\n")]
    [InlineData("puts Math.Clamp(1,\n")]
    public void Incomplete_input_reports_an_error_instead_of_looping(string source)
    {
        var error = Assert.Throws<CompileException>(() => Parser.ParseFile(source, "incomplete.al"));
        Assert.Contains(error.Diagnostics, d => d.File == "incomplete.al" && d.Line >= 1);
    }
}
