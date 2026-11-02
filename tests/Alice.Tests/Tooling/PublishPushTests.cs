using System.Net;
using System.Net.Sockets;
using System.Text;
using Alice.Cli.Projects;

namespace Alice.Tests.Tooling;

/// <summary>`alice publish` pushes over HTTPS itself: the API key goes in a header, never on a command line.</summary>
public class PublishPushTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-push-" + Guid.NewGuid().ToString("N"));

    public PublishPushTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    sealed record Seen(string Method, string Path, string? ApiKey, string Body);

    /// <summary>A feed with a v3 service index whose PackagePublish/2.0.0 resource answers <paramref name="status"/>.</summary>
    static async Task<(NuGetPush.Outcome Outcome, List<Seen> Seen)> PushTo(string nupkg, int status)
    {
        int port = FreePort();
        string root = $"http://127.0.0.1:{port}/";
        using var http = new HttpListener();
        http.Prefixes.Add(root);
        http.Start();
        var seen = new List<Seen>();
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var ctx = await http.GetContextAsync();
                using var r = new StreamReader(ctx.Request.InputStream, Encoding.Latin1);
                seen.Add(new Seen(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["X-NuGet-ApiKey"], await r.ReadToEndAsync()));
                byte[] body = [];
                if (ctx.Request.HttpMethod == "GET")
                    body = Encoding.UTF8.GetBytes($$"""{"version":"3.0.0","resources":[{"@id":"{{root}}api/v2/package","@type":"PackagePublish/2.0.0"}]}""");
                ctx.Response.StatusCode = ctx.Request.HttpMethod == "GET" ? 200 : status;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });
        var outcome = await NuGetPush.PushAsync(root + "v3/index.json", "s3cr3t-key", nupkg, CancellationToken.None);
        await server;
        return (outcome, seen);
    }

    string Package()
    {
        var f = Path.Combine(dir, "Acme.Geo.1.2.3.nupkg");
        File.WriteAllBytes(f, Encoding.ASCII.GetBytes("PK-fake-package-bytes"));
        return f;
    }

    [Fact]
    public async Task Push_finds_the_publish_resource_and_sends_the_key_as_a_header()
    {
        var (outcome, seen) = await PushTo(Package(), 201);
        Assert.Equal(NuGetPush.Outcome.Published, outcome);
        Assert.Equal(("GET", "/v3/index.json"), (seen[0].Method, seen[0].Path));
        Assert.Null(seen[0].ApiKey);   // the index needs no key
        Assert.Equal("PUT", seen[1].Method);
        Assert.Equal("/api/v2/package/", seen[1].Path);
        Assert.Equal("s3cr3t-key", seen[1].ApiKey);
        Assert.Contains("PK-fake-package-bytes", seen[1].Body);
        Assert.Contains("Acme.Geo.1.2.3.nupkg", seen[1].Body);
    }

    [Fact]
    public async Task Conflict_means_the_version_is_already_published()
    {
        var (outcome, _) = await PushTo(Package(), 409);
        Assert.Equal(NuGetPush.Outcome.AlreadyExists, outcome);
    }

    [Fact]
    public async Task Rejection_names_the_status_but_never_the_key()
    {
        var e = await Assert.ThrowsAsync<NuGetPush.PushException>(() => PushTo(Package(), 403));
        Assert.Contains("403", e.Message);
        Assert.DoesNotContain("s3cr3t-key", e.Message);
    }

    [Theory]
    [InlineData("http://feed.example/v3/index.json")]
    [InlineData("ftp://feed.example/v3/index.json")]
    public async Task Keys_are_only_sent_over_https(string source)
    {
        var e = await Assert.ThrowsAsync<NuGetPush.PushException>(() => NuGetPush.PushAsync(source, "k", Package(), CancellationToken.None));
        Assert.Contains("https", e.Message);
    }
}
