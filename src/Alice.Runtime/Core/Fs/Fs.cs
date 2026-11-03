using System.Buffers;
using System.Text;
using Alice.Core.Text;
using Alice.Runtime;
using Microsoft.Win32.SafeHandles;

namespace Alice.Core.Fs;

/// <summary>
/// Ruby's `File` / `Dir` / `FileUtils` surface over <see cref="File"/>, <see cref="Directory"/> and
/// <see cref="RandomAccess"/>. Names follow .NET (`Exist`, `MkdirP`, `EachLine`) so Alice reads them as
/// `Fs.exist?(p)`, `Fs.mkdir_p(d)`, `Fs.each_line(p) { |l| }`. <see cref="EachLine(string, bool)"/> is a struct
/// enumerator over a pooled buffer that yields UTF-8 spans: 0 bytes per line.
/// </summary>
public static class Fs
{
    // ---- whole files ----

    /// <summary>Ruby `File.read`: the file as UTF-8 text.</summary>
    public static string Read(string path) => File.ReadAllText(path);

    /// <summary>Ruby `File.binread`: the file bytes.</summary>
    public static byte[] ReadBytes(string path) => File.ReadAllBytes(path);

    /// <summary>Ruby `File.readlines(path, chomp: true)`.</summary>
    public static string[] ReadLines(string path) => File.ReadAllLines(path);

