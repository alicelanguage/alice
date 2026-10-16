using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    string Interp(Token tok)
    {
        string raw = tok.Text;
        bool interp = false;
        for (int i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == '\\') { i++; continue; }
            if (raw[i] == '#' && raw[i + 1] == '{') interp = true;
        }
        var sb = new StringBuilder();
        int k = 0;
        while (k < raw.Length)
        {
            char ch = raw[k];
            if (ch == '\\' && k + 1 < raw.Length)
            {
                char nx = raw[k + 1];
                if (nx == '#') sb.Append('#');
                else if (nx == 'e') sb.Append("\\u001b");
                else if (nx == '{' || nx == '}') sb.Append(interp ? new string(nx, 2) : nx.ToString());
                else sb.Append('\\').Append(nx);
                k += 2;
                continue;
            }
            if (ch == '#' && k + 1 < raw.Length && raw[k + 1] == '{')
            {
                int d = 1;
                int j = k + 2;
                bool inStr = false;
                while (j < raw.Length)
                {
                    char x = raw[j];
                    if (inStr)
                    {
                        if (x == '\\') j++;
                        else if (x == '"') inStr = false;
                    }
                    else if (x == '"') inStr = true;
                    else if (x == '{') d++;
                    else if (x == '}')
                    {
                        d--;
                        if (d == 0) break;
                    }
                    j++;
                }
                string code = raw.Substring(k + 2, j - k - 2);
                sb.Append("{Prelude.str(").Append(SubExpr(code, tok.Line)).Append(")}");
                k = j + 1;
                continue;
            }
            if (ch == '\n') sb.Append("\\n");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\t') sb.Append("\\t");
            else if (interp && (ch == '{' || ch == '}')) sb.Append(ch).Append(ch);
            else sb.Append(ch);
            k++;
        }
        kind = "value";
        return (interp ? "$\"" : "\"") + sb.ToString() + "\"";
    }

    string SubExpr(string code, int line)
    {
        var savedT = t;
        int savedP = p;
        string savedKind = kind;
        t = new Lexer(code, file, line).Lex();
        p = 0;
        string e = ParseExpr();
        SkipNl();
        if (t[p].Kind != "eof") throw Err("unexpected token in interpolation");
        t = savedT;
        p = savedP;
        kind = savedKind;
        return e;
    }
}
