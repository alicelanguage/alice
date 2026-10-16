using Alice.Compiler;

namespace Alice.Tests.Runtime;

/// <summary>Ruby/Crystal-compatible Prelude: Alice source -> stdout.</summary>
[Collection("console")]
public class StdlibTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        // String
        { "center", "puts \"ab\".center(6, '*')", "**ab**" },
        { "chomp chop", "puts \"hi\\n\".chomp + \"|\"\nputs \"abc\".chop", "hi|\nab" },
        { "squeeze", "puts \"aaabbc\".squeeze", "abc" },
        { "count str", "puts \"hello world\".count(\"lo\")", "5" },
        { "index rindex", "puts \"hello\".index(\"l\")\nputs \"hello\".rindex(\"l\")", "2\n3" },
        { "sub first", "puts \"aaa\".sub(\"a\", \"b\")", "baa" },
        { "gsub regex", "using System.Text.RegularExpressions\nputs \"a1b22\".gsub(Regex.new(\"[0-9]+\"), \"#\")", "a#b#" },
        { "tr delete", "puts \"hello\".tr(\"el\", \"ip\")\nputs \"hello\".delete(\"l\")", "hippo\nheo" },
        { "each_line", "\"a\\nb\".each_line { |l| print l.strip }\nputs", "ab" },
        { "scan regex", "using System.Text.RegularExpressions\nputs \"a1b22c3\".scan(Regex.new(\"[0-9]+\")).join(\",\")", "1,22,3" },
        { "match?", "puts \"abc123\".match?(\"[0-9]\")\nputs \"abc\".match?(\"[0-9]\")", "true\nfalse" },
        { "repeat to_sym", "puts \"ab\".repeat(3)\nputs \"x\".to_sym", "ababab\nx" },
        { "succ string", "puts \"az\".succ\nputs \"zz\".succ", "ba\naaa" },
        { "bytes", "puts \"AB\".bytes.sum", "131" },
        { "hex oct", "puts \"ff\".hex\nputs \"17\".oct", "255\n15" },
        { "format", "puts format(\"%d-%s-%.2f-%x-%05d\", 7, \"a\", 3.14159, 255, 42)", "7-a-3.14-ff-00042" },
        { "format f", "puts format(\"%f\", 1.5)", "1.500000" },
        { "case helpers", "puts \"hello\".capitalize\nputs \"HeLLo\".swapcase\nputs \"hello big world\".title", "Hello\nhEllO\nHello Big World" },
        // Array / Enumerable
        { "each_slice", "[1, 2, 3, 4, 5].each_slice(2) { |s| print s.sum, \" \" }\nputs", "3 7 5" },
        { "each_cons", "[1, 2, 3].each_cons(2) { |s| print s.join }\nputs", "1223" },
        { "zip", "puts [1, 2].zip([3, 4]).size", "2" },
        { "flatten", "puts [[1, 2], [3]].flatten.sum", "6" },
        { "compact", "xs = [\"a\", nil, \"b\"]\nputs xs.compact.size", "2" },
        { "group_by", "g = [1, 2, 3, 4].group_by { |x| x % 2 }\nputs g[0].sum", "6" },
        { "partition", "parts = [1, 2, 3, 4].partition { |x| x.even? }\nputs parts[0].join(\",\")\nputs parts[1].join(\",\")", "2,4\n1,3" },
        { "tally", "t = [\"a\", \"b\", \"a\"].tally\nputs t[\"a\"]", "2" },
        { "chunk_while", "puts [1, 2, 4, 5, 7].chunk_while { |a, b| b == a + 1 }.size", "3" },
        { "flat_map", "puts [1, 2].flat_map { |x| [x, x] }.sum", "6" },
        { "minmax", "m = [3, 1, 2].minmax\nputs m[0]\nputs m[1]", "1\n3" },
        { "product", "puts [1, 2].product([3, 4]).size", "4" },
        { "sample shuffle", "xs = [1, 2, 3]\nputs xs.shuffle.sum\nputs xs.include?(xs.sample)\nputs xs.shuffle(Random.new(1)).size", "6\ntrue\n3" },
        { "rotate", "puts [1, 2, 3].rotate.join\nputs [1, 2, 3].rotate(-1).join", "231\n312" },
        { "first last n", "puts [1, 2, 3].first(2).join\nputs [1, 2, 3].last(2).join", "12\n23" },
        { "take_while drop_while", "puts [1, 2, 3, 1].take_while { |x| x < 3 }.join\nputs [1, 2, 3, 1].drop_while { |x| x < 3 }.join", "12\n31" },
        { "inject", "puts [1, 2, 3].inject { |a, b| a * b }\nputs [1, 2, 3].inject(10) { |a, b| a + b }", "6\n16" },
        { "each_with_object", "r = [1, 2].each_with_object(List(Int32).new) { |x, acc| acc.Add(x * 3) }\nputs r.sum", "9" },
        { "range step", "puts (1..10).step(3).to_a.join(\",\")", "1,4,7,10" },
        { "find_index", "puts [5, 6, 7].find_index { |x| x > 5 }", "1" },
        { "uniq block", "puts [1, 2, 3, 4].uniq { |x| x % 2 }.join", "12" },
        { "sort comparator", "puts [1, 3, 2].sort { |a, b| b <=> a }.join", "321" },
        { "reverse_each", "[1, 2, 3].reverse_each { |x| print x }\nputs", "321" },
        { "cycle", "[1, 2].cycle(2) { |x| print x }\nputs", "1212" },
        { "combination permutation", "puts [1, 2, 3].combination(2).size\nputs [1, 2, 3].permutation(2).size", "3\n6" },
        { "array to_h", "h = [{\"a\", 1}, {\"b\", 2}].to_h\nputs h[\"b\"]", "2" },
        // Hash
        { "each_pair", "h = {\"a\" => 1}\nh.each_pair { |k, v| puts \"#{k}=#{v}\" }", "a=1" },
        { "hash map", "h = {\"a\" => 1, \"b\" => 2}\nputs h.map { |k, v| v * 10 }.sum", "30" },
        { "hash select reject", "h = {\"a\" => 1, \"b\" => 2}\nputs h.select { |k, v| v > 1 }.keys.join\nputs h.reject { |k, v| v > 1 }.keys.join", "b\na" },
        { "fetch", "h = {\"a\" => 1}\nputs h.fetch(\"a\", 0)\nputs h.fetch(\"z\", 9)", "1\n9" },
        { "dig", "h = {\"a\" => \"x\"}\nputs h.dig(\"z\").nil?", "true" },
        { "merge", "h = {\"a\" => 1}.merge({\"b\" => 2})\nputs h.size", "2" },
        { "key? value?", "h = {\"a\" => 1}\nputs h.key?(\"a\")\nputs h.value?(2)", "true\nfalse" },
        { "invert", "h = {\"a\" => 1}.invert\nputs h[1]", "a" },
        { "hash to_a", "puts({\"a\" => 1, \"b\" => 2}.to_a.size)", "2" },
        { "hash max_by", "h = {\"a\" => 1, \"b\" => 5}\nputs h.max_by { |k, v| v }.Key\nputs h.min_by { |k, v| v }.Key", "b\na" },
        { "hash sort_by", "h = {\"a\" => 3, \"b\" => 1}\nputs h.sort_by { |k, v| v }.map { |kv| kv.Key }.join", "ba" },
        { "transform", "h = {\"a\" => 1}\nputs h.transform_values { |v| v + 1 }[\"a\"]\nputs h.transform_keys { |k| k.upcase }[\"A\"]", "2\n1" },
        { "hash group_by sum count", "h = {\"a\" => 1, \"b\" => 2, \"c\" => 3}\nputs h.group_by { |k, v| v % 2 }.size\nputs h.sum { |k, v| v }\nputs h.count { |k, v| v > 1 }", "2\n6\n2" },
        // Numbers
        { "gcd lcm", "puts 12.gcd(18)\nputs 4.lcm(6)", "6\n12" },
        { "digits divmod", "puts 123.digits.join\nd = 7.divmod(2)\nputs d.join(\",\")", "321\n3,1" },
        { "clamp between", "puts 15.clamp(0, 10)\nputs 5.between?(1, 10)", "10\ntrue" },
        { "round floor ceil n", "puts 3.14159.round(2)\nputs 3.14159.floor(1)\nputs 3.11.ceil(1)", "3.14\n3.1\n3.2" },
        { "int step", "1.step(10, 4) { |i| print i, \" \" }\nputs", "1 5 9" },
        { "to_s base", "puts 255.to_s(16)\nputs 5.to_s(2)", "ff\n101" },
        { "math", "puts Math.Sqrt(16.0)\nputs 2.0.sqrt.round(3)", "4\n1.414" },
        // Range
        { "range ops", "r = 1..5\nputs r.to_a.size\nputs r.sum\nputs r.size\nputs r.include?(3)\nputs (1..5).cover?(9)", "5\n15\n5\ntrue\nfalse" },
        // IO / System / JSON
        { "files", "path = System.IO.Path.GetTempFileName()\nwrite_file(path, \"x\\ny\")\nputs file_exists?(path)\nputs read_file(path)\nputs read_lines(path).size", "true\nx\ny\n2" },
        { "sleep now env", "sleep(0.001)\nputs now.Year > 2000\nputs env(\"ALICE_NOPE_XYZ\").nil?", "true\ntrue" },
        { "json", "puts to_json([1, 2])\nputs to_json({\"k\" => [true]})\nn = parse_json(\"{\\\"a\\\": 5}\")\nputs n[\"a\"]", "[1,2]\n{\"k\":[true]}\n5" },
    };

    [Theory]
    [MemberData(nameof(Programs))]
    public void Runs(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }
}
