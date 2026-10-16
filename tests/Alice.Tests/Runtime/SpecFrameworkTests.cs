using Alice.Compiler;
using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>`alice test` framework: RSpec-style specs, matchers, hooks, focus, xUnit-style tests and theories.</summary>
[Collection("console")]
public class SpecFrameworkTests
{
    static List<TestResult> RunSpec(string src, RunOptions? o = null)
    {
        var cs = AliceCompiler.TranspileAll([new SourceFile("spec.al", src)]);
        var asm = AliceCompiler.Load(AliceCompiler.Emit(cs, "spec" + Guid.NewGuid().ToString("N")[..8], exe: false));
        return TestRunner.Run(asm, o ?? new RunOptions());
    }

    const string Stack = """
        class Stack
          getter items : List(Int32)
          def initialize
            @items = List(Int32).new
          end
          def push(x : Int32)
            @items.Add(x)
          end
          def pop : Int32
            raise ArgumentException.new("empty") if @items.Count == 0
            x = @items[@items.Count - 1]
            @items.RemoveAt(@items.Count - 1)
            x
          end
        end

        describe "Stack" do
          s = Stack.new
          log = [] of String
          before do
            s = Stack.new
            log.Add("before")
          end
          after do
            log.Add("after")
          end

          context "when empty" do
            it "raises on pop" do
              expect { s.pop }.to raise_error(typeof(ArgumentException), "empty")
            end
            it "has no items" do
              expect(s.items).to be_empty
            end
          end

          context "after push" do
            it "pops the last item" do
              s.push(1)
              s.push(2)
              expect(s.pop).to eq(2)
              expect(s.items).to contain_exactly(1)
            end
            it "fails with a diff" do
              s.push(3)
              expect(s.pop).to eq(4)
            end
          end

          it "is pending"
          xit "is skipped" do
            expect(1).to eq(2)
          end
          it "ran hooks" do
            expect(log).to include("before", "after")
          end
        end
        """;

    [Fact]
    public void Spec_groups_hooks_matchers_and_statuses()
    {
        var r = RunSpec(Stack).ToDictionary(x => x.FullName);
        Assert.Equal(TestStatus.Passed, r["Stack when empty raises on pop"].Status);
        Assert.Equal(TestStatus.Passed, r["Stack when empty has no items"].Status);
        Assert.Equal(TestStatus.Passed, r["Stack after push pops the last item"].Status);
        var f = r["Stack after push fails with a diff"];
        Assert.Equal(TestStatus.Failed, f.Status);
        Assert.Equal("4", f.Expected);
        Assert.Equal("3", f.Actual);
        Assert.EndsWith("spec.al", f.File);
        Assert.Equal(44, f.Line);
        Assert.Equal(TestStatus.Pending, r["Stack is pending"].Status);
        Assert.Equal(TestStatus.Skipped, r["Stack is skipped"].Status);
        Assert.Equal(TestStatus.Passed, r["Stack ran hooks"].Status);
    }

    [Fact]
    public void Filters_lines_and_focus()
    {
        Assert.Equal(["Stack when empty raises on pop", "Stack when empty has no items"],
            RunSpec(Stack, new RunOptions { Filters = { "when empty" } }).Select(x => x.FullName));
        Assert.Equal(["Stack after push pops the last item"], RunSpec(Stack, new RunOptions { Lines = { 38 } }).Select(x => x.FullName));
        Assert.Equal(["Stack after push pops the last item", "Stack after push fails with a diff"],
            RunSpec(Stack, new RunOptions { Lines = { 37 } }).Select(x => x.FullName));
        var focused = RunSpec("describe \"A\" do\n  it \"a\" do\n  end\n  fit \"b\" do\n  end\nend\n");
        Assert.Equal(["A b"], focused.Select(x => x.FullName));
    }

    [Theory]
    [InlineData("expect(3).to eq(3)", true)]
    [InlineData("expect(3).to eq(3.0)", true)]
    [InlineData("expect([1, 2]).to eq([1, 2])", true)]
    [InlineData("expect(\"hello\").to start_with(\"he\").and(end_with(\"lo\"))", true)]
    [InlineData("expect(\"hello\").to match(\"l+\")", true)]
    [InlineData("expect(0.1 + 0.2).to be_within(0.0001).of(0.3)", true)]
    [InlineData("expect(5).to be_gt(3)", true)]
    [InlineData("expect(5).to be_between(1, 4)", false)]
    [InlineData("expect(nil).to be_nil", true)]
    [InlineData("expect([1, 2, 3]).to have_size(3)", true)]
    [InlineData("expect([2, 4]).to all(satisfy(->(x : Object?) { x.as(Int32) % 2 == 0 }))", true)]
    [InlineData("expect(\"x\").to be_a(typeof(String))", true)]
    [InlineData("expect(1).not_to eq(2)", true)]
    [InlineData("expect { 1 + 1 }.not_to raise_error", true)]
    [InlineData("expect([3, 1]).to contain_exactly(1, 3)", true)]
    [InlineData("expect([3, 1]).to contain_exactly(1)", false)]
    public void Matchers(string line, bool passes)
    {
        var r = RunSpec($"describe \"m\" do\n  it \"x\" do\n    {line}\n  end\nend\n").Single();
        Assert.True((r.Status == TestStatus.Passed) == passes, r.Message);
    }

    [Fact]
    public void XUnit_style_classes_theories_and_legacy_tests()
    {
        var src = """
            class CalcTest
              @log : List(String) = [] of String
              def setup
                @log.Add("setup")
              end
              def teardown
              end
              @[Test]
              def adds
                assert_equal(4, 2 + 2)
                assert_equal(["setup"], @log)
              end
              @[Test("subtracts correctly")]
              def subtracts
                expect(5 - 3).to eq(1)
              end
              @[Cases(1, 2, 3)]
              @[Cases(2, 2, 4)]
              def sum(a : Int32, b : Int32, c : Int32)
                expect(a + b).to eq(c)
              end
            end
            def test_legacy
              assert(true)
            end
            """;
        var r = RunSpec(src).ToDictionary(x => x.FullName);
        Assert.Equal(TestStatus.Passed, r["CalcTest adds"].Status);
        Assert.Equal(TestStatus.Failed, r["CalcTest subtracts correctly"].Status);
        Assert.Equal(TestStatus.Passed, r["CalcTest sum(1, 2, 3)"].Status);
        Assert.Equal(TestStatus.Passed, r["CalcTest sum(2, 2, 4)"].Status);
        Assert.Equal(TestStatus.Passed, r["test legacy"].Status);
    }

    [Fact]
    public void Seeded_shuffle_is_deterministic()
    {
        var src = string.Concat(Enumerable.Range(0, 8).Select(i => $"it \"t{i}\" do\nend\n"));
        var a = RunSpec(src, new RunOptions { Seed = 42 }).Select(x => x.Name).ToList();
        var b = RunSpec(src, new RunOptions { Seed = 42 }).Select(x => x.Name).ToList();
        Assert.Equal(a, b);
        Assert.NotEqual(Enumerable.Range(0, 8).Select(i => "t" + i), a);
    }
}
