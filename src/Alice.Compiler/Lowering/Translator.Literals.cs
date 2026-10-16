using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    string ParseArrayLit()
    {
        Next();
        var items = new List<string>();
        SkipNl();
        while (!IsOp("]"))
        {
            items.Add(ParseExpr());
            SkipNl();
            if (IsOp(",")) { Next(); SkipNl(); }
        }
        Next();
        kind = "value";
        if (IsKw("of"))
        {
            Next();
            string ty = ParseType();
            if (items.Count == 0) return $"new List<{ty}>()";
            return $"new List<{ty}> {{ {string.Join(", ", items)} }}";
        }
        if (items.Count == 0) throw Err("empty array literal needs a type: [] of Type");
        return $"new[] {{ {string.Join(", ", items)} }}";
    }

    string ParseHashLit()
    {
        Next();
        SkipNl();
        kind = "value";
        if (IsOp("}"))
        {
            Next();
            ExpectKw("of");
            string k = ParseType();
            Expect("=>");
            string v = ParseType();
            return $"new Dictionary<{k}, {v}>()";
        }
        string first = ParseExpr();
        SkipNl();
        if (IsOp("=>"))
        {
            var pairs = new List<string>();
            Next();
            SkipNl();
            pairs.Add($"({first}, {ParseExpr()})");
            SkipNl();
            while (IsOp(","))
            {
                Next();
                SkipNl();
                if (IsOp("}")) break;
                string k = ParseExpr();
                Expect("=>");
                SkipNl();
                pairs.Add($"({k}, {ParseExpr()})");
                SkipNl();
            }
            Expect("}");
            return $"Prelude.hash({string.Join(", ", pairs)})";
        }
        var items = new List<string>();
        items.Add(first);
        while (IsOp(",")) { Next(); SkipNl(); items.Add(ParseExpr()); SkipNl(); }
        Expect("}");
        return "(" + string.Join(", ", items) + ")";
    }

    string ParseLambdaLit()
    {
        Next();
        var ps = new List<string>();
        var names = new List<string>();
        if (IsOp("("))
        {
            Next();
            while (!IsOp(")"))
            {
                string n = Next().Text;
                string ty = "";
                if (IsOp(":")) { Next(); ty = ParseType() + " "; }
                names.Add(n);
                ps.Add(ty + n);
                if (IsOp(",")) Next();
            }
            Next();
        }
        bool brace = IsOp("{");
        if (!brace && !IsKw("do")) throw Err("expected '{' or 'do' after lambda parameters");
        Next();
        return LambdaRest(ps, names, brace);
    }
}
