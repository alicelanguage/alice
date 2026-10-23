using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Alice.Runtime;

/// <summary>
/// A String argument of a `lib` `fun`, as a NUL-terminated UTF-8 C string. Short strings are encoded into the
/// caller's stack buffer (no allocation); longer ones into native memory freed by <see cref="Dispose"/>. An embedded
/// NUL raises instead of silently truncating what C sees. `nil` becomes a null pointer.
/// </summary>
public unsafe ref struct Utf8Arg
{
    public const int StackBytes = 256;

    readonly byte* owned;

    public byte* Pointer { get; }

    /// <param name="stack">stackalloc'd scratch of <see cref="StackBytes"/> bytes: stack memory never moves.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Utf8Arg(string? s, Span<byte> stack)
    {
        owned = null;
        if (s == null) { Pointer = null; return; }
        int max = s.Length * 3 + 1;   // UTF-16 code unit -> at most 3 UTF-8 bytes
        Span<byte> buf;
        if (max <= stack.Length)
        {
            buf = stack;
            Pointer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(stack));
        }
        else
        {
            int need = Encoding.UTF8.GetByteCount(s) + 1;
            owned = (byte*)NativeMemory.Alloc((nuint)need);
            buf = new Span<byte>(owned, need);
            Pointer = owned;
        }
        // static transcoder: no Encoding virtual dispatch; lone surrogates become U+FFFD like Encoding.UTF8
        System.Text.Unicode.Utf8.FromUtf16(s, buf, out _, out int n);
        // U+0000 is the only character encoding to a 0 byte: one vectorized scan of the output
        if (buf[..n].Contains((byte)0)) { Dispose(); throw new ArgumentException("string passed to native code contains a NUL character"); }
        buf[n] = 0;
    }

    public void Dispose()
    {
        if (owned != null) NativeMemory.Free(owned);
    }

    /// <summary>A `char*` returned by C, borrowed (never freed): decoded as UTF-8, null stays null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? Decode(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);

    /// <summary>Non-nullable `String` return: a null pointer raises, as `.not_nil!` would.</summary>
    public static string DecodeNonNull(byte* p, string fun) =>
        p == null ? throw new NullReferenceException($"native function {fun} returned a null string") : Marshal.PtrToStringUTF8((nint)p)!;
}
