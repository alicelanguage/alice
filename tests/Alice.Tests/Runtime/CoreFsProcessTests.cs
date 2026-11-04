using System.Text;
using Alice.Core.Fs;
using Alice.Core.Process;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Fs (Ruby File/Dir surface, line enumerator, glob) and Alice.Core.Process (shell, Open3).</summary>
public class CoreFsProcessTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"alice-core-fs-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public CoreFsProcessTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Fs.RmRf(dir);

    string P(string name) => Path.Combine(dir, name);

    [Fact]
    public void Files_read_write_and_query()
    {
        Assert.Equal(6, Fs.Write(P("a.txt"), "héllo"));
        Assert.Equal("héllo", Fs.Read(P("a.txt")));
        Fs.Append(P("a.txt"), "\nx");
        Assert.Equal(["héllo", "x"], Fs.ReadLines(P("a.txt")));
        Assert.True(Fs.Exist(P("a.txt")));
        Assert.True(Fs.Exist(dir));
        Assert.True(Fs.IsFile(P("a.txt")));
        Assert.False(Fs.IsDirectory(P("a.txt")));
        Assert.Equal(8, Fs.Size(P("a.txt")));
        Assert.True((Alice.Core.Time.Time.Now - Fs.Mtime(P("a.txt"))).TotalSeconds < 60);
        Fs.Touch(P("empty"));
        Assert.True(Fs.IsZero(P("empty")));
        Fs.Rename(P("empty"), P("moved"));
        Fs.Copy(P("moved"), P("copied"));
        Fs.Delete(P("moved"));
        Assert.False(Fs.Exist(P("moved")));
        Fs.Open(P("w.txt"), "w", f => f.Write("ab"u8));
        Fs.Open(P("w.txt"), "a", f => f.Write("cd"u8));
        Assert.Equal("abcd", Fs.Read(P("w.txt")));
        Assert.Equal("a.rb", Fs.Basename("/x/y/a.rb"));
        Assert.Equal("a", Fs.Basename("/x/y/a.rb", ".rb"));
        Assert.Equal("a", Fs.Basename("a.tar", ".*"));
        Assert.Equal(".rb", Fs.Extname("a.rb"));
        Assert.Equal(".", Fs.Dirname("a.rb"));
        Assert.Equal(Path.Join("a", "b"), Fs.Join("a", "b"));
    }

    [Fact]
    public void Each_line_yields_spans_and_handles_long_lines()
    {
        string longLine = new string('z', 200_000);
        Fs.Write(P("l.txt"), "one\r\ntwo\n\n" + longLine + "\nlast");
        var lines = new List<string>();
        foreach (var l in Fs.EachLine(P("l.txt"), chomp: true)) lines.Add(Encoding.UTF8.GetString(l));
        Assert.Equal(["one", "two", "", longLine, "last"], lines);
        var raw = new List<string>();
        Fs.EachLine(P("l.txt"), raw.Add);
        Assert.Equal("one\r\n", raw[0]);
        Assert.Equal(5, Fs.CountLines(P("l.txt")));
        Fs.Write(P("e.txt"), "");
        Assert.Equal(0, Fs.CountLines(P("e.txt")));
        using var en = Fs.EachLine(P("e.txt"));
        Assert.False(en.MoveNext());
    }

    [Fact]
    public void Glob_and_directories()
    {
        Fs.MkdirP(P("src/a/b"));
        Fs.Touch(P("src/x.al"));
        Fs.Touch(P("src/a/y.al"));
        Fs.Touch(P("src/a/b/z.al"));
        Fs.Touch(P("src/a/b/z.cs"));
        Fs.Touch(P("src/.hidden.al"));
        string root = dir.Replace('\\', '/') + "/";
        string[] Rel(string pattern) => Fs.Glob(root + pattern).Select(p => p[root.Length..]).ToArray();
        Assert.Equal(["src/a/b/z.al", "src/a/y.al", "src/x.al"], Rel("src/**/*.al"));
        Assert.Equal(["src/x.al"], Rel("src/*.al"));
        Assert.Equal(["src/a/b/z.al", "src/a/b/z.cs"], Rel("src/a/b/z.{al,cs}"));
        Assert.Equal(["src/a/b/z.cs"], Rel("src/**/?.[c]s"));
        Assert.Equal(["src/.hidden.al"], Rel("src/.*.al"));
        Assert.Equal(["a", "x.al"], Fs.Children(P("src")).Where(n => !n.StartsWith('.')).ToArray());
        var seen = new List<string>();
        Fs.EachChild(P("src/a"), seen.Add);
        Assert.Equal(2, seen.Count);
        Assert.True(Fs.Match("*.al", "x.al"));
        Assert.True(Fs.Match("[!a]?", "bz"));
        Assert.False(Fs.Match("[!a]?", "az"));
    }

    [Fact]
    public void Shell_runs_commands()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("hi\n", Shell.Capture("echo hi"));
        Assert.True(Shell.LastStatus.IsSuccess);
        Assert.False(Shell.System("exit 3"));
        Assert.Equal(3, Shell.LastStatus.ExitCode);
        Assert.True(Shell.System("true"));
        Assert.True(Shell.System("/bin/sh", "-c", "test \"$0\" = 'a b'", "a b"));
        Assert.False(Shell.System("/definitely/not/a/program"));
        var c = Open3.Capture3("cat; echo err 1>&2; exit 2", "in\n");
        Assert.Equal("in\n", c.Stdout);
        Assert.Equal("err\n", c.Stderr);
        Assert.Equal(2, c.Status.ExitCode);
        var c2 = Open3.Capture3("/bin/echo", ["a", "b c"]);
        Assert.Equal("a b c\n", c2.Stdout);
        int pid = Shell.Spawn("exit 4");
        Assert.Equal(4, Shell.Wait(pid).ExitCode);
        var lines = new List<string>();
        var st = Shell.Run("/bin/sh", ["-c", "read x; echo got $x; echo two"], input: "v\n", output: lines.Add);
        Assert.True(st.IsSuccess);
        Assert.Equal(["got v", "two"], lines);
        var (both, s2) = Open3.Capture2e("echo out; echo err 1>&2");
        Assert.Contains("out", both);
        Assert.Contains("err", both);
        Assert.True(s2.IsSuccess);
    }

    [Fact]
    public void Trap_registers_and_unregisters()
    {
        if (OperatingSystem.IsWindows()) return;
        using (Signals.Trap("INT", () => { })) { }
        using (Signals.Trap("SIGTERM", () => { })) { }
        using (Signals.Trap("EXIT", () => { })) { }
        Assert.Throws<ArgumentException>(() => Signals.Trap("USR9", () => { }));
    }
}

/// <summary>Iterating file lines allocates nothing per line.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class CoreFsAllocationTests
{
    [Fact]
    public void Each_line_allocates_nothing_per_line()
    {
        string path = Path.Combine(Path.GetTempPath(), $"alice-core-lines-{Environment.ProcessId}.txt");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 50_000).Select(i => $"line {i} with some text\n")));
        try
        {
            long sink = 0;
            var r = Fs.EachLine(path, chomp: true);
            try
            {
                for (int i = 0; i < 100; i++) { r.MoveNext(); sink += r.Current.Length; }   // warm: the buffer is full
                long bytes = AllocationProbe.Least(() =>
                {
                    for (int i = 0; i < 10_000 && r.MoveNext(); i++) sink += r.Current.Length;
                });
                Assert.Equal(0, bytes);
            }
            finally { r.Dispose(); }
            Assert.NotEqual(0, sink);
        }
        finally { File.Delete(path); }
    }
}
