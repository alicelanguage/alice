using System.Buffers;
using System.Text;
using Alice.Core.Csv;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Csv: RFC 4180 cases, a differential check against a scalar state machine, the writer.</summary>
public class CoreCsvTests
{
    static List<string[]> Rows(string text, CsvOptions o = default) => Csv.Parse(text, o);

    // RFC 4180 as a byte-at-a-time state machine (the model the SIMD reader must agree with)
    static List<string[]> Reference(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var f = new StringBuilder();
        bool q = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            any = true;
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                else f.Append(c);
                continue;
            }
            if (c == '"') q = true;
            else if (c == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (c == '\n')
            {
                string last = f.ToString();
                if (last.EndsWith('\r')) last = last[..^1];
                row.Add(last); f.Clear();
                rows.Add(row.Count == 1 && row[0] == "" ? [] : [.. row]);
                row.Clear();
                any = false;
            }
            else f.Append(c);
        }
        if (any)
        {
            string last = f.ToString();
            if (last.EndsWith('\r')) last = last[..^1];
            row.Add(last);
            rows.Add(row.Count == 1 && row[0] == "" ? [] : [.. row]);
        }
        return rows;
    }

    [Fact]
    public void Rfc_4180_cases()
    {
        Assert.Equal([["a", "b", "c"], ["1", "2", "3"]], Rows("a,b,c\n1,2,3\n"));
        Assert.Equal([["a", "b"]], Rows("a,b"));
        Assert.Equal([["a", "", ""], ["", "x", ""]], Rows("a,,\n,x,\n"));
        Assert.Equal([["he said \"hi\"", "x,y", "line1\nline2"]], Rows("\"he said \"\"hi\"\"\",\"x,y\",\"line1\nline2\"\n"));
        Assert.Equal([["a", "b"], ["c", "d"]], Rows("a,b\r\nc,d\r\n"));
        Assert.Equal([["a"], [], ["b"]], Rows("a\n\nb\n"));
        Assert.Equal([["", ""]], Rows("\"\",\n"));
        Assert.Equal([["bom"]], Rows("﻿bom\n"));
        Assert.Equal([["a", "b"]], Rows("a\tb\n", new CsvOptions((byte)'\t')));
        Assert.Equal([["x;y", "z"]], Rows("\"x;y\";z", new CsvOptions((byte)';')));
        Assert.Empty(Rows(""));
    }

    [Fact]
    public void Fields_spanning_64_byte_blocks()
    {
        string longField = new string('x', 150);
        string quoted = "\"" + new string('y', 70) + ",\n\"\"" + new string('z', 70) + "\"";
        string text = $"{longField},{quoted}\n" + string.Concat(Enumerable.Repeat("a,b\n", 40));
        var rows = Rows(text);
        Assert.Equal(41, rows.Count);
        Assert.Equal(longField, rows[0][0]);
        Assert.Equal(new string('y', 70) + ",\n\"" + new string('z', 70), rows[0][1]);
        Assert.Equal(["a", "b"], rows[40]);
    }

    [Fact]
    public void Simd_reader_agrees_with_the_state_machine_on_random_input()
    {
        var rng = new Random(11);
        string[] pieces = ["a", "bc", "", "\"q\"", "\"x,y\"", "\"l1\nl2\"", "\"e\"\"e\"", "é", "12345678901234567890"];
        for (int round = 0; round < 300; round++)
        {
            var sb = new StringBuilder();
            int rows = rng.Next(1, 12);
            for (int r = 0; r < rows; r++)
            {
                int fields = rng.Next(1, 8);
                for (int f = 0; f < fields; f++)
                {
                    if (f > 0) sb.Append(',');
                    string piece = pieces[rng.Next(pieces.Length)];
                    // long runs cross the 64-byte blocks; inside the quotes for a quoted piece (well-formed CSV only)
                    if (rng.Next(10) == 0) piece = piece.StartsWith('"') ? "\"" + new string('w', rng.Next(60, 140)) + piece[1..] : piece + new string('w', rng.Next(60, 140));
                    sb.Append(piece);
                }
                if (r < rows - 1 || rng.Next(2) == 0) sb.Append(rng.Next(4) == 0 ? "\r\n" : "\n");
            }
            string text = sb.ToString();
            Assert.Equal(Reference(text), Rows(text));
        }
    }

    [Fact]
    public void Fields_are_zero_copy_views_with_typed_reads()
    {
        var data = "id,price,name\n1,2.5,\"a \"\"b\"\"\"\n"u8;
        var r = new CsvReader(data);
        Assert.True(r.MoveNext());
        Assert.Equal(3, r.Current.Count);
        Assert.True(r.MoveNext());
        var row = r.Current;
        Assert.Equal(1, row[0].GetInt64());
        Assert.Equal(2.5, row[1].GetDouble());
        Assert.True(row[2].IsQuoted);
        Assert.True(row[2].HasEscapes);
        Assert.Equal("a \"b\"", row[2].ToString());
        Span<byte> buf = stackalloc byte[16];
        Assert.Equal(5, row[2].CopyTo(buf));
        Assert.True(row[0].Equals("1"u8));
        Assert.False(r.MoveNext());
        r.Dispose();
        Assert.Equal(2, Csv.CountRows(data));
    }

    [Fact]
    public void Writer_quotes_only_when_needed_and_round_trips()
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new CsvWriter(buf);
        w.WriteRow("plain", "has,comma", "has \"quote\"", "multi\nline", "");
        w.WriteField(42L); w.WriteField(2.5); w.WriteField("é"); w.EndRow();
        string text = Encoding.UTF8.GetString(buf.WrittenSpan);
        Assert.Equal("plain,\"has,comma\",\"has \"\"quote\"\"\",\"multi\nline\",\n42,2.5,é\n", text);
        Assert.Equal([["plain", "has,comma", "has \"quote\"", "multi\nline", ""], ["42", "2.5", "é"]], Rows(text));
        Assert.Equal("a,\"b,c\"\n", Csv.GenerateLine(["a", "b,c"]));
        Assert.Equal("x\n", Csv.Generate(c => c.WriteRow("x")));
    }

    [Fact]
    public void Files_read_through_the_convenience_surface()
    {
        string path = Path.Combine(Path.GetTempPath(), $"alice-core-csv-{Environment.ProcessId}.csv");
        File.WriteAllText(path, "a,b\n1,2\n");
        try
        {
            Assert.Equal([["a", "b"], ["1", "2"]], Csv.Read(path));
            var seen = new List<string>();
            Csv.Foreach(path, row => seen.Add(string.Join("|", row)));
            Assert.Equal(["a|b", "1|2"], seen);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Reading a row allocates nothing.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class CoreCsvAllocationTests
{
    [Fact]
    public void Reading_rows_and_fields_allocates_nothing()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"{i},name{i},\"q,{i}\",{i * 0.5}\n")));
        long sink = 0;
        void Body()
        {
            var r = new CsvReader(text);
            while (r.MoveNext())
            {
                var row = r.Current;
                for (int f = 0; f < row.Count; f++) sink += row[f].Raw.Length;
                if (row[0].TryGetInt64(out long id)) sink += id;
                if (row[3].TryGetDouble(out double d)) sink += (long)d;
            }
            r.Dispose();
        }
        Body();
        Assert.Equal(0, AllocationProbe.Least(Body));
        Assert.NotEqual(0, sink);
    }
}
