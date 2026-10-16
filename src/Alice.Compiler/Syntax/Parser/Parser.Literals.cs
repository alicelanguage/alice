using System.Text;

namespace Alice.Compiler.Syntax;

public sealed partial class Parser
{
    Expr ArrayLiteral()
    {
        Token tok = Next();
        var a = At(new ArrayLit(), tok);
        SkipNl();
        while (!IsOp("]"))
        {
            a.Items.Add(Expression());
            SkipNl();
            if (IsOp(",")) { Next(); SkipNl(); }
        }
        Next();
        if (IsKw("of")) { Next(); a.Of = Type(); }
        else if (a.Items.Count == 0) throw Err("empty array literal needs a type: [] of Type");
        return a;
    }

    Expr HashLiteral()
    {
        Token tok = Next();
        SkipNl();
        if (IsOp("}"))
        {
            Next();
            ExpectKw("of");
            var h = At(new HashLit { K = Type() }, tok);
            Expect("=>");
            h.V = Type();
            return h;
        }
        Expr first = Expression();
        SkipNl();
        if (IsOp("=>"))
        {
            var h = At(new HashLit(), tok);
            Next();
            SkipNl();
            h.Keys.Add(first);
            h.Values.Add(Expression());
            SkipNl();
            while (IsOp(","))
            {
                Next();
                SkipNl();
                if (IsOp("}")) break;
                h.Keys.Add(Expression());
                Expect("=>");
                SkipNl();
                h.Values.Add(Expression());
                SkipNl();
            }
            Expect("}");
            return h;
        }
        var tl = At(new TupleLit(), tok);
        tl.Items.Add(first);
        while (IsOp(",")) { Next(); SkipNl(); tl.Items.Add(Expression()); SkipNl(); }
        Expect("}");
        return tl;
    }

    LambdaExpr LambdaLiteral()
    {
        Next();
        var ps = new List<Param>();
        if (IsOp("("))
        {
            Next();
            while (!IsOp(")"))
            {
                var pr = At(new Param { Name = Next().Text }, t[p - 1]);
                if (IsOp(":")) { Next(); pr.Type = Type(); }
                ps.Add(pr);
                if (IsOp(",")) Next();
            }
            Next();
        }
        return LambdaBody(ps);
    }

    LambdaExpr BlockLambda()
    {
        var ps = new List<Param>();
        bool brace = IsOp("{");
        Token tok = Next();
        if (IsOp("||")) Next();
        else if (IsOp("|"))
        {
            Next();
            while (!IsOp("|"))
            {
                var pr = At(new Param { Name = Next().Text }, t[p - 1]);
                if (IsOp(":")) { Next(); pr.Type = Type(); }
                ps.Add(pr);
                if (IsOp(",")) Next();
            }
            Next();
        }
        return LambdaRest(ps, brace, tok);
    }

    LambdaExpr LambdaBody(List<Param> ps)
    {
        bool brace = IsOp("{");
        if (!brace && !IsKw("do")) throw Err("expected '{' or 'do'");
        return LambdaRest(ps, brace, Next());
    }

    LambdaExpr LambdaRest(List<Param> ps, bool brace, Token tok)
    {
        PushLocals();
        foreach (var pr in ps) DeclareLocal(pr.Name);
        bool saved = noDo;
        noDo = false;
        var l = At(new LambdaExpr { Params = ps, Body = Block(brace ? " } " : " end ") }, tok);
        noDo = saved;
        if (brace) Expect("}"); else ExpectKw("end");
        PopLocals();
        return l;
    }

    /// <summary>Splits an Alice string into C#-escaped literal chunks and parsed `#{}` holes.</summary>
    Expr StringLit(Token tok)
    {
        string raw = tok.Text;
        var ie = At(new InterpExpr(), tok);
        var sb = new StringBuilder();
        int k = 0;
        while (k < raw.Length)
        {
            char ch = raw[k];
            if (ch == '\\' && k + 1 < raw.Length)
            {
                char nx = raw[k + 1];
                if (nx == '#' || nx == '{' || nx == '}') sb.Append(nx);
                else if (nx == 'e') sb.Append("\\u001b");
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
                    else if (x == '}' && --d == 0) break;
                    j++;
                }
                ie.Chunks.Add(sb.ToString());
                sb.Clear();
                ie.Holes.Add(SubExpr(raw.Substring(k + 2, j - k - 2), tok.Line));
                k = j + 1;
                continue;
            }
            if (ch == '\n') sb.Append("\\n");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\t') sb.Append("\\t");
            else sb.Append(ch);
            k++;
        }
        ie.Chunks.Add(sb.ToString());
        if (ie.Holes.Count == 0) return At(new LitExpr { Kind = "str", Text = ie.Chunks[0] }, tok);
        return ie;
    }

    Expr SubExpr(string code, int line)
    {
        var savedT = t;
        int savedP = p;
        t = new Lexer(code, file, line).Lex();
        p = 0;
        Expr e = Expression();
        SkipNl();
        if (t[p].Kind != "eof") throw Err("unexpected token in interpolation");
        t = savedT;
        p = savedP;
        return e;
    }
}
