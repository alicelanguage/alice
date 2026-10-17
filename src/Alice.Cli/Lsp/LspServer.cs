using System.Text.Json;
using System.Text.Json.Nodes;
using Alice.Compiler;

namespace Alice.Cli.Lsp;

/// <summary>Stdio LSP. A single gate serializes compiler access; one timer coalesces edits per document.</summary>
public sealed class LspServer(Stream input, Stream output) : IDisposable
{
    readonly object gate = new();
    readonly Dictionary<string, Document> documents = new(StringComparer.Ordinal);
    readonly Dictionary<string, Timer> pending = new(StringComparer.Ordinal);
    bool initialized, shutdown, disposed, canWatch;
    int debounce = 180;
    bool deepChecks = true;
    public bool Synchronous { get; init; }
    public bool Exited { get; private set; }
    public int ExitCode { get; private set; } = 1;
    public NuGetService Packages { get; init; } = new();

    public int Run()
    {
        while (!Exited)
        {
            JsonObject? message;
            try { message = JsonRpc.Read(input); }
            catch (JsonException) { JsonRpc.Write(output, Error(null, -32700, "Invalid JSON")); continue; }
            catch (InvalidDataException e) { Console.Error.WriteLine(e.Message); return 1; }
            if (message == null) break;
            foreach (var response in Handle(message)) JsonRpc.Write(output, response);
        }
        return ExitCode;
    }

