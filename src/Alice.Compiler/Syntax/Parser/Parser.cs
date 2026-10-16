namespace Alice.Compiler.Syntax;

/// <summary>Recursive-descent parser: tokens -> AST. Tracks locals only to disambiguate Ruby command calls (`foo x`).</summary>
public sealed partial class Parser
{
    readonly string file;
    List<Token> t;
    int p;
    List<HashSet<string>> locals = new();
    bool noDo;
    readonly List<string> attrs = new();

    const string Keywords = " if unless while until for in do end then else elsif case when begin rescue ensure return break next and or not def class struct module interface enum lib fun namespace using require getter property setter include abstract private protected self nil true false super yield loop raise of forall spawn val where record async await ";

    public Parser(string src, string file)
    {
        this.file = file;
        t = new Lexer(src, file, 1).Lex();
    }

    public static FileNode ParseFile(string src, string file) => new Parser(src, file).File();

    // ---- tokens
    Token Cur => t[p];
    Token Peek(int k) => t[Math.Min(p + k, t.Count - 1)];
    bool IsOp(string s) => t[p].Kind == "op" && t[p].Text == s;
    bool IsKw(string s) => t[p].Kind == "id" && t[p].Text == s;
    static bool IsKeyword(string s) => Keywords.Contains(" " + s + " ");

    Token Next()
    {
        Token x = t[p];
        if (p < t.Count - 1) p++;
        return x;
    }

    CompileException Err(string msg)
    {
        Token c = t[p];
        string found = c.Kind == "nl" ? "newline" : c.Kind == "eof" ? "end of file" : c.Text;
        return new CompileException($"{file}:{c.Line}:{c.Col}: {msg} (found '{found}')");
    }

    void Expect(string op)
    {
        if (!IsOp(op)) throw Err($"expected '{op}'");
        Next();
    }

    void ExpectKw(string kw)
    {
        if (!IsKw(kw)) throw Err($"expected '{kw}'");
        Next();
    }

    void SkipNl()
    {
        while (t[p].Kind == "nl") Next();
    }

    bool AtStmtEnd()
    {
        Token c = t[p];
        if (c.Kind == "nl" || c.Kind == "eof" || IsOp("}")) return true;
        return c.Kind == "id" && " end else elsif when rescue ensure if unless ".Contains(" " + c.Text + " ");
    }

    void EndStmt()
    {
        if (t[p].Kind == "nl") { SkipNl(); return; }
        if (t[p].Kind == "eof" || IsOp("}")) return;
        if (t[p].Kind == "id" && " end else elsif when rescue ensure ".Contains(" " + t[p].Text + " ")) return;
        throw Err("expected end of statement");
    }

    T At<T>(T node, Token tok) where T : Node
    {
        node.Line = tok.Line;
        node.Col = tok.Col;
        return node;
    }

    // ---- locals (parse-time only)
    void PushLocals() => locals.Add(new HashSet<string>());
    void PopLocals() => locals.RemoveAt(locals.Count - 1);
    void DeclareLocal(string n) => locals[locals.Count - 1].Add(n);

    bool IsLocal(string n)
    {
        foreach (var s in locals) if (s.Contains(n)) return true;
        return false;
    }

    List<string> TakeAttrs()
    {
        var r = new List<string>(attrs);
        attrs.Clear();
        return r;
    }
}
