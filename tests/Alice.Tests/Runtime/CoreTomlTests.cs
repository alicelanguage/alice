using System.Globalization;
using System.Text.Json;
using Alice.Core.Toml;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Toml: TOML 1.0 values, table rules, errors, the writer, and the toml-test suite.</summary>
public class CoreTomlTests
{
    [Fact]
    public void Parses_the_toml_1_0_value_kinds()
    {
        var t = Toml.Parse(""""
            # comment
            title = "TOML \"x\" \u00e9 \U0001F600"
            lit = 'C:\Users\x'
            int = +1_000
            neg = -17
            hex = 0xDEAD_beef
            oct = 0o755
            bin = 0b1101
            flt = 6.626e-34
            exp = 1e3
            frac = -0.5
            pinf = inf
            nan = -nan
            yes = true
            odt = 1979-05-27T07:32:00.999999-07:00
            odt2 = 1979-05-27 07:32:00Z
            ldt = 1979-05-27T07:32:00
            ld = 1979-05-27
            lt = 00:32:00.5
            arr = [ 1, "two", [3.0], { four = 4 }, ]
            ml = """
            Roses \
              are red"""
            mll = '''
            raw \n'''
            "quoted key" = 1
            a.b.c = 2
            """");
        Assert.Equal("TOML \"x\" é 😀", t["title"].GetString());
        Assert.Equal("C:\\Users\\x", t["lit"].GetString());
        Assert.Equal(1000, t["int"].GetInt64());
        Assert.Equal(-17, t["neg"].GetInt64());
        Assert.Equal(0xDEADBEEF, t["hex"].GetInt64());
        Assert.Equal(493, t["oct"].GetInt64());
        Assert.Equal(13, t["bin"].GetInt64());
        Assert.Equal(6.626e-34, t["flt"].GetDouble());
        Assert.Equal(1000.0, t["exp"].GetDouble());
        Assert.Equal(-0.5, t["frac"].GetDouble());
        Assert.Equal(double.PositiveInfinity, t["pinf"].GetDouble());
        Assert.True(double.IsNaN(t["nan"].GetDouble()));
        Assert.True(t["yes"].GetBoolean());
        Assert.Equal(new DateTimeOffset(1979, 5, 27, 7, 32, 0, 999, TimeSpan.FromHours(-7)).AddTicks(9990), t["odt"].GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(1979, 5, 27, 7, 32, 0, TimeSpan.Zero), t["odt2"].GetDateTimeOffset());
        Assert.Equal(new DateTime(1979, 5, 27, 7, 32, 0), t["ldt"].GetDateTime());
        Assert.Equal(new DateOnly(1979, 5, 27), t["ld"].GetDate());
        Assert.Equal(new TimeOnly(0, 32, 0, 500), t["lt"].GetTime());
        var arr = t["arr"];
        Assert.Equal(4, arr.AsArray()!.Count);
        Assert.Equal("two", arr[1].GetString());
        Assert.Equal(3.0, arr[2][0].GetDouble());
        Assert.Equal(4, arr[3]["four"].GetInt64());
        Assert.Equal("Roses are red", t["ml"].GetString());
        Assert.Equal("raw \\n", t["mll"].GetString());
        Assert.Equal(1, t["quoted key"].GetInt64());
        Assert.Equal(2, t["a"]["b"]["c"].GetInt64());
        Assert.Equal(2, ((TomlAny)t).Dig("a", "b", "c").GetInt64());
        Assert.False(((TomlAny)t)["missing"]["x"].Exists);
    }

    [Fact]
    public void Tables_and_arrays_of_tables()
    {
        var t = Toml.Parse("""
            [package]
            name = "demo"
            [dependencies]
            foo = { path = "../foo" }
            [[bin]]
            name = "a"
            [[bin]]
            name = "b"
            [x.y.z]
            v = 1
            [x]
            w = 2
            """);
        Assert.Equal("demo", t["package"]["name"].GetString());
        Assert.Equal("../foo", t["dependencies"]["foo"]["path"].GetString());
        Assert.Equal("b", t["bin"].AsArray()![1]["name"].GetString());
        Assert.Equal(1, t["x"]["y"]["z"]["v"].GetInt64());
        Assert.Equal(2, t["x"]["w"].GetInt64());
        Assert.Equal(["package", "dependencies", "bin", "x"], t.Keys);
    }

    [Theory]
    [InlineData("a = 1\na = 2", 2, "duplicate key 'a'")]
    [InlineData("[t]\n[t]", 2, "defined twice")]
    [InlineData("a = {b = 1}\n[a]", 2, "already defined")]
    [InlineData("a = {b = 1}\na.c = 2", 2, "dotted keys cannot extend")]
    [InlineData("[a.b]\nz = 1\n[a]\nb.c = 2", 4, "dotted keys cannot extend")]
    [InlineData("x = 01", 1, "leading zeros")]
    [InlineData("x = 1__0", 1, "underscores")]
    [InlineData("x = \"abc", 1, "unterminated string")]
    [InlineData("x = 1979-13-01", 1, "invalid date")]
    [InlineData("x = {a = 1,}", 1, "trailing comma")]
    [InlineData("x = [1 2]", 1, "expected ',' or ']'")]
    [InlineData("x = abc", 1, "strings need quotes")]
    [InlineData("x = 9223372036854775808", 1, "out of range")]
    [InlineData("x = \"\\q\"", 1, "invalid escape")]
    [InlineData("x = 1 y = 2", 1, "expected end of line")]
    public void Errors_name_the_line(string text, int line, string reason)
    {
        var e = Assert.Throws<TomlParseException>(() => Toml.Parse(text));
        Assert.Equal(line, e.Line);
        Assert.Contains(reason, e.Reason);
    }

    [Fact]
    public void Writer_round_trips()
    {
        string src = """
            title = "x \"q\" \t"
            n = 42
            f = 1.0
            when = 1979-05-27T07:32:00Z
            list = [1, 2, 3]
            inline = { a = 1, "b c" = "d" }

            [server]
            host = "localhost"

            [server.tls]
            on = true

            [[item]]
            id = 1

            [[item]]
            id = 2
            """;
        var t = Toml.Parse(src);
        string written = Toml.Write(t);
        var again = Toml.Parse(written);
        Assert.True(((TomlAny)t).Equals(again), written);
        Assert.Contains("[server.tls]", written);
        Assert.Contains("[[item]]", written);
        Assert.Contains("inline = { a = 1, \"b c\" = \"d\" }", written);
    }

    [Fact]
    public void To_object_gives_plain_dotnet_values()
    {
        var d = Toml.Parse("a = 1\nb = [\"x\"]\n[t]\nc = true").ToDictionary();
        Assert.Equal(1L, d["a"]);
        Assert.Equal(new List<object> { "x" }, d["b"]);
        Assert.Equal(true, ((Dictionary<string, object>)d["t"])["c"]);
    }

    /// <summary>The toml-test suite (https://github.com/toml-lang/toml-test), TOML 1.0 file list: every valid file
    /// must parse to its JSON, every invalid file must fail. Runs when <c>ALICE_TOML_TEST_DIR</c> points at the
    /// suite's <c>tests</c> directory (not vendored); otherwise it checks nothing.</summary>
    [Fact]
    public void Toml_test_suite_1_0()
    {
        string? dir = Environment.GetEnvironmentVariable("ALICE_TOML_TEST_DIR");
        if (dir is null || !File.Exists(Path.Combine(dir, "files-toml-1.0.0"))) return;
        var failures = new List<string>();
        int valid = 0, invalid = 0;
        foreach (var rel in File.ReadAllLines(Path.Combine(dir, "files-toml-1.0.0")))
        {
            if (!rel.EndsWith(".toml", StringComparison.Ordinal)) continue;
            string path = Path.Combine(dir, rel);
            byte[] bytes = File.ReadAllBytes(path);
            if (rel.StartsWith("valid/", StringComparison.Ordinal))
            {
                valid++;
                try
                {
                    var t = Toml.Parse(bytes);
                    using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
                    string? diff = Compare(t, json.RootElement, "");
                    if (diff is not null) failures.Add($"{rel}: {diff}");
                }
                catch (Exception e) { failures.Add($"{rel}: {e.Message}"); }
            }
            else
            {
                invalid++;
                try { Toml.Parse(bytes); failures.Add($"{rel}: accepted"); }
                catch (TomlParseException) { }
                catch (Exception e) { failures.Add($"{rel}: wrong exception {e.GetType().Name}: {e.Message}"); }
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {valid} valid + {invalid} invalid failed:\n" + string.Join("\n", failures));
    }

    static string? Compare(TomlAny v, JsonElement j, string path)
    {
        if (j.ValueKind == JsonValueKind.Array)
        {
            if (v.AsArray() is not { } a) return $"{path}: expected an array, got {v.Kind}";
            if (a.Count != j.GetArrayLength()) return $"{path}: array length {a.Count} != {j.GetArrayLength()}";
            int i = 0;
            foreach (var e in j.EnumerateArray()) { if (Compare(a[i], e, $"{path}[{i}]") is { } d) return d; i++; }
            return null;
        }
        if (j.ValueKind != JsonValueKind.Object) return $"{path}: unexpected JSON {j.ValueKind}";
        JsonElement value = default;
        bool tagged = j.TryGetProperty("type", out var type) && j.TryGetProperty("value", out value) && type.ValueKind == JsonValueKind.String
            && j.EnumerateObject().Count() == 2 && value.ValueKind == JsonValueKind.String;
        if (!tagged)
        {
            if (v.AsTable() is not { } t) return $"{path}: expected a table, got {v.Kind}";
            int n = 0;
            foreach (var p in j.EnumerateObject())
            {
                n++;
                if (!t.TryGetValue(p.Name, out var sub)) return $"{path}.{p.Name}: missing";
                if (Compare(sub, p.Value, $"{path}.{p.Name}") is { } d) return d;
            }
            return n == t.Count ? null : $"{path}: {t.Count} keys, expected {n}";
        }
        string s = value.GetString()!;
        var inv = CultureInfo.InvariantCulture;
        bool ok = type.GetString() switch
        {
            "string" => v.Kind == TomlKind.String && v.GetString() == s,
            "integer" => v.Kind == TomlKind.Integer && v.GetInt64() == long.Parse(s, inv),
            "float" => v.Kind == TomlKind.Float && FloatEq(v.GetDouble(), s),
            "bool" => v.Kind == TomlKind.Boolean && v.GetBoolean() == (s == "true"),
            "datetime" => v.Kind == TomlKind.OffsetDateTime && v.GetDateTimeOffset() == DateTimeOffset.Parse(s, inv),
            "datetime-local" => v.Kind == TomlKind.LocalDateTime && v.GetDateTime() == DateTime.Parse(s, inv),
            "date-local" => v.Kind == TomlKind.LocalDate && v.GetDate() == DateOnly.Parse(s, inv),
            "time-local" => v.Kind == TomlKind.LocalTime && v.GetTime() == TimeOnly.Parse(s, inv),
            _ => false,
        };
        return ok ? null : $"{path}: expected {type.GetString()} {s}, got {v.Kind} {v}";
    }

    static bool FloatEq(double d, string s) => s switch
    {
        "nan" or "+nan" or "-nan" => double.IsNaN(d),
        "inf" or "+inf" => double.IsPositiveInfinity(d),
        "-inf" => double.IsNegativeInfinity(d),
        _ => d == double.Parse(s, CultureInfo.InvariantCulture),
    };
}
