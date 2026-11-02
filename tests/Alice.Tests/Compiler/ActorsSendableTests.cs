using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>AL0230 checks a generic message type with its type arguments substituted (audit F11).</summary>
[Collection("console")]
public class ActorsSendableTests
{
    const string Box = "class Box(T)\n  def initialize(@v : T)\n  end\n  def get : T\n    @v\n  end\nend\n";

    static void NotSendable(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0230", ex.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData(Box + "a = Actor(Box(Array(Int32))).new { |m| m.get[0] += 1 }\n")]
    [InlineData(Box + "a = Actor(Box(List(Int32))).new { |m| m.get.Add(1) }\n")]
    [InlineData(Box + "a = Actor(Box(Box(Array(Int32)))).new { |m| puts 1 }\n")]
    [InlineData("class Hub(T)\n  def start : Int32\n    a = Actor(T).new { |m| puts m }\n    0\n  end\nend\n")]   // unsubstituted parameter
    [InlineData("lib LibC\n  fun malloc(n : UInt64) : Pointer(Int32)\nend\na = Actor(Pointer(Int32)).new { |m| puts 1 }\n")]   // raw memory
    public void Mutable_type_arguments_are_not_sendable(string src) => NotSendable(src);

    [Fact]
    public void Immutable_type_arguments_are_sendable() =>
        Assert.Equal("3", AliceCompiler.Run(Box + "seen = Atomic.new(0)\na = Actor(Box(Int32)).new(->(m : Box(Int32)) { seen.add(m.get) })\na.send(Box(Int32).new(3))\na.stop\nputs seen.get\n").Trim());
}
