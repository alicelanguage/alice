using System.Buffers;
using System.Text;

namespace Alice.Core.Net.Http;

// Frozen contract (ADR 0036): the handler-facing request/response views of `Http.serve(port) { |req, res| ... }`.
// Two transports produce them: the own HTTP/1.1 server (C4.3, SIMD parser over a per-connection owned ring buffer)
// and the Kestrel adapter package (C4.4, HTTP/2, HTTP/3, TLS). Views are ref structs: valid only during the handler
// call, nothing is materialized unless the handler asks for a String.

/// <summary>Request method. <see cref="Other"/>: read <see cref="HttpRequest.MethodBytes"/>.</summary>
public enum HttpVerb : byte
{
    /// <summary>An extension method.</summary>
    Other,
    /// <summary>GET</summary>
    Get,
    /// <summary>HEAD</summary>
    Head,
    /// <summary>POST</summary>
    Post,
    /// <summary>PUT</summary>
    Put,
    /// <summary>DELETE</summary>
    Delete,
    /// <summary>PATCH</summary>
    Patch,
    /// <summary>OPTIONS</summary>
    Options,
    /// <summary>TRACE</summary>
    Trace,
    /// <summary>CONNECT</summary>
    Connect,
}

/// <summary>Protocol version of a request.</summary>
public enum HttpVersionKind : byte
{
    /// <summary>HTTP/1.0</summary>
    Http10,
    /// <summary>HTTP/1.1</summary>
    Http11,
    /// <summary>HTTP/2 (Kestrel adapter)</summary>
    Http2,
    /// <summary>HTTP/3 (Kestrel adapter)</summary>
    Http3,
}

/// <summary>Where the request line's parts sit in the request head (byte offsets).</summary>
public readonly record struct HttpRequestLine(HttpVerb Verb, int MethodStart, int MethodLength, int TargetStart, int TargetLength, HttpVersionKind Version);

/// <summary>One header field: name and value as offsets into the request head (value trimmed of OWS).</summary>
public readonly record struct HttpField(int NameStart, int NameLength, int ValueStart, int ValueLength);

/// <summary>
/// A parsed request, as views over the transport's buffer. Header names compare ASCII case-insensitively.
/// </summary>
public readonly ref struct HttpRequest
{
    readonly ReadOnlySpan<byte> head;
    readonly HttpRequestLine line;
    readonly ReadOnlySpan<HttpField> fields;

    /// <summary>Built by a transport after parsing: <paramref name="head"/> holds the request line and header
    /// bytes, <paramref name="fields"/> indexes them, <paramref name="body"/> is the (de-chunked) body.</summary>
    public HttpRequest(ReadOnlySpan<byte> head, HttpRequestLine line, ReadOnlySpan<HttpField> fields, ReadOnlySpan<byte> body)
    {
        this.head = head;
        this.line = line;
        this.fields = fields;
        Body = body;
    }

    /// <summary>The method.</summary>
    public HttpVerb Verb => line.Verb;

    /// <summary>The method as sent.</summary>
    public ReadOnlySpan<byte> MethodBytes => head.Slice(line.MethodStart, line.MethodLength);

    /// <summary>The request target as sent (`/path?query`).</summary>
    public ReadOnlySpan<byte> Target => head.Slice(line.TargetStart, line.TargetLength);

    /// <summary>The target before `?`.</summary>
    public ReadOnlySpan<byte> Path => Target.IndexOf((byte)'?') is int q and >= 0 ? Target[..q] : Target;

    /// <summary>The target after `?` (empty when absent).</summary>
    public ReadOnlySpan<byte> Query => Target.IndexOf((byte)'?') is int q and >= 0 ? Target[(q + 1)..] : default;

    /// <summary>Protocol version.</summary>
    public HttpVersionKind Version => line.Version;

    /// <summary>Number of header fields.</summary>
    public int HeaderCount => fields.Length;

    /// <summary>Name of header <paramref name="i"/>.</summary>
    public ReadOnlySpan<byte> HeaderName(int i) => head.Slice(fields[i].NameStart, fields[i].NameLength);

    /// <summary>Value of header <paramref name="i"/>.</summary>
    public ReadOnlySpan<byte> HeaderValue(int i) => head.Slice(fields[i].ValueStart, fields[i].ValueLength);

    /// <summary>First header named <paramref name="name"/> (ASCII case-insensitive), without allocation.</summary>
    public bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < fields.Length; i++)
            if (Ascii.EqualsIgnoreCase(HeaderName(i), name)) { value = HeaderValue(i); return true; }
        value = default;
        return false;
    }

    /// <summary>The body bytes (empty when none).</summary>
    public ReadOnlySpan<byte> Body { get; }

    /// <summary>The path, materialized (Alice `req.path`).</summary>
    public string GetPath() => Encoding.UTF8.GetString(Path);

    /// <summary>A header value, materialized; null when absent (Alice `req.headers["X"]`).</summary>
    public string? GetHeader(string name)
    {
        Span<byte> n = name.Length <= 256 ? stackalloc byte[name.Length] : new byte[name.Length];
        return Ascii.FromUtf16(name, n, out int w) == System.Buffers.OperationStatus.Done && TryGetHeader(n[..w], out var v) ? Encoding.UTF8.GetString(v) : null;
    }
}

/// <summary>What a transport provides to <see cref="HttpResponse"/>: status and headers until the first body write,
/// then the body bytes (`IBufferWriter`: our JSON writer serializes straight into the transport's buffer).</summary>
public interface IHttpResponseSink : IBufferWriter<byte>
{
    /// <summary>Status code; settable until <see cref="HeadersSent"/>.</summary>
    int StatusCode { get; set; }

    /// <summary>Adds a header; invalid after <see cref="HeadersSent"/>.</summary>
    void AddHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);

    /// <summary>True once the status line and headers are on the wire (first flush of the body).</summary>
    bool HeadersSent { get; }
}

/// <summary>The handler's response: status, headers, body writes. A thin view over the transport's sink.</summary>
public readonly ref struct HttpResponse
{
    readonly IHttpResponseSink sink;

    /// <summary>Built by a transport for one request.</summary>
    public HttpResponse(IHttpResponseSink sink) => this.sink = sink;

    /// <summary>Status code (default 200).</summary>
    public int Status { get => sink.StatusCode; set => sink.StatusCode = value; }

    /// <summary>Adds a header from UTF-8 / ASCII bytes (`"content-type"u8`).</summary>
    public void SetHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) => sink.AddHeader(name, value);

    /// <summary>Adds a header from strings.</summary>
    public void SetHeader(string name, string value) => sink.AddHeader(Encoding.ASCII.GetBytes(name), Encoding.UTF8.GetBytes(value));

    /// <summary>True once headers can no longer change.</summary>
    public bool HeadersSent => sink.HeadersSent;

    /// <summary>Appends body bytes.</summary>
    public void Write(ReadOnlySpan<byte> utf8) => sink.Write(utf8);

    /// <summary>Appends body text as UTF-8.</summary>
    public void Write(ReadOnlySpan<char> text) => Encoding.UTF8.GetBytes(text, sink);

    /// <summary>The body as a buffer writer (for <see cref="Alice.Core.Json.JsonWriter"/>).</summary>
    public IBufferWriter<byte> BodyWriter => sink;
}