    public List<JsonObject> Handle(JsonObject message)
    {
        lock (gate)
        {
            var replies = new List<JsonObject>();
            if (disposed || Exited) return replies;
            if (!message.ContainsKey("method")) return replies; // Response to a server registration request.
            var id = message["id"];
            bool request = message.ContainsKey("id");
            var method = message["method"]?.GetValue<string>();
            var p = message["params"] as JsonObject ?? new();
            if (method == "exit") { Exited = true; ExitCode = shutdown ? 0 : 1; CancelAll(); return replies; }
            if (method == "initialize" && !initialized)
            {
                initialized = true;
                canWatch = p["capabilities"]?["workspace"]?["didChangeWatchedFiles"]?["dynamicRegistration"]?.GetValue<bool>() == true;
                Configure(p["initializationOptions"] as JsonObject);
                replies.Add(Reply(id, new JsonObject
                {
                    ["serverInfo"] = new JsonObject { ["name"] = "Alice", ["version"] = App.Version },
                    ["capabilities"] = new JsonObject
                    {
                        ["positionEncoding"] = "utf-16",
                        ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 2, ["save"] = new JsonObject { ["includeText"] = false } },
                        ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".", "\""), ["resolveProvider"] = false },
                        ["hoverProvider"] = true, ["definitionProvider"] = true,
                        ["documentSymbolProvider"] = true, ["documentFormattingProvider"] = true,
                        ["codeActionProvider"] = true,
                        ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",") },
                        ["workspace"] = new JsonObject { ["workspaceFolders"] = new JsonObject { ["supported"] = true, ["changeNotifications"] = true } },
                    },
                }));
                return replies;
            }
            if (!initialized || shutdown)
            {
                if (request) replies.Add(Error(id, initialized ? -32600 : -32002, initialized ? "Server shut down" : "Server not initialized"));
                return replies;
            }
            try
            {
                JsonNode? result = null;
                switch (method)
                {
                    case "initialized":
                        if (canWatch) replies.Add(new JsonObject
                        {
                            ["jsonrpc"] = "2.0", ["id"] = "alice-watch", ["method"] = "client/registerCapability",
                            ["params"] = new JsonObject { ["registrations"] = new JsonArray(new JsonObject
                            {
                                ["id"] = "alice-project-files", ["method"] = "workspace/didChangeWatchedFiles",
                                ["registerOptions"] = new JsonObject { ["watchers"] = new JsonArray(new JsonObject { ["globPattern"] = "**/{*.al,*.dll,*.xml,*.alproj,alice.toml,alice.lock,refs.txt}", ["kind"] = 7 }) },
                            }) },
                        });
                        break;
                    case "$/cancelRequest": case "$/setTrace": break;
                    case "shutdown": shutdown = true; CancelAll(); documents.Clear(); break;
                    case "workspace/didChangeConfiguration": Configure(p["settings"]?["alice"] as JsonObject ?? p["settings"] as JsonObject); break;
                    case "workspace/didChangeWorkspaceFolders": break;
                    case "workspace/didChangeWatchedFiles":
                        Project.Invalidate();
                        foreach (var doc in documents.Values) Schedule(doc, true, replies, debounce);
                        break;
                    case "textDocument/didOpen":
                    {
                        var td = p["textDocument"]!;
                        var doc = new Document(td["uri"]!.GetValue<string>(), td["text"]!.GetValue<string>(), td["version"]!.GetValue<int>());
                        if (!IsAlice(doc) && Path.GetFileName(doc.Path) != "alice.toml" && !doc.Path.EndsWith(".alproj")) break;
                        documents[doc.Uri] = doc;
                        Schedule(doc, true, replies);
                        ScheduleDependents(doc, replies);
                        break;
                    }
                    case "textDocument/didChange":
                    {
                        var doc = Get(p);
                        if (doc == null) break;
                        int version = p["textDocument"]!["version"]!.GetValue<int>();
                        if (version <= doc.Version) break;
                        foreach (var change in p["contentChanges"]!.AsArray()) doc.Apply(change!.AsObject());
                        doc.Version = version;
                        doc.DeepDiagnostics.Clear();
                        Schedule(doc, false, replies);
                        ScheduleDependents(doc, replies);
                        break;
                    }
                    case "textDocument/didSave":
                        if (Get(p) is { } saved) Schedule(saved, true, replies);
                        break;
                    case "textDocument/didClose":
                    {
                        string uri = p["textDocument"]!["uri"]!.GetValue<string>();
                        Cancel(uri);
                        if (documents.Remove(uri, out var closed)) ScheduleDependents(closed, replies);
                        replies.Add(Publish(uri, null, []));
                        if (documents.Count == 0) Project.Invalidate();
                        break;
                    }
                    case "textDocument/completion": case "textDocument/hover": case "textDocument/definition":
                    case "textDocument/documentSymbol": case "textDocument/formatting": case "textDocument/codeAction": case "textDocument/signatureHelp":
                    {
                        var doc = Get(p);
                        if (doc == null) break;
                        var project = Project.For(doc.Path);
                        if (IsAlice(doc) && (doc.AnalyzedVersion != doc.Version || !ReferenceEquals(doc.AnalyzedProject, project))) Analyzer.Quick(doc, project, Project.Sources(doc, documents.Values));
                        int line = p["position"]?["line"]?.GetValue<int>() ?? 0;
                        int ch = p["position"]?["character"]?.GetValue<int>() ?? 0;
                        if (!IsAlice(doc))
                        {
                            result = ProjectFeatures.Handle(method, doc, line, ch, Packages);
                            break;
                        }
                        result = method switch
                        {
                            "textDocument/completion" => Features.Completion(doc, project, line, ch),
                            "textDocument/hover" => Features.Hover(doc, project, line, ch),
                            "textDocument/definition" => Definition(doc, line, ch),
                            "textDocument/documentSymbol" => Features.Symbols(doc),
                            "textDocument/formatting" => Features.Formatting(doc),
                            "textDocument/codeAction" => Features.CodeActions(doc, project, p["context"]?["diagnostics"] as JsonArray ?? []),
                            "textDocument/signatureHelp" => Features.SignatureHelp(doc, project, line, ch),
                            _ => null,
                        };
                        break;
                    }
                    default:
                        if (request) replies.Add(Error(id, -32601, "Method not found: " + method));
                        return replies;
                }
                if (request) replies.Add(Reply(id, result));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (request) replies.Add(Error(id, -32603, e.Message));
                else Console.Error.WriteLine($"Alice LSP: {method}: {e.Message}");
            }
            return replies;
        }
    }

    JsonNode? Definition(Document doc, int line, int ch)
    {
        var local = Features.Definition(doc, line, ch);
        if (local != null) return local;
        foreach (var source in Project.Sources(doc, documents.Values))
        {
            if (source.Uri == doc.Uri) continue;
            Analyzer.Parse(source);
            var hit = Features.Definition(source, doc.WordAt(line, ch).Word);
            if (hit != null) return hit;
        }
        return null;
    }

    static bool IsAlice(Document doc) => doc.Path.EndsWith(".al", StringComparison.OrdinalIgnoreCase);
    Document? Get(JsonObject p) => documents.GetValueOrDefault(p["textDocument"]?["uri"]?.GetValue<string>() ?? "");

    void Configure(JsonObject? options)
    {
        if (options?["debounceMs"] is { } ms) debounce = Math.Clamp(ms.GetValue<int>(), 0, 2000);
        if (options?["deepChecks"] is { } deep) deepChecks = deep.GetValue<bool>();
        if (options?["nugetCompletion"] is { } nuget) Packages.Enabled = nuget.GetValue<bool>();
    }

    void ScheduleDependents(Document changed, List<JsonObject> replies)
    {
        string? root = Project.Root(changed.Path);
        foreach (var other in documents.Values)
        {
            if (other == changed || !IsAlice(other)) continue;
            if ((root != null && Project.Root(other.Path) == root) || other.Related.Any(s => s.Uri == changed.Uri))
            {
                other.DeepDiagnostics.Clear();
                Schedule(other, false, replies);
            }
        }
    }

    void Schedule(Document doc, bool deep, List<JsonObject> replies, int? delay = null)
    {
        Cancel(doc.Uri);
        doc.AnalyzedVersion = -1;
        long generation = ++doc.AnalysisGeneration;
        if (Synchronous) { replies.Add(Analyze(doc, deep)); return; }
        pending[doc.Uri] = new Timer(_ =>
        {
            lock (gate)
            {
                if (disposed || shutdown || doc.AnalysisGeneration != generation || !documents.TryGetValue(doc.Uri, out var current) || !ReferenceEquals(current, doc)) return;
                Cancel(doc.Uri);
                try { JsonRpc.Write(output, Analyze(doc, deep)); }
                catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine("Alice LSP: " + e.Message); }
            }
        }, null, delay ?? (deep ? 0 : debounce), Timeout.Infinite);
    }

    JsonObject Analyze(Document doc, bool deep)
    {
        List<Diagnostic> diagnostics;
        if (!IsAlice(doc)) diagnostics = ProjectFeatures.Diagnostics(doc);
        else
        {
            var project = Project.For(doc.Path);
            diagnostics = Analyzer.Quick(doc, project, Project.Sources(doc, documents.Values));
            if (deep && deepChecks && diagnostics.Count == 0) doc.DeepDiagnostics = Analyzer.Deep(doc, project, Project.Sources(doc, documents.Values));
            if (diagnostics.Count == 0) diagnostics = doc.DeepDiagnostics;
        }
        return Publish(doc.Uri, doc.Version, diagnostics.Select(d =>
        {
            int line = Math.Clamp(d.Line - 1, 0, doc.Text.Count(c => c == '\n'));
            int col = Math.Clamp(d.Col - 1, 0, doc.Line(line).Length);
            return (JsonNode)new JsonObject
            {
                ["range"] = Features.Range(line, col, line, Math.Min(col + 1, doc.Line(line).Length)),
                ["severity"] = d.Severity == "error" ? 1 : 2,
                ["code"] = d.Code, ["source"] = "alice", ["message"] = d.Message,
            };
        }).ToArray());
    }

    static JsonObject Publish(string uri, int? version, JsonNode[] diagnostics) => new()
    {
        ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics",
        ["params"] = new JsonObject { ["uri"] = uri, ["version"] = version, ["diagnostics"] = new JsonArray(diagnostics) },
    };
    static JsonObject Reply(JsonNode? id, JsonNode? result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
    static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    void Cancel(string uri) { if (pending.Remove(uri, out var timer)) timer.Dispose(); }
    void CancelAll() { foreach (var timer in pending.Values) timer.Dispose(); pending.Clear(); }
    public void Dispose() { lock (gate) { disposed = true; CancelAll(); documents.Clear(); Project.Invalidate(); Packages.Dispose(); } }
}
