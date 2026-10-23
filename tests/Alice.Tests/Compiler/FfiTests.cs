using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>`lib`/`fun` imports beyond primitives: UTF-8 strings, string returns, pinned arrays. Calls the C library.</summary>
[Collection("console")]
public class FfiTests
{
    static string LibC => OperatingSystem.IsWindows() ? "ucrtbase.dll"
        : OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";

    static string Lib(string funs) => $"@[Link(\"{LibC}\")]\nlib LibC\n{funs}end\n";

    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Fact]
    public void String_parameters_cross_as_utf8()
    {
        var src = Lib("  fun strlen(s : String) : UInt64\n") +
            "puts LibC.strlen(\"héllo\")\nputs LibC.strlen(\"\")\nputs LibC.strlen(\"x\" * 5000)\n";   // stack buffer and native buffer
        Assert.Equal("6\n0\n5000", Run(src));
    }

    [Fact]
    public void Embedded_nul_is_rejected_not_truncated()
    {
        var src = Lib("  fun strlen(s : String) : UInt64\n") +
            "begin\n  LibC.strlen(\"ab\\0cd\")\nrescue e : ArgumentException\n  puts \"rejected\"\nend\n";
        Assert.Equal("rejected", Run(src));
    }

    [Fact]
    public void String_returns_decode_utf8_and_null_is_nil()
    {
        // .NET's SetEnvironmentVariable does not touch the C environ on Unix: read an inherited variable instead
        var src = Lib("  fun getenv(name : String) : String?\n  fun strerror(code : Int32) : String\n") +
            "puts LibC.getenv(\"PATH\") == Environment.GetEnvironmentVariable(\"PATH\")\nputs LibC.getenv(\"ALICE_FFI_MISSING_42\").nil?\nputs LibC.strerror(2).size > 0\n";
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("true\ntrue\ntrue", Run(src));
    }

    [Fact]
    public void Non_nullable_string_return_raises_on_null()
    {
        if (OperatingSystem.IsWindows()) return;
        var src = Lib("  fun getenv(name : String) : String\n") +
            "begin\n  LibC.getenv(\"ALICE_FFI_MISSING_42\")\nrescue e : NullReferenceException\n  puts \"null\"\nend\n";
        Assert.Equal("null", Run(src));
    }

    [Fact]
    public void Nullable_string_parameter_passes_null()
    {
        // strtol(nil, ...) would crash; use a function that accepts NULL: setlocale(LC_ALL, NULL) queries the locale
        if (OperatingSystem.IsWindows()) return;
        var src = Lib("  fun setlocale(category : Int32, locale : String?) : String?\n") +
            "puts LibC.setlocale(6, nil).nil? ? \"nil\" : \"locale\"\n";
        Assert.Equal("locale", Run(src));
    }

    [Fact]
    public void Arrays_are_pinned_not_copied()
    {
        var src = Lib("  fun memset(dst : Array(UInt8), c : Int32, n : UInt64) : Void\n") +
            "buf = Array(UInt8).new(8, 0_u8)\nLibC.memset(buf, 7, 8_u64)\nputs buf.sum\n";
        Assert.Equal("56", Run(src));
    }

    [Fact]
    public void Generated_wrappers_stay_blittable()
    {
        var cs = AliceCompiler.Transpile(Lib("  @[Fast]\n  fun strlen(s : String) : UInt64\n  fun getenv(name : String) : String?\n  fun memset(dst : Array(UInt8), c : Int32, n : UInt64) : Void\n"));
        Assert.Contains("private static extern ulong __raw_strlen(byte* s);", cs);
        Assert.Contains("private static extern byte* __raw_getenv(byte* name);", cs);
        Assert.Contains("private static extern void __raw_memset(byte* dst, int c, ulong n);", cs);
        Assert.DoesNotContain("MarshalAs", cs);
        Assert.DoesNotContain("CharSet", cs);
    }

    const string Malloc = "  fun malloc(n : UInt64) : Pointer(Int32)\n  fun free(p : Pointer(Int32)) : Void\n";

    [Fact]
    public void Pointers_need_an_unsafe_block()
    {
        var src = Lib(Malloc) + "unsafe\n  p = LibC.malloc(16_u64)\n  p[0] = 41\n  p[1] = 1\n  puts p[0] + p[1]\n  LibC.free(p)\nend\n";
        Assert.Equal("42", Run(src));
    }

    [Fact]
    public void Unsafe_def_marks_a_whole_method()
    {
        var src = Lib(Malloc) + "unsafe def sum3 : Int32\n  p = LibC.malloc(12_u64)\n  3.times { |i| p[i] = i + 1 }\n  t = p[0] + p[1] + p[2]\n  LibC.free(p)\n  t\nend\nputs sum3\n";
        Assert.Equal("6", Run(src));
    }

    [Fact]
    public void Pointer_use_outside_unsafe_is_an_alice_error()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Run(Lib(Malloc) + "p = LibC.malloc(4_u64)\nLibC.free(p)\n"));
        var d = ex.Diagnostics.First();
        Assert.Equal("AL0214", d.Code);
        Assert.Contains("unsafe", d.Message);
    }

    const string Qsort = "  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void\n";

    [Fact]
    public void Lambdas_become_c_function_pointers()
    {
        var src = Lib(Qsort) + "xs = [5, 3, 9, 1]\nLibC.qsort(xs, 4_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })\nputs xs.join(\",\")\n";
        Assert.Equal("1,3,5,9", Run(src));
        var cs = AliceCompiler.Transpile(src);
        Assert.Contains("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]", cs);
        Assert.Contains("delegate* unmanaged[Cdecl]<int*, int*, int>", cs);
        Assert.DoesNotContain("GetFunctionPointerForDelegate", cs);   // no delegate, no GC handle
    }

    [Fact]
    public void Capturing_callback_is_a_compile_error()
    {
        var src = Lib(Qsort) + "k = 1\nxs = [2, 1]\nLibC.qsort(xs, 2_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { k })\n";
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Contains("AL0215", ex.Message + string.Join("", ex.Diagnostics.Select(d => d.Code)));
    }

    [Fact]
    public void Exception_in_a_callback_surfaces_after_the_c_call()
    {
        // an exception must not unwind through C frames (that kills the process): it is rethrown once qsort returns
        var src = Lib(Qsort) + "xs = [2, 1, 3]\nbegin\n  LibC.qsort(xs, 3_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { raise \"boom\" })\nrescue e\n  puts e.message\nend\nputs \"alive\"\n";
        Assert.Equal("boom\nalive", Run(src));
    }

    [Fact]
    public void Out_and_ref_struct_parameters_need_no_pointers()
    {
        if (OperatingSystem.IsWindows()) return;   // clock_gettime / timespec layout: Unix (64-bit time_t)
        var src = "struct Timespec\n  getter sec : Int64\n  getter nsec : Int64\n  def initialize(@sec : Int64, @nsec : Int64)\n  end\nend\n"
            + Lib("  fun clock_gettime(clock : Int32, out ts : Timespec) : Int32\n  fun frexp(x : Float64, ref exp : Int32) : Float64\n")
            + "rc = LibC.clock_gettime(0, out ts)\nputs rc\nputs ts.sec > 1_600_000_000_i64\nputs ts.nsec < 1_000_000_000_i64\ne = 0\nm = LibC.frexp(8.0, ref e)\nputs \"#{m} #{e}\"\n";
        Assert.Equal("0\ntrue\ntrue\n0.5 4", Run(src));
    }

    [Fact]
    public void LibraryImport_through_a_partial_def()
    {
        // .NET's own source-generated P/Invoke works too: the generator writes the marshalling for the partial method
        var src = "using System.Runtime.InteropServices\nmodule Native\n  @[LibraryImport(\"" + LibC + "\", EntryPoint = \"strlen\", StringMarshalling = StringMarshalling.Utf8)]\n  partial def self.c_strlen(s : String) : UInt64\nend\nputs Native.c_strlen(\"héllo\")\n";
        Assert.Equal("6", Run(src));
    }
}
