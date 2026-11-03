using Alice.Compiler;
using Alice.Compiler.Mir;

namespace Alice.Tests.Compiler;

/// <summary>
/// `dataflow do ... end` (ADR 0038, P3.2/P3.3): binder checks AL0244-AL0246, the AIR form (AirDataflow, AirStage,
/// AirRing), static stage methods over function pointers, results equal to the serial program, determinism of the
/// ordered replicated stage, and no allocation per item.
/// </summary>
[Collection("console")]
public class DataflowLowerTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    static string Code(string src)
    {
        var e = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        return e.Diagnostics[0].Code + " " + e.Message;
    }

    const string Squares = """
        n = 100000
        total = 0_i64
        dataflow do
          nums = source { |out|
            for i in 0...n
              out << i
            end
          }
          sq = nums.stage { |x, out| out << x.to_i64 * x.to_i64 }
          sq.sink { |v| total += v }
        end
        puts "total=#{total}"
        """;

    [Fact]
    public void Three_stage_network_runs_and_copies_sink_writes_back()
    {
        Assert.Equal("total=333328333350000", Run(Squares));
    }

    [Fact]
    public void Lowers_to_static_stage_methods_over_function_pointers()
    {
        string cs = AliceCompiler.Transpile(Squares);
        Assert.Contains("total = __df_main_1(n, total);", cs);
        Assert.Contains("__df_g.Source<global::System.Collections.Generic.IEnumerator<int>, int>(__df_main_1_n0(n), &__df_main_1_n0_pump)", cs);
        Assert.Contains("private static void __df_main_1_n1(ref byte __df_s, global::System.ReadOnlySpan<int> __df_in, ref global::Alice.Runtime.RingWriter<long> out_", cs);
        Assert.Contains("__df_e1.Sink<long>(total, &__df_main_1_n2)", cs);
        Assert.Contains("yield return i_", cs);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, @"__df_g\.Run\(\)"));
        Assert.DoesNotContain("=>", cs[cs.IndexOf("__df_main_1_n1", StringComparison.Ordinal)..]);   // no lambda per item
    }

    [Fact]
    public void Dump_shows_the_graph()
    {
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(Squares));
        Assert.Contains("(dataflow __df_main_1 (ring int) (ring long) (stage source __df_main_1_n0 out 0:int (state n:int)) (stage stage __df_main_1_n1 in 0:int out 1:long) (stage sink __df_main_1_n2 in 1:long (state total:long)) (result 2.0:long)", dump);
    }

    [Fact]
    public void State_map_form_and_options()
    {
        string src = """
            words = ["a", "bb", "ccc", "dddd"]
            longest = 0
            count = 0
            dataflow(capacity: 64, batch: 8) do
              ws = source(state: words) { |all, out|
                for i in 0...all.size
                  out << all[i]
                end
              }
              lens = ws.stage(capacity: 16) { |w| w.size }
              big = lens.stage(state: 1) { |lim, n, out|
                next if n <= lim
                out << n * 10
              }
              big.sink { |n|
                longest = n if n > longest
                count += 1
              }
            end
            puts "#{longest} #{count}"
            """;
        Assert.Equal("40 3", Run(src));
        string cs = AliceCompiler.Transpile(src);
        Assert.Contains("new global::Alice.Runtime.Graph(capacity: 64, batch: 8)", cs);
        Assert.Contains("(longest, count) = __df_main_1(", cs);
    }

    const string Par = """
        def h(x : Int32) : Int64
          v = x.to_i64
          v * v % 1000003_i64
        end
        n = 200000
        acc = 0_i64
        dataflow do
          xs = source { |out|
            for i in 0...n
              out << i
            end
          }
          ys = xs.stage(par: true, workers: 4, batch: 64) { |x| h(x) }
          ys.sink { |y| acc = acc &* 31_i64 &+ y }
        end
        serial = 0_i64
        for i in 0...n
          serial = serial &* 31_i64 &+ h(i)
        end
        puts acc == serial ? "same #{acc}" : "differ #{acc} #{serial}"
        """;

    [Fact]
    public void Replicated_ordered_stage_equals_the_serial_program()
    {
        string first = Run(Par);
        Assert.StartsWith("same ", first);
        for (int i = 0; i < 3; i++) Assert.Equal(first, Run(Par));   // deterministic: order-sensitive fold
        Assert.Contains(".Replicate<byte, long>((byte)0, &__df_main_1_n1, 4, true, 64, 0)", AliceCompiler.Transpile(Par));
    }

    [Fact]
    public void No_allocation_per_item()
    {
        static string Prog(int n) => $$"""
            total = 0_i64
            def go(n : Int32) : Int64
              t = 0_i64
              dataflow do
                a = source { |out|
                  for i in 0...n
                    out << i
                  end
                }
                b = a.stage { |x, out| out << x.to_i64 }
                b.sink { |v| t += v }
              end
              t
            end
            go(1000)
            a0 = GC.GetTotalAllocatedBytes(true)
            total = go({{n}})
            puts GC.GetTotalAllocatedBytes(true) - a0
            """;
        long small = long.Parse(Run(Prog(10_000)));
        long big = long.Parse(Run(Prog(1_000_000)));
        // 100x the items: allocation is per graph (rings, nodes, spills), not per item
        Assert.True(big < small + 64 * 1024, $"10k items: {small} B, 1M items: {big} B");
    }

    [Fact]
    public void AL0246_two_consumers()
    {
        Assert.StartsWith("AL0246", Code("dataflow do\n  a = source { |out| out << 1 }\n  a.sink { |x| puts x }\n  a.sink { |x| puts x }\nend\n"));
    }

    [Fact]
    public void AL0246_cycle()
    {
        var c = Code("dataflow do\n  a = b.stage { |x, out| out << x }\n  b = a.stage { |x, out| out << x }\n  b.sink { |x| puts x }\nend\n");
        Assert.StartsWith("AL0246", c);
        Assert.Contains("cycle", c);
    }

    [Fact]
    public void AL0246_no_consumer_and_two_producers()
    {
        Assert.Contains("no consumer", Code("dataflow do\n  a = source { |out| out << 1 }\n  b = source { |out| out << 2 }\n  b.sink { |x| puts x }\nend\n"));
        Assert.Contains("more than one producer", Code("dataflow do\n  a = source { |out| out << 1 }\n  a = source { |out| out << 2 }\n  a.sink { |x| puts x }\nend\n"));
        Assert.StartsWith("AL0246", Code("dataflow do\n  puts 1\nend\n"));
    }

    [Fact]
    public void AL0244_stage_captures_a_mutable_value()
    {
        var c = Code("xs = List(Int32).new\ndataflow do\n  a = source { |out| out << 1 }\n  b = a.stage { |v, out|\n    xs.Add(v)\n    out << v\n  }\n  b.sink { |x| puts x }\nend\n");
        Assert.StartsWith("AL0244", c);
        Assert.Contains("xs", c);
    }

    [Fact]
    public void AL0244_stage_writes_a_captured_local()
    {
        Assert.StartsWith("AL0244", Code("k = 0\ndataflow do\n  a = source { |out| out << 1 }\n  b = a.stage { |v, out|\n    k += v\n    out << v\n  }\n  b.sink { |x| puts x }\nend\n"));
    }

    [Fact]
    public void Sink_may_own_a_mutable_local()
    {
        Assert.Equal("3", Run("xs = List(Int32).new\ndataflow do\n  a = source { |out|\n    for i in 0...3\n      out << i\n    end\n  }\n  a.sink { |x| xs.Add(x) }\nend\nputs xs.Count\n"));
    }

    [Fact]
    public void AL0245_par_stage_with_an_effect()
    {
        var c = Code("dataflow do\n  a = source { |out| out << 1 }\n  b = a.stage(par: true) { |v|\n    puts v\n    v\n  }\n  b.sink { |x| puts x }\nend\n");
        Assert.StartsWith("AL0245", c);
    }

    [Fact]
    public void Stream_def_parses_and_checks_its_return_type()
    {
        Assert.StartsWith("AL0246", Code("stream def f(n : Int32) : Int32\n  n\nend\nputs f(1)\n"));
        Assert.Equal("6", Run("stream def nums(n : Int32) : IEnumerable(Int32)\n  for i in 1..n\n    yield i\n  end\nend\ns = 0\nnums(3).each { |x| s += x }\nputs s\n"));
    }
}
