using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Alice.Compiler;
using Alice.Compiler.Syntax;
using Alice.Runtime;

namespace Alice.Cli.Repl;

public sealed record ReplResult(string Output, string? Value, string? ValueType, string? Error, TimeSpan Elapsed);

/// <summary>
/// A REPL session. Each input is compiled together with the earlier successful inputs (definitions persist,
/// earlier statements replay with output muted), so values keep their static types across inputs. The last
/// expression's value is shown and is `ans` in the next input.
/// </summary>
public sealed class ReplSession(IEnumerable<string>? refs = null)
{
    sealed record Chunk(string Text, HashSet<string> Defines, bool DefinitionsOnly, string? ValueVar);

    readonly List<Chunk> chunks = new();
    readonly List<string> refs = refs?.ToList() ?? new();
    int counter;
    string? lastValueVar;

    public IReadOnlyList<string> Inputs => chunks.Select(c => c.Text).ToList();

    public void Reset()
    {
        chunks.Clear();
        lastValueVar = null;
    }

    /// <summary>Evaluates one input; `live` streams its output (interactive), otherwise Output captures it.</summary>
    public ReplResult Eval(string input, TextWriter? live = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int index = ++counter;
        input = lastValueVar != null ? ReplaceAns(input, lastValueVar) : input;
        FileNode ast;
        try { ast = Parser.ParseFile(input, "repl"); }
        catch (CompileException e) { return Fail(e, index, input, sw); }

        var defines = Definitions(ast);
        bool defsOnly = ast.Items.Count > 0 && ast.Items.All(i => i is not StmtItem);
        var (captured, valueVar) = Capture(ast, input, index);
        var kept = defsOnly ? chunks.Where(c => !(c.DefinitionsOnly && c.Defines.IsSubsetOf(defines))).ToList() : chunks.ToList();

        // try with value capture first; a void last expression can't be captured, so retry plainly
        var attempt = Run(kept, captured, index, live);
        if (attempt.Error != null && valueVar != null && captured != input)
        {
            var plain = Run(kept, input, index, live);
            if (plain.Error == null || attempt.CompileFailed) { attempt = plain; valueVar = null; }
        }
        if (attempt.Error != null) return new ReplResult(attempt.Output, null, null, attempt.Error, sw.Elapsed);

        chunks.Clear();
        chunks.AddRange(kept);
        chunks.Add(new Chunk(valueVar != null ? captured : input, defines, defsOnly, valueVar));
        if (valueVar != null) lastValueVar = valueVar;
        return new ReplResult(attempt.Output, valueVar != null ? attempt.Shown : null, valueVar != null ? attempt.ShownType : null, null, sw.Elapsed);
    }

    sealed record Attempt(string Output, string? Shown, string? ShownType, string? Error, bool CompileFailed);

    Attempt Run(List<Chunk> previous, string current, int index, TextWriter? live)
    {
        var text = new StringBuilder("ReplHost.mute()\n");
        var map = new List<(int Start, int Lines, int Index)>();
        int line = 2, ci = 1;
        foreach (var c in previous)
        {
            int n = c.Text.Split('\n').Length;
            map.Add((line, n, ci++));
            text.Append(c.Text).Append('\n');
            line += n;
        }
        text.Append("ReplHost.unmute()\n");
        line++;
        map.Add((line, current.Split('\n').Length, index));
        text.Append(current).Append('\n');

        if (Environment.GetEnvironmentVariable("ALICE_REPL_DEBUG") == "1") Console.Error.WriteLine("---- session program ----\n" + text + "----");
        var capture = new StringWriter();
        var oldOut = Console.Out;
        ReplHost.Out = live ?? capture;
        ReplHost.Shown = null;
        try
        {
            byte[] image;
            try { image = AliceCompiler.Emit(AliceCompiler.TranspileAll([new SourceFile("repl", text.ToString())], refs), "repl" + index, exe: true, refs); }
            catch (CompileException e) { return new Attempt("", null, null, Remap(e.Diagnostics, map, current, index), true); }
            var asm = AliceCompiler.Load(image);
            try { asm.EntryPoint!.Invoke(null, [Array.Empty<string>()]); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                var inner = e.InnerException;
                string where = AliceLine(inner, map) is { } w ? $" ({w})" : "";
                return new Attempt(capture.ToString(), null, null, $"{inner.GetType().Name}: {inner.Message}{where}", false);
            }
            return new Attempt(capture.ToString(), ReplHost.Shown, ReplHost.ShownType, null, false);
        }
        finally { Console.SetOut(oldOut); }
    }

