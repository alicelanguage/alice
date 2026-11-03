using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Alice.Core.Json;

public sealed partial class JsonDocument
{
    byte[] input;      // pooled copy of the UTF-8 input, 0 at [length]
    int length;
    ulong[] tape;      // pooled
    int tapeLength;
    byte[]? arena;     // unescaped strings (plain array: spans handed out stay valid when it grows)
    int arenaUsed;
    bool disposed;

    JsonDocument(byte[] input, int length, ulong[] tape, int tapeLength)
    {
        this.input = input;
        this.length = length;
        this.tape = tape;
        this.tapeLength = tapeLength;
    }

    public static partial JsonDocument Parse(ReadOnlySpan<byte> utf8, JsonParseOptions options)
    {
        utf8 = Input.SkipBom(utf8);
        byte[] buf = ArrayPool<byte>.Shared.Rent(utf8.Length + 1);
        if (options.AllowComments)
        {
            utf8.CopyTo(buf);
            return Build(buf, utf8.Length, options);
        }
        // the copy into the owned buffer happens inside stage 1 (one pass over the input)
        uint[]? idx = null;
        try
        {
            if (!Utf8.IsValid(utf8)) Input.Validate(utf8.ToArray(), default);
            idx = ArrayPool<uint>.Shared.Rent(StructuralIndex.Capacity(utf8.Length));
            int count = StructuralIndex.Build(utf8, idx, buf);
            buf[utf8.Length] = 0;
            return Stage2(buf, utf8.Length, idx, count, options);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
        finally
        {
            if (idx != null) ArrayPool<uint>.Shared.Return(idx);
        }
    }

    static JsonDocument Stage2(byte[] buf, int len, uint[] idx, int count, JsonParseOptions options)
    {
        ulong[] tape = ArrayPool<ulong>.Shared.Rent(Tape.Capacity(count));
        try
        {
            int t = Tape.Build(buf, len, idx, count, tape, options.MaxDepth > 0 ? options.MaxDepth : 1024);
            return new JsonDocument(buf, len, tape, t);
        }
        catch
        {
            ArrayPool<ulong>.Shared.Return(tape);
            throw;
        }
    }

    public static partial JsonDocument Parse(string json, JsonParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] buf = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(json.Length) + 1);
        int n = Encoding.UTF8.GetBytes(json, buf);
        int skip = Input.SkipBom(buf.AsSpan(0, n)).Length;
        if (skip != n) { Buffer.BlockCopy(buf, 3, buf, 0, n - 3); n -= 3; }
        return Build(buf, n, options);
    }

    static JsonDocument Build(byte[] buf, int len, JsonParseOptions options)
    {
        uint[]? idx = null;
        ulong[]? tape = null;
        try
        {
            buf[len] = 0;
            var span = buf.AsSpan(0, len);
            Input.Validate(span, options);
            idx = ArrayPool<uint>.Shared.Rent(StructuralIndex.Capacity(len));
            int count = StructuralIndex.Build(span, idx);
            tape = ArrayPool<ulong>.Shared.Rent(Tape.Capacity(count));
            int t = Tape.Build(buf, len, idx, count, tape, options.MaxDepth > 0 ? options.MaxDepth : 1024);
            ArrayPool<uint>.Shared.Return(idx);
            idx = null;
            return new JsonDocument(buf, len, tape, t);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            if (idx != null) ArrayPool<uint>.Shared.Return(idx);
            if (tape != null) ArrayPool<ulong>.Shared.Return(tape);
            throw;
        }
    }

    public partial JsonAny Root
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new JsonAny(this, 1);
        }
    }

    public partial void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ArrayPool<byte>.Shared.Return(input);
        ArrayPool<ulong>.Shared.Return(tape);
        input = [];
        tape = [];
        length = tapeLength = 0;
        arena = null;
    }

    /// <summary>The UTF-8 input.</summary>
    internal ReadOnlySpan<byte> InputSpan => input.AsSpan(0, length);

    /// <summary>Tape length (words).</summary>
    internal int TapeLength => tapeLength;

    /// <summary>The tape word at <paramref name="k"/>.</summary>
    internal ulong Word(int k)
    {
        if ((uint)k >= (uint)tapeLength) ThrowDisposedOrRange();
        return tape[k];
    }

    static void ThrowDisposedOrRange() => throw new ObjectDisposedException(nameof(JsonDocument), "the JsonDocument was disposed or the index is invalid");

    /// <summary>Index of the value after the one at <paramref name="k"/>.</summary>
    internal int Next(int k)
    {
        ulong w = Word(k);
        return Tape.Tag(w) switch
        {
            Tape.StartObject or Tape.StartArray => Tape.Low(w),
            Tape.Int64 or Tape.UInt64 or Tape.Double => k + 2,
            _ => k + 1,
        };
    }

    /// <summary>Raw bytes of the string at <paramref name="k"/> as found in the input (escapes not decoded), or the
    /// unescaped arena copy once decoded.</summary>
    internal ReadOnlySpan<byte> RawString(int k, out bool clean)
    {
        ulong w = Word(k);
        byte tag = Tape.Tag(w);
        int start = Tape.Low(w), len = Tape.Mid(w);
        if (tag == Tape.ArenaString)
        {
            clean = true;
            return arena.AsSpan(start, len);
        }
        if (len == Tape.LongMark) len = Tape.ScanStringLength(InputSpan, start);
        clean = tag == Tape.CleanString;
        return input.AsSpan(start, len);
    }

    /// <summary>Unescaped UTF-8 of the string at <paramref name="k"/> (decoded once, cached in the arena).</summary>
    internal ReadOnlySpan<byte> Utf8String(int k)
    {
        var raw = RawString(k, out bool clean);
        if (clean) return raw;
        ulong w = tape[k];
        if (!Strings.HasEscapes(raw))
        {
            tape[k] = (w & ~(0xFFUL << 56)) | ((ulong)Tape.CleanString << 56);
            return raw;
        }
        lock (this)
        {
            if (Tape.Tag(tape[k]) == Tape.ArenaString) return RawString(k, out _);
            if (arena == null || arena.Length - arenaUsed < raw.Length)
            {
                var grown = new byte[Math.Max(Math.Max(256, raw.Length), (arena?.Length ?? 0) * 2 + raw.Length)];
                if (arena != null) arena.AsSpan(0, arenaUsed).CopyTo(grown);
                arena = grown;
            }
            int n = Strings.Unescape(raw, arena.AsSpan(arenaUsed));
            if (n < 0) throw new JsonParseException("invalid escape", Tape.Low(w));
            int at = arenaUsed;
            arenaUsed += n;
            if (n < Tape.LongMark && at >= 0) tape[k] = Tape.Word(Tape.ArenaString, n, at);
            return arena.AsSpan(at, n);
        }
    }

    /// <summary>Raw number text at <paramref name="k"/>.</summary>
    internal ReadOnlySpan<byte> NumberText(int k)
    {
        int p = Tape.Low(Word(k));
        return InputSpan.Slice(p, Numbers.TokenEnd(InputSpan, p) - p);
    }
}

