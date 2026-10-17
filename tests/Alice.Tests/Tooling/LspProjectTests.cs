using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Alice.Cli.Lsp;

namespace Alice.Tests.Tooling;

public class LspProjectTests
{
    [Fact]
    public void Signature_help_tracks_parameter_and_dotnet_overloads()
    {
        var doc = new Document("file:///tmp/signature.al", "def add(x : Int32, y : Int32) : Int32 = x + y\nputs add(1, \nMath.Clamp(1, ", 1);
        Analyzer.Quick(doc, Project.For(doc.Path));
        var own = Features.SignatureHelp(doc, Project.For(doc.Path), 1, 12)!;
        Assert.Equal(1, own["activeParameter"]!.GetValue<int>());
        Assert.Contains("add", own["signatures"]![0]!["label"]!.GetValue<string>());
        var clr = Features.SignatureHelp(doc, Project.For(doc.Path), 2, 14)!;
        Assert.NotEmpty(clr["signatures"]!.AsArray());
    }

    [Fact]
    public void Incomplete_member_access_keeps_inferred_generic_types_and_declarations()
    {
        var doc = new Document("file:///tmp/generic-lsp.al", "def square(n : Int32) : Int32 = n * n\nxs = List(Int32).new\nxs.\n", 1);
        Analyzer.Quick(doc, Project.For(doc.Path));
        var members = Features.Completion(doc, Project.For(doc.Path), 2, 3)["items"]!.AsArray();
        Assert.Contains(members, i => i!["label"]!.GetValue<string>() == "Add");
        Assert.NotNull(Features.Definition(doc, "square"));
    }

