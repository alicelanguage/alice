using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    string ParseBlockLambda()
    {
        var ps = new List<string>();
        var names = new List<string>();
        bool brace = IsOp("{");
        Next();
        if (IsOp("||")) Next();
        else if (IsOp("|"))
        {
            Next();
            while (!IsOp("|"))
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
        return LambdaRest(ps, names, brace);
    }

    string ParseLambdaBody(string unused, List<string> names)
    {
        bool brace = IsOp("{");
        if (!brace && !IsKw("do")) throw Err("expected '{' or 'do'");
        Next();
        return LambdaRest(new List<string>(), names, brace);
    }

    string LambdaRest(List<string> ps, List<string> names, bool brace)
    {
        PushScope();
        var outs = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            string cs = Declare(names[i]);
            string pdecl = ps[i];
            outs.Add(pdecl.Substring(0, pdecl.Length - names[i].Length) + cs);
        }
        ctx.Add("block");
        bool saved = noDo;
        noDo = false;
        List<Stmt> body = ParseBlock(brace ? " } " : " end ");
        noDo = saved;
        ctx.RemoveAt(ctx.Count - 1);
        if (brace) Expect("}"); else ExpectKw("end");
        PopScope();
        string plist = "(" + string.Join(", ", outs) + ")";
        kind = "value";
        if (body.Count == 1 && body[0].HasExpr && body[0].Conds == null) return $"{plist} => {body[0].Expr}";
        if (body.Count == 0) return $"{plist} => {{ }}";
        return $"{plist} =>\n{{\n{Render(body, 2, 1)}}}";
    }
}
