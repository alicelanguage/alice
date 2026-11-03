using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Alice.Core.Net;

/// <summary>
/// A connected stream socket (TCP or Unix domain) with Ruby's IO surface: span reads and writes, sync and
/// <see cref="ValueTask"/> async (the BCL's pooled socket awaitables: 0 bytes per operation in steady state), line
/// reads (<see cref="TryReadLine"/>, <see cref="Gets"/>) through an owned <see cref="RingBuffer"/>. One reader and one
/// writer at a time.
/// </summary>
public class StreamSocket : IDisposable
{
    readonly Socket socket;
    RingBuffer? ring;
    byte[]? lineCopy;

    /// <summary>Wraps a connected <see cref="Socket"/> (ownership moves here).</summary>
    public StreamSocket(Socket socket) => this.socket = socket;

    /// <summary>The underlying socket.</summary>
    public Socket Socket => socket;

    /// <summary>The peer address.</summary>
    public EndPoint? RemoteEndPoint => socket.RemoteEndPoint;

    /// <summary>Ruby `readpartial` into a span: bytes read, 0 at end of stream. Buffered line data comes first.</summary>
    public int Read(Span<byte> buffer)
    {
        if (ring is { Available: > 0 } r) return r.Read(buffer);
        return socket.Receive(buffer, SocketFlags.None);
    }

    /// <summary>Async read; 0 at end of stream.</summary>
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (ring is { Available: > 0 } r) return new(r.Read(buffer.Span));
        return socket.ReceiveAsync(buffer, SocketFlags.None, ct);
    }

    /// <summary>Ruby `read(n)`: exactly <paramref name="buffer"/>.Length bytes; false when the stream ends first.</summary>
    public bool ReadExactly(Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int n = Read(buffer);
            if (n == 0) return false;
            buffer = buffer[n..];
        }
        return true;
    }

    /// <summary>Ruby `write`: sends all of <paramref name="data"/>.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty) data = data[socket.Send(data, SocketFlags.None)..];
    }

    /// <summary>Async send of all of <paramref name="data"/>.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        while (!data.IsEmpty) data = data[await socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false)..];
    }

    /// <summary>Ruby `write(str)`: UTF-8 text (encoded through the stack up to 1 KB).</summary>
    public void Write(string text)
    {
        if (Encoding.UTF8.GetMaxByteCount(text.Length) <= 1024)
        {
            Span<byte> b = stackalloc byte[1024];
            Write(b[..Encoding.UTF8.GetBytes(text, b)]);
        }
        else Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Ruby `puts`: text plus a newline.</summary>
    public void Puts(string text)
    {
        Write(text);
        Write("\n"u8);
    }

    /// <summary>The next line including its newline as a UTF-8 view into the owned ring (valid until the next read);
    /// false at end of stream. A line longer than <paramref name="maxLength"/> is returned in pieces. 0 bytes
    /// allocated in steady state.</summary>
    public bool TryReadLine(out ReadOnlySpan<byte> line, int maxLength = 64 * 1024)
    {
        ring ??= new RingBuffer(Math.Max(4096, maxLength));
        while (true)
        {
            int nl = ring.IndexOf((byte)'\n');
            if (nl >= 0 || ring.FreeSpace == 0)
            {
                line = Take(nl >= 0 ? nl + 1 : ring.Available);
                return true;
            }
            int n = socket.Receive(ring.GetWriteSpan(), SocketFlags.None);
            if (n == 0)
            {
                if (ring.Available == 0) { line = default; return false; }
                line = Take(ring.Available);
                return true;
            }
            ring.Commit(n);
        }
    }

    ReadOnlySpan<byte> Take(int len)
    {
        var first = ring!.GetReadSpan();
        if (first.Length >= len)
        {
            var view = first[..len];   // Consume only moves the head: the bytes stay until the next receive
            ring.Consume(len);
            return view;
        }
        if (lineCopy is null || lineCopy.Length < len) lineCopy = new byte[Math.Max(len, 256)];
        ring.Read(lineCopy.AsSpan(0, len));
        return lineCopy.AsSpan(0, len);
    }

    /// <summary>Ruby `gets`: the next line as a string, or null at end of stream.</summary>
    public string? Gets() => TryReadLine(out var l) ? Encoding.UTF8.GetString(l) : null;

    /// <summary>Ruby `close_write`: half-closes the sending side.</summary>
    public void CloseWrite() => socket.Shutdown(SocketShutdown.Send);

    /// <summary>Ruby `close`.</summary>
    public void Close() => Dispose();

    /// <summary>Closes the socket and returns the ring.</summary>
    public void Dispose()
    {
        try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        socket.Dispose();
        ring?.Dispose();
        ring = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Ruby `TCPSocket`: a TCP connection (Nagle off).</summary>
public sealed class TcpSocket : StreamSocket
{
    /// <summary>Wraps an accepted or connected TCP socket.</summary>
    public TcpSocket(Socket s) : base(s) { s.NoDelay = true; }

    /// <summary>Ruby `TCPSocket.new(host, port)`.</summary>
    public static TcpSocket Connect(string host, int port)
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try { s.Connect(host, port); }
        catch { s.Dispose(); throw; }
        return new TcpSocket(s);
    }

    /// <summary>Async connect.</summary>
    public static async ValueTask<TcpSocket> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try { await s.ConnectAsync(host, port, ct).ConfigureAwait(false); }
        catch { s.Dispose(); throw; }
        return new TcpSocket(s);
    }
}