    [Fact]
    public void Referenced_csharp_api_completion_and_xml_docs()
    {
        var root = Path.Combine(Path.GetTempPath(), "alice-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        try
        {
            string dll = Path.Combine(root, "Library" + Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllBytes(dll, Alice.Compiler.AliceCompiler.Emit(["namespace Garden; public static class RabbitApi { public static int Jump(int height) => height; }"], Path.GetFileNameWithoutExtension(dll), exe: false));
            File.WriteAllText(Path.ChangeExtension(dll, ".xml"), "<doc><members><member name=\"M:Garden.RabbitApi.Jump(System.Int32)\"><summary>Jump over the garden gate.</summary><param name=\"height\">Height in metres.</param></member></members></doc>");
            File.WriteAllText(Path.Combine(root, "alice.toml"), "[package]\nname = \"api\"\n");
            File.WriteAllText(Path.Combine(root, "obj", "refs.txt"), dll);
            var doc = new Document(new Uri(Path.Combine(root, "main.al")).AbsoluteUri, "using Garden\nputs RabbitApi.Jump(2)\nRabbitApi.Ju\n", 1);
            var project = Project.For(doc.Path);
            Analyzer.Quick(doc, project);
            Assert.Contains(Features.Completion(doc, project, 2, 12)["items"]!.AsArray(), i => i!["label"]!.GetValue<string>() == "Jump");
            var hover = Features.Hover(doc, project, 1, 17)!["contents"]!["value"]!.GetValue<string>();
            Assert.Contains("Jump over the garden gate", hover);
            Assert.Contains("Height in metres", hover);
        }
        finally
        {
            Project.Invalidate();
            try { Directory.Delete(root, true); } catch (IOException) { } // Windows can retain loaded DLL handles.
        }
    }

    [Fact]
    public void Completion_distinguishes_instance_receivers_and_offers_import_namespaces()
    {
        var doc = new Document("file:///tmp/receiver-lsp.al", "puts \"rabbit\".Len\nDateTime.Now.ToStr\nusing System.Di\n", 1);
        Analyzer.Quick(doc, Project.For(doc.Path));
        Assert.Contains(Features.Completion(doc, Project.For(doc.Path), 0, 17)["items"]!.AsArray(), i => i!["label"]!.GetValue<string>() == "Length");
        Assert.Contains(Features.Completion(doc, Project.For(doc.Path), 1, 18)["items"]!.AsArray(), i => i!["label"]!.GetValue<string>() == "ToString");
        Assert.Contains(Features.Completion(doc, Project.For(doc.Path), 2, 15)["items"]!.AsArray(), i => i!["label"]!.GetValue<string>() == "Diagnostics");
    }

    [Fact]
    public void Document_symbol_ranges_contain_their_children()
    {
        var doc = new Document("file:///tmp/symbol-lsp.al", "class Rabbit\n  getter name : String = \"white\"\n  def jump : Int32\n    1\n  end\nend\n", 1);
        Analyzer.Quick(doc, Project.For(doc.Path));
        var symbol = Features.Symbols(doc)[0]!;
        int endLine = symbol["range"]!["end"]!["line"]!.GetValue<int>();
        foreach (var child in symbol["children"]!.AsArray())
            Assert.True(endLine >= child!["range"]!["end"]!["line"]!.GetValue<int>());
        Assert.Equal(5, endLine);
    }

    sealed class Feed : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.ToString());
            string json = request.RequestUri.AbsolutePath.Contains("flatcontainer") ? "{\"versions\":[\"1.0.0\",\"2.0.0\",\"3.0.0-beta\"]}" : "{\"data\":[\"Newtonsoft.Json\"]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void Manifest_completion_fetches_ids_and_versions_and_caches_requests()
    {
        var feed = new Feed();
        using var service = new NuGetService(new HttpClient(feed));
        var doc = new Document("file:///tmp/alice.toml", "[dependencies]\nNewt", 1);
        var ids = ProjectFeatures.Handle("textDocument/completion", doc, 1, 4, service)!;
        Assert.Contains(ids["items"]!.AsArray(), i => i!["label"]!.GetValue<string>() == "Newtonsoft.Json");
        ProjectFeatures.Handle("textDocument/completion", doc, 1, 4, service);
        Assert.Single(feed.Requests);
        var versions = new Document(doc.Uri, "[dependencies]\n\"Newtonsoft.Json\" = \"", 2);
        var items = ProjectFeatures.Handle("textDocument/completion", versions, 1, 21, service)!["items"]!.AsArray();
        Assert.Contains(items, i => i!["label"]!.GetValue<string>() == "2.0.0");
        Assert.DoesNotContain(items, i => i!["label"]!.GetValue<string>() == "3.0.0-beta");
        service.Enabled = false;
        Assert.Empty(ProjectFeatures.Handle("textDocument/completion", doc, 1, 4, service)!["items"]!.AsArray());
        Assert.Equal(2, feed.Requests.Count);
    }

    [Fact]
    public void Xml_project_formatting_preserves_values_and_manifest_comments()
    {
        using var service = new NuGetService();
        var xml = new Document("file:///tmp/test.alproj", "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>", 1);
        var edits = ProjectFeatures.Handle("textDocument/formatting", xml, 0, 0, service)!.AsArray();
        Assert.Contains("\n  <PropertyGroup>", edits[0]!["newText"]!.GetValue<string>());
        Assert.Contains("<OutputType>Exe</OutputType>", edits[0]!["newText"]!.GetValue<string>());
        var toml = new Document("file:///tmp/alice.toml", "[package]\nname=\"rabbit\" # keep me\n", 1);
        var formatted = ProjectFeatures.Handle("textDocument/formatting", toml, 0, 0, service)!.AsArray()[0]!["newText"]!.GetValue<string>();
        Assert.Contains("name = \"rabbit\" # keep me", formatted);
        Assert.Empty(ProjectFeatures.Diagnostics(new Document(xml.Uri, edits[0]!["newText"]!.GetValue<string>(), 2)));
        Assert.NotEmpty(ProjectFeatures.Diagnostics(new Document(xml.Uri, "<Project>", 2)));
    }

    [Fact]
    public void Invalid_or_oversized_protocol_lengths_are_rejected()
    {
        foreach (var length in new[] { "-1", "999999999999", "33554433", "bad" })
            Assert.Throws<InvalidDataException>(() => JsonRpc.Read(new MemoryStream(Encoding.ASCII.GetBytes($"Content-Length: {length}\r\n\r\n"))));
    }
}