/// <summary>Input checks shared by <see cref="JsonDocument"/> and <see cref="JsonIterator"/>.</summary>
static class Input
{
    /// <summary>Drops a UTF-8 byte order mark (as System.Text.Json does).</summary>
    public static ReadOnlySpan<byte> SkipBom(ReadOnlySpan<byte> s) => s.Length >= 3 && s[0] == 0xEF && s[1] == 0xBB && s[2] == 0xBF ? s.Slice(3) : s;

    /// <summary>UTF-8 validity; comments blanked in place when allowed.</summary>
    public static void Validate(Span<byte> span, JsonParseOptions options)
    {
        if (!Utf8.IsValid(span)) throw new JsonParseException("invalid UTF-8", InvalidUtf8At(span));
        if (options.AllowComments && span.IndexOf((byte)'/') >= 0) StripComments(span);
    }

    static int InvalidUtf8At(ReadOnlySpan<byte> s)
    {
        int at = 0;
        while (at < s.Length)
        {
            if (System.Text.Rune.DecodeFromUtf8(s.Slice(at), out _, out int used) != OperationStatus.Done) return at;
            at += used;
        }
        return at;
    }

    /// <summary>Replaces `//` and `/* */` comments outside strings with spaces; a stray `/` is an error.</summary>
    public static void StripComments(Span<byte> s)
    {
        int i = 0;
        while (true)
        {
            int k = s.Slice(i).IndexOfAny((byte)'"', (byte)'/');
            if (k < 0) return;
            i += k;
            if (s[i] == '"')
            {
                i++;
                while (true)
                {
                    int q = s.Slice(i).IndexOfAny((byte)'"', (byte)'\\');
                    if (q < 0) return; // unterminated: stage 1 reports it
                    i += q;
                    if (s[i] == '"') { i++; break; }
                    i += 2;
                    if (i >= s.Length) return;
                }
                continue;
            }
            if (i + 1 < s.Length && s[i + 1] == '/')
            {
                int e = s.Slice(i).IndexOf((byte)'\n');
                int end = e < 0 ? s.Length : i + e;
                s.Slice(i, end - i).Fill((byte)' ');
                i = end;
                continue;
            }
            if (i + 1 < s.Length && s[i + 1] == '*')
            {
                int e = s.Slice(i + 2).IndexOf("*/"u8);
                if (e < 0) throw new JsonParseException("unterminated comment", i);
                int end = i + 2 + e + 2;
                s.Slice(i, end - i).Fill((byte)' ');
                i = end;
                continue;
            }
            throw new JsonParseException("unexpected '/'", i);
        }
    }
}
