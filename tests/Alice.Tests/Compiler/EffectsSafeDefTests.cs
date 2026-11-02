using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// `safe def` (AL0212) is an allow-list: Alice code, values the method was handed, pure math and the non-ambient
/// runtime. Everything else is rejected, however it is reached (security audit F5, F6).
/// </summary>
[Collection("console")]
public class EffectsSafeDefTests
{
    static void Rejected(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0212", ex.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("safe def f(p : String) : String\n  write_file(p, \"x\")\n  \"\"\nend\nputs f(\"/tmp/x\")\n")]                // Prelude I/O
    [InlineData("safe def f : String\n  env(\"HOME\") || \"none\"\nend\nputs f\n")]                                              // environment
    [InlineData("safe def f(p : String) : String\n  read_file(p)\nend\nputs f(\"/etc/hostname\")\n")]
    [InlineData("safe def f : Int32\n  exit(3)\n  0\nend\nputs f\n")]
    [InlineData("safe def f(x : Int32) : Int32\n  puts x\n  x\nend\nputs f(1)\n")]                                              // console I/O
    [InlineData("safe def f : Bool\n  file_exists?(\"/etc/passwd\")\nend\nputs f\n")]
    public void Prelude_functions_with_ambient_authority_are_rejected(string src) => Rejected(src);

    [Fact]
    public void Lib_functions_are_rejected() =>
        Rejected("lib LibC\n  fun abs(x : Int32) : Int32\nend\nsafe def f : Int32\n  LibC.abs(-1)\nend\nputs f\n");

    [Theory]
    [InlineData("safe def f(p : String) : Int32\n  using r = System.IO.File.OpenText(p)\n    return r.ReadToEnd.size\n  end\n  0\nend\nputs f(\"/etc/hostname\")\n")]
    [InlineData("safe def f(p : String) : Int32\n  t = spawn { write_file(p, \"x\") }\n  t.wait\n  0\nend\nputs f(\"/tmp/x\")\n")]
    [InlineData("safe def f(p : String) : Int32\n  n = if p.size > 0\n    read_file(p).size\n  else\n    0\n  end\n  n\nend\nputs f(\"/etc/hostname\")\n")]
    public void Bodies_of_using_spawn_and_if_values_are_checked(string src) => Rejected(src);

    [Theory]
    [InlineData("safe def f : String\n  \"\".GetType().Name\nend\nputs f\n")]                                                   // reflection
    [InlineData("class A\n  def initialize\n    @a = 1\n  end\nend\nsafe def f(a : A) : Int32\n  a.instance_variable_set(\"@a\", 2)\n  0\nend\nputs f(A.new)\n")]
    [InlineData("class A\n  def initialize\n    @a = 1\n  end\nend\nsafe def f(a : A) : String\n  a.instance_variable_get(\"@a\").to_s\nend\nputs f(A.new)\n")]
    [InlineData("class A\n  def b : Int32\n    1\n  end\nend\nsafe def f(a : A) : String\n  a.send(\"b\").to_s\nend\nputs f(A.new)\n")]
    [InlineData("class A\n  def b : Int32\n    1\n  end\nend\nsafe def f(a : A) : String\n  a.public_send(\"b\").to_s\nend\nputs f(A.new)\n")]
    [InlineData("class S\n  def method_missing(name : String, args : Array(Object)) : Int32\n    write_file(name, \"x\")\n    0\n  end\nend\nsafe def f(s : S) : Int32\n  s.store(1)\nend\nputs f(S.new)\n")]
    public void Reflection_and_dynamic_entry_points_are_rejected(string src) => Rejected(src);

    [Theory]
    [InlineData("safe def f(p : String) : String\n  System.IO.File.ReadAllText(p)\nend\nputs f(\"/etc/hostname\")\n")]
    [InlineData("safe def f : Int64\n  System.DateTime.Now.Ticks\nend\nputs f\n")]
    public void Dotnet_statics_outside_the_allow_list_are_rejected(string src) => Rejected(src);

    [Fact]
    public void Ambient_prelude_functions_are_marked()
    {
        // defence in depth: the runtime itself says which entry points reach the machine
        string[] ambient = ["puts", "print", "p", "gets", "exit", "read_file", "write_file", "file_exists_q", "read_lines", "sleep", "now", "env",
            "send", "public_send", "respond_to_q", "define_method", "instance_variable_get", "instance_variable_set", "instance_variables", "methods"];
        var all = typeof(Alice.Runtime.Prelude).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        foreach (var name in ambient)
            Assert.All(all.Where(m => m.Name == name).ToList(), m => Assert.True(m.IsDefined(typeof(Alice.Runtime.AmbientAttribute), false), name));
        Assert.DoesNotContain(all, m => m.Name is "format" or "raise" or "to_json" && m.IsDefined(typeof(Alice.Runtime.AmbientAttribute), false));
        Rejected("safe def f(p : String) : Int32\n  Prelude.write_file(p, \"x\")\n  0\nend\nputs f(\"/tmp/x\")\n");
    }

    public static TheoryData<string, string> SafePrograms => new()
    {
        { "safe def f(xs : Array(Int32)) : Int32\n  xs.sum + Math.sqrt(16.0).to_i\nend\nputs f([1, 2, 3])\n", "10" },
        { "safe def f(xs : Array(Int32)) : Array(Int32)\n  xs.map { |x| x * 2 }\nend\nputs f([1, 2]).join(\",\")\n", "2,4" },
        { "safe def f(n : Int32) : String\n  ys = List(Int32).new\n  n.times { |i| ys << i }\n  \"#{ys.size}:#{ys.sum}\"\nend\nputs f(4)\n", "4:6" },
        { "def g(x : Int32) : Int32\n  x * x\nend\nsafe def f(x : Int32) : Int32\n  Int32::MAX - Int32::MAX + g(x)\nend\nputs f(3)\n", "9" },
        { "safe def f(s : String) : String\n  s.upcase + s.size.to_s\nend\nputs f(\"ab\")\n", "AB2" },
    };

    [Theory, MemberData(nameof(SafePrograms))]
    public void Alice_code_on_given_values_stays_safe(string src, string expected) =>
        Assert.Equal(expected, AliceCompiler.Run(src).Trim());
}
