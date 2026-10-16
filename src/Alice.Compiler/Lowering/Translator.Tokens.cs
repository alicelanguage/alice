using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    Token Cur() => t[p];
    Token Peek(int k) => t[Math.Min(p + k, t.Count - 1)];
    bool IsOp(string s) => t[p].Kind == "op" && t[p].Text == s;
    bool IsKw(string s) => t[p].Kind == "id" && t[p].Text == s;

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

    static bool IsKeyword(string s) => Keywords.Contains(" " + s + " ");

    public static string Mangle(string n)
    {
        if (n.EndsWith("?")) n = n.Substring(0, n.Length - 1) + "_q";
        else if (n.EndsWith("!")) n = n.Substring(0, n.Length - 1) + "_b";
        if (CsKeywords.Contains(" " + n + " ")) return "@" + n;
        return n;
    }
}
