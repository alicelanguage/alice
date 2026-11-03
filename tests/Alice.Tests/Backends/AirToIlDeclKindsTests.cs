using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using Xunit;

namespace Alice.Tests.Backends;

/// <summary>Declaration kinds that used to be IlUnsupportedException (D1.10): whole programs on both backends, stdout compared,
/// the IL module checked by the decoder and ILVerify.</summary>
[Collection("console")]
public class AirToIlDeclKindsTests
{
    static void Same(string src)
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "decl", exe: true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        Assert.Empty(AirToIlTests.DecodeCheck(res.Pe!));
        var verify = AirToIlTests.IlVerify(res.Pe!);
        Assert.True(verify.Count == 0, "ILVerify:\n" + string.Join("\n", verify));
        string cs = AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), []);
        Assert.Equal(cs, AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []));
    }

    [Fact]
    public void Delegate_declaration_method_group_and_invoke() => Same("""
        delegate Op(a : Int32, b : Int32) : Int32
        def add(a : Int32, b : Int32) : Int32
          a + b
        end
        def apply(f : Op, x : Int32) : Int32
          f.(x, x)
        end
        op : Op = ->add
        puts op.(2, 3)
        puts apply(->add, 5)
        puts op.Invoke(4, 4)
        fn : Func(Int32, Int32, Int32) = ->add
        puts fn.(6, 7)
        """);

    [Fact]
    public void Enum_members_with_constant_expressions() => Same("""
        enum Flags
          A = 1
          B = 1 << 1
          C = 4
          AB = A | B
          D = C * 2
        end
        puts Flags::B
        puts Flags::AB
        puts Flags::D
        puts (Flags::AB).to_i
        puts (Flags::D).to_i
        """);

    [Fact]
    public void Default_interface_methods() => Same("""
        interface Greet
          def name : String
          def greet : String
            "hi " + name
          end
        end
        class Bob < Greet
          def name : String
            "bob"
          end
        end
        class Amy < Greet
          def name : String
            "amy"
          end
          def greet : String
            "yo " + name
          end
        end
        def show(g : Greet)
          puts g.greet
        end
        show(Bob.new)
        show(Amy.new)
        """);

    [Fact]
    public void Generic_base_class_instance() => Same("""
        class Box(T)
          def initialize(@v : T)
          end
          def get : T
            @v
          end
          def show : String
            "box " + get.to_s
          end
        end
        class IntBox < Box(Int32)
          def initialize(v : Int32)
            super(v)
          end
          def double : Int32
            get * 2
          end
        end
        class Named(T) < Box(T)
          def initialize(v : T, @n : String)
            super(v)
          end
          def show : String
            @n + ":" + super
          end
        end
        x = IntBox.new(4)
        puts x.double
        puts x.show
        puts Named(String).new("s", "nm").show
        """);

    [Fact]
    public void Block_parameters_taking_method_groups() => Same("""
        def sq(x : Int32) : Int32
          x * x
        end
        def show(i : Int32)
          puts i
        end
        def apply(x : Int32, &f : Int32 -> Int32) : Int32
          f.(x) + 1
        end
        def each_up(n : Int32, &f : Int32 ->)
          i = 0
          while i < n
            f.(i)
            i += 1
          end
        end
        puts apply(3, &->sq)
        each_up(3, &->show)
        """);

    [Fact]
    public void Interface_property_and_ref_struct() => Same("""
        interface Named
          property title : String
        end
        class Doc < Named
          property title : String
          def initialize(@title : String)
          end
        end
        @[Stack]
        struct Acc
          def initialize(@n : Int32)
          end
          def add(x : Int32) : Int32
            @n + x
          end
        end
        d : Named = Doc.new("a")
        d.title = "b"
        puts d.title
        a = Acc.new(5)
        puts a.add(2)
        """);

    [Fact]
    public void Events_with_method_group_handlers() => Same("""
        class Btn
          event clicked : EventHandler
          def fire
            clicked&.Invoke(self, EventArgs.Empty)
          end
        end
        def hit(s : Object?, e : EventArgs)
          puts "hit"
        end
        def hit2(s : Object?, e : EventArgs)
          puts "hit2"
        end
        b = Btn.new
        b.fire
        b.clicked += ->hit
        b.clicked += ->hit2
        b.fire
        b.clicked -= ->hit
        b.fire
        """);

    [Fact]
    public void Method_and_field_attributes_become_custom_attributes()
    {
        var res = AirToIl.Compile([new SourceFile("main.al", """
            class Cfg
              @[Obsolete("old", DiagnosticId = "X1")]
              def old : Int32
                1
              end
              @[Obsolete("field")]
              @x : Int32 = 3
              def x : Int32
                @x
              end
            end
            puts Cfg.new.x
            """)], "attrs", exe: true);
        Assert.True(res.Ok, string.Join('\n', res.Errors.Select(e => e.Message)));
        var verify = AirToIlTests.IlVerify(res.Pe!);
        Assert.True(verify.Count == 0, "ILVerify:\n" + string.Join('\n', verify));
        var cfg = AliceCompiler.Load(res.Pe!).GetType("Cfg")!;
        Assert.Equal("old", cfg.GetMethod("old")!.GetCustomAttributes(typeof(ObsoleteAttribute), false).Cast<ObsoleteAttribute>().Single().Message);
        Assert.Equal("X1", cfg.GetMethod("old")!.GetCustomAttributes(typeof(ObsoleteAttribute), false).Cast<ObsoleteAttribute>().Single().DiagnosticId);
        Assert.Equal("field", cfg.GetField("_x", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetCustomAttributes(typeof(ObsoleteAttribute), false).Cast<ObsoleteAttribute>().Single().Message);
    }
}
