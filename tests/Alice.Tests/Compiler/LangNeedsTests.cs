using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Ruby core behaviour that needed compiler work (/work/reports/lang-needs-compiler.md, items 1-8): Hash defaults,
/// result types of Prelude methods, block parameter types, tuple/pair indexing, `===`, `Array * n`, `String.build`
/// and named-tuple names.
/// </summary>
[Collection("console")]
public class LangNeedsTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        // 1. Hash(K, V).new(default) / new { |h, k| } is a DefaultHash; new(capacity: n) is a capacity
        { "hash default value", "h = Hash(String, Int32).new(0)\nh[\"a\"] += 1\nh[\"a\"] += 1\nputs h[\"a\"]\nputs h[\"b\"]\nputs h.size\n", "2\n0\n1" },
        { "hash default block", "h = Hash(String, Int32).new { |hh, k| k.size }\nputs h[\"abc\"]\nputs h.size\n", "3\n0" },
        { "hash default block that stores", "h = Hash(String, Int32).new { |hh, k| hh[k] = 7 }\nputs h[\"x\"]\nputs h.size\n", "7\n1" },
        { "hash capacity", "h = Hash(String, Int32).new(capacity: 16)\nh[\"a\"] = 1\nputs h.size\n", "1" },
        { "hash default in a field", "class Counter\n  def initialize\n    @counts = Hash(String, Int32).new(0)\n  end\n  def add(w : String)\n    @counts[w] += 1\n  end\n  def get(w : String) : Int32\n    @counts[w]\n  end\nend\nc = Counter.new\nc.add(\"x\")\nc.add(\"x\")\nputs c.get(\"x\")\nputs c.get(\"y\")\n", "2\n0" },
        // 2. result types
        { "to_i? is Int32?", "puts \"x\".to_i?.nil?\nputs \"12\".to_i?.not_nil! + 1\nputs \"2.5\".to_f?.not_nil! * 2.0\n", "true\n13\n5.0" },
        { "match is MatchData?", "m = \"abc\".match(/b/)\nif m\n  puts \"yes\"\nend\nputs \"abc\".match(/z/).nil?\n", "yes\ntrue" },
        { "Hash#delete is V?", "h = {\"a\" => \"x\"}\nv = h.delete(\"a\")\nputs v.nil?\nputs h.delete(\"z\").nil?\nputs h.size\n", "false\ntrue\n0" },
        { "each_char / reverse_each without a block", "puts \"ab\".each_char.to_a.size\nputs [1, 2, 3].reverse_each.to_a.join(\",\")\n", "2\n3,2,1" },
        { "max(n) / min(n)", "puts [3, 1, 2].max(2).join(\",\")\nputs [3, 1, 2].min(2).join(\",\")\n", "3,2\n1,2" },
        { "dup / clone keep the type", "l = List(Int32).new\nl << 1\nm = l.clone\nm << 2\nn = l.dup\nn << 3\nputs l.Count\nputs m.Count\nputs n.Count\n", "1\n2\n2" },
        { "multi-key dig", "h = {\"a\" => {\"b\" => \"leaf\"}}\nputs h.dig(\"a\", \"b\").not_nil!.size\nputs h.dig(\"a\", \"z\").nil?\n", "4\ntrue" },
        // 3. block parameter types
        { "slice_when", "puts [1, 2, 4, 5, 7].slice_when { |a, b| b != a + 1 }.map { |g| g.size }.join(\",\")\n", "2,2,1" },
        { "filter_map with a nilable value", "puts [1, 2, 3].filter_map { |x| x.odd? ? x * 10 : nil }.join(\",\")\n", "10,30" },
        // 4. t[0] / kv[0]
        { "tuple index", "t = {1, \"a\"}\nputs t[0] + 1\nputs t[1]\n", "2\na" },
        { "pair index", "h = {\"a\" => 1, \"b\" => 5}\nkv = h.max_by { |k, v| v }\nputs kv[0]\nputs kv[1] + 1\n", "b\n6" },
        // 5. ===
        { "case equality", "puts (1..5) === 3\nputs (1..5) === 9\nputs /ab/ === \"cab\"\nputs Int32 === 3\nputs String === 3\nputs 3 === 3\n", "true\nfalse\ntrue\ntrue\nfalse\ntrue" },
        // 6. Array * Int / String
        { "array times", "puts ([1, 2] * 2).size\nputs [1, 2] * \",\"\n", "4\n1,2" },
        // 7. String.build
        { "String.build", "s = String.build { |io| io << \"a\" << 1 }\nputs s\n", "a1" },
        // 8. named tuples keep their names
        { "named tuple printing and to_h/keys/merge", "t = {name: \"ada\", age: 36}\nputs t\nputs t.keys.join(\",\")\nputs t.to_h.size\nu = t.merge({age: 37})\nputs u[:age]\nputs u[:name]\n", "{name: \"ada\", age: 36}\nname,age\n2\n37\nada" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Ruby_core_behaviour(string name, string src, string expected)
    {
        Assert.True(expected == Run(src), name);
    }
}
