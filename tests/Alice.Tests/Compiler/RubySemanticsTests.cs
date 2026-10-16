using Alice.Compiler;

namespace Alice.Tests.Compiler;

[Collection("console")]
public class RubySemanticsTests
{
    static string Run(string src, params string[] args) => AliceCompiler.Run(src, "main.al", args).Trim().ReplaceLineEndings("\n");

    [Fact]
    public void Locals_assigned_in_branches_are_visible_after_the_if() =>
        Assert.Equal("3\n7", Run("def f(c : Bool) : Int32\n  if c\n    p = 3\n  else\n    p = 7\n  end\n  p\nend\nputs f(true)\nputs f(false)\n"));

    [Fact]
    public void Case_branch_locals_are_visible_after_the_case() =>
        Assert.Equal("two", Run("x = 2\ncase x\nwhen 1\n  name = \"one\"\nelse\n  name = \"two\"\nend\nputs name\n"));

    [Fact]
    public void ARGV_is_global() =>
        Assert.Equal("--size", Run("def first_arg : String\n  ARGV[0]\nend\nputs first_arg\n", "--size", "8"));

    [Fact]
    public void Nil_check_on_a_value_type_is_false() =>
        Assert.Equal("false", Run("i = 3\nputs i.nil?\n"));

    [Fact]
    public void Narrow_integer_conversions() =>
        Assert.Equal("255 200 65535 7", Run("puts \"#{255.to_u8} #{200.to_i16} #{65535.to_u16} #{7_i64.to_u32}\"\n"));

    [Fact]
    public void Array_patterns_match_literal_elements() =>
        Assert.Equal("L\ngo north\ngo east\n?\nthree from 1", Run("def look : String\n  \"L\"\nend\ndef go(d : String) : String\n  \"go \" + d\nend\ndef cmd(w : Array(String)) : String\n  case w\n  in [\"look\"] | [\"l\"]\n    look\n  in [\"go\", dir]\n    go(dir)\n  in [dir]\n    go(dir)\n  else\n    \"?\"\n  end\nend\nputs cmd([\"l\"])\nputs cmd([\"go\", \"north\"])\nputs cmd([\"east\"])\nputs cmd([\"x\", \"y\"])\ncase [1, 2, 3]\nin [1, _, x]\n  puts \"three from 1\"\nelse\n  puts \"no\"\nend\n"));
}
