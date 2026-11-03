using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Alice.Core.Json;

namespace Alice.Core.Net.Http;

/// <summary>Reads a value of type <typeparamref name="T"/> from JSON (a generated `__json_read_T` or a hand-written
/// reader). The hook through which <see cref="HttpReply.Json{T}"/> parses the pooled body without a DOM.</summary>
public delegate T JsonReadFunc<out T>(ref JsonIterator reader);

/// <summary>Writes a JSON value (a generated `__json_write_T` or hand-written); used for request bodies.</summary>
public delegate void JsonWriteAction(ref JsonWriter writer);

/// <summary>
/// An HTTP client session over <see cref="SocketsHttpHandler"/> (HTTP/1.1, HTTP/2, TLS, connection pooling): Ruby
/// `HTTP::Client.new(base_url:, headers:, timeout:)`. Thread-safe; create one per service and reuse it. Responses
/// are <see cref="HttpReply"/> with the body in a pooled buffer. Failed requests (connection errors, 502/503/504)
/// are retried <see cref="Retries"/> times with exponential backoff, for idempotent methods only.
/// </summary>
public sealed class HttpSession : IDisposable
{
    readonly HttpClient client;

    /// <summary>Extra attempts for GET/HEAD/PUT/DELETE on connection errors and 502/503/504 (default 0).</summary>
    public int Retries { get; set; }

    /// <summary>Backoff before the first retry (doubled each time).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>A session; relative URLs resolve against <paramref name="baseUrl"/>.</summary>
    public HttpSession(string? baseUrl = null, IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
        if (baseUrl is not null) client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        if (headers is not null)
            foreach (var (k, v) in headers) client.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
    }

    /// <summary>The underlying <see cref="HttpClient"/>.</summary>
    public HttpClient Client => client;

    /// <summary>Ruby `get(url)`.</summary>
    public HttpReply Get(string url) => Send(HttpMethod.Get, url, null);

    /// <summary>Ruby `delete(url)`.</summary>
    public HttpReply Delete(string url) => Send(HttpMethod.Delete, url, null);

    /// <summary>Ruby `post(url, body:)` with a text body.</summary>
    public HttpReply Post(string url, string body, string contentType = "text/plain; charset=utf-8")
        => Send(HttpMethod.Post, url, Text(body, contentType));

    /// <summary>Ruby `put(url, body:)` with a text body.</summary>
    public HttpReply Put(string url, string body, string contentType = "text/plain; charset=utf-8")
        => Send(HttpMethod.Put, url, Text(body, contentType));

    /// <summary>Ruby `post(url, json: obj)`: the body is written by <paramref name="write"/> through
    /// <see cref="JsonWriter"/> straight into a pooled buffer.</summary>
    public HttpReply PostJson(string url, JsonWriteAction write) => Send(HttpMethod.Post, url, Json(write));

    /// <summary>Ruby `put(url, json: obj)`.</summary>
    public HttpReply PutJson(string url, JsonWriteAction write) => Send(HttpMethod.Put, url, Json(write));

    /// <summary>Async <see cref="Get"/>.</summary>
    public Task<HttpReply> GetAsync(string url, CancellationToken ct = default) => SendAsync(HttpMethod.Get, url, null, ct);

    /// <summary>Async <see cref="Post"/>.</summary>
    public Task<HttpReply> PostAsync(string url, string body, string contentType = "text/plain; charset=utf-8", CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, url, Text(body, contentType), ct);

    static ByteArrayContent Text(string body, string contentType)
    {
        var c = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        c.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return c;
    }

    static ByteArrayContent Json(JsonWriteAction write)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(buf);
        write(ref w);
        w.Flush();
        var c = new ByteArrayContent(buf.WrittenSpan.ToArray());
        c.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return c;
    }

    static bool Idempotent(HttpMethod m) => m == HttpMethod.Get || m == HttpMethod.Head || m == HttpMethod.Put || m == HttpMethod.Delete;

    static bool Transient(HttpStatusCode s) => s is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>Sends a request synchronously and buffers the body.</summary>
    public HttpReply Send(HttpMethod method, string url, HttpContent? content)
    {
        int attempt = 0;
        while (true)
        {
            var req = new HttpRequestMessage(method, url) { Content = content };   // not disposed: a retry resends the content
            try
            {
                using var resp = client.Send(req, HttpCompletionOption.ResponseHeadersRead);
                if (attempt < Retries && Idempotent(method) && Transient(resp.StatusCode)) { Backoff(attempt++); continue; }
                using var body = resp.Content.ReadAsStream();
                return HttpReply.Read(resp, body);
            }
            catch (HttpRequestException) when (attempt < Retries && Idempotent(method))
            {
                Backoff(attempt++);
            }
        }
    }

    /// <summary>Sends a request asynchronously and buffers the body.</summary>
    public async Task<HttpReply> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct = default)
    {
        int attempt = 0;
        while (true)
        {
            var req = new HttpRequestMessage(method, url) { Content = content };   // not disposed: a retry resends the content
            try
            {
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (attempt < Retries && Idempotent(method) && Transient(resp.StatusCode))
                {
                    await Task.Delay(RetryDelay * (1 << attempt++), ct).ConfigureAwait(false);
                    continue;
                }
                await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await HttpReply.ReadAsync(resp, body, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < Retries && Idempotent(method))
            {
                await Task.Delay(RetryDelay * (1 << attempt++), ct).ConfigureAwait(false);
            }
        }
    }

    void Backoff(int attempt) => Thread.Sleep(RetryDelay * (1 << attempt));

    /// <summary>Closes pooled connections.</summary>
    public void Dispose() => client.Dispose();
}

