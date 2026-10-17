using System.Text;
using System.Text.Json.Nodes;
using Alice.Cli.Lsp;

namespace Alice.Tests.Tooling;

/// <summary>`alice lsp` driven in-process over real JSON-RPC framing (no editor needed).</summary>
public class LspTests
{
    const string Uri = "file:///tmp/alice-lsp-test/main.al";

    sealed class Session
    {
        readonly LspServer server = new(new MemoryStream(), new MemoryStream()) { Synchronous = true };
        int id;

        public JsonNode? Request(string method, JsonObject? p = null)
        {
            var reqId = ++id;
            var outs = server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = reqId, ["method"] = method, ["params"] = p ?? new JsonObject() });
            return outs.FirstOrDefault(o => o["id"]?.GetValue<int>() == reqId)?["result"];
        }

        public List<JsonObject> Notify(string method, JsonObject p) =>
            server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p });

        public List<JsonObject> Open(string text) => Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["languageId"] = "alice", ["version"] = 1, ["text"] = text },
        });
    }

    static JsonObject Pos(int line, int ch) => new() { ["textDocument"] = new JsonObject { ["uri"] = Uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = ch } };

    static Session Started()
    {
        var s = new Session();
        var caps = s.Request("initialize", new JsonObject { ["processId"] = null, ["rootUri"] = null, ["capabilities"] = new JsonObject() })!["capabilities"]!;
        Assert.Equal(2, caps["textDocumentSync"]!["change"]!.GetValue<int>());   // incremental sync
        Assert.NotNull(caps["completionProvider"]);
        s.Notify("initialized", new JsonObject());
        return s;
    }

    [Fact]
    public void Framing_reads_and_writes_content_length_messages()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""";
        var input = new MemoryStream(Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}"));
        var msg = JsonRpc.Read(input);
        Assert.Equal("shutdown", msg!["method"]!.GetValue<string>());
        var output = new MemoryStream();
        JsonRpc.Write(output, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = "ü" });
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.StartsWith("Content-Length: ", text);
        Assert.EndsWith("""{"jsonrpc":"2.0","id":1,"result":"ü"}""", text);
        Assert.Null(JsonRpc.Read(new MemoryStream()));
    }

    [Fact]
    public void Diagnostics_on_open_and_cleared_after_a_fix()
    {
        var s = Started();
        var pub = s.Open("val x = 1\nx = 2\n").Single(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        var d = pub["params"]!["diagnostics"]!.AsArray().Single()!;
        Assert.Equal(1, d["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Contains("immutable", d["message"]!.GetValue<string>());
        Assert.Equal("AL0203", d["code"]!.GetValue<string>());

        // incremental edit: replace `val` with `var` on line 0
        var fixedPub = s.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject
            {
                ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = 3 } },
                ["text"] = "var",
            }),
        }).Single(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.Empty(fixedPub["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Parse_errors_point_at_the_line()
    {
        var s = Started();
        var pub = s.Open("def f(\n").Single(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.NotEmpty(pub["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Completion_offers_keywords_definitions_prelude_and_dotnet_members()
    {
        var s = Started();
        s.Open("def square(n : Int32) : Int32 = n * n\nsq\nMath.Sq\nputs \"x\".Len\n");
        var labels = Labels(s.Request("textDocument/completion", Pos(1, 2)));
        Assert.Contains("square", labels);
        var math = Labels(s.Request("textDocument/completion", Pos(2, 7)));
        Assert.Contains("Sqrt", math);
        Assert.DoesNotContain("square", math);
        var kw = Labels(s.Request("textDocument/completion", Pos(1, 0)));
        Assert.Contains("def", kw);
        Assert.Contains("puts", kw);
    }

    [Fact]
    public void Completing_a_dotnet_type_adds_the_using_line()
    {
        var s = Started();
        s.Open("puts 1\nsw = Stopwat\nsb = StringBui\n");
        var items = s.Request("textDocument/completion", Pos(1, 12))!["items"]!.AsArray();
        var sw = items.First(i => i!["label"]!.GetValue<string>() == "Stopwatch")!;
        Assert.Contains("System.Diagnostics", sw["detail"]!.GetValue<string>());
        var edit = sw["additionalTextEdits"]!.AsArray().Single()!;
        Assert.Equal("using System.Diagnostics\n", edit["newText"]!.GetValue<string>());
        Assert.Equal(0, edit["range"]!["start"]!["line"]!.GetValue<int>());
        // default namespaces (System.Text) need no using
        var sb = s.Request("textDocument/completion", Pos(2, 13))!["items"]!.AsArray().First(i => i!["label"]!.GetValue<string>() == "StringBuilder")!;
        Assert.Null(sb["additionalTextEdits"]);
    }

    [Fact]
    public void Unknown_type_gets_an_add_using_quick_fix()
    {
        var s = Started();
        var pub = s.Open("sw = Stopwatch.new\nputs sw\n").Single(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        var diags = pub["params"]!["diagnostics"]!.AsArray();
        Assert.NotEmpty(diags);
        var actions = s.Request("textDocument/codeAction", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["range"] = diags[0]!["range"]!.DeepClone(),
            ["context"] = new JsonObject { ["diagnostics"] = diags.DeepClone() },
        })!.AsArray();
        var fix = actions.First(a => a!["title"]!.GetValue<string>() == "Add `using System.Diagnostics`")!;
        var edits = fix["edit"]!["changes"]![Uri]!.AsArray();
        Assert.Equal("using System.Diagnostics\n", edits[0]!["newText"]!.GetValue<string>());
    }

    [Fact]
    public void Hover_definition_symbols_and_formatting()
    {
        var s = Started();
        s.Open("# squares\ndef square(n : Int32) : Int32\n  n * n\nend\n\nclass Rabbit\n  getter name : String = \"White\"\nend\n\nputs square(3)\n");

        var hover = s.Request("textDocument/hover", Pos(9, 7))!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("def square(n : Int32) : Int32", hover);
        var puts = s.Request("textDocument/hover", Pos(9, 1))!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("puts", puts);

        var def = s.Request("textDocument/definition", Pos(9, 7))!;
        Assert.Equal(1, def["range"]!["start"]!["line"]!.GetValue<int>());

        var symbols = s.Request("textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } })!.AsArray();
        Assert.Contains(symbols, x => x!["name"]!.GetValue<string>() == "square");
        var rabbit = symbols.First(x => x!["name"]!.GetValue<string>() == "Rabbit")!;
        Assert.Contains(rabbit["children"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "name");

        s.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = 3 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "def f\n1\nend\n" }),
        });
        var edits = s.Request("textDocument/formatting", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri }, ["options"] = new JsonObject { ["tabSize"] = 2, ["insertSpaces"] = true } })!.AsArray();
        Assert.Equal("def f\n  1\nend\n", edits.Single()!["newText"]!.GetValue<string>());
    }

    [Fact]
    public void Shutdown_and_exit()
    {
        var s = Started();
        Assert.Null(s.Request("shutdown"));
        var server = new LspServer(new MemoryStream(), new MemoryStream()) { Synchronous = true };
        server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" });
        Assert.True(server.Exited);
    }

    static List<string> Labels(JsonNode? result) =>
        result!["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>()).ToList();
}
