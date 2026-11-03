using Alice.Compiler;
using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>
/// Prelude methods closing the runtime-only gaps of the Crystal/Ruby gap report (part 3: #8 Hash, #10 Enum, #13 String,
/// #14 Range, #15 Tuple, #18 object protocol, Array and Char extras, #24 Prelude names): Alice source -> stdout.
/// </summary>
[Collection("console")]
public class StdlibCompletionTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        // #8 Hash
        { "hash membership", """
            h = {"a" => 1, "b" => 2}
            puts h.include?("a"), h.member?("a"), h.includes?("z"), h.has_value?(2), h.has_value?(9)
            """, "true\ntrue\nfalse\ntrue\nfalse" },
        { "hash key length store", """
            h = {"a" => 1, "b" => 2}
            puts h.key(2)
            puts h.key(9).nil?
            puts h.length
            puts h.store("c", 3)
            puts h.size
            """, "b\ntrue\n2\n3\n3" },
        { "hash fetch block", """
            h = {"a" => 1}
            puts h.fetch("a") { 7 }
            puts h.fetch("x") { 7 }
            puts h.fetch("xyz") { |k| k.size }
            """, "1\n7\n3" },
        { "hash delete", """
            h = {"a" => 1, "b" => 2}
            puts h.delete("a")
            puts h.size
            puts h.delete("zz").nil?
            puts h.delete("zz") { |k| k.size }
            s = {"k" => "v"}
            puts s.delete("k"), s.delete("k").nil?
            """, "1\n1\ntrue\n2\nv\ntrue" },
        { "hash each_key each_value", """
            h = {"a" => 1, "b" => 2}
            h.each_key { |k| print k }
            h.each_value { |v| print v }
            puts
            """, "ab12" },
        { "hash dig nested", """
            n = {"a" => {"b" => {"c" => "deep"}}}
            puts n.dig("a", "b", "c")
            puts n.dig("a", "x", "c").nil?
            m = {"a" => {"b" => 5}}
            puts m.dig("a", "b")
            puts m.dig("a", "q").nil?
            puts({"k" => 1}.dig("zz").nil?)
            """, "deep\ntrue\n5\ntrue\ntrue" },
        { "hash update merge block", """
            h = {"a" => 1, "b" => 2}
            puts h.merge({"b" => 9}) { |k, a, b| a + b }["b"]
            puts h["b"]
            puts h.update({"c" => 3}).size
            puts h.update({"a" => 5}) { |k, a, b| a * 100 + b }["a"]
            """, "11\n2\n3\n105" },
        { "hash filter_map to_h block", """
            h = {"a" => 1, "b" => 3}
            puts h.filter_map { |k, v| v > 2 ? k : nil }.join
            puts h.to_h { |k, v| {k.upcase, v * 10} }["B"]
            """, "b\n30" },
        { "hash values_at slice except filter clear", """
            h = {"a" => 1, "b" => 2, "c" => 3}
            puts h.values_at("a", "c").inspect
            puts h.slice("b", "z").size, h.except("b").size, h.filter { |k, v| v > 1 }.size
            puts h.clear.size
            """, "[1, 3]\n1\n2\n2\n0" },
        { "hash pair first last to_a", """
            h = {"b" => 2, "a" => 1}
            puts h.min_by { |k, v| v }.first
            puts h.sort_by { |k, v| -v }.first.last
            kv = h.first
            puts kv.key, kv.value
            p kv.to_a
            """, "a\n2\nb\n2\n{\"b\", 2}" },
        { "hash pairs to_h", """
            h = {"b" => 2, "a" => 1}
            puts h.sort_by { |k, v| v }.to_h.keys.join
            """, "ab" },
        { "hash default value", """
            d = DefaultHash(String, Int32).new(0)
            d["m"] += 1
            d["m"] += 1
            puts d["m"], d["other"], d.size
            """, "2\n0\n1" },
        { "hash prints like crystal", """
            puts({"a" => 1, "b" => 2})
            p({"a" => [1]})
            puts({} of String => Int32)
            """, "{\"a\" => 1, \"b\" => 2}\n{\"a\" => [1]}\n{}" },
        // #10 Enum
        { "enum to_i value", """
            enum Color
              Red = 1
              Green = 2
              Blue = 4
            end
            puts Color::Green.to_i
            puts Color::Blue.value
            x = Color::Red.to_i
            puts x + 1
            """, "2\n4\n2" },
        { "enum flags", """
            @[Flags]
            enum Perm
              Read = 1
              Write = 2
              Exec = 4
            end
            q = Perm::Read | Perm::Write
            puts q.includes?(Perm::Read), q.includes?(Perm::Exec), q.has_flag(Perm::Write)
            """, "true\nfalse\ntrue" },
        { "enum class methods", """
            enum Color
              Red = 1
              Green = 2
            end
            puts Color.values.size, Color.values[1], Color.names.join(",")
            puts Color.parse("green"), Color.parse?("x").nil?
            puts Color.from_value(1), Color.from_value?(2), Color.from_value?(9).nil?
            """, "2\nGreen\nRed,Green\nGreen\ntrue\nRed\nGreen\ntrue" },
        // #13 String
        { "lstrip rstrip", "puts \" ab \".lstrip + \"|\"\nputs \"|\" + \" ab \".rstrip", "ab |\n| ab" },
        { "bytesize codepoints", "puts \"héllo\".bytesize\nputs \"ab\".codepoints.join(\",\")", "6\n97,98" },
        { "blank?", "puts \" \".blank?, \"\".blank?, \"a\".blank?", "true\ntrue\nfalse" },
        { "camelcase underscore", "puts \"foo_bar\".camelcase, \"foo_bar\".camelcase(lower: true), \"FooBar\".underscore, \"HTTPServer\".underscore", "FooBar\nfooBar\nfoo_bar\nhttp_server" },
        { "to_i base", "puts \"ff\".to_i(16), \"-101\".to_i(2), \"z\".to_i(36)", "255\n-5\n35" },
        { "to_i?", "puts \"12\".to_i?\nputs \"ab\".to_i?(16)", "12\n171" },
        { "each_char without block", "puts \"abc\".each_char.to_a.size\nputs \"abc\".each_char.first", "3\na" },
        { "pad with string", "puts \"ab\".ljust(6, \"12\") + \"|\"\nputs \"ab\".rjust(5, \"xy\")\nputs \"ab\".center(7, \"*-\")", "ab1212|\nxyxab\n*-ab*-*" },
        { "match data", """
            m = "2024-05".match(/(\d+)-(?<m>\d+)/)
            puts m.not_nil![0], m.not_nil![1], m.not_nil!["m"]
            puts "x".match(/\d/).nil?
            r = /b(\d)/.match("ab7c").not_nil!
            puts r[1], r.pre_match, r.post_match, r.begin(0), r.captures.size
            """, "2024-05\n05\n05\ntrue\n7\na\nc\n1\n1" },   // Ruby: once a group is named, (\d+) does not capture
        { "gsub block", "puts \"a1b22\".gsub(/\\d+/) { |d| \"<#{d}>\" }\nputs \"a1b22\".sub(/\\d+/) { |d| \"<#{d}>\" }", "a<1>b<22>\na<1>b22" },
        { "string dup", "puts \"a\".dup + \"b\"", "ab" },
        // Char
        { "char methods", """
            c = 'a'
            puts c.upcase, 'A'.downcase, c.letter?, c.digit?, '1'.digit?, ' '.whitespace?, c.alphanumeric?, c.succ
            """, "A\na\ntrue\nfalse\ntrue\ntrue\ntrue\nb" },
        // #14 Range
        { "string range", "puts (\"a\"..\"e\").to_a.join\nputs (\"a\"...\"e\").to_a.join\nputs (\"y\"..\"ab\").to_a.join(\",\")", "abcde\nabcd\ny,z,aa,ab" },
        { "char range", "puts ('a'..'d').to_a.join", "abcd" },
        { "reverse_each step no block", "puts (1..3).reverse_each.to_a.join\nputs 1.step(10, 3).to_a.join(\",\")", "321\n1,4,7,10" },
        { "comparable range", "puts (1.0..2.0).step(0.5).to_a.join(\",\")\nputs (1.0..2.0).include?(1.5), (1.0...2.0).cover?(2.0)", "1.0,1.5,2.0\ntrue\nfalse" },
        // #15 Tuple
        { "tuple size print", "t = {1, \"one\", 2.5}\nputs t.size\nputs t\np t", "3\n{1, \"one\", 2.5}\n{1, \"one\", 2.5}" },
        // #18 object protocol
        { "p many and nil", "p 1, \"two\"\np nil\nx = pp [1, [2, 3]]\nputs x.size", "1\n\"two\"\nnil\n[1, [2, 3]]\n2" },
        { "hash eql equal", """
            puts 5.hash == 5.hash, "a".hash == "a".hash, [1, 2].hash == [1, 2].hash
            puts [1, 2].eql?([1, 2]), [1, 2].equal?([1, 2]), "a".eql?("a"), 1.eql?(1)
            a = [1]
            puts a.equal?(a)
            """, "true\ntrue\ntrue\ntrue\nfalse\ntrue\ntrue\ntrue" },
        { "dup clone", """
            a = [1, 2]
            b = a.dup
            b[0] = 9
            puts a[0], b[0], a.clone.size
            h = {"a" => 1}
            g = h.dup
            g["b"] = 2
            puts h.size, g.size
            l = [1] of Int32
            m = l.clone
            m.push(2)
            puts l.size, m.size
            """, "1\n9\n2\n1\n2\n1\n2" },
        // Array extras
        { "flatten depth", "puts [1, [2, [3]]].flatten(1).inspect\nputs [1, [2, [3]]].flatten.inspect\nputs [[1], [2]].flatten(1).inspect", "[1, 2, [3]]\n[1, 2, 3]\n[1, 2]" },
        { "max min n", "puts [3, 1, 2].max(2).inspect, [3, 1, 2].min(2).inspect", "[3, 2]\n[1, 2]" },
        { "slice_when to_set", "puts [1, 2, 4, 5, 7].slice_when { |a, b| b != a + 1 }.size\nputs [1, 2, 2].to_set.size", "3\n2" },
        { "grep regex", "puts [\"ab\", \"cd\", \"a1\"].grep(/\\d/).inspect\nputs [\"ab\", \"a1\"].grep_v(/\\d/).inspect", "[\"a1\"]\n[\"ab\"]" },
        { "find_index value", "puts [5, 6, 7].find_index(6), [5, 6].find_index(9)", "1\n-1" },
        { "transpose", "puts [[1, 2], [3, 4]].transpose.inspect", "[[1, 3], [2, 4]]" },
        { "to_h pairs and block", "puts [[1, 2], [3, 4]].to_h[3]\nputs [1, 2, 3].to_h { |x| {x, x * 2} }[3]", "4\n6" },
        { "sum init", "puts [1, 2, 3].sum(10)\nputs [0.5].sum(1.0)", "16\n1.5" },
        { "array filter_map", "puts [1, 2, 3].filter_map { |x| x.odd? ? \"a#{x}\" : nil }.join(\",\")", "a1,a3" },
        // #24 Prelude names
        { "ENV", """
            puts ENV["PATH"].size > 0
            puts ENV.fetch("ALICE_NOPE_ENV", "dflt")
            puts ENV.has_key?("ALICE_NOPE_ENV")
            ENV["ALICE_SET_ENV"] = "v1"
            puts ENV["ALICE_SET_ENV"], env("ALICE_SET_ENV")
            """, "true\ndflt\nfalse\nv1\nv1" },
        { "STDOUT", "STDOUT.puts \"x\"\nSTDOUT.print \"a\", \"b\"\nSTDOUT.puts\nSTDOUT.flush", "x\nab" },
    };

    [Theory]
    [MemberData(nameof(Programs))]
    public void Runs(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }

    [Fact]
    public void At_exit_handlers_run_last_first_once()
    {
        Assert.Equal("main", Run("at_exit { puts \"bye\" }\nat_exit { puts \"bye2\" }\nputs \"main\""));
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { Prelude.run_at_exit(); Prelude.run_at_exit(); }
        finally { Console.SetOut(old); }
        Assert.Equal("bye2\nbye", sw.ToString().Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Bad_input_raises()
    {
        Assert.ThrowsAny<Exception>(() => Run("puts \"12xyz\".to_i(16)"));
        Assert.ThrowsAny<Exception>(() => Run("puts [[1, 2], [3]].transpose.size"));
        Assert.ThrowsAny<Exception>(() => Run("puts (1.0..2.0).to_a.size"));
    }

    // runtime halves the compiler lowers to (Hash(K, V).new(default), Color.values, String.build, Array * n)
    [Fact]
    public void Hash_new_default_and_block()
    {
        var d = Prelude.hash_new<string, int>(0);
        d["a"] += 1;
        Assert.Equal(1, d["a"]);
        Assert.Equal(0, d["b"]);
        Assert.False(d.ContainsKey("b"));
        Assert.Equal(0, Prelude.hash_get<string, int>(d, "zz"));
        var b = Prelude.hash_new<string, int>((h, k) => h[k] = k.Length);
        Assert.Equal(4, b["four"]);
        Assert.True(b.ContainsKey("four"));
        Assert.Throws<KeyNotFoundException>(() => Prelude.hash_get(new Dictionary<string, int>(), "x"));
    }

    enum Shade { Light = 1, Dark = 5 }

    [Fact]
    public void Enum_class_methods()
    {
        Assert.Equal([Shade.Light, Shade.Dark], Prelude.enum_values<Shade>());
        Assert.Equal(["Light", "Dark"], Prelude.enum_names<Shade>());
        Assert.Equal(Shade.Dark, Prelude.enum_parse<Shade>("dark"));
        Assert.Null(Prelude.enum_parse_q<Shade>("x"));
        Assert.Equal(Shade.Dark, Prelude.enum_from_value<Shade>(5));
        Assert.Null(Prelude.enum_from_value_q<Shade>(2));
        Assert.Throws<ArgumentException>(() => Prelude.enum_from_value<Shade>(2));
        Assert.Equal(5, Shade.Dark.to_i());
    }

    [Fact]
    public void String_build_and_array_mul()
    {
        Assert.Equal("a1", Prelude.string_build(io => Prelude.append_op(Prelude.append_op(io, "a"), 1)));
        Assert.Equal([1, 2, 1, 2], Prelude.arr_mul(new[] { 1, 2 }, 2));
        Assert.Equal("1,2", Prelude.arr_mul(new[] { 1, 2 }, ","));
    }
}