/// <summary>
/// A buffered HTTP response: status, headers and the body in a pooled buffer. <see cref="Json{T}"/> parses the body
/// in place through <see cref="JsonIterator"/> (no intermediate string); <see cref="Text"/> decodes it. Dispose
/// returns the buffer.
/// </summary>
public sealed class HttpReply : IDisposable
{
    byte[] body;
    readonly int length;
    readonly HttpResponseHeaders headers;
    readonly HttpContentHeaders contentHeaders;

    HttpReply(int status, string? reason, HttpResponseHeaders h, HttpContentHeaders ch, byte[] body, int length)
    {
        Status = status; Reason = reason; headers = h; contentHeaders = ch; this.body = body; this.length = length;
    }

    /// <summary>Ruby `status` / `code`.</summary>
    public int Status { get; }

    /// <summary>The reason phrase.</summary>
    public string? Reason { get; }

    /// <summary>Ruby `success?`: status 200-299.</summary>
    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>The body bytes (valid until <see cref="Dispose"/>).</summary>
    public ReadOnlySpan<byte> Body => body.AsSpan(0, length);

    /// <summary>The body bytes as memory (valid until <see cref="Dispose"/>).</summary>
    public ReadOnlyMemory<byte> BodyMemory => body.AsMemory(0, length);

    /// <summary>Ruby `body` / `to_s`: the body decoded as UTF-8.</summary>
    public string Text => Encoding.UTF8.GetString(Body);

    /// <summary>A response or content header (comma-joined when repeated), or null.</summary>
    public string? Header(string name)
        => headers.TryGetValues(name, out var v) || contentHeaders.TryGetValues(name, out v) ? string.Join(", ", v) : null;

    /// <summary>The media type of the body ("application/json"), or null.</summary>
    public string? ContentType => contentHeaders.ContentType?.MediaType;

    /// <summary>Ruby `json(as: T)`: reads <typeparamref name="T"/> from the pooled body with
    /// <paramref name="read"/> (generated per type by the compiler, task C2.5).</summary>
    public T Json<T>(JsonReadFunc<T> read)
    {
        var it = new JsonIterator(Body);
        try { return read(ref it); }
        finally { it.Dispose(); }
    }

    /// <summary>Raises <see cref="HttpRequestException"/> unless <see cref="IsSuccess"/>.</summary>
    public HttpReply EnsureSuccess() => IsSuccess ? this : throw new HttpRequestException($"HTTP {Status} {Reason}", null, (HttpStatusCode)Status);

    internal static HttpReply Read(HttpResponseMessage resp, Stream s)
    {
        long? len = resp.Content.Headers.ContentLength;
        var buf = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(len ?? 16 * 1024, 256, 1 << 30));
        int n = 0;
        while (true)
        {
            if (n == buf.Length) buf = Grow(buf, n);
            int k = s.Read(buf, n, buf.Length - n);
            if (k == 0) break;
            n += k;
        }
        return new HttpReply((int)resp.StatusCode, resp.ReasonPhrase, resp.Headers, resp.Content.Headers, buf, n);
    }

    internal static async Task<HttpReply> ReadAsync(HttpResponseMessage resp, Stream s, CancellationToken ct)
    {
        long? len = resp.Content.Headers.ContentLength;
        var buf = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(len ?? 16 * 1024, 256, 1 << 30));
        int n = 0;
        while (true)
        {
            if (n == buf.Length) buf = Grow(buf, n);
            int k = await s.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false);
            if (k == 0) break;
            n += k;
        }
        return new HttpReply((int)resp.StatusCode, resp.ReasonPhrase, resp.Headers, resp.Content.Headers, buf, n);
    }

    static byte[] Grow(byte[] buf, int n)
    {
        var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
        buf.AsSpan(0, n).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(buf);
        return bigger;
    }

    /// <summary>Returns the body buffer to the pool.</summary>
    public void Dispose()
    {
        if (body.Length > 0) ArrayPool<byte>.Shared.Return(body);
        body = [];
    }
}

/// <summary>Ruby `HTTP.get(url)` one-shots over a shared default <see cref="HttpSession"/> (created on first use).</summary>
public static class Http
{
    static class Default { internal static readonly HttpSession Session = new(); }

    /// <summary>The shared session.</summary>
    public static HttpSession Session => Default.Session;

    /// <summary>Ruby `HTTP.get(url)`.</summary>
    public static HttpReply Get(string url) => Default.Session.Get(url);

    /// <summary>Ruby `HTTP.get(url).to_s`.</summary>
    public static string GetString(string url)
    {
        using var r = Default.Session.Get(url);
        return r.Text;
    }

    /// <summary>Ruby `HTTP.post(url, body:)`.</summary>
    public static HttpReply Post(string url, string body, string contentType = "text/plain; charset=utf-8") => Default.Session.Post(url, body, contentType);

    /// <summary>Ruby `HTTP.post(url, json:)`.</summary>
    public static HttpReply PostJson(string url, JsonWriteAction write) => Default.Session.PostJson(url, write);

    /// <summary>Async <see cref="Get"/>.</summary>
    public static Task<HttpReply> GetAsync(string url, CancellationToken ct = default) => Default.Session.GetAsync(url, ct);
}
