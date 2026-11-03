using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using Xunit;

namespace Alice.Tests.Backends;

/// <summary>Generic types and methods under the IL backend (D1.10, monomorphization, AirToIl.Generics.cs): whole programs
/// compiled by both backends, stdout compared, the IL module checked by the decoder and ILVerify.</summary>
[Collection("console")]
public class AirToIlGenericsTests
{
    static void Same(string src)
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "gen", exe: true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        Assert.Empty(AirToIlTests.DecodeCheck(res.Pe!));
        var verify = AirToIlTests.IlVerify(res.Pe!);
        Assert.True(verify.Count == 0, "ILVerify:\n" + string.Join("\n", verify));
        string cs = AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), []);
        Assert.Equal(cs, AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []));
    }

    [Fact]
    public void Generic_class_has_one_instance_per_argument_list() => Same("""
        class Box(T)
          def initialize(@v : T)
          end
          def get : T
            @v
          end
          def with(x : T) : Box(T)
            Box(T).new(x)
          end
        end
        b = Box(Int32).new(5)
        puts b.get
        puts b.with(9).get
        c = Box(String).new("hi")
        puts c.get
        d = Box(Float64).new(2.5)
        puts d.get
        """);

    [Fact]
    public void Generic_method_is_inferred_per_call() => Same("""
        def id<T>(x : T) : T
          x
        end
        def pair<A, B>(a : A, b : B) : String
          "#{a}/#{b}"
        end
        def first<T>(xs : Array(T)) : T
          xs[0]
        end
        puts id(3)
        puts id("s")
        puts pair(1, "x")
        puts pair(2.5, true)
        puts first([4, 5, 6])
        """);

    [Fact]
    public void Generic_two_parameter_class_and_constraint() => Same("""
        class Pair(A, B)
          def initialize(@a : A, @b : B)
          end
          def left : A
            @a
          end
          def right : B
            @b
          end
        end
        def biggest<T>(a : T, b : T) : T where T : IComparable(T)
          a.CompareTo(b) >= 0 ? a : b
        end
        p = Pair(Int32, String).new(1, "one")
        q = Pair(String, Pair(Int32, Int32)).new("n", Pair(Int32, Int32).new(2, 3))
        puts q.right.right
        puts p.left
        puts p.right
        puts biggest(3, 8)
        puts biggest("pear", "apple")
        """);
}