/// <summary>Ruby `UNIXSocket`: a Unix-domain stream connection.</summary>
public sealed class UnixSocket : StreamSocket
{
    /// <summary>Wraps an accepted or connected Unix socket.</summary>
    public UnixSocket(Socket s) : base(s) { }

    /// <summary>Ruby `UNIXSocket.new(path)`.</summary>
    public static UnixSocket Connect(string path)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { s.Connect(new UnixDomainSocketEndPoint(path)); }
        catch { s.Dispose(); throw; }
        return new UnixSocket(s);
    }
}

/// <summary>
/// A listening stream socket. <see cref="Serve"/> is Ruby's `TCPServer.listen(port) { |conn| }`: one handler per
/// connection scheduled on the thread pool (spawn-per-connection), the connection closed when the handler returns.
/// <see cref="ServeAsync"/> runs async handlers, so thousands of connections need no thread each.
/// </summary>
public abstract class SocketServer : IDisposable
{
    readonly Socket listener;

    private protected SocketServer(Socket listener) => this.listener = listener;

    /// <summary>The bound local address.</summary>
    public EndPoint LocalEndPoint => listener.LocalEndPoint!;

    private protected abstract StreamSocket Wrap(Socket s);

    /// <summary>Ruby `accept`: waits for the next connection.</summary>
    public StreamSocket Accept() => Wrap(listener.Accept());

    /// <summary>Async accept.</summary>
    public async ValueTask<StreamSocket> AcceptAsync(CancellationToken ct = default) => Wrap(await listener.AcceptAsync(ct).ConfigureAwait(false));

