using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Alice.Compiler;

namespace Alice.Cli.Lsp;

public static class ProjectFeatures
{
    static readonly Dictionary<string, string> Docs = new()
    {
        ["package"] = "Package identity and output. `name`, `version`, `kind` (exe, lib, game), `target` (.NET TFM).",
        ["dependencies"] = "NuGet dependencies: `\"Package.Id\" = \"1.2.3\"`. Local projects: `name = { path = \"../project\" }`; workspace members: `name = { workspace = true }`. Run `alice install` after editing.",
        ["workspace"] = "Workspace members: `members = [\"packages/*\"]`. Build in dependency order with `alice build`.",
        ["build"] = "Build settings. `native = true` enables NativeAOT publishing.",
        ["name"] = "Package name and output assembly name.", ["version"] = "Package version (SemVer).",
        ["kind"] = "Output kind: exe, lib, or game (MonoGame).", ["target"] = ".NET target framework, for example net10.0.",
        ["native"] = "Publish using .NET NativeAOT.", ["members"] = "Workspace member paths or globs.",
        ["PackageReference"] = "NuGet package reference: Include is the package ID, Version is its version or range. Prefer editing alice.toml for generated projects.",
        ["ProjectReference"] = "Reference another .NET project by path.", ["TargetFramework"] = ".NET target framework, for example net10.0.",
    };

    public static JsonNode? Handle(string method, Document doc, int line, int ch, NuGetService packages) => method switch
    {
        "textDocument/completion" => Completion(doc, line, ch, packages),
        "textDocument/hover" => Hover(doc, line, ch),
        "textDocument/formatting" => Formatting(doc),
        "textDocument/documentSymbol" => Symbols(doc),
        "textDocument/codeAction" => new JsonArray(),
        _ => null,
    };

    static JsonObject Completion(Document doc, int line, int ch, NuGetService packages)
    {
        var items = new JsonArray();
        var text = doc.Line(line);
        var before = text[..Math.Clamp(ch, 0, text.Length)];
        bool xml = doc.Path.EndsWith(".alproj");
        var version = xml
            ? Regex.Match(before, "<PackageReference\\b[^>]*Include=\"([^\"]+)\"[^>]*Version=\"([^\"]*)$")
            : Regex.Match(before, "^\\s*\"?([\\w.-]+)\"?\\s*=\\s*\"([^\"]*)$");
        var section = doc.Text[..doc.Offset(line, 0)].Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('['))?.Trim();
        if (version.Success && (xml || section == "[dependencies]"))
        {
            string prefix = version.Groups[2].Value;
            foreach (var v in packages.Versions(version.Groups[1].Value).Where(v => v.StartsWith(prefix))) Add(v, "NuGet version", v, prefix.Length);
        }
        else if (xml && Regex.Match(before, "Include=\"([^\"]*)$") is { Success: true } include)
        {
            foreach (var id in packages.Ids(include.Groups[1].Value)) Add(id, "NuGet package", id, include.Groups[1].Length);
        }
        else if (!xml && section == "[dependencies]" && !before.Contains('='))
        {
            string prefix = before.Trim().Trim('"');
            foreach (var id in packages.Ids(prefix)) Add(id, "NuGet package", "\"" + id + "\" = \"*\"", before.TrimStart().Length);
        }
        else
        {
            string[] keys = xml ? ["PropertyGroup", "ItemGroup", "PackageReference", "ProjectReference", "TargetFramework", "OutputType"] :
                section switch { "[package]" => ["name", "version", "kind", "target"], "[workspace]" => ["members"], "[build]" => ["native"], _ => ["[package]", "[dependencies]", "[workspace]", "[build]"] };
            var prefix = Regex.Match(before, @"[\w\[.]*$").Value;
            foreach (var key in keys.Where(k => k.StartsWith(prefix))) Add(key, Docs.GetValueOrDefault(key.Trim('[', ']')) ?? "Project setting", key, prefix.Length);
        }
        return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        void Add(string label, string detail, string replacement, int length) => items.Add(new JsonObject
        {
            ["label"] = label, ["kind"] = 12, ["detail"] = detail,
            ["textEdit"] = new JsonObject { ["range"] = Features.Range(line, Math.Max(0, ch - length), line, ch), ["newText"] = replacement },
        });
    }

    static JsonNode? Hover(Document doc, int line, int ch)
    {
        var word = doc.WordAt(line, ch).Word;
        return Docs.TryGetValue(word, out var text) ? new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = text } } : null;
    }

    static XDocument ParseXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    public static List<Diagnostic> Diagnostics(Document doc)
    {
        try
        {
            if (doc.Path.EndsWith(".alproj")) ParseXml(doc.Text);
            else Projects.TomlLite.Parse(doc.Text);
            return [];
        }
        catch (XmlException e) { return [new Diagnostic("error", "AL0401", doc.Path, e.LineNumber, e.LinePosition, e.Message)]; }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { return [new Diagnostic("error", "AL0401", doc.Path, 1, 1, e.Message)]; }
    }

    static JsonArray Formatting(Document doc)
    {
        string formatted;
        try
        {
            if (doc.Path.EndsWith(".alproj")) formatted = ParseXml(doc.Text).ToString().Replace("\r\n", "\n") + "\n";
            else
            {
                // Only space the assignment separator; retain comments, values, quotes and inline tables verbatim.
                formatted = string.Join('\n', doc.Text.Replace("\r\n", "\n").Split('\n').Select(l =>
                    Regex.Replace(l.TrimEnd(), "^(\\s*(?:\"[^\"]+\"|[\\w.-]+))\\s*=\\s*", "$1 = ")));
            }
        }
        catch (Exception e) when (e is XmlException or FormatException) { return []; }
        return formatted == doc.Text ? [] : [new JsonObject { ["range"] = Features.Range(0, 0, doc.Text.Count(c => c == '\n'), doc.Text.Split('\n')[^1].Length), ["newText"] = formatted }];
    }

    static JsonArray Symbols(Document doc)
    {
        var result = new JsonArray();
        var lines = doc.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith('[')) continue;
            result.Add(new JsonObject { ["name"] = lines[i].Trim(), ["kind"] = 3, ["range"] = Features.Range(i, 0, i, lines[i].Length), ["selectionRange"] = Features.Range(i, 0, i, lines[i].Length) });
        }
        return result;
    }
}
