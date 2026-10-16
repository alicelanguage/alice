using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Ruby DSL capabilities: implicit-self blocks, class-body macros, builders, method_missing, kwargs.</summary>
[Collection("console")]
public class RubyDslTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    public static TheoryData<string, string, string> Programs => new()
    {
        { "yield builder", "class Html\n  getter out : String\n  def initialize\n    @out = \"\"\n  end\n  def tag(name : String)\n    @out += \"<#{name}>\"\n    yield\n    @out += \"</#{name}>\"\n  end\nend\nh = Html.new\nh.tag(\"p\") { h.tag(\"b\") { } }\nputs h.out", "<p><b></b></p>" },
        { "instance_eval implicit self", "class Config\n  property name : String = \"\"\n  property port : Int32 = 0\n  def named(n : String)\n    @name = n\n  end\n  def listen(p : Int32)\n    @port = p\n  end\nend\nc = Config.new\nc.instance_eval do\n  named \"web\"\n  listen 8080\nend\nputs \"#{c.name}:#{c.port}\"", "web:8080" },
        { "configure with yield self", "class App\n  property env : String = \"dev\"\n  def self.configure\n    a = App.new\n    yield a\n    a\n  end\nend\napp = App.configure { |c| c.env = \"prod\" }\nputs app.env", "prod" },
        { "class body macro", "class Model\n  def self.fields : List(String)\n    @@fields ||= List(String).new\n  end\n  def self.field(n : String)\n    fields.Add(n)\n  end\nend\nclass User < Model\n  field \"name\"\n  field \"email\"\nend\nputs Model.fields.join(\",\")", "name,email" },
        { "method_missing dsl", "class Rec\n  getter calls : List(String)\n  def initialize\n    @calls = List(String).new\n  end\n  def method_missing(name : String, args : Array(Object?)) : Object?\n    @calls.Add(name)\n    nil\n  end\nend\nr = Rec.new\nr.send(\"get\", \"/x\")\nr.send(\"post\", \"/y\")\nputs r.calls.join(\",\")", "get,post" },
        { "keyword args", "def route(path : String, method : String = \"GET\", auth : Bool = false) : String\n  \"#{method} #{path} #{auth}\"\nend\nputs route(\"/a\", auth: true)\nputs route(\"/b\", method: \"POST\")", "GET /a true\nPOST /b false" },
        { "instance_eval sees receiver ivars", "class Counter\n  getter n : Int32 = 0\n  def initialize\n    @n = 5\n  end\nend\nclass Outer\n  def run : Int32\n    c = Counter.new\n    c.instance_eval { @n + 1 }\n  end\nend\nputs Outer.new.run", "6" },
        { "instance_exec with args", "class Box\n  getter v : Int32 = 3\nend\nb = Box.new\nputs b.instance_exec(4) { |k| v * k }", "12" },
        { "define_method generated api", "class Api\n  [\"users\", \"posts\"].each do |r|\n    define_method(\"#{r}_path\") { \"/#{r}\" }\n  end\nend\nputs Api.new.send(\"users_path\")", "/users" },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Program_output(string name, string src, string expected)
    {
        _ = name;
        Assert.Equal(expected, Run(src));
    }
}
