using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// A non-nullable reference field must be assigned by every `initialize` or have an initializer: otherwise it is
/// nil at runtime with no diagnostic (audit F17). AL0221.
/// </summary>
[Collection("console")]
public class NullSafetyTests
{
    [Theory]
    [InlineData("class P\n  @name : String\n  def initialize\n  end\nend\nputs P.new.class\n")]
    [InlineData("class P\n  @name : String\nend\nputs P.new.class\n")]
    [InlineData("class P\n  @name : String\n  def initialize(f : Bool)\n    if f\n      @name = \"a\"\n    end\n  end\nend\nputs P.new(true).class\n")]
    [InlineData("class P\n  getter name : String\n  def initialize\n  end\nend\nputs P.new.name.size\n")]
    [InlineData("class P\n  @xs : List(Int32)\n  def initialize\n  end\n  def initialize(n : Int32)\n    @xs = List(Int32).new\n  end\nend\nputs P.new.class\n")]
    public void Unassigned_non_nullable_fields_are_errors(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0221", ex.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("class P\n  @name : String\n  def initialize\n    @name = \"a\"\n  end\n  def name : String\n    @name\n  end\nend\nputs P.new.name\n", "a")]
    [InlineData("class P\n  @name : String = \"b\"\n  def name : String\n    @name\n  end\nend\nputs P.new.name\n", "b")]
    [InlineData("class P\n  @name : String?\n  def name : String\n    @name || \"nil\"\n  end\nend\nputs P.new.name\n", "nil")]
    [InlineData("class P\n  @n : Int32\n  def n : Int32\n    @n\n  end\nend\nputs P.new.n\n", "0")]
    [InlineData("class P\n  def initialize(@name : String)\n  end\n  def get : String\n    @name\n  end\nend\nputs P.new(\"c\").get\n", "c")]
    [InlineData("class P\n  @name : String\n  def initialize(f : Bool)\n    if f\n      @name = \"t\"\n    else\n      @name = \"f\"\n    end\n  end\n  def name : String\n    @name\n  end\nend\nputs P.new(false).name\n", "f")]
    public void Assigned_nullable_or_value_fields_compile(string src, string expected) =>
        Assert.Equal(expected, AliceCompiler.Run(src).Trim());
}
