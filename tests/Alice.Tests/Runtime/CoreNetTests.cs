using System.Buffers;
using System.Net;
using System.Text;
using Alice.Core.Net;
using Alice.Core.Net.Http;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Net: TCP/Unix/UDP sockets, the per-connection server, line reads, the ring; the HTTP client
/// against a loopback responder.</summary>
public class CoreNetTests
{
    internal static async ValueTask Echo(StreamSocket c, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int n;
            while ((n = await c.ReadAsync(buf, ct)) > 0) await c.WriteAsync(buf.AsMemory(0, n), ct);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    [Fact]
    public void Ring_buffer_wraps()
    {
        using var r = new RingBuffer(64);
        Assert.Equal(64, r.Capacity);
        Span<byte> out1 = stackalloc byte[64];
        for (int round = 0; round < 50; round++)
        {
            var data = Enumerable.Range(round, 40).Select(i => (byte)i).ToArray();
            Assert.Equal(40, r.Write(data));
            Assert.Equal(data.Length, r.Available);
            int nl = r.IndexOf((byte)(round + 39));
            Assert.Equal(39, nl);
            Assert.Equal(40, r.Read(out1));
            Assert.True(out1[..40].SequenceEqual(data));
        }
        r.Write(new byte[60]);
        Assert.Equal(4, r.FreeSpace);
        Assert.Equal(4, r.Write(new byte[10]));
        r.Consume(64);
        Assert.Equal(0, r.Available);
    }

    [Fact]
    public async Task Tcp_echo_with_many_connections()
    {
        using var srv = TcpServer.Listen(0);
        using var cts = new CancellationTokenSource();
        var serving = srv.ServeAsync(Echo, cts.Token);
        var clients = new List<TcpSocket>();
        for (int i = 0; i < 64; i++) clients.Add(TcpSocket.Connect("127.0.0.1", srv.Port));
        var msg = new byte[100];
        var back = new byte[100];
        for (int round = 0; round < 5; round++)
            foreach (var c in clients)
            {
                msg[0] = (byte)round;
                c.Write(msg);
                Assert.True(c.ReadExactly(back));
                Assert.Equal(round, back[0]);
            }
        foreach (var c in clients) c.Dispose();
        cts.Cancel();
        srv.Dispose();
        await serving;
    }

    [Fact]
    public async Task Sync_serve_spawns_a_handler_per_connection_and_reads_lines()
    {
        using var srv = TcpServer.Listen(0);
        using var cts = new CancellationTokenSource();
        var t = Task.Run(() => srv.Serve(conn =>
        {
            string? line;
            while ((line = conn.Gets()) is not null) conn.Write(line.ToUpperInvariant());
        }, cts.Token));
        using (var a = TcpSocket.Connect("127.0.0.1", srv.Port))
        using (var b = TcpSocket.Connect("127.0.0.1", srv.Port))
        {
            a.Puts("hello");
            b.Puts("world");
            Assert.Equal("WORLD\n", b.Gets());
            Assert.Equal("HELLO\n", a.Gets());
            a.Write("no newline at end");
            a.CloseWrite();
            Assert.Equal("NO NEWLINE AT END", a.Gets());
            Assert.Null(a.Gets());
        }
        cts.Cancel();
        await t.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unix_sockets_echo()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = Path.Combine(Path.GetTempPath(), $"alice-core-{Environment.ProcessId}-{Guid.NewGuid():N}.sock");
        using var srv = UnixServer.Listen(path);
        using var cts = new CancellationTokenSource();
        var serving = srv.ServeAsync(Echo, cts.Token);
        using (var c = UnixSocket.Connect(path))
        {
            c.Write("ping"u8);
            var b = new byte[4];
            Assert.True(c.ReadExactly(b));
            Assert.Equal("ping"u8.ToArray(), b);
        }
        cts.Cancel();
        srv.Dispose();
        await serving;
        File.Delete(path);
    }

    [Fact]
    public void Udp_round_trip()
    {
        using var a = new UdpSocket();
        using var b = new UdpSocket();
        a.Bind("127.0.0.1", 0);
        b.Bind("127.0.0.1", 0);
        b.SendTo("dgram"u8, new IPEndPoint(IPAddress.Loopback, a.Port));
        var buf = new byte[64];
        int n = a.Receive(buf);
        Assert.Equal("dgram", Encoding.UTF8.GetString(buf, 0, n));
        Assert.Equal(b.Port, a.LastSender.Port);
        a.Connect("127.0.0.1", b.Port);
        a.Send("back"u8);
        Assert.Equal(4, b.Receive(buf));
    }

    // a minimal HTTP/1.1 responder for the client tests (keep-alive, Content-Length bodies)
    static void Respond(StreamSocket c)
    {
        while (true)
        {
            string? requestLine = c.Gets();
            if (requestLine is null) return;
            int length = 0;
            string? h;
            while ((h = c.Gets()) is not null && h != "\r\n")
                if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(h[15..].Trim());
            var body = new byte[length];
            c.ReadExactly(body);
            string path = requestLine.Split(' ')[1];
            int status = path == "/flaky" ? 503 : path == "/missing" ? 404 : 200;
            byte[] reply = path == "/echo" ? body : Encoding.UTF8.GetBytes($"{requestLine.Split(' ')[0]} {path}");
            c.Write($"HTTP/1.1 {status} X\r\nContent-Type: text/plain\r\nX-Test: yes\r\nContent-Length: {reply.Length}\r\n\r\n");
            c.Write(reply);
        }
    }

    [Fact]
    public async Task Http_client_gets_and_posts()
    {
        using var srv = TcpServer.Listen(0);
        using var cts = new CancellationTokenSource();
        var t = Task.Run(() => srv.Serve(Respond, cts.Token));
        using var http = new HttpSession($"http://127.0.0.1:{srv.Port}", new Dictionary<string, string> { ["X-Client"] = "alice" }, TimeSpan.FromSeconds(10));
        using (var r = http.Get("/hello"))
        {
            Assert.Equal(200, r.Status);
            Assert.True(r.IsSuccess);
            Assert.Equal("GET /hello", r.Text);
            Assert.Equal("yes", r.Header("X-Test"));
            Assert.Equal("text/plain", r.ContentType);
        }
        using (var r = http.Post("/echo", "body é"))
            Assert.Equal("body é", Encoding.UTF8.GetString(r.Body));
        using (var r = await http.GetAsync("/async"))
            Assert.Equal("GET /async", r.Text);
        using (var r = http.Get("/missing"))
        {
            Assert.False(r.IsSuccess);
            Assert.Throws<HttpRequestException>(() => r.EnsureSuccess());
        }
        http.Retries = 2;
        http.RetryDelay = TimeSpan.FromMilliseconds(1);
        using (var r = http.Get("/flaky")) Assert.Equal(503, r.Status);
        cts.Cancel();
        srv.Dispose();
        await t.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// Echo over the async server allocates 0 bytes per message in steady state. The count is process-wide (the server
/// runs on pool threads), so it is taken in a child process that runs nothing else: inside the test host a background
/// allocation of about 1.7 KB/s goes on with no server at all (168 B about ten times a second, measured on the forge
/// with an idle window), so a 0.3-1 s window read 500-2,400 B there (1,512 B on the ubuntu runner) while the same echo
/// in a bare process reads 0 B.
/// </summary>
[Collection(IsolatedAllocationCollection.Name)]
public class CoreNetAllocationTests
{
    // the measured program: server on the pool, the 256 clients on one dedicated thread (a blocked pool thread made
    // the pool inject threads, whose start-up allocations land in the window), least of up to 5 windows
    const string EchoProgram = """
        using System;
        using System.Buffers;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Alice.Core.Net;

        public static class EchoAllocation
        {
            static async ValueTask Echo(StreamSocket c, CancellationToken ct)
            {
                var buf = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    int n;
                    while ((n = await c.ReadAsync(buf, ct)) > 0) await c.WriteAsync(buf.AsMemory(0, n), ct);
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
            }

            public static void Main()
            {
                using var srv = TcpServer.Listen(0);
                using var cts = new CancellationTokenSource();
                var serving = srv.ServeAsync(Echo, cts.Token);
                var clients = new List<TcpSocket>();
                for (int i = 0; i < 256; i++) clients.Add(TcpSocket.Connect("127.0.0.1", srv.Port));
                var msg = new byte[64];
                var back = new byte[64];
                var windows = new long[5];
                int done = 0;
                var client = new Thread(() =>
                {
                    void Round() { foreach (var c in clients) { c.Write(msg); c.ReadExactly(back); } }
                    for (int i = 0; i < 150; i++) Round();
                    for (; done < windows.Length; done++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        long before = GC.GetTotalAllocatedBytes(precise: true);
                        for (int i = 0; i < 100; i++) Round();
                        windows[done] = GC.GetTotalAllocatedBytes(precise: true) - before;
                        if (windows[done] == 0) { done++; break; }
                    }
                });
                client.Start();
                client.Join();
                foreach (var c in clients) c.Dispose();
                cts.Cancel();
                srv.Dispose();
                Console.WriteLine("windows: " + string.Join(" ", windows[..done]));
            }
        }
        """;

    [Fact]
    public void Echo_with_256_connections_allocates_nothing_per_message()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-echo-alloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var runtime = typeof(TcpServer).Assembly.Location;
            File.WriteAllBytes(Path.Combine(dir, "EchoAllocation.dll"), Alice.Compiler.AliceCompiler.Emit([EchoProgram], "EchoAllocation", exe: true));   // references the host's TPA, Alice.Runtime included
            File.Copy(runtime, Path.Combine(dir, Path.GetFileName(runtime)));
            string major = Environment.Version.Major.ToString();
            File.WriteAllText(Path.Combine(dir, "EchoAllocation.runtimeconfig.json"),
                """{"runtimeOptions":{"tfm":"netMAJOR.0","framework":{"name":"Microsoft.NETCore.App","version":"MAJOR.0.0"}}}""".Replace("MAJOR", major));
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = dir };
            psi.ArgumentList.Add(Path.Combine(dir, "EchoAllocation.dll"));
            var (code, output) = Alice.Tests.Integration.ChildProcess.Run(psi, timeoutMinutes: 5);
            Assert.True(code == 0, output);
            var line = output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("windows: "));
            var windows = line["windows: ".Length..].Split(' ').Select(long.Parse).ToList();
            // 25,600 echoed messages per window: one window at 0 B shows the steady state allocates nothing
            // (a window can still see the pool's one-time growth, which does not repeat)
            Assert.True(windows.Min() == 0, $"bytes per window of 25,600 echoed messages: {line}");
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