    /// <summary>Ruby `File.write`: replaces the file with <paramref name="text"/>; returns the bytes written.</summary>
    public static int Write(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bytes);
        return bytes.Length;
    }

    /// <summary>Ruby `File.binwrite`.</summary>
    public static int Write(string path, ReadOnlySpan<byte> bytes)
    {
        File.WriteAllBytes(path, bytes);
        return bytes.Length;
    }

    /// <summary>Appends <paramref name="text"/> (Ruby `File.write(p, s, mode: "a")`).</summary>
    public static void Append(string path, string text) => File.AppendAllText(path, text);

    /// <summary>Ruby `File.open(path, mode) { |f| }`: opens, runs <paramref name="body"/>, closes (also on error).
    /// Modes: "r", "w", "a", "r+", "w+", "a+" (a trailing "b" is ignored).</summary>
    public static void Open(string path, string mode, Action<FileStream> body)
    {
        using var f = OpenStream(path, mode);
        body(f);
    }

    /// <summary>Ruby `File.open(path, mode)` without a block; the caller disposes.</summary>
    public static FileStream OpenStream(string path, string mode = "r") => mode.Replace("b", "") switch
    {
        "r" => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan),
        "w" => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
        "a" => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
        "r+" => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read),
        "w+" => new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read),
        "a+" => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read),
        _ => throw new ArgumentException($"invalid access mode {mode}", nameof(mode)),
    };

    // ---- lines ----

    /// <summary>Ruby `File.foreach(path)` / `each_line`: a struct enumerator of UTF-8 lines (each keeps its `\n`
    /// unless <paramref name="chomp"/>) over <see cref="RandomAccess"/> and a pooled buffer. 0 bytes per line; the
    /// span is valid until the next MoveNext. Dispose (or finishing a foreach) closes the file.</summary>
    public static FileLineReader EachLine(string path, bool chomp = false) => new(path, chomp);

    /// <summary>Ruby `File.foreach(path) { |line| }` with string lines (allocates each line's string).</summary>
    public static void EachLine(string path, Action<string> body, bool chomp = false)
    {
        var r = new FileLineReader(path, chomp);
        try { while (r.MoveNext()) body(Encoding.UTF8.GetString(r.Current)); }
        finally { r.Dispose(); }
    }

    /// <summary>Number of lines (`\n`-terminated, plus a final unterminated one), SIMD-counted over the mapped
    /// file (<see cref="MappedFile"/>): 0 bytes allocated after opening.</summary>
    public static long CountLines(string path)
    {
        long len = new FileInfo(path).Length;
        if (len == 0) return 0;
        using var m = MappedFile.Open(path);
        long n = 0, off = 0;
        byte last = 0;
        while (off < len)
        {
            int take = (int)Math.Min(len - off, 1 << 30);
            var s = m.Span<byte>(off, take);
            n += s.Count((byte)'\n');
            last = s[^1];
            off += take;
        }
        return last == (byte)'\n' ? n : n + 1;
    }

    /// <summary>Maps a file read-only (ADR 0030 <see cref="MappedFile"/>): spans, tensors, zero-copy parsing.</summary>
    public static MappedFile Map(string path) => MappedFile.Open(path);

    // ---- queries ----

    /// <summary>Ruby `File.exist?`: a file or a directory.</summary>
    public static bool Exist(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Ruby `File.file?`.</summary>
    public static bool IsFile(string path) => File.Exists(path);

    /// <summary>Ruby `File.directory?`.</summary>
    public static bool IsDirectory(string path) => Directory.Exists(path);

    /// <summary>Ruby `File.size`.</summary>
    public static long Size(string path) => new FileInfo(path).Length;

    /// <summary>Ruby `File.zero?`: exists with size 0.</summary>
    public static bool IsZero(string path) => File.Exists(path) && new FileInfo(path).Length == 0;

    /// <summary>Ruby `File.mtime`.</summary>
    public static Time.Time Mtime(string path) => Time.Time.FromDateTimeOffset(new DateTimeOffset(File.GetLastWriteTime(path)));

    /// <summary>Ruby `File.basename(path)` / `File.basename(path, ".rb")`.</summary>
    [Pure]
    public static string Basename(string path, string? suffix = null)
    {
        string b = Path.GetFileName(path.TrimEnd('/', '\\'));
        if (suffix == ".*") return Path.GetFileNameWithoutExtension(b);
        return suffix is { Length: > 0 } && b.EndsWith(suffix, StringComparison.Ordinal) && b.Length > suffix.Length ? b[..^suffix.Length] : b;
    }

    /// <summary>Ruby `File.dirname` ("." for a bare name).</summary>
    [Pure] public static string Dirname(string path) => Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";

    /// <summary>Ruby `File.extname` (".rb", or "" when none).</summary>
    [Pure] public static string Extname(string path) => Path.GetExtension(path);

    /// <summary>Ruby `File.join`.</summary>
    [Pure] public static string Join(params string[] parts) => Path.Join(parts);

    /// <summary>Ruby `File.expand_path` (`~` is the home directory).</summary>
    public static string ExpandPath(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.GetFullPath(path);
    }

    // ---- changes ----

    /// <summary>Ruby `File.delete` (missing files are ignored).</summary>
    public static void Delete(string path) => File.Delete(path);

    /// <summary>Ruby `File.rename` (replaces the target).</summary>
    public static void Rename(string from, string to) => File.Move(from, to, overwrite: true);

    /// <summary>Ruby `FileUtils.cp`.</summary>
    public static void Copy(string from, string to) => File.Copy(from, to, overwrite: true);

    /// <summary>Ruby `FileUtils.touch`.</summary>
    public static void Touch(string path)
    {
        if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        else File.WriteAllBytes(path, []);
    }

    /// <summary>Ruby `FileUtils.mkdir_p`.</summary>
    public static void MkdirP(string path) => Directory.CreateDirectory(path);

    /// <summary>Ruby `FileUtils.rm_rf` (no error when missing).</summary>
    public static void RmRf(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    // ---- directories ----

    /// <summary>Ruby `Dir.children`: names in <paramref name="dir"/> without "." and "..", sorted.</summary>
    public static string[] Children(string dir)
    {
        var a = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToArray();
        Array.Sort(a, StringComparer.Ordinal);
        return a!;
    }

    /// <summary>Ruby `Dir.each_child(dir) { |name| }`.</summary>
    public static void EachChild(string dir, Action<string> body)
    {
        foreach (var e in Directory.EnumerateFileSystemEntries(dir)) body(Path.GetFileName(e));
    }

    /// <summary>Ruby `Dir.glob(pattern)`: `*`, `?`, `[abc]`, `{a,b}` and `**/` (any depth); sorted paths. Hidden
    /// entries (leading dot) match only patterns that start with a dot, as in Ruby.</summary>
    public static string[] Glob(string pattern)
    {
        var results = new List<string>();
        foreach (var p in Braces(pattern))
        {
            bool abs = Path.IsPathRooted(p);
            string norm = p.Replace('\\', '/');
            string root = abs ? (norm.StartsWith('/') ? "/" : norm[..(norm.IndexOf('/') + 1)]) : "";
            var segs = norm[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            Walk(root, segs, 0, results);
        }
        var r = results.Distinct().ToArray();
        Array.Sort(r, StringComparer.Ordinal);
        return r;
    }

    static void Walk(string at, string[] segs, int i, List<string> into)
    {
        string dir = at.Length == 0 ? "." : at;
        if (i == segs.Length) { if (at.Length > 0) into.Add(at.TrimEnd('/')); return; }
        string seg = segs[i];
        bool last = i == segs.Length - 1;
        if (seg == "**")
        {
            Walk(at, segs, i + 1, into);
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                Walk(at + name + "/", segs, i, into);
            }
            return;
        }
        if (!HasMeta(seg))
        {
            string next = at + seg;
            if (last ? Exist(next) : Directory.Exists(next)) Walk(next + (last ? "" : "/"), segs, i + 1, into);
            return;
        }
        if (!Directory.Exists(dir)) return;
        foreach (var e in Directory.EnumerateFileSystemEntries(dir))
        {
            string name = Path.GetFileName(e);
            if (name.StartsWith('.') && !seg.StartsWith('.')) continue;
            if (!Match(seg, name)) continue;
            if (last) into.Add(at + name);
            else if (Directory.Exists(e)) Walk(at + name + "/", segs, i + 1, into);
        }
    }

    static bool HasMeta(string s) => s.AsSpan().IndexOfAny("*?[") >= 0;

    static IEnumerable<string> Braces(string p)
    {
        int open = p.IndexOf('{');
        if (open < 0) { yield return p; yield break; }
        int depth = 0, close = -1;
        var alts = new List<string>();
        int st = open + 1;
        for (int i = open; i < p.Length; i++)
        {
            if (p[i] == '{') depth++;
            else if (p[i] == '}' && --depth == 0) { alts.Add(p[st..i]); close = i; break; }
            else if (p[i] == ',' && depth == 1) { alts.Add(p[st..i]); st = i + 1; }
        }
        if (close < 0) { yield return p; yield break; }
        foreach (var a in alts)
            foreach (var x in Braces(p[..open] + a + p[(close + 1)..])) yield return x;
    }

    /// <summary>Ruby `File.fnmatch(pattern, name)` for one path segment: `*`, `?`, `[a-z]`, `[!x]`.</summary>
    [Pure]
    public static bool Match(string pattern, string name) => Match(pattern.AsSpan(), name.AsSpan());

    static bool Match(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
    {
        int pi = 0, si = 0, starP = -1, starS = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && p[pi] == '*') { starP = pi++; starS = si; continue; }
            if (pi < p.Length && (p[pi] == '?' || (p[pi] == '[' ? Class(p, ref pi, s[si]) : p[pi] == s[si])))
            {
                pi++;
                si++;
                continue;
            }
            if (starP >= 0) { pi = starP + 1; si = ++starS; continue; }
            return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    // on a match, leaves pi on the closing ']'
    static bool Class(ReadOnlySpan<char> p, ref int pi, char c)
    {
        int i = pi + 1;
        bool neg = i < p.Length && (p[i] == '!' || p[i] == '^');
        if (neg) i++;
        bool hit = false;
        int first = i;
        while (i < p.Length && (p[i] != ']' || i == first))
        {
            if (i + 2 < p.Length && p[i + 1] == '-' && p[i + 2] != ']') { if (c >= p[i] && c <= p[i + 2]) hit = true; i += 3; }
            else { if (p[i] == c) hit = true; i++; }
        }
        if (i >= p.Length) return false;
        if (hit == neg) return false;
        pi = i;
        return true;
    }
}

/// <summary>
/// Struct line enumerator over a file (see <see cref="Fs.EachLine(string, bool)"/>): reads with
/// <see cref="RandomAccess"/> into a pooled buffer (64 KB, grown for longer lines) and yields spans into it.
/// </summary>
public struct FileLineReader : IDisposable
{
    readonly SafeFileHandle handle;
    readonly bool chomp;
    byte[] buf;
    long fileOffset;
    int start, end, curStart, curLen;
    bool eof;

    /// <summary>Opens <paramref name="path"/> for reading.</summary>
    public FileLineReader(string path, bool chomp = false)
    {
        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        this.chomp = chomp;
        buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        fileOffset = 0;
        start = end = curStart = curLen = 0;
        eof = false;
    }

    /// <summary>The current line (UTF-8), valid until the next <see cref="MoveNext"/>.</summary>
    public readonly ReadOnlySpan<byte> Current => buf.AsSpan(curStart, curLen);

    /// <summary>The current line as a string.</summary>
    public readonly string CurrentString => Encoding.UTF8.GetString(Current);

    /// <summary>The current line as a <see cref="Utf8String"/> (copies).</summary>
    public readonly Utf8String CurrentUtf8 => Utf8String.FromUtf8(Current);

    /// <summary>foreach support.</summary>
    public readonly FileLineReader GetEnumerator() => this;

    /// <summary>Advances to the next line.</summary>
    public bool MoveNext()
    {
        while (true)
        {
            int nl = buf.AsSpan(start, end - start).IndexOf((byte)'\n');
            if (nl >= 0)
            {
                Set(start, nl + 1);
                start += nl + 1;
                return true;
            }
            if (eof)
            {
                if (start >= end) return false;
                Set(start, end - start);
                start = end;
                return true;
            }
            Fill();
        }
    }

    void Set(int s, int len)
    {
        if (chomp && len > 0 && buf[s + len - 1] == (byte)'\n')
        {
            len--;
            if (len > 0 && buf[s + len - 1] == (byte)'\r') len--;
        }
        curStart = s;
        curLen = len;
    }

    void Fill()
    {
        if (start > 0)
        {
            buf.AsSpan(start, end - start).CopyTo(buf);
            end -= start;
            start = 0;
        }
        if (end == buf.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
            buf.AsSpan(0, end).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(buf);
            buf = bigger;
        }
        int n = RandomAccess.Read(handle, buf.AsSpan(end), fileOffset);
        if (n == 0) { eof = true; return; }
        fileOffset += n;
        end += n;
    }

    /// <summary>Closes the file and returns the buffer.</summary>
    public void Dispose()
    {
        handle?.Dispose();
        if (buf is { Length: > 0 }) ArrayPool<byte>.Shared.Return(buf);
        buf = [];
        start = end = curStart = curLen = 0;
        eof = true;
    }
}
