using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Alice.Runtime;

namespace Alice.Cli.Lsp;

/// <summary>Completion, hover, definition, symbols, formatting and quick fixes over a Document.</summary>
public static class Features
{
    static readonly string[] Keywords = ("def class module struct enum interface record lib fun if elsif else unless while until for in do end case when then " +
        "begin rescue ensure return break next yield self super nil true false and or not require using include extend getter setter property " +
        "spawn async await val raise loop lambda proc describe context it expect private protected public abstract").Split(' ');

    static readonly Dictionary<string, string> KeywordDocs = new()
    {
        ["def"] = "def name(arg : Type) : Ret ... end — method; `def name(x) = expr` for one-liners",
        ["class"] = "class Name < Base ... end — reference type; getter/property/setter declare fields",
        ["struct"] = "struct Name ... end — value type",
        ["module"] = "module Name ... end — namespace for `def self.` functions and mixins",
        ["val"] = "val name = expr — immutable binding (reassignment is a compile error, AL0203)",
        ["spawn"] = "spawn { ... } — run concurrently; data races are compile errors (AL0202)",
        ["using"] = "using System.Text — import a .NET namespace",
        ["require"] = "require \"./file\" — load another Alice file",
    };

    // Prelude functions (Ruby-style helpers), computed once.
    static readonly Lazy<(string Name, string Sig)[]> Prelude = new(() =>
        typeof(Alice.Runtime.Prelude).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.Name.StartsWith("__") && !m.IsSpecialName)
            .GroupBy(m => m.Name.EndsWith("_q") ? m.Name[..^2] + "?" : m.Name.EndsWith("_b") ? m.Name[..^2] + "!" : m.Name)
            .Select(g => (g.Key, Repl.ReplHelp.Lookup(g.Key).Split('\n')[0]))
            .ToArray());

    static readonly Regex UsingLine = new(@"^\s*using\s+([\w.]+)", RegexOptions.Multiline);

    // ---------------- completion ----------------

    public static JsonObject Completion(Document doc, Project project, int line, int character)
    {
        var text = doc.Line(line);
        int cur = Math.Clamp(character, 0, text.Length);
        int s = cur;
        while (s > 0 && (Document.IsIdent(text[s - 1]) || text[s - 1] is '?' or '!')) s--;
        string prefix = text[s..cur];
        var items = new JsonArray();
        var seen = new HashSet<string>();
        void Add(string label, int kind, string? detail = null, string? doc_ = null, JsonArray? edits = null)
        {
            if (!label.StartsWith(prefix, StringComparison.Ordinal) || !seen.Add(label)) return;
            var item = new JsonObject { ["label"] = label, ["kind"] = kind };
            if (detail != null) item["detail"] = detail;
            if (doc_ != null) item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = doc_ };
            if (edits != null) item["additionalTextEdits"] = edits;
            items.Add(item);
        }

        if (Regex.Match(text[..cur], @"^\s*using\s+([\w.]*)$") is { Success: true } import)
        {
            string path = import.Groups[1].Value;
            int dot = path.LastIndexOf('.');
            foreach (var ns in project.Index.NamespacePrefix(path).Take(150))
                Add(ns[(dot + 1)..].Split('.')[0], 9, ns);
            return new JsonObject { ["isIncomplete"] = true, ["items"] = items };
        }

        if (s > 0 && text[s - 1] == '.')
        {
            // receiver: the dotted identifier chain before the dot
            int r = s - 1;
            while (r > 0 && (Document.IsIdent(text[r - 1]) || text[r - 1] == '.')) r--;
            string recv = text[r..(s - 1)];
            if (recv.Length == 0 && s >= 2 && text[s - 2] == '"') recv = "\0string";
            foreach (var (name, kind, detail) in Members(doc, project, recv, line)) Add(name, kind, detail);
            return new JsonObject { ["isIncomplete"] = true, ["items"] = items };
        }

        foreach (var k in Keywords) Add(k, 14, "keyword");
        foreach (var source in new[] { doc }.Concat(doc.Related))
            if (source.Ast is { } ast)
                foreach (var (name, kind, node) in Declarations(ast.Items))
                    Add(name, kind, Signature(source, node));
        foreach (Match m in Regex.Matches(doc.Text, @"(?<![\w.:@])([a-z_]\w*[?!]?)\s*=(?!=)"))
            Add(m.Groups[1].Value, 6, "local");
        foreach (var (name, sig) in Prelude.Value) Add(name, 3, "Prelude", "```alice\n" + sig + "\n```");
        if (prefix.Length >= 2 && char.IsUpper(prefix[0]))
        {
            var usings = Usings(doc);
            int insertAt = doc.UsingInsertLine();
            foreach (var type in project.Index.WithPrefix(prefix).Take(150))
            {
                var nss = project.Index.Namespaces(type);
                if (nss.Length == 0) continue;
                bool visible = nss.Any(n => ClrTypes.DefaultNamespaces.Contains(n) || usings.Contains(n));
                var ns = nss.FirstOrDefault(n => ClrTypes.DefaultNamespaces.Contains(n) || usings.Contains(n)) ?? Preferred(nss);
                JsonArray? edits = visible ? null : [Insert(insertAt, $"using {ns}\n")];
                Add(type, 7, ns + "." + type, null, edits);
            }
        }
        return new JsonObject { ["isIncomplete"] = prefix.Length < 2, ["items"] = items };
    }

    /// <summary>Prefer System.* over other namespaces when a type name is ambiguous.</summary>
    static string Preferred(string[] nss) => nss.OrderBy(n => n.StartsWith("System") ? 0 : 1).ThenBy(n => n.Length).First();

    static IEnumerable<(string Name, int Kind, string Detail)> Members(Document doc, Project project, string recv, int line)
    {
        // user types (and `x = Type.new` locals) first
        string typeName = recv;
        var assigned = Regex.Match(doc.Text, @"(?<![\w.])" + Regex.Escape(recv) + @"\s*=\s*([A-Z][\w.]*)\.new\b");
        if (assigned.Success) typeName = assigned.Groups[1].Value;
        if (doc.Ast is { } ast && FindType(ast.Items, typeName) is { } td)
        {
            bool statics = typeName == recv;
            foreach (var (name, kind, node) in Declarations(td.Members))
                if (node is MethodDecl md ? md.IsStatic == statics || td.Kind == "module" : !statics)
                    yield return (name, kind, td.Name);
            if (statics) yield return ("new", 2, "constructor");
            yield break;
        }
        var type = ResolveReceiver(doc, project, recv, line) ?? project.Clr.Find(typeName, 0, Usings(doc).Concat(ClrTypes.DefaultNamespaces).ToList()) ?? Repl.ReplHelp.ResolveType(typeName);
        if (type != null)
        {
            bool statics = recv.Length > 0 && char.IsUpper(recv[0]) &&
                (project.Clr.Find(recv, 0, Usings(doc).ToList()) ?? Repl.ReplHelp.ResolveType(recv)) != null;
            var flags = BindingFlags.Public | (statics ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var m in type.GetMembers(flags).Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property or MemberTypes.Field && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") && !m.Name.StartsWith("op_") && !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_")).GroupBy(m => m.Name))
                yield return (m.Key, m.First().MemberType == MemberTypes.Method ? 2 : m.First().MemberType == MemberTypes.Property ? 10 : 5, ClrHelp.Signature(m.First()));
            if (statics && !type.IsAbstract) yield return ("new", 4, "constructor");
            if (!statics) foreach (var (name, sig) in Prelude.Value) yield return (name, 2, sig);
            yield break;
        }
        // unknown receiver (an inferred local): Ruby-style Prelude methods apply to most values
        foreach (var (name, sig) in Prelude.Value) yield return (name, 2, sig);
    }

    static Type? ResolveReceiver(Document doc, Project project, string receiver, int line = int.MaxValue)
    {
        if (receiver == "\0string") return typeof(string);
        int dot = receiver.LastIndexOf('.');
        if (dot > 0 && ResolveReceiver(doc, project, receiver[..dot], line) is { } parent)
        {
            var member = parent.GetMember(receiver[(dot + 1)..], BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).FirstOrDefault();
            if (member is PropertyInfo property) return property.PropertyType;
            if (member is FieldInfo field) return field.FieldType;
        }
        if (doc.Ast is { } ast)
        {
            var candidates = new List<(int Line, Type Type)>();
            void Inspect(Node node)
            {
                if (node.Line > (long)line + 1) return;
                Type? found = node switch
                {
                    NameExpr n when n.Name == receiver => n.Type.Clr,
                    Assign a when a.Declares?.Name == receiver => a.Declares.Type.Clr,
                    LocalDecl l when l.Name == receiver => l.Local?.Type.Clr,
                    MethodDecl m when m.Sym is MethodSym method => method.Params.FirstOrDefault(p => p.Name == receiver)?.Type.Clr,
                    _ => null,
                };
                if (found != null) candidates.Add((node.Line, found));
                if (node is MethodDecl md) AstWalk.Each(md.Body, Inspect);
                if (node is TypeDecl td) AstWalk.Each(td.Members, Inspect);
            }
            AstWalk.Each(ast, Inspect);
            if (candidates.Count > 0) return candidates.OrderByDescending(c => c.Line).First().Type;
        }
        var usings = Usings(doc).Concat(ClrTypes.DefaultNamespaces).ToList();
        Type? type = receiver.Length > 0 && char.IsUpper(receiver[0])
            ? project.Clr.Find(receiver, 0, usings) ?? Repl.ReplHelp.ResolveType(receiver) : null;
        if (type != null) return type;
        var annotation = Regex.Match(doc.Text, @"\b" + Regex.Escape(receiver) + @"\s*:\s*([A-Z][\w.]*)");
        var assignment = Regex.Match(doc.Text, @"\b" + Regex.Escape(receiver) + @"\s*=\s*([A-Z][\w.]*)\.new\b");
        string name = annotation.Success ? annotation.Groups[1].Value : assignment.Success ? assignment.Groups[1].Value : "";
        if (name.Length > 0) return project.Clr.Find(name, 0, usings) ?? Repl.ReplHelp.ResolveType(name);
        if (Regex.IsMatch(doc.Text, @"\b" + Regex.Escape(receiver) + "\\s*=\\s*\"")) return typeof(string);
        return null;
    }

    public static JsonObject? SignatureHelp(Document doc, Project project, int line, int character)
    {
        var text = doc.Text[..doc.Offset(line, character)];
        // Lexical nesting tracks nested calls, collection literals, comments and quoted strings.
        var stack = new Stack<(int Start, int Parameter)>();
        char quote = '\0'; bool escape = false, comment = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (comment) { if (c == '\n') comment = false; continue; }
            if (quote != '\0') { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == quote) quote = '\0'; continue; }
            if (c == '#') { comment = true; continue; }
            if (c is '\"' or '\'') { quote = c; continue; }
            if (c is '(' or '[' or '{') stack.Push((i, 0));
            else if (c is ')' or ']' or '}') { if (stack.Count > 0) stack.Pop(); }
            else if (c == ',' && stack.Count > 0) { var call = stack.Pop(); stack.Push((call.Start, call.Parameter + 1)); }
        }
        var signatures = new JsonArray();
        foreach (var call in stack)
        {
            if (text[call.Start] != '(') continue;
            var match = Regex.Match(text[..call.Start], @"([\w.?!]+)\s*$");
            if (!match.Success) continue;
            string name = match.Groups[1].Value;
            int dot = name.LastIndexOf('.');
            if (dot > 0 && ResolveReceiver(doc, project, name[..dot], line) is { } owner)
            {
                var methods = name[(dot + 1)..] == "new" ? owner.GetConstructors().Cast<MethodBase>() : owner.GetMethods().Where(m => m.Name == name[(dot + 1)..]).Cast<MethodBase>();
                foreach (var method in methods.Take(25))
                    signatures.Add(new JsonObject { ["label"] = ClrHelp.Signature(method), ["parameters"] = new JsonArray(method.GetParameters().Select(p => (JsonNode)new JsonObject { ["label"] = p.Name + " : " + ClrHelp.TypeName(p.ParameterType) }).ToArray()), ["documentation"] = ClrHelp.Documentation(method) });
            }
            else
            {
                foreach (Match decl in Regex.Matches(doc.Text, @"(?m)^\s*def\s+(?:self\.)?" + Regex.Escape(name) + @"\s*(\([^\n]*?\))?(?:\s*:\s*[^=\n]+)?"))
                {
                    var args = decl.Groups[1].Value.Trim('(', ')').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    signatures.Add(new JsonObject { ["label"] = decl.Value.Trim(), ["parameters"] = new JsonArray(args.Select(a => (JsonNode)new JsonObject { ["label"] = a }).ToArray()) });
                }
            }
            if (signatures.Count > 0) return new JsonObject { ["signatures"] = signatures, ["activeSignature"] = 0, ["activeParameter"] = Math.Min(call.Parameter, Math.Max(0, signatures.Max(s => s!["parameters"]!.AsArray().Count) - 1)) };
        }
        return null;
    }

    // ---------------- declarations ----------------

    static IEnumerable<(string Name, int Kind, Node Node)> Declarations(IEnumerable<Item> items)
    {
        foreach (var it in items)
        {
            switch (it)
            {
                case MethodDecl m: yield return (m.Name, m.IsStatic ? 3 : 2, m); break;
                case TypeDecl t:
                    yield return (t.Name, t.Kind switch { "module" => 9, "interface" => 8, "struct" => 22, _ => 7 }, t);
                    break;
                case EnumDecl e: yield return (e.Name, 13, e); break;
                case AccessorDecl a: yield return (a.Name, 10, a); break;
                case FieldDecl f: yield return (f.Name.TrimStart('@'), 5, f); break;
                case ConstDecl c: yield return (c.Name, 21, c); break;
                case NamespaceItem n: foreach (var d in Declarations(n.Items)) yield return d; break;
            }
        }
    }

    static TypeDecl? FindType(IEnumerable<Item> items, string name)
    {
        foreach (var it in items)
        {
            if (it is TypeDecl t)
            {
                if (t.Name == name) return t;
                if (FindType(t.Members, name) is { } inner) return inner;
            }
            if (it is NamespaceItem n && FindType(n.Items, name) is { } ns) return ns;
        }
        return null;
    }

    static (string Name, Node Node)? FindDecl(IEnumerable<Item> items, string name)
    {
        foreach (var it in items)
        {
            foreach (var (n, _, node) in Declarations([it]))
                if (n == name) return (n, node);
            var nested = it switch { TypeDecl t => t.Members, NamespaceItem ns => ns.Items, _ => null };
            if (nested != null && FindDecl(nested, name) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>The declaration's own source line(s), e.g. `def square(n : Int32) : Int32`.</summary>
    static string Signature(Document doc, Node node)
    {
        if (node.Line <= 0) return "";
        var line = doc.Line(node.Line - 1).Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    static HashSet<string> Usings(Document doc) =>
        UsingLine.Matches(doc.Text).Select(m => m.Groups[1].Value).ToHashSet();

    // ---------------- hover / definition ----------------

    public static JsonObject? Hover(Document doc, Project project, int line, int character)
    {
        var (word, qualified) = doc.WordAt(line, character);
        if (word.Length == 0) return null;
        string? body = null;
        foreach (var source in new[] { doc }.Concat(doc.Related))
            if (source.Ast is { } sourceAst && FindDecl(sourceAst.Items, word) is { } declaration)
                return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "```alice\n" + Signature(source, declaration.Node) + "\n```" + Comment(source, declaration.Node.Line) } };
        if (KeywordDocs.TryGetValue(word, out var kd))
            body = "```alice\n" + kd + "\n```";
        else
        {
            int dot = qualified.LastIndexOf('.');
            var owner = ResolveReceiver(doc, project, dot > 0 ? qualified[..dot] : word, line);
            MemberInfo? member = dot > 0 ? owner?.GetMember(word, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).FirstOrDefault() : owner;
            if (member != null)
                return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "```alice\n" + ClrHelp.Signature(member) + "\n```\n\n" + ClrHelp.Documentation(member) } };
            var help = Repl.ReplHelp.Lookup(qualified.Contains('.') ? qualified : word);
            if (!help.StartsWith("no help for")) body = "```alice\n" + help + "\n```";
            else if (char.IsUpper(word[0]) && project.Index.Namespaces(word) is { Length: > 0 } nss)
                body = "```alice\n" + string.Join("\n", nss.Select(n => $"{n}.{word}")) + "\n```";
        }
        return body == null ? null : new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = body } };
    }

    /// <summary>`#` comment lines directly above a declaration, as hover documentation.</summary>
    static string Comment(Document doc, int declLine)
    {
        var lines = new List<string>();
        for (int l = declLine - 2; l >= 0; l--)
        {
            var t = doc.Line(l).Trim();
            if (!t.StartsWith('#')) break;
            lines.Insert(0, t.TrimStart('#').Trim());
        }
        return lines.Count == 0 ? "" : "\n\n" + string.Join("\n", lines);
    }

    public static JsonObject? Definition(Document doc, int line, int character) => Definition(doc, doc.WordAt(line, character).Word);

    public static JsonObject? Definition(Document doc, string word)
    {
        if (word.Length == 0) return null;
        if (doc.Ast is { } ast && FindDecl(ast.Items, word) is { } decl && decl.Node.Line > 0)
            return Location(doc, decl.Node.Line - 1, Math.Max(0, doc.Line(decl.Node.Line - 1).IndexOf(word, StringComparison.Ordinal)), word.Length);
        // first assignment of a local
        var m = Regex.Match(doc.Text, @"(?m)^[ \t]*(?:val\s+)?" + Regex.Escape(word) + @"\s*=(?!=)");
        if (m.Success)
        {
            int l = doc.Text[..m.Index].Count(c => c == '\n');
            int col = doc.Line(l).IndexOf(word, StringComparison.Ordinal);
            return Location(doc, l, Math.Max(0, col), word.Length);
        }
        return null;
    }

    static JsonObject Location(Document doc, int line, int col, int len) =>
        new() { ["uri"] = doc.Uri, ["range"] = Range(line, col, line, col + len) };

    // ---------------- symbols / formatting / actions ----------------

    public static JsonArray Symbols(Document doc)
    {
        var arr = new JsonArray();
        // Binding expands mixins into the AST, and recovery may retain an older AST.
        // Outline locations must describe declarations in the current source buffer.
        var syntax = new Document(doc.Uri, doc.Text, doc.Version);
        Analyzer.Parse(syntax);
        if (syntax.Ast is { } ast) foreach (var s in SymbolsOf(doc, ast.Items)) arr.Add(s);
        return arr;
    }

    static IEnumerable<JsonObject> SymbolsOf(Document doc, IEnumerable<Item> items)
    {
        foreach (var it in items)
        {
            if (it is NamespaceItem ns) { foreach (var s in SymbolsOf(doc, ns.Items)) yield return s; continue; }
            foreach (var (name, kind, node) in Declarations([it]))
            {
                int last = doc.Text.Count(c => c == '\n');
                int l = Math.Clamp(node.Line - 1, 0, last);
                string line = doc.Line(l);
                int col = line.IndexOf(name, StringComparison.Ordinal);
                if (col < 0) continue;
                int len = line.Length;
                int end = Math.Clamp(EndLine(doc, node), l, last);
                var sym = new JsonObject
                {
                    ["name"] = name,
                    ["kind"] = kind switch { 7 => 5, 9 => 2, 8 => 11, 22 => 23, 13 => 10, 10 => 7, 5 => 8, 21 => 14, 3 => 12, _ => 6 },
                    ["range"] = Range(l, 0, end, doc.Line(end).Length),
                    ["selectionRange"] = Range(l, col, l, col + name.Length),
                };
                if (node is MethodDecl md) sym["detail"] = Signature(doc, md);
                if (node is TypeDecl td) sym["children"] = new JsonArray(SymbolsOf(doc, td.Members).Cast<JsonNode>().ToArray());
                if (node is EnumDecl ed)
                    sym["children"] = new JsonArray(ed.Names.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["kind"] = 22, ["range"] = Range(l, 0, l, len), ["selectionRange"] = Range(l, 0, l, len) }).ToArray());
                yield return sym;
            }
        }
    }

    static int EndLine(Document doc, Node node)
    {
        int start = Math.Max(0, node.Line - 1);
        int last = doc.Text.Count(c => c == '\n');
        if (node is MethodDecl { SourceText.Length: > 0 } method)
            return Math.Min(last, start + method.SourceText.Count(c => c == '\n'));
        if (node is TypeDecl type)
        {
            int end = type.Members.Count == 0 ? start : type.Members.Max(m => EndLine(doc, m));
            for (int i = end + 1; i <= last; i++)
            {
                string line = doc.Line(i).Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                return line == "end" ? i : end;
            }
            return end;
        }
        return start;
    }

    public static JsonArray Formatting(Document doc)
    {
        string formatted;
        try { formatted = Formatter.Format(doc.Text); }
        catch (Alice.Compiler.CompileException) { return []; }
        if (formatted == doc.Text) return [];
        var lines = doc.Text.Split('\n');
        return [new JsonObject { ["range"] = Range(0, 0, lines.Length - 1, lines[^1].Length), ["newText"] = formatted }];
    }

    static readonly Regex UndefinedName = new(@"(?:undefined name|unknown type|unknown constant|cannot find type|undefined constant)\s+'([A-Z]\w*)'", RegexOptions.IgnoreCase);

    public static JsonArray CodeActions(Document doc, Project project, JsonArray diagnostics)
    {
        var actions = new JsonArray();
        var usings = Usings(doc);
        int at = doc.UsingInsertLine();
        foreach (var d in diagnostics)
        {
            var m = UndefinedName.Match(d?["message"]?.GetValue<string>() ?? "");
            if (!m.Success) continue;
            foreach (var ns in project.Index.Namespaces(m.Groups[1].Value).Where(n => !usings.Contains(n)).OrderBy(n => n.StartsWith("System") ? 0 : 1).Take(5))
            {
                actions.Add(new JsonObject
                {
                    ["title"] = $"Add `using {ns}`",
                    ["kind"] = "quickfix",
                    ["diagnostics"] = new JsonArray(d!.DeepClone()),
                    ["isPreferred"] = actions.Count == 0,
                    ["edit"] = new JsonObject { ["changes"] = new JsonObject { [doc.Uri] = new JsonArray(Insert(at, $"using {ns}\n")) } },
                });
            }
        }
        return actions;
    }

    // ---------------- helpers ----------------

    public static JsonObject Range(int l1, int c1, int l2, int c2) => new()
    {
        ["start"] = new JsonObject { ["line"] = l1, ["character"] = c1 },
        ["end"] = new JsonObject { ["line"] = l2, ["character"] = c2 },
    };

    static JsonObject Insert(int line, string text) => new() { ["range"] = Range(line, 0, line, 0), ["newText"] = text };
}
