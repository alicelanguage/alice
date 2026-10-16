using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    bool IsStop(string stops)
    {
        if (t[p].Kind == "eof") return true;
        if (t[p].Kind == "id" && stops.Contains(" " + t[p].Text + " ")) return true;
        return stops.Contains(" } ") && IsOp("}");
    }

    List<Stmt> ParseBlock(string stops)
    {
        var list = new List<Stmt>();
        while (true)
        {
            SkipNl();
            if (t[p].Kind == "eof")
            {
                string want = stops.Trim().Split(' ')[stops.Trim().Split(' ').Length - 1];
                throw Err($"expected '{want}'");
            }
            if (IsStop(stops)) return list;
            list.Add(ParseStatement());
        }
    }

    Stmt ParseStatement()
    {
        int line = t[p].Line;
        Stmt s;
        if (IsKw("if") || IsKw("unless")) s = ParseIf();
        else if (IsKw("while") || IsKw("until")) s = ParseWhile();
        else if (IsKw("for")) s = ParseFor();
        else if (IsKw("case")) s = ParseCase();
        else if (IsKw("begin")) s = ParseBegin();
        else if (IsKw("loop") && (Peek(1).Text == "do" || Peek(1).Text == "{")) s = ParseLoop();
        else s = ParseSimple();
        s.Line = line;
        while (IsKw("if") || IsKw("unless"))
        {
            bool neg = Next().Text == "unless";
            string c = ParseExpr();
            if (neg) c = $"!({c})";
            var w = new Stmt { Line = line, Conds = new List<string>(), Bodies = new List<List<Stmt>>() };
            w.Conds.Add(c);
            var b = new List<Stmt>();
            b.Add(s);
            w.Bodies.Add(b);
            s = w;
        }
        EndStmt();
        return s;
    }

    static Stmt Simple(string code)
    {
        return new Stmt { Code = code };
    }

    Stmt ParseSimple()
    {
        if (IsKw("return"))
        {
            Next();
            if (AtStmtEnd()) return Simple("return;");
            return Simple("return " + ParseExprList() + ";");
        }
        if (IsKw("break")) { Next(); return Simple("break;"); }
        if (IsKw("next"))
        {
            Next();
            bool inBlock = ctx.Count > 0 && ctx[ctx.Count - 1] == "block";
            if (!inBlock) return Simple("continue;");
            if (AtStmtEnd()) return Simple("return;");
            return Simple("return " + ParseExpr() + ";");
        }
        if (IsKw("raise"))
        {
            Next();
            string e = ParseExpr();
            if (e.StartsWith("new ")) return Simple("throw " + e + ";");
            return Simple("throw Prelude.raise(" + e + ");");
        }
        if (IsKw("spawn"))
        {
            Next();
            string body = ParseLambdaBody("", new List<string>());
            return Simple($"Prelude.spawn({body});");
        }
        if (t[p].Kind == "id" && !IsKeyword(t[p].Text) && Peek(1).Kind == "op" && Peek(1).Text == ":" && Peek(1).Space)
        {
            string name = Next().Text;
            Next();
            string type = ParseType();
            string v = "default!";
            if (IsOp("=")) { Next(); v = ParseExpr(); }
            string cs = Declare(name);
            return new Stmt { Code = $"{type} {cs} = {v};", Kind = "assign" };
        }
        if (t[p].Kind == "id" && !IsKeyword(t[p].Text) && Peek(1).Kind == "op" && Peek(1).Text == "=")
        {
            string name = Next().Text;
            Next();
            SkipNl();
            string v = ParseExpr();
            if (IsLocal(name)) return new Stmt { Code = $"{Local(name)} = {v};", Kind = "assign" };
            if (v == "null") throw Err($"cannot infer type of '{name}' from nil; declare it: {name} : Type? = nil");
            string cs = Declare(name);
            return new Stmt { Code = $"var {cs} = {v};", Kind = "assign" };
        }
        if (t[p].Kind == "id" && !IsKeyword(t[p].Text) && Peek(1).Kind == "op" && Peek(1).Text == "," && IsMultiAssign())
            return ParseMultiAssign();
        string ex = ParseExpr();
        string k = kind;
        if (IsOp("="))
        {
            Next();
            SkipNl();
            return new Stmt { Code = $"{ex} = {ParseExpr()};", Kind = "assign" };
        }
        if (t[p].Kind == "op" && " += -= *= /= %= |= &= ^= <<= >>= ".Contains(" " + t[p].Text + " "))
        {
            string op = Next().Text;
            return new Stmt { Code = $"{ex} {op} {ParseExpr()};", Kind = "assign" };
        }
        if (IsOp("**=")) { Next(); return new Stmt { Code = $"{ex} = Prelude.pow({ex}, {ParseExpr()});", Kind = "assign" }; }
        if (IsOp("||=")) { Next(); return new Stmt { Code = $"{ex} ??= {ParseExpr()};", Kind = "assign" }; }
        if (IsOp("&&=")) { Next(); return new Stmt { Code = $"{ex} = {ex} && {ParseExpr()};", Kind = "assign" }; }
        return new Stmt { Code = ex + ";", Expr = ex, HasExpr = true, Kind = k };
    }

    bool IsMultiAssign()
    {
        int k = p;
        while (t[k].Kind == "id" || (t[k].Kind == "op" && t[k].Text == ",")) k++;
        return t[k].Kind == "op" && t[k].Text == "=";
    }

    Stmt ParseMultiAssign()
    {
        var names = new List<string>();
        names.Add(Next().Text);
        while (IsOp(",")) { Next(); names.Add(Next().Text); }
        Expect("=");
        string rhs = ParseExprList();
        if (!rhs.StartsWith("(")) rhs = "(" + rhs + ")";
        bool allDeclared = true;
        foreach (var n in names) if (!IsLocal(n)) allDeclared = false;
        var lhs = new List<string>();
        foreach (var n in names) lhs.Add(allDeclared ? Local(n) : (IsLocal(n) ? throw Err("mixing new and existing variables in multiple assignment") : "var " + Declare(n)));
        return new Stmt { Code = $"({string.Join(", ", lhs)}) = {rhs};", Kind = "assign" };
    }

    string ParseExprList()
    {
        string e = ParseExpr();
        if (!IsOp(",")) return e;
        var parts = new List<string>();
        parts.Add(e);
        while (IsOp(",")) { Next(); parts.Add(ParseExpr()); }
        return "(" + string.Join(", ", parts) + ")";
    }

    Stmt ParseIf()
    {
        bool neg = Next().Text == "unless";
        var s = new Stmt { Conds = new List<string>(), Bodies = new List<List<Stmt>>() };
        string c = ParseExpr();
        if (neg) c = $"!({c})";
        if (IsKw("then")) Next();
        PushScope();
        s.Conds.Add(c);
        s.Bodies.Add(ParseBlock(" elsif else end "));
        PopScope();
        while (IsKw("elsif"))
        {
            Next();
            string c2 = ParseExpr();
            if (IsKw("then")) Next();
            PushScope();
            s.Conds.Add(c2);
            s.Bodies.Add(ParseBlock(" elsif else end "));
            PopScope();
        }
        if (IsKw("else"))
        {
            Next();
            PushScope();
            s.Else = ParseBlock(" end ");
            PopScope();
        }
        ExpectKw("end");
        return s;
    }

    string LoopBody(string stops)
    {
        ctx.Add("loop");
        List<Stmt> body = ParseBlock(stops);
        ctx.RemoveAt(ctx.Count - 1);
        return Render(body, 0, 1);
    }

    Stmt ParseWhile()
    {
        bool neg = Next().Text == "until";
        string c = ParseExpr();
        if (neg) c = $"!({c})";
        if (IsKw("do")) Next();
        PushScope();
        string body = LoopBody(" end ");
        PopScope();
        ExpectKw("end");
        return Simple($"while ({c})\n{{\n{body}}}");
    }

    Stmt ParseLoop()
    {
        Next();
        bool brace = IsOp("{");
        Next();
        PushScope();
        string body = LoopBody(brace ? " } " : " end ");
        PopScope();
        if (brace) Expect("}"); else ExpectKw("end");
        return Simple($"while (true)\n{{\n{body}}}");
    }

    Stmt ParseFor()
    {
        Next();
        var names = new List<string>();
        names.Add(Next().Text);
        while (IsOp(",")) { Next(); names.Add(Next().Text); }
        ExpectKw("in");
        string start = ParseBinary(0);
        PushScope();
        string head;
        if (IsOp("..") || IsOp("..."))
        {
            bool excl = Next().Text == "...";
            string end = ParseBinary(0);
            string v = Declare(names[0]);
            head = $"for (var {v} = {start}; {v} {(excl ? "<" : "<=")} {end}; {v}++)";
        }
        else if (names.Count == 1) head = $"foreach (var {Declare(names[0])} in {start})";
        else
        {
            var vs = new List<string>();
            foreach (var n in names) vs.Add(Declare(n));
            head = $"foreach (var ({string.Join(", ", vs)}) in {start})";
        }
        if (IsKw("do")) Next();
        string body = LoopBody(" end ");
        PopScope();
        ExpectKw("end");
        return Simple($"{head}\n{{\n{body}}}");
    }

    Stmt ParseCase()
    {
        Next();
        var s = new Stmt { Conds = new List<string>(), Bodies = new List<List<Stmt>>() };
        string subject = "";
        if (t[p].Kind != "nl")
        {
            string e = ParseExpr();
            tmp++;
            subject = "__case" + tmp;
            s.Pre = $"var {subject} = {e};\n";
        }
        SkipNl();
        while (IsKw("when"))
        {
            Next();
            var parts = new List<string>();
            while (true)
            {
                parts.Add(WhenCond(subject));
                if (!IsOp(",")) break;
                Next();
                SkipNl();
            }
            if (IsKw("then")) Next();
            PushScope();
            s.Conds.Add(string.Join(" || ", parts));
            s.Bodies.Add(ParseBlock(" when else end "));
            PopScope();
        }
        if (IsKw("else"))
        {
            Next();
            PushScope();
            s.Else = ParseBlock(" end ");
            PopScope();
        }
        ExpectKw("end");
        return s;
    }

    string WhenCond(string subject)
    {
        if (subject == "") return ParseExpr();
        if (t[p].Kind == "const" && (Peek(1).Kind == "nl" || Peek(1).Text == "," || Peek(1).Text == "then") && !Peek(1).Text.StartsWith(":"))
        {
            string ty = Next().Text;
            if (ty.Length > 0 && IsTypeName(ty)) return $"{subject} is {MapType(ty)}";
            return $"{subject} == {ty}";
        }
        string a = ParseBinary(0);
        if (IsOp("..") || IsOp("..."))
        {
            bool excl = Next().Text == "...";
            string b = ParseBinary(0);
            return $"({subject} >= {a} && {subject} {(excl ? "<" : "<=")} {b})";
        }
        return $"{subject} == {a}";
    }

    static bool IsTypeName(string n) => MapType(n) != n || n == "Exception" || n.EndsWith("Exception") || n.EndsWith("Error");

    Stmt ParseBegin()
    {
        Next();
        PushScope();
        string body = Render(ParseBlock(" rescue ensure end "), 0, 1);
        PopScope();
        var sb = new StringBuilder($"try\n{{\n{body}}}");
        while (IsKw("rescue"))
        {
            Next();
            string name = "";
            string type = "Exception";
            PushScope();
            if (t[p].Kind == "id")
            {
                name = Declare(Next().Text);
                if (IsOp(":")) { Next(); type = ParseType(); }
            }
            else if (t[p].Kind == "const") type = ParseType();
            string rb = Render(ParseBlock(" rescue ensure end "), 0, 1);
            PopScope();
            sb.Append(name == "" ? $"\ncatch ({type})\n{{\n{rb}}}" : $"\ncatch ({type} {name})\n{{\n{rb}}}");
        }
        if (IsKw("ensure"))
        {
            Next();
            PushScope();
            string eb = Render(ParseBlock(" end "), 0, 1);
            PopScope();
            sb.Append($"\nfinally\n{{\n{eb}}}");
        }
        ExpectKw("end");
        return Simple(sb.ToString());
    }
}
