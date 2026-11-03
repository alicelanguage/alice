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
}
