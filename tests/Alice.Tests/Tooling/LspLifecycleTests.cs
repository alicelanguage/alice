using System.Text.Json.Nodes;
using Alice.Cli.Lsp;

namespace Alice.Tests.Tooling;

public class LspLifecycleTests
{
    static JsonObject Message(string method, JsonObject? p = null, int? id = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p ?? new() };
        if (id != null) message["id"] = id;
        return message;
    }
    static void Initialize(LspServer server) => server.Handle(Message("initialize", new JsonObject { ["initializationOptions"] = new JsonObject { ["deepChecks"] = false } }, 1));
    static JsonObject TextDocument(string uri) => new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };

    [Fact]
    public void Lifecycle_rejects_early_requests_unknown_methods_and_requests_after_shutdown()
    {
        using var server = new LspServer(new MemoryStream(), new MemoryStream()) { Synchronous = true };
        Assert.Equal(-32002, server.Handle(Message("textDocument/hover", id: 1))[0]["error"]!["code"]!.GetValue<int>());
        Initialize(server);
        Assert.Equal(-32601, server.Handle(Message("does/not/exist", id: 2))[0]["error"]!["code"]!.GetValue<int>());
        Assert.Empty(server.Handle(Message("does/not/exist")));
        server.Handle(Message("shutdown", id: 3));
        Assert.NotNull(server.Handle(Message("textDocument/hover", id: 4))[0]["error"]);
        server.Handle(Message("exit"));
        Assert.True(server.Exited);
        Assert.Equal(0, server.ExitCode);
    }

    [Fact]
    public void Stale_edits_are_ignored_and_closing_clears_diagnostics()
    {
        const string uri = "file:///tmp/lifecycle.al";
        using var server = new LspServer(new MemoryStream(), new MemoryStream()) { Synchronous = true };
        Initialize(server);
        server.Handle(Message("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["text"] = "puts 1\n", ["version"] = 2 } }));
        var stale = TextDocument(uri);
        stale["textDocument"]!["version"] = 1;
        stale["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "invalid(" });
        Assert.Empty(server.Handle(Message("textDocument/didChange", stale)));
        var closed = server.Handle(Message("textDocument/didClose", TextDocument(uri)));
        Assert.Empty(closed[0]["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Project_analysis_uses_unsaved_sibling_sources_and_preserves_framework_references()
    {
        var root = Path.Combine(Path.GetTempPath(), "alice-lsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "obj", "packs"));
        try
        {
            File.WriteAllText(Path.Combine(root, "alice.toml"), "[package]\nname = \"test\"\n");
            var refs = typeof(System.Text.Json.JsonDocument).Assembly.Location;
            File.WriteAllText(Path.Combine(root, "obj", "refs.txt"), refs);
            var main = new Document(new Uri(Path.Combine(root, "src", "main.al")).AbsoluteUri, "puts answer()\n", 1);
            var sibling = new Document(new Uri(Path.Combine(root, "src", "lib.al")).AbsoluteUri, "def answer : Int32 = 42\n", 1);
            File.WriteAllText(sibling.Path, "def wrong = 1\n");
            var project = Project.For(main.Path);
            Assert.Contains(refs, project.Refs);
            var sources = Project.Sources(main, [main, sibling]);
            Assert.Contains(sibling, sources);
            Assert.Empty(Analyzer.Quick(main, project, sources));
            Assert.Empty(Analyzer.Deep(main, project, sources));
        }
        finally { Directory.Delete(root, true); Project.Invalidate(); }
    }

    [Fact]
    public void Edits_and_close_of_a_sibling_refresh_other_open_documents()
    {
        string root = Path.Combine(Path.GetTempPath(), "alice-siblings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            File.WriteAllText(Path.Combine(root, "alice.toml"), "[package]\nname = \"siblings\"\n");
            string main = new Uri(Path.Combine(root, "src", "main.al")).AbsoluteUri;
            string lib = new Uri(Path.Combine(root, "src", "lib.al")).AbsoluteUri;
            File.WriteAllText(new Uri(lib).LocalPath, "def disk_version = 1\n");
            using var server = new LspServer(new MemoryStream(), new MemoryStream()) { Synchronous = true };
            Initialize(server);
            void Open(string uri, string text) => server.Handle(Message("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["text"] = text, ["version"] = 1 } }));
            Open(main, "puts 1\n");
            Open(lib, "def first_version = 1\n");
            var change = TextDocument(lib);
            change["textDocument"]!["version"] = 2;
            change["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "def new_version = 2\n" });
            var publications = server.Handle(Message("textDocument/didChange", change));
            Assert.Contains(publications, m => m["params"]?["uri"]?.GetValue<string>() == main);
            JsonArray Completions()
            {
                var p = TextDocument(main);
                p["position"] = new JsonObject { ["line"] = 0, ["character"] = 0 };
                return server.Handle(Message("textDocument/completion", p, 2)).Single(m => m.ContainsKey("result"))["result"]!["items"]!.AsArray();
            }
            Assert.Contains(Completions(), i => i!["label"]!.GetValue<string>() == "new_version");
            server.Handle(Message("textDocument/didClose", TextDocument(lib)));
            Assert.Contains(Completions(), i => i!["label"]!.GetValue<string>() == "disk_version");
            Assert.DoesNotContain(Completions(), i => i!["label"]!.GetValue<string>() == "new_version");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Library_declarations_do_not_require_an_entry_point()
    {
        var doc = new Document("file:///tmp/library.al", "class Rabbit\n  getter name : String = \"white\"\nend\n", 1);
        var project = Project.For(doc.Path);
        Assert.Empty(Analyzer.Quick(doc, project));
        Assert.Empty(Analyzer.Deep(doc, project));
    }

    [Fact]
    public void Utf16_edits_respect_surrogate_pairs_and_crlf()
    {
        var doc = new Document("file:///tmp/utf16.al", "# 🐇\r\nputs 1\r\n", 1);
        doc.Apply(new JsonObject { ["range"] = Features.Range(0, 2, 0, 4), ["text"] = "Alice" });
        Assert.Equal("# Alice\r\nputs 1\r\n", doc.Text);
        Assert.Equal("puts 1", doc.Line(1));
    }
}