    static string Remap(IReadOnlyList<Diagnostic> ds, List<(int Start, int Lines, int Index)> map, string current, int index)
    {
        var sb = new StringBuilder();
        foreach (var d in ds)
        {
            var at = map.LastOrDefault(m => d.Line >= m.Start);
            string where = at.Lines > 0 ? $"repl[{at.Index}]:{d.Line - at.Start + 1}:{d.Col}" : $"repl:{d.Line}:{d.Col}";
            sb.Append($"error[{d.Code}]: {d.Message}\n  --> {where}\n");
            if (at.Index == index && d.Line - at.Start < current.Split('\n').Length)
            {
                string src = current.Split('\n')[d.Line - at.Start];
                sb.Append("   | ").Append(src).Append('\n').Append("   | ").Append(new string(' ', Math.Max(0, d.Col - 1))).Append(new string('^', Math.Max(1, d.Length))).Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }

    static string? AliceLine(Exception e, List<(int Start, int Lines, int Index)> map)
    {
        var f = new System.Diagnostics.StackTrace(e, true).GetFrames().FirstOrDefault(x => x.GetFileName() == "repl");
        if (f == null) return null;
        int line = f.GetFileLineNumber();
        var at = map.LastOrDefault(m => line >= m.Start);
        return at.Lines > 0 ? $"repl[{at.Index}]:{line - at.Start + 1}" : null;
    }

    static ReplResult Fail(CompileException e, int index, string input, System.Diagnostics.Stopwatch sw) =>
        new("", null, null, Remap(e.Diagnostics, [(1, input.Split('\n').Length, index)], input, index), sw.Elapsed);

    static HashSet<string> Definitions(FileNode ast)
    {
        var r = new HashSet<string>();
        foreach (var it in ast.Items)
            switch (it)
            {
                case MethodDecl m: r.Add("def " + m.Name); break;
                case TypeDecl t: r.Add("type " + t.Name); break;
                case EnumDecl e: r.Add("type " + e.Name); break;
            }
        return r;
    }

    /// <summary>Rewrites the input so its last value is kept: `expr` -> `__vN = (expr)`; `x = ...` shows x.</summary>
    static (string Text, string? ValueVar) Capture(FileNode ast, string input, int index)
    {
        if (ast.Items.LastOrDefault() is not StmtItem { Stmt: var last }) return (input, null);
        switch (last)
        {
            case Assign { Target: NameExpr n }:
                return (input + $"\nReplHost.show({n.Name})", n.Name);
            case ExprStmt { Expr: CallExpr { Recv: null, Name: "puts" or "print" or "p" or "pp" or "require" or "sleep" } }:
                return (input, null);
            case ExprStmt es:
                {
                    var lines = input.Split('\n');
                    int l0 = es.Line - 1, c0 = Math.Max(0, es.Col - 1);
                    if (l0 < 0 || l0 >= lines.Length || c0 > lines[l0].Length) return (input, null);
                    string head = string.Join('\n', lines.Take(l0)) + (l0 > 0 ? "\n" : "") + lines[l0][..c0];
                    string expr = lines[l0][c0..] + (l0 + 1 < lines.Length ? "\n" + string.Join('\n', lines.Skip(l0 + 1)) : "");
                    string v = "__v" + index;
                    return ($"{head}{v} = ({expr.TrimEnd()})\nReplHost.show({v})", v);
                }
        }
        return (input, null);
    }

    static string ReplaceAns(string input, string var) =>
        Regex.Replace(input, "\"(?:\\\\.|[^\"\\\\])*\"|\\bans\\b", m => m.Value == "ans" ? var : m.Value);

    // ---- multi-line input ----

    static readonly Regex Opener = new(@"^\s*(def|class|module|struct|enum|interface|record\s+struct|record|lib|if|unless|while|until|for|case|begin|macro|loop\s+do|describe|context|it)\b");
    static readonly Regex DoBlock = new(@"\bdo(\s*\|[^|]*\|)?\s*(#.*)?$");

    /// <summary>True when the input forms complete statements (blocks closed, strings/brackets balanced).</summary>
    public static bool IsComplete(string src)
    {
        int depth = 0, paren = 0;
        string? heredoc = null;
        foreach (var raw in src.Split('\n'))
        {
            if (heredoc != null) { if (raw.Trim() == heredoc) heredoc = null; continue; }
            var hd = Regex.Match(raw, @"<<[-~]?([A-Z_]+)");
            if (hd.Success) heredoc = hd.Groups[1].Value;
            string line = StripStrings(raw, out bool openString);
            if (openString) return false;
            string code = line.Split('#')[0];
            bool endless = Regex.IsMatch(code, @"^\s*def\s+[\w?!]+(\([^)]*\))?\s*(:\s*\S+\s*)?=\s");
            bool modifier = Regex.IsMatch(code, @"\S.*\b(if|unless|while|until)\b") && !Regex.IsMatch(code, @"^\s*(if|unless|while|until)\b") && !Regex.IsMatch(code, @"=\s*(if|unless|case|begin)\b");
            if (!endless && !modifier && Opener.IsMatch(code) && !(Regex.IsMatch(code, @"^\s*(it|describe|context)\b") && !DoBlock.IsMatch(code))) depth++;
            else if (Regex.IsMatch(code, @"=\s*(if|unless|case|begin)\b")) depth++;
            if (DoBlock.IsMatch(code) && !Opener.IsMatch(code)) depth++;
            foreach (Match _ in Regex.Matches(code, @"(^|[\s;])end\b")) depth--;
            foreach (char ch in code) { if (ch is '(' or '[' or '{') paren++; else if (ch is ')' or ']' or '}') paren--; }
        }
        return depth <= 0 && paren <= 0 && heredoc == null;
    }

    static string StripStrings(string line, out bool open)
    {
        var sb = new StringBuilder();
        char? q = null;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q != null)
            {
                if (c == '\\') { i++; continue; }
                if (c == q) q = null;
                continue;
            }
            if (c == '#') { sb.Append(line[i..]); break; }
            if (c is '"' or '\'' && !(c == '\'' && i > 0 && char.IsLetterOrDigit(line[i - 1]))) { q = c; continue; }
            sb.Append(c);
        }
        open = q == '"';
        return sb.ToString();
    }

