using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    static string Indent(string code, int ind)
    {
        if (ind == 0) return code;
        string pad = new string(' ', ind * 4);
        var sb = new StringBuilder();
        foreach (var l in code.Split('\n'))
        {
            if (l.Length > 0) sb.Append(l.StartsWith("#line") ? "" : pad);
            sb.Append(l).Append('\n');
        }
        return sb.ToString(0, sb.Length - 1);
    }

    string Render(List<Stmt> body, int tail, int ind)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < body.Count; i++)
            sb.Append(RenderStmt(body[i], i == body.Count - 1 ? tail : 0, ind));
        return sb.ToString();
    }

    string RenderStmt(Stmt s, int tail, int ind)
    {
        string line = $"#line {s.Line} \"{file}\"\n";
        if (s.Conds != null && s.Bodies != null)
        {
            var sb = new StringBuilder();
            sb.Append(line);
            sb.Append(Indent(s.Pre, ind));
            for (int k = 0; k < s.Conds.Count; k++)
            {
                sb.Append(Indent((k == 0 ? "if (" : "else if (") + s.Conds[k] + ")\n{", ind)).Append('\n');
                sb.Append(Render(s.Bodies[k], tail, ind + 1));
                sb.Append(Indent("}", ind)).Append('\n');
            }
            if (s.Else != null)
            {
                sb.Append(Indent("else\n{", ind)).Append('\n');
                sb.Append(Render(s.Else, tail, ind + 1));
                sb.Append(Indent("}", ind)).Append('\n');
            }
            return sb.ToString();
        }
        string code = s.Code;
        if (s.HasExpr && (tail == 1 || (tail == 2 && s.Kind == "value"))) code = "return " + s.Expr + ";";
        return line + Indent(code, ind) + "\n";
    }
}
