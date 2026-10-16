using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Module system: require, Ruby-style mixins (include/extend), module functions, nesting.</summary>
[Collection("console")]
public class ModuleTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Fact]
    public void Include_mixes_instance_methods_into_the_class()
    {
        var src = "module Greeter\n  def greet : String\n    \"hi #{name}\"\n  end\n  def shout : String\n    greet.upcase\n  end\nend\nclass Person\n  include Greeter\n  getter name : String\n  def initialize(@name : String)\n  end\nend\np = Person.new(\"bob\")\nputs p.greet\nputs p.shout";
        Assert.Equal("hi bob\nHI BOB", Run(src));
    }

    [Fact]
    public void Extend_adds_class_methods()
    {
        var src = "module Factory\n  def make_default : String\n    \"made\"\n  end\nend\nclass Widget\n  extend Factory\nend\nputs Widget.make_default";
        Assert.Equal("made", Run(src));
    }

    [Fact]
    public void Module_functions_and_nesting()
    {
        var src = "module Geo\n  PI2 : Float64 = 6.28\n  module Units\n    def self.km(m : Float64) : Float64\n      m / 1000.0\n    end\n  end\n  def self.circle(r : Float64) : Float64\n    PI2 * r\n  end\nend\nputs Geo.circle(1.0)\nputs Geo::Units.km(2500.0)";
        Assert.Equal("6.28\n2.5", Run(src));
    }

    [Fact]
    public void Using_a_module_imports_its_functions()
        => Assert.Equal("5\n4", Run("module Tools\n  def self.five : Int32\n    5\n  end\nend\nusing Tools\nusing System.Text\nputs five\nputs StringBuilder.new(\"abcd\").Length"));

    [Fact]
    public void Same_mixin_in_several_classes()
    {
        var src = "module Sized\n  def big? : Bool\n    size > 3\n  end\nend\nclass A\n  include Sized\n  def size : Int32\n    5\n  end\nend\nclass B\n  include Sized\n  def size : Int32\n    1\n  end\nend\nputs A.new.big?\nputs B.new.big?";
        Assert.Equal("true\nfalse", Run(src));
    }

    [Fact]
    public void Require_loads_files_transitively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-req-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        File.WriteAllText(Path.Combine(dir, "lib", "math.al"), "require \"./util\"\nmodule MyMath\n  def self.twice(x : Int32) : Int32\n    Util.inc(x) * 2 - 2\n  end\nend\n");
        File.WriteAllText(Path.Combine(dir, "lib", "util.al"), "module Util\n  def self.inc(x : Int32) : Int32\n    x + 1\n  end\nend\n");
        File.WriteAllText(Path.Combine(dir, "main.al"), "require \"./lib/math\"\nputs MyMath.twice(21)\n");
        var files = AliceCompiler.LoadWithRequires([Path.Combine(dir, "main.al")]);
        Assert.Equal(3, files.Count);
        var image = AliceCompiler.Emit(AliceCompiler.TranspileAll(files), "req", exe: true);
        Assert.Equal("42", AliceCompiler.Invoke(AliceCompiler.Load(image), []).Trim());
    }

    [Fact]
    public void Missing_require_is_a_clear_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-req-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.al"), "require \"./nope\"\n");
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.LoadWithRequires([Path.Combine(dir, "main.al")]));
        Assert.Contains("cannot find", ex.Message);
    }
}
