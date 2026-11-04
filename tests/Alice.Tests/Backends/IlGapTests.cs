using Alice.Compiler;
using Alice.Compiler.Backend.Il;

namespace Alice.Tests.Backends;

/// <summary>
/// IL backend gaps found in 1.0.2, when `alice run` / `alice build` started defaulting to IL: these programs compile with
/// the C# backend (the CLI falls back with a `note:`) and fail under AirToIl. 1.1 items, listed in
/// /work/reports/il-gaps-1.0.2.md; drop the Skip when AirToIl handles them.
/// </summary>
[Collection("console")]
public class IlGapTests
{
    const string Pending = "pending (1.1): IL backend gap, the CLI falls back to C# with a note";

    static void Differential(string src)
    {
        string expected = AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "ilgap", exe: true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        Assert.Equal(expected, AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []).Trim().ReplaceLineEndings("\n"));
    }

    // AirToIl: Internal:IndexOutOfRangeException in Main (examples/data/pipeline/src/report.al `group_by ... .map do |k, rows|`)
    [Fact(Skip = Pending), Trait("Category", "Pending")]
    public void Hash_map_with_key_and_value_block() => Differential("""
        h = {"a" => 1, "b" => 2}
        puts h.map { |k, v| v * 10 }.sum
        """);

    // AirToIl: Type:unresolved `delegate* unmanaged[Cdecl]<...>` for a `Callback(...)` parameter (examples/interop/libc.al)
    [Fact(Skip = Pending), Trait("Category", "Pending")]
    public void C_callback_parameter_in_a_lib_declaration() => Differential("""
        @[Link("libc.so.6")]
        lib LibC
          fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void
        end
        puts 42
        """);
}
