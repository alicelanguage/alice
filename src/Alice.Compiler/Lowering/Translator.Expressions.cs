using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    public string ParseExpr()
    {
        string l = ParseNotOperand();
        while (IsKw("and") || IsKw("or"))
        {
            string op = Next().Text == "and" ? "&&" : "||";
            SkipNl();
            string r = ParseNotOperand();
            l = $"({l} {op} {r})";
            kind = "value";
        }
        return l;
    }

    string ParseNotOperand()
    {
        if (IsKw("not"))
        {
            Next();
            string e = ParseNotOperand();
            kind = "value";
            return $"!({e})";
        }
        return ParseTernary();
    }

    string ParseTernary()
    {
        string c = ParseRange();
        if (IsOp("?"))
        {
            Next();
            SkipNl();
            string a = ParseTernary();
            SkipNl();
            Expect(":");
            SkipNl();
            string b = ParseTernary();
            kind = "value";
            return $"({c} ? {a} : {b})";
        }
        return c;
    }

    string ParseRange()
    {
        string a = ParseBinary(0);
        if (IsOp("..") || IsOp("..."))
        {
            bool excl = Next().Text == "...";
            string b = ParseBinary(0);
            kind = "value";
            return $"Prelude.range({a}, {b}, {(excl ? "true" : "false")})";
        }
        return a;
    }

    string ParseBinary(int level)
    {
        string[] levels = Levels.Split(';');
        if (level == levels.Length) return ParseUnary();
        string ops = " " + levels[level];
        string l = ParseBinary(level + 1);
        while (t[p].Kind == "op" && ops.Contains(" " + t[p].Text + " "))
        {
            string op = Next().Text;
            SkipNl();
            string r = ParseBinary(level + 1);
            if (op == "<=>") l = $"{l}.CompareTo({r})";
            else if (op == "|" || op == "^" || op == "&") l = $"({l} {op} {r})";
            else if (op == "===") l = $"{l} == {r}";
            else l = $"{l} {op} {r}";
            kind = "value";
        }
        return l;
    }

    string ParseUnary()
    {
        if (IsOp("-") || IsOp("!") || IsOp("~") || IsOp("+"))
        {
            string op = Next().Text;
            string e = ParseUnary();
            kind = "value";
            if (op == "+") return e;
            if (e.StartsWith("-") || e.StartsWith("!")) return $"{op}({e})";
            return op + e;
        }
        if (IsOp("&") && Peek(1).Kind == "id")
        {
            Next();
            return "&" + ParseUnary();
        }
        return ParsePower();
    }

    string ParsePower()
    {
        string b = ParsePostfix();
        if (IsOp("**"))
        {
            Next();
            string e = ParseUnary();
            kind = "value";
            return $"Prelude.pow({b}, {e})";
        }
        return b;
    }

    bool BlockFollows() => IsOp("{") || (IsKw("do") && !noDo);

    string WithBlock(string args)
    {
        if (!BlockFollows()) return args;
        string lam = ParseBlockLambda();
        return args == "" ? lam : args + ", " + lam;
    }

    string ParsePostfix()
    {
        string e = ParsePrimary();
        bool isType = lastIsType;
        while (true)
        {
            if (IsOp(".") || IsOp("&."))
            {
                bool safe = Next().Text == "&.";
                string dot = safe ? "?." : ".";
                if (IsOp("("))
                {
                    Next();
                    e = $"{e}({ParseArgs(")")})";
                    kind = "call";
                    isType = false;
                    continue;
                }
                Token nt = Next();
                if (nt.Kind != "id" && nt.Kind != "const") throw Err("expected member name");
                string name = nt.Text;
                if (isType && name == "new")
                {
                    string a = OptArgs();
                    if (e.EndsWith("[]")) e = $"new {e.Substring(0, e.Length - 2)}[{a}]";
                    else e = $"new {e}({a})";
                    kind = "call";
                    isType = false;
                    continue;
                }
                if (name == "call") { e = $"{e}({OptArgs()})"; kind = "call"; isType = false; continue; }
                if (name == "nil?") { e = $"({e} is null)"; kind = "value"; isType = false; continue; }
                if (name == "not_nil!") { e = $"{e}!"; kind = "value"; continue; }
                if (name == "is_a?") { Expect("("); e = $"({e} is {ParseType()})"; Expect(")"); kind = "value"; isType = false; continue; }
                if (name == "as") { Expect("("); e = $"(({ParseType()}){e})"; Expect(")"); kind = "value"; isType = false; continue; }
                if (name == "to_s" && !(IsOp("(") && !t[p].Space)) { e = $"Prelude.str({e})"; kind = "value"; isType = false; continue; }
                if (name == "class") { e = $"{e}.GetType()"; kind = "value"; isType = false; continue; }
                string mn = nt.Kind == "const" ? name : Mangle(name);
                if (IsOp("(") && !t[p].Space)
                {
                    Next();
                    string a = ParseArgs(")");
                    e = $"{e}{dot}{mn}({WithBlock(a)})";
                    kind = "call";
                    isType = false;
                }
                else if (CanStartArg())
                {
                    e = $"{e}{dot}{mn}({WithBlock(CommandArgs())})";
                    kind = "call";
                    isType = false;
                }
                else if (BlockFollows())
                {
                    e = $"{e}{dot}{mn}({ParseBlockLambda()})";
                    kind = "call";
                    isType = false;
                }
                else if (nt.Kind == "const")
                {
                    e = $"{e}{dot}{mn}";
                    kind = "value";
                }
                else
                {
                    e = $"{e}{dot}\u0001M:{mn}\u0001";
                    kind = "member";
                    isType = false;
                }
            }
            else if (IsOp("::"))
            {
                Next();
                e = e + "." + Next().Text;
                if (IsOp("(") && !t[p].Space && isType) e = GenericSuffix(e);
            }
            else if (IsOp("[") && !t[p].Space)
            {
                Next();
                e = $"{e}[{ParseArgs("]")}]";
                kind = "value";
                isType = false;
            }
            else break;
        }
        lastIsType = isType;
        return e;
    }

    string GenericSuffix(string name)
    {
        Next();
        var args = new List<string>();
        args.Add(ParseType());
        while (IsOp(",")) { Next(); args.Add(ParseType()); }
        Expect(")");
        return GenericType(name, args);
    }

    string OptArgs()
    {
        if (IsOp("(") && !t[p].Space)
        {
            Next();
            return WithBlock(ParseArgs(")"));
        }
        if (CanStartArg()) return WithBlock(CommandArgs());
        return WithBlock("");
    }

    bool CanStartArg()
    {
        Token c = t[p];
        if (!c.Space) return false;
        switch (c.Kind)
        {
            case "int":
            case "float":
            case "str":
            case "char":
            case "ivar":
            case "const":
                return true;
            case "id":
                return c.Text == "not" || c.Text == "nil" || c.Text == "true" || c.Text == "false" || c.Text == "self" || !IsKeyword(c.Text);
            case "op":
                if (c.Text == "->" || c.Text == "(" || c.Text == "[" || c.Text == "!") return true;
                if (c.Text == "-" || c.Text == "*" || c.Text == "&") return !Peek(1).Space;
                return false;
        }
        return false;
    }

    string CommandArgs()
    {
        bool saved = noDo;
        noDo = true;
        var parts = new List<string>();
        parts.Add(Arg());
        while (IsOp(","))
        {
            Next();
            SkipNl();
            parts.Add(Arg());
        }
        noDo = saved;
        return string.Join(", ", parts);
    }

    string ParseArgs(string close)
    {
        bool saved = noDo;
        noDo = false;
        var parts = new List<string>();
        SkipNl();
        while (!IsOp(close))
        {
            parts.Add(Arg());
            SkipNl();
            if (IsOp(",")) { Next(); SkipNl(); }
            else if (!IsOp(close)) throw Err($"expected ',' or '{close}'");
        }
        Next();
        noDo = saved;
        return string.Join(", ", parts);
    }

    string Arg()
    {
        if ((IsKw("out") || IsKw("ref")) && Peek(1).Kind == "id")
        {
            string mod = Next().Text;
            string n = Next().Text;
            if (IsLocal(n)) return mod + " " + Local(n);
            if (mod == "ref") throw Err($"'ref {n}' needs an existing variable");
            return "out var " + Declare(n);
        }
        if (t[p].Kind == "id" && Peek(1).Kind == "op" && Peek(1).Text == ":" && !Peek(1).Space)
        {
            string n = Next().Text;
            Next();
            return Mangle(n) + ": " + ParseExpr();
        }
        if (IsOp("*") && !Peek(1).Space) { Next(); return ParseExpr(); }
        return ParseExpr();
    }

    string ParsePrimary()
    {
        lastIsType = false;
        Token c = t[p];
        kind = "value";
        switch (c.Kind)
        {
            case "int":
            case "float":
            case "char":
                Next();
                return c.Text;
            case "str":
                Next();
                return Interp(c);
            case "ivar":
                Next();
                return "this." + Mangle(c.Text);
            case "const":
                return ParseConstPrimary();
            case "id":
                return ParseIdPrimary();
            case "op":
                if (c.Text == "(")
                {
                    Next();
                    SkipNl();
                    string e = ParseExprList();
                    SkipNl();
                    Expect(")");
                    kind = "value";
                    return e.StartsWith("(") && e.EndsWith(")") && IsBalanced(e) ? e : "(" + e + ")";
                }
                if (c.Text == "[") return ParseArrayLit();
                if (c.Text == "{") return ParseHashLit();
                if (c.Text == "->") return ParseLambdaLit();
                if (c.Text == "::") { Next(); return "global::" + ParseConstPrimary(); }
                break;
        }
        throw Err("unexpected token");
    }

    static bool IsBalanced(string e)
    {
        int d = 0;
        for (int i = 0; i < e.Length; i++)
        {
            if (e[i] == '(') d++;
            if (e[i] == ')') d--;
            if (d == 0 && i < e.Length - 1) return false;
        }
        return true;
    }

    string ParseConstPrimary()
    {
        string name = Next().Text;
        string r = MapType(name.EndsWith("?") ? name.Substring(0, name.Length - 1) : name);
        lastIsType = true;
        if (IsOp("(") && !t[p].Space) r = GenericSuffix(name);
        lastIsType = true;
        return r;
    }

    string ParseIdPrimary()
    {
        Token c = Next();
        string n = c.Text;
        switch (n)
        {
            case "nil": return "null";
            case "true": return "true";
            case "false": return "false";
            case "self": return "this";
            case "super":
            {
                string a = methodArgs;
                if (IsOp("(") && !t[p].Space) { Next(); a = ParseArgs(")"); }
                kind = "call";
                return $"base.{Mangle(method)}({a})";
            }
            case "typeof":
            case "sizeof":
            {
                Expect("(");
                string ty = ParseType();
                Expect(")");
                return $"{n}({ty})";
            }
            case "uninitialized":
                return $"default({ParseType()})";
            case "spawn":
            {
                kind = "call";
                return $"Prelude.spawn({ParseLambdaBody("", new List<string>())})";
            }
        }
        if (IsKeyword(n)) { p--; throw Err("unexpected keyword"); }
        if (IsLocal(n))
        {
            string l = Local(n);
            if (IsOp("(") && !t[p].Space)
            {
                Next();
                kind = "call";
                return $"{l}({ParseArgs(")")})";
            }
            return l;
        }
        string m = Mangle(n);
        if (IsOp("<") && !t[p].Space && Peek(1).Kind == "const" && GenericCallAhead())
        {
            Next();
            var ts = new List<string>();
            ts.Add(ParseType());
            while (IsOp(",")) { Next(); ts.Add(ParseType()); }
            Expect(">");
            m += "<" + string.Join(", ", ts) + ">";
        }
        if (IsOp("(") && !t[p].Space)
        {
            Next();
            string a = ParseArgs(")");
            kind = "call";
            return $"{m}({WithBlock(a)})";
        }
        if (CanStartArg())
        {
            kind = "call";
            return $"{m}({WithBlock(CommandArgs())})";
        }
        if (BlockFollows())
        {
            kind = "call";
            return $"{m}({ParseBlockLambda()})";
        }
        kind = "member";
        return $"\u0001M:{m}\u0001";
    }

    bool GenericCallAhead()
    {
        int k = p + 1;
        int d = 1;
        while (k < t.Count && d > 0)
        {
            Token x = t[k];
            if (x.Kind == "op" && x.Text == "<") d++;
            else if (x.Kind == "op" && x.Text == ">") d--;
            else if (!(x.Kind == "const" || (x.Kind == "op" && (x.Text == "," || x.Text == "(" || x.Text == ")" || x.Text == "::")))) return false;
            k++;
        }
        return d == 0 && k < t.Count && t[k].Kind == "op" && t[k].Text == "(";
    }
}