    // ---- completion ----

    static readonly string[] Keywords = ("def class module struct enum interface record lib fun if elsif else unless while until for in do end case when then " +
        "begin rescue ensure return break next yield self nil true false and or not require using include extend getter setter property " +
        "spawn async await val raise loop lambda proc describe context it expect").Split(' ');

    public List<string> Complete(string prefix)
    {
        int dot = prefix.LastIndexOf('.');
        if (dot > 0 && ReplHelp.ResolveType(prefix[..dot]) is { } t)
        {
            string rest = prefix[(dot + 1)..];
            return t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property or MemberTypes.Field && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") && !m.Name.StartsWith("op_"))
                .Select(m => m.Name).Where(n => n.StartsWith(rest)).Distinct().Order().Select(n => prefix[..dot] + "." + n).ToList();
        }
        var names = new HashSet<string>(Keywords);
        foreach (var c in chunks) foreach (Match m in Regex.Matches(c.Text, @"\b[A-Za-z_][\w]*[?!]?")) if (!m.Value.StartsWith("__")) names.Add(m.Value);
        foreach (var m in typeof(Prelude).GetMethods(BindingFlags.Public | BindingFlags.Static)) if (!m.Name.StartsWith("__")) names.Add(m.Name.EndsWith("_q") ? m.Name[..^2] + "?" : m.Name);
        foreach (var ct in ReplHelp.CommonTypes) names.Add(ct);
        return names.Where(n => n.StartsWith(prefix) && n != prefix).Order().ToList();
    }
}