    /// <summary>Accepts until <paramref name="ct"/> is cancelled or the server is disposed; each connection runs
    /// <paramref name="handler"/> on a pool thread and is closed afterwards. A handler exception closes only its
    /// connection.</summary>
    public void Serve(Action<StreamSocket> handler, CancellationToken ct = default)
    {
        using var reg = ct.Register(static s => ((Socket)s!).Dispose(), listener);
        while (!ct.IsCancellationRequested)
        {
            Socket s;
            try { s = listener.Accept(); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            var conn = Wrap(s);
            ThreadPool.UnsafeQueueUserWorkItem(static st =>
            {
                try { st.handler(st.conn); }
                catch (Exception) { }
                finally { st.conn.Dispose(); }
            }, (handler, conn), preferLocal: false);
        }
    }

    /// <summary>Async <see cref="Serve"/>: every connection runs <paramref name="handler"/> concurrently.</summary>
    public async Task ServeAsync(Func<StreamSocket, CancellationToken, ValueTask> handler, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket s;
            try { s = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            _ = RunConnection(handler, Wrap(s), ct);
        }
    }

    static async Task RunConnection(Func<StreamSocket, CancellationToken, ValueTask> h, StreamSocket c, CancellationToken ct)
    {
        await Task.Yield();
        try { await h(c, ct).ConfigureAwait(false); }
        catch (Exception) { }
        finally { c.Dispose(); }
    }

    /// <summary>Stops listening.</summary>
    public void Dispose()
    {
        listener.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Ruby `TCPServer`.</summary>
public sealed class TcpServer : SocketServer
{
    TcpServer(Socket s) : base(s) { }

    /// <summary>Ruby `TCPServer.new(host, port)`: binds and listens (port 0 picks a free port, see <see cref="Port"/>;
    /// host "0.0.0.0" or "::" for all interfaces).</summary>
    public static TcpServer Listen(int port, string host = "127.0.0.1", int backlog = 512)
    {
        var addr = IPAddress.Parse(host);
        var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(addr, port));
            s.Listen(backlog);
        }
        catch { s.Dispose(); throw; }
        return new TcpServer(s);
    }

    /// <summary>The bound port.</summary>
    public int Port => ((IPEndPoint)LocalEndPoint).Port;

    private protected override StreamSocket Wrap(Socket s) => new TcpSocket(s);

    /// <summary>`TCPServer.listen(port) { |conn| }` until cancelled.</summary>
    public static void Serve(int port, Action<StreamSocket> handler, CancellationToken ct = default, string host = "127.0.0.1")
    {
        using var srv = Listen(port, host);
        srv.Serve(handler, ct);
    }
}

/// <summary>Ruby `UNIXServer`.</summary>
public sealed class UnixServer : SocketServer
{
    UnixServer(Socket s) : base(s) { }

    /// <summary>Ruby `UNIXServer.new(path)` (an existing socket file is replaced).</summary>
    public static UnixServer Listen(string path, int backlog = 512)
    {
        if (File.Exists(path)) File.Delete(path);
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { s.Bind(new UnixDomainSocketEndPoint(path)); s.Listen(backlog); }
        catch { s.Dispose(); throw; }
        return new UnixServer(s);
    }

    private protected override StreamSocket Wrap(Socket s) => new UnixSocket(s);
}

/// <summary>Ruby `UDPSocket`: datagrams with span buffers; receives fill a reusable <see cref="SocketAddress"/>
/// (0 bytes per datagram).</summary>
public sealed class UdpSocket : IDisposable
{
    readonly Socket socket;
    readonly SocketAddress from;

    /// <summary>An unbound UDP socket (IPv6 when <paramref name="ipv6"/>).</summary>
    public UdpSocket(bool ipv6 = false)
    {
        socket = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        from = new SocketAddress(socket.AddressFamily);
    }

    /// <summary>The underlying socket.</summary>
    public Socket Socket => socket;

    /// <summary>Ruby `bind(host, port)`; port 0 picks one (see <see cref="Port"/>).</summary>
    public void Bind(string host, int port) => socket.Bind(new IPEndPoint(IPAddress.Parse(host), port));

    /// <summary>The bound port.</summary>
    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;

    /// <summary>Ruby `connect(host, port)`: the default destination of <see cref="Send"/>.</summary>
    public void Connect(string host, int port) => socket.Connect(host, port);

    /// <summary>Ruby `send(data)` to the connected peer.</summary>
    public int Send(ReadOnlySpan<byte> data) => socket.Send(data, SocketFlags.None);

    /// <summary>Ruby `send(data, 0, host, port)`.</summary>
    public int SendTo(ReadOnlySpan<byte> data, EndPoint to) => socket.SendTo(data, SocketFlags.None, to);

    /// <summary>Ruby `recvfrom`: one datagram; the sender is in <see cref="LastSender"/>.</summary>
    public int Receive(Span<byte> buffer) => socket.ReceiveFrom(buffer, SocketFlags.None, from);

    /// <summary>The sender of the last <see cref="Receive"/> (materializes an endpoint).</summary>
    public IPEndPoint LastSender => (IPEndPoint)new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0).Create(from);

    /// <summary>Closes the socket.</summary>
    public void Dispose() => socket.Dispose();
}
