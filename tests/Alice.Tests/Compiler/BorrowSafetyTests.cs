using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Rust-inspired aliasing and lifetime checks (ADR 0027): a value lent mutably may not be lent again in the same
/// call (AL0217), a `@[Stack]` value may not outlive its frame (AL0218), a mutable value sent to another thread
/// is gone for the sender (AL0219), and a value a running task reads may not be written until the task is joined
/// (AL0202).
/// </summary>
[Collection("console")]
public class BorrowSafetyTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    static void Rejects(string src, string code, string needle)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains(needle, ex.Message);
        Assert.Equal(code, ex.Diagnostics[0].Code);
    }

    const string CopyInto = "def copy_into(dst : Array(Int32), src : Array(Int32))\n  for i in 0...src.size\n    dst[i] = src[i] + 1\n  end\nend\n";

    [Theory]
    [InlineData(CopyInto + "a = [1, 2, 3]\ncopy_into(a, a)\n")]
    [InlineData("def fill(xs : List(Int32), ys : List(Int32))\n  ys.each { |y| xs.Add(y) }\nend\nl = List(Int32).new\nfill(l, l)\n")]
    public void Mutable_and_shared_borrow_of_one_value_is_rejected(string src) =>
        Rejects(src, "AL0217", "conflicting borrow");

    [Theory]
    [InlineData(CopyInto + "a = [1, 2, 3]\nb = Array(Int32).new(3)\ncopy_into(b, a)\nputs b.sum\n", "9")]
    [InlineData("def total(xs : Array(Int32), ys : Array(Int32)) : Int32\n  xs.sum + ys.sum\nend\na = [1, 2]\nputs total(a, a)\n", "6")]
    public void Distinct_or_read_only_borrows_compile(string src, string expected) =>
        Assert.Equal(expected, Run(src));

    const string P = "@[Stack]\nstruct P\n  getter x : Int32\n  def initialize(@x : Int32)\n  end\nend\n";

    [Theory]
    [InlineData(P + "p = P.new(1)\nf = ->() { p.x }\nputs f.call\n")]
    [InlineData(P + "p = P.new(1)\n[1, 2].each { |i| puts p.x + i }\n")]
    [InlineData(P + "class Keep\n  def initialize(@p : P)\n  end\nend\n")]
    [InlineData(P + "p = P.new(1)\nxs = [p]\n")]
    public void Stack_value_that_outlives_its_frame_is_rejected(string src) =>
        Rejects(src, "AL0218", "stack value");

    [Fact]
    public void Stack_value_used_in_its_frame_compiles() =>
        Assert.Equal("8", Run(P + "def twice(p : P) : Int32\n  p.x * 2\nend\np = P.new(4)\nputs twice(p)\n"));

    [Theory]
    [InlineData("ch = Channel(Array(Int32)).new(1)\nxs = [1, 2]\nch.send(xs)\nxs[0] = 9\n")]
    [InlineData("ch = Channel(List(Int32)).new(1)\nl = List(Int32).new\nch.send(l)\nl.Add(1)\n")]
    public void Mutable_value_used_after_send_is_rejected(string src) =>
        Rejects(src, "AL0219", "sent to another thread");

    [Theory]
    [InlineData("ch = Channel(Array(Int32)).new(1)\nxs = [1, 2]\nch.send(xs)\nxs = [3]\nputs xs[0]\nputs ch.receive.sum\n", "3\n3")]
    [InlineData("ch = Channel(Int32).new(1)\nn = 4\nch.send(n)\nputs n + ch.receive\n", "8")]
    [InlineData("ch = Channel(String).new(1)\ns = \"a\"\nch.send(s)\nputs s + ch.receive\n", "aa")]
    public void Resend_after_reassign_and_immutable_values_compile(string src, string expected) =>
        Assert.Equal(expected, Run(src));

    [Theory]
    [InlineData("xs = [1, 2, 3]\nt = spawn { puts xs.sum }\nxs[0] = 5\nt.wait\n")]
    [InlineData("l = List(Int32).new\nt = spawn { puts l.Count }\nl.Add(1)\nt.wait\n")]
    public void Writing_what_a_running_task_reads_is_a_race(string src) =>
        Rejects(src, "AL0202", "data race");

    [Fact]
    public void Writing_after_the_task_is_joined_compiles() =>
        Assert.Equal("6\n10", Run("xs = [1, 2, 3]\nt = spawn { puts xs.sum }\nt.wait\nxs[0] = 5\nputs xs.sum\n"));
}
