using System.Buffers;
using System.Globalization;
using System.Text;

namespace Alice.Core.Csv;

/// <summary>
/// RFC 4180 writer into an <see cref="IBufferWriter{T}"/>: a field is quoted only when it contains the separator, a
/// quote, CR or LF (quotes doubled); rows end with LF (or CRLF). Numbers are formatted straight into the buffer.
/// 0 bytes allocated per field when the buffer writer is pooled.
/// </summary>
public sealed class CsvWriter
{
    readonly IBufferWriter<byte> output;
    readonly byte sep, quote;
    readonly bool crlf;
    bool rowStarted;

    /// <summary>A writer into <paramref name="output"/>.</summary>
    public CsvWriter(IBufferWriter<byte> output, CsvOptions options = default, bool crlf = false)
    {
        this.output = output;
        sep = options.Separator == 0 ? (byte)',' : options.Separator;
        quote = options.Quote == 0 ? (byte)'"' : options.Quote;
        this.crlf = crlf;
    }

    void Sep()
    {
        if (rowStarted) Put(sep);
        rowStarted = true;
    }

    void Put(byte b)
    {
        var s = output.GetSpan(1);
        s[0] = b;
        output.Advance(1);
    }

    /// <summary>Writes a UTF-8 field.</summary>
    public void WriteField(ReadOnlySpan<byte> utf8)
    {
        Sep();
        Span<byte> specials = [sep, quote, (byte)'\n', (byte)'\r'];
        if (utf8.IndexOfAny(specials) < 0) { output.Write(utf8); return; }
        int quotes = utf8.Count(quote);
        var dst = output.GetSpan(utf8.Length + quotes + 2);
        int w = 0;
        dst[w++] = quote;
        foreach (byte b in utf8)
        {
            dst[w++] = b;
            if (b == quote) dst[w++] = quote;
        }
        dst[w++] = quote;
        output.Advance(w);
    }

    /// <summary>Writes a text field (UTF-8 encoded through the stack for short fields).</summary>
    public void WriteField(ReadOnlySpan<char> text)
    {
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        Span<byte> tmp = max <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(max));
        int n = Encoding.UTF8.GetBytes(text, tmp);
        WriteField(tmp[..n]);
        if (rented != null) ArrayPool<byte>.Shared.Return(rented);
    }

    /// <summary>Writes a string field (null writes an empty field).</summary>
    public void WriteField(string? text) => WriteField(text.AsSpan());

    /// <summary>Writes an integer field.</summary>
    public void WriteField(long value)
    {
        Sep();
        var dst = output.GetSpan(20);
        value.TryFormat(dst, out int n, default, CultureInfo.InvariantCulture);
        output.Advance(n);
    }

    /// <summary>Writes a float field (shortest round-trip, invariant).</summary>
    public void WriteField(double value)
    {
        Sep();
        var dst = output.GetSpan(32);
        value.TryFormat(dst, out int n, "R", CultureInfo.InvariantCulture);
        output.Advance(n);
    }

    /// <summary>Ends the row.</summary>
    public void EndRow()
    {
        if (crlf) Put((byte)'\r');
        Put((byte)'\n');
        rowStarted = false;
    }

    /// <summary>Writes a whole row of strings.</summary>
    public void WriteRow(params ReadOnlySpan<string?> fields)
    {
        foreach (var f in fields) WriteField(f);
        EndRow();
    }

    /// <summary>Writes a whole row of strings.</summary>
    public void WriteRow(IEnumerable<string?> fields)
    {
        foreach (var f in fields) WriteField(f);
        EndRow();
    }
}

/// <summary>
/// Ruby's `CSV` convenience surface (allocates arrays and strings; the zero-copy path is <see cref="CsvReader"/>):
/// `CSV.parse(text)`, `CSV.read(path)`, `CSV.foreach(path) { |row| }`, `CSV.generate { |csv| }`, `CSV.generate_line`.
/// </summary>
public static class Csv
{
    /// <summary>Ruby `CSV.parse(text)`: every row as a string array (blank lines give empty arrays).</summary>
    public static List<string[]> Parse(string text, CsvOptions options = default) => ParseBytes(Encoding.UTF8.GetBytes(text), options);

    /// <summary>Every row of UTF-8 <paramref name="utf8"/> as a string array.</summary>
    public static List<string[]> ParseBytes(ReadOnlySpan<byte> utf8, CsvOptions options = default)
    {
        var rows = new List<string[]>();
        var r = new CsvReader(utf8, options);
        try
        {
            while (r.MoveNext()) rows.Add(r.Current.IsBlank ? [] : r.Current.ToArray());
        }
        finally { r.Dispose(); }
        return rows;
    }

    /// <summary>Ruby `CSV.read(path)`.</summary>
    public static List<string[]> Read(string path, CsvOptions options = default) => ParseBytes(File.ReadAllBytes(path), options);

    /// <summary>Ruby `CSV.foreach(path) { |row| }` with string-array rows.</summary>
    public static void Foreach(string path, Action<string[]> each, CsvOptions options = default)
    {
        var bytes = File.ReadAllBytes(path);
        var r = new CsvReader(bytes, options);
        try
        {
            while (r.MoveNext()) each(r.Current.IsBlank ? [] : r.Current.ToArray());
        }
        finally { r.Dispose(); }
    }

    /// <summary>Number of rows (no allocation per row).</summary>
    public static long CountRows(ReadOnlySpan<byte> utf8, CsvOptions options = default)
    {
        long n = 0;
        var r = new CsvReader(utf8, options);
        while (r.MoveNext()) n++;
        r.Dispose();
        return n;
    }

    /// <summary>Ruby `CSV.generate_line(fields)`: one row with its line end.</summary>
    public static string GenerateLine(IEnumerable<string?> fields, CsvOptions options = default)
    {
        var buf = new ArrayBufferWriter<byte>();
        new CsvWriter(buf, options).WriteRow(fields);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>Ruby `CSV.generate { |csv| csv &lt;&lt; row }`.</summary>
    public static string Generate(Action<CsvWriter> body, CsvOptions options = default)
    {
        var buf = new ArrayBufferWriter<byte>();
        body(new CsvWriter(buf, options));
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }
}
