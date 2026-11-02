using Alice.Compiler;
using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>
/// `instance_variable_get/set` (Ruby reflection) see only what Alice code could see: every ivar of an Alice type
/// (marked [AliceType] by the compiler), public fields of any other type. Readonly fields are never written, and
/// arrays held by readonly or foreign fields come back as copies (security audit F4).
/// </summary>
[Collection("console")]
public class ReflectionSecurityTests
{
    sealed class Foreign
    {
#pragma warning disable CS0414
        private int secret = 1;
#pragma warning restore CS0414
        public int Pub = 2;
        public readonly int Ro = 3;
        public readonly int[] Shape = [1, 2];
        public int Secret => secret;
    }

    [AliceType]
    sealed class Mine
    {
        private int x = 1;
        private readonly int y = 2;
        private int[] arr = [1, 2, 3];
        public int X => x;
        public int Y => y;
        public int[] Arr => arr;
    }

    [Fact]
    public void Private_fields_of_foreign_types_are_hidden()
    {
        var f = new Foreign();
        Assert.Throws<MissingFieldException>(() => f.instance_variable_get("@secret"));
        Assert.Throws<MissingFieldException>(() => f.instance_variable_set("@secret", 9));
        Assert.Equal(1, f.Secret);
        Assert.DoesNotContain("@secret", f.instance_variables());
        Assert.Contains("@Pub", f.instance_variables());
    }

    [Fact]
    public void Public_fields_of_foreign_types_stay_reachable()
    {
        var f = new Foreign();
        Assert.Equal(2, f.instance_variable_get("@Pub"));
        f.instance_variable_set("@Pub", 5);
        Assert.Equal(5, f.Pub);
        Assert.Equal(3, f.instance_variable_get("@Ro"));
    }

    [Fact]
    public void Readonly_fields_are_never_written()
    {
        Assert.Throws<FieldAccessException>(() => new Foreign().instance_variable_set("@Ro", 7));
        var m = new Mine();
        Assert.Throws<FieldAccessException>(() => m.instance_variable_set("@y", 7));
        Assert.Equal(2, m.Y);
    }

    [Fact]
    public void Arrays_of_readonly_or_foreign_fields_are_copies()
    {
        var f = new Foreign();
        var s = (int[])f.instance_variable_get("@Shape")!;
        s[0] = 99;
        Assert.Equal(1, f.Shape[0]);
    }

    [Fact]
    public void Alice_ivars_keep_ruby_reflection()
    {
        var m = new Mine();
        Assert.Equal(1, m.instance_variable_get("@x"));
        m.instance_variable_set("@x", 5);
        Assert.Equal(5, m.X);
        ((int[])m.instance_variable_get("@arr")!)[0] = 7;   // an Alice ivar is the object itself, as in Ruby
        Assert.Equal(7, m.Arr[0]);
        Assert.Contains("@x", m.instance_variables());
    }

    [Fact]
    public void Runtime_internals_are_not_reachable()
    {
        var mx = new Mutex<int[]>([1, 2]);
        Assert.Throws<MissingFieldException>(() => mx.instance_variable_get("@_value"));
        Assert.Throws<MissingFieldException>(() => mx.instance_variable_set("@_value", new int[0]));
        Assert.Empty(mx.instance_variables());
    }

    [Fact]
    public void Compiled_alice_classes_are_marked()
    {
        var src = "class P\n  def initialize\n    @x = 1\n  end\nend\np = P.new\np.instance_variable_set(\"@x\", 5)\nputs p.instance_variable_get(\"@x\")\nm = Mutex(Int32).new(3)\nbegin\n  m.instance_variable_get(\"@_value\")\n  puts \"leak\"\nrescue e\n  puts \"hidden\"\nend\n";
        Assert.Equal("5\nhidden", AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n"));
    }
}
