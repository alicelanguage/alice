using Alice.Compiler;
using Alice.Compiler.Backend.Il;

namespace Alice.Tests.Backends;

/// <summary>
/// `lib` / `fun` programs of Compiler/FfiTests through the IL backend (AirToIl -> AirToIlFfi, D1.10): the same stdout
/// as the C# backend. Wrappers pin and localloc, so the images are checked by the decoder, not by ILVerify (unsafe
/// code is unverifiable for Roslyn's output too).
/// </summary>
[Collection("console")]
public class AirToIlInteropTests
{
    static string LibC => OperatingSystem.IsWindows() ? "ucrtbase.dll"
        : OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";

    static string Lib(string funs) => $"@[Link(\"{LibC}\")]\nlib LibC\n{funs}end\n";

    static string Same(string src)
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "ffi");
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        Assert.Empty(AirToIlTests.DecodeCheck(res.Pe!));
        string il = AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []).Trim().ReplaceLineEndings("\n");
        Assert.Equal(AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n"), il);
        return il;
    }

    [Fact]
    public void Utf8_strings_embedded_nul_and_pinned_arrays()
    {
        var src = Lib("  fun strlen(s : String) : UInt64\n  fun memset(dst : Array(UInt8), c : Int32, n : UInt64) : Void\n  @[Fast]\n  fun abs(x : Int32) : Int32\n") +
            "puts LibC.strlen(\"héllo\")\nputs LibC.strlen(\"\")\nputs LibC.strlen(\"x\" * 5000)\n" +
            "buf = Array(UInt8).new(8, 0_u8)\nLibC.memset(buf, 7, 8_u64)\nputs buf.sum\nputs LibC.abs(-4)\n" +
            "begin\n  LibC.strlen(\"ab\\0cd\")\nrescue e : ArgumentException\n  puts \"rejected\"\nend\n";
        Assert.Equal("6\n0\n5000\n56\n4\nrejected", Same(src));
    }

    [Fact]
    public void String_returns_and_nullable_strings()
    {
        if (OperatingSystem.IsWindows()) return;
        var src = Lib("  fun getenv(name : String) : String?\n  fun strerror(code : Int32) : String\n  fun setlocale(category : Int32, locale : String?) : String?\n") +
            "puts LibC.getenv(\"PATH\") == Environment.GetEnvironmentVariable(\"PATH\")\nputs LibC.getenv(\"ALICE_FFI_MISSING_42\").nil?\n" +
            "puts LibC.strerror(2).size > 0\nputs LibC.setlocale(6, nil).nil? ? \"nil\" : \"locale\"\n";
        Assert.Equal("true\ntrue\ntrue\nlocale", Same(src));
    }

    [Fact]
    public void Non_nullable_string_return_raises_on_null()
    {
        if (OperatingSystem.IsWindows()) return;
        var src = Lib("  fun getenv(name : String) : String\n") +
            "begin\n  LibC.getenv(\"ALICE_FFI_MISSING_42\")\nrescue e : NullReferenceException\n  puts \"null\"\nend\n";
        Assert.Equal("null", Same(src));
    }

    [Fact]
    public void Out_and_ref_struct_parameters()
    {
        if (OperatingSystem.IsWindows()) return;   // clock_gettime / timespec layout: Unix (64-bit time_t)
        var src = "struct Timespec\n  getter sec : Int64\n  getter nsec : Int64\n  def initialize(@sec : Int64, @nsec : Int64)\n  end\nend\n"
            + Lib("  fun clock_gettime(clock : Int32, out ts : Timespec) : Int32\n  fun frexp(x : Float64, ref exp : Int32) : Float64\n")
            + "rc = LibC.clock_gettime(0, out ts)\nputs rc\nputs ts.sec > 1_600_000_000_i64\nputs ts.nsec < 1_000_000_000_i64\ne = 0\nm = LibC.frexp(8.0, ref e)\nputs \"#{m} #{e}\"\n";
        Assert.Equal("0\ntrue\ntrue\n0.5 4", Same(src));
    }
}
