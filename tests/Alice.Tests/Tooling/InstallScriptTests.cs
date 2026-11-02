using System.Diagnostics;

namespace Alice.Tests.Tooling;

/// <summary>scripts/install.sh `fetch` with a fake wget (no curl on PATH): plain http is refused, also after a redirect.</summary>
public class InstallScriptTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "alice-inst-" + Guid.NewGuid().ToString("N"));

    public InstallScriptTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string Script()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "scripts", "install.sh"))) return Path.Combine(d.FullName, "scripts", "install.sh");
        throw new FileNotFoundException("scripts/install.sh");
    }

    /// <summary>Runs install.sh's need/die/fetch functions with PATH = a bin dir holding only the fake wget and core tools.</summary>
    (int Code, string Err) Fetch(string url, string wgetStderr)
    {
        var lines = File.ReadAllLines(Script());
        string Fn(string name)
        {
            int s = Array.FindIndex(lines, l => l.StartsWith(name + "()"));
            int e = lines[s].TrimEnd().EndsWith('}') ? s : Array.FindIndex(lines, s, l => l == "}");
            return string.Join("\n", lines[s..(e + 1)]);
        }
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        foreach (var tool in new[] { "grep", "mktemp", "rm", "cat" })
            if (!File.Exists(Path.Combine(bin, tool)) && new[] { "/usr/bin/", "/bin/" }.Select(p => p + tool).FirstOrDefault(File.Exists) is { } real)
                File.CreateSymbolicLink(Path.Combine(bin, tool), real);
        File.WriteAllText(Path.Combine(root, "stderr.txt"), wgetStderr);
        var wget = Path.Combine(bin, "wget");
        File.WriteAllText(wget, $"#!/bin/sh\ncat '{Path.Combine(root, "stderr.txt")}' >&2\nfor a in \"$@\"; do case \"$a\" in -O) out=1 ;; *) [ \"${{out:-}}\" = 1 ] && {{ echo data > \"$a\"; out=; }} ;; esac; done\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(wget, (UnixFileMode)0b111_101_101);
        var test = Path.Combine(root, "t.sh");
        File.WriteAllText(test, $"set -eu\nR=; N=; C=\n{Fn("die")}\n{Fn("need")}\n{Fn("fetch")}\nfetch '{url}' '{Path.Combine(root, "out")}'\n");
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(test);
        psi.Environment["PATH"] = bin;
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, err);
    }

    [Fact]
    public void Wget_fallback_refuses_http_and_downgrade_redirects()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, Fetch("https://github.com/a/b", "  HTTP/1.1 302 Found\n  location: https://objects.githubusercontent.com/x\n  HTTP/1.1 200 OK\n").Code);
        Assert.True(File.Exists(Path.Combine(root, "out")));

        var (code, err) = Fetch("http://github.com/a/b", "");
        Assert.NotEqual(0, code);
        Assert.Contains("plain http", err);

        File.Delete(Path.Combine(root, "out"));
        (code, err) = Fetch("https://github.com/a/b", "  HTTP/1.1 302 Found\n  Location: http://evil.example/x\n  HTTP/1.1 200 OK\n");
        Assert.NotEqual(0, code);
        Assert.Contains("redirect to plain http", err);
        Assert.False(File.Exists(Path.Combine(root, "out")));
    }
}
