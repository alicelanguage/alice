using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Edge cases of the FFI wrappers (ADR 0018) where a bug would corrupt memory rather than fail loudly.</summary>
[Collection("console")]
public class FfiStressTests
{
    static string LibC => OperatingSystem.IsWindows() ? "ucrtbase.dll"
        : OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";

    static string Lib(string funs) => $"@[Link(\"{LibC}\")]\nlib LibC\n{funs}end\n";

    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    [Fact]
    public void Utf8_lengths_at_the_stack_buffer_boundary()
    {
        // 85 UTF-16 units fit the 256-byte stack buffer (85 * 3 + 1); 86 go to native memory. Multi-byte text
        // (é = 2 bytes, 😀 = 4 bytes from a surrogate pair) must come out with exact byte lengths on both paths.
        var src = Lib("  fun strlen(s : String) : UInt64\n") + string.Join("", new[]
        {
            "puts LibC.strlen(\"a\" * 84)", "puts LibC.strlen(\"a\" * 85)", "puts LibC.strlen(\"a\" * 86)",
            "puts LibC.strlen(\"é\" * 85)", "puts LibC.strlen(\"é\" * 86)", "puts LibC.strlen(\"😀\" * 42)", "puts LibC.strlen(\"😀\" * 43)",
            "puts LibC.strlen(\"x\" * 100_000)",
        }.Select(l => l + "\n"));
        Assert.Equal("84\n85\n86\n170\n172\n168\n172\n100000", Run(src));
    }

    [Fact]
    public void Lone_surrogates_become_replacement_characters()
    {
        // an unpaired surrogate is not valid UTF-8: it is encoded as U+FFFD (3 bytes), never as raw bytes
        var src = Lib("  fun strlen(s : String) : UInt64\n") + "s = String.new(['a', Char.ConvertFromUtf32(0x1F600)[0], 'b'].ToArray())\nputs LibC.strlen(s)\n";
        Assert.Equal("5", Run(src));
    }

    [Fact]
    public void Concurrent_string_calls_do_not_share_buffers()
    {
        // each call encodes into its own stack frame: 8 threads x 20k calls with distinct lengths must all agree
        var src = Lib("  fun strlen(s : String) : UInt64\n") + """
            bad = Atomic.new(0)
            ts = (1..8).to_a.map { |k| Task.Run(->() {
              s = "z" * (k * 10)
              20000.times { |i| bad.add(1) if LibC.strlen(s) != (k * 10).to_u64 }
            }) }
            Task.WaitAll(ts.ToArray())
            puts bad.get
            """;
        Assert.Equal("0", Run(src));
    }

    [Fact]
    public void Callback_exceptions_are_relayed_once_per_call()
    {
        // the first call raises from inside qsort's comparator; the second call must not see a stale exception
        var src = Lib("  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void\n") + """
            caught = 0
            5.times do
              begin
                LibC.qsort([3, 1, 2], 3_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { raise "boom" })
              rescue e
                caught += 1
              end
            end
            xs = [3, 1, 2]
            LibC.qsort(xs, 3_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })
            puts "#{caught} #{xs.join(",")}"
            """;
        Assert.Equal("5 1,2,3", Run(src));
    }

    [Fact]
    public void Empty_and_large_arrays_pin_correctly()
    {
        // an empty array pins to a null pointer (C must not touch it for n = 0); 16 MiB is written in place
        var src = Lib("  fun memset(dst : Array(UInt8), c : Int32, n : UInt64) : Void\n")
            + "e = Array(UInt8).new(0, 0_u8)\nLibC.memset(e, 1, 0_u64)\nbig = Array(UInt8).new(16_777_216, 0_u8)\nLibC.memset(big, 2, 16_777_216_u64)\nputs \"#{e.size} #{big[0]} #{big[16_777_215]}\"\n";
        Assert.Equal("0 2 2", Run(src));
    }
}
