using System.Text;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>Prints MIR as C#. Every statement carries a #line directive back to the Alice source.</summary>
public sealed class Printer
{
    readonly StringBuilder sb = new();
    readonly string file;
    int ind;
    int lastLine = -1;

    public Printer(string file, int indent)
    {
        this.file = file;
        ind = indent;
    }

    public override string ToString() => sb.ToString();

    void Line(string text) => sb.Append(' ', ind * 4).Append(text).Append('\n');

    void LineDir(int line)
    {
        if (line <= 0 || line == lastLine || file.Length == 0) return;
        sb.Append("#line ").Append(line).Append(" \"").Append(file).Append("\"\n");
        lastLine = line;
    }

    public void Stmts(List<MStmt> list)
    {
        foreach (var s in list) Stmt(s);
    }

    void Block(List<MStmt> body)
    {
        Line("{");
        ind++;
        Stmts(body);
        ind--;
        Line("}");
    }

    public void Stmt(MStmt s)
    {
        LineDir(s.Line);
        switch (s)
        {
            case MDecl d:
                Line($"{d.TypeCs ?? "var"} {d.Name}{(d.Init != null ? " = " + E(d.Init) : "")};");
                break;
            case MDeconstruct dc:
                Line(dc.Declare ? $"var ({string.Join(", ", dc.Names)}) = {E(dc.Value)};" : $"({string.Join(", ", dc.Names)}) = {E(dc.Value)};");
                break;
            case MAssign a:
                Line($"{E(a.Target)} {a.Op} {E(a.Value)};");
                break;
            case MExprStmt es:
                Line(E(es.E) + ";");
                break;
            case MIf i:
                Line($"if ({E(i.Cond)})");
                Block(i.Then);
                if (i.Else != null)
                {
                    Line("else");
                    Block(i.Else);
                }
                break;
            case MWhile w:
                Line($"while ({E(w.Cond)})");
                Block(w.Body);
                break;
            case MFor f:
                Line($"for (var {f.Var} = {E(f.Start)}; {f.Var} {f.Cmp} {E(f.End)}; {f.Var}{f.Step})");
                Block(f.Body);
                break;
            case MForeach fe:
                Line(fe.Vars.Count == 1 ? $"foreach (var {fe.Vars[0]} in {E(fe.Coll)})" : $"foreach (var ({string.Join(", ", fe.Vars)}) in {E(fe.Coll)})");
                Block(fe.Body);
                break;
            case MReturn r:
                Line(r.Value != null ? $"return {E(r.Value)};" : "return;");
                break;
            case MBreak:
                Line("break;");
                break;
            case MContinue:
                Line("continue;");
                break;
            case MThrow t:
                Line($"throw {E(t.Value)};");
                break;
            case MTry tr:
                Line("try");
                Block(tr.Body);
                foreach (var c in tr.Catches)
                {
                    Line(c.Name != null ? $"catch ({c.TypeCs} {c.Name})" : $"catch ({c.TypeCs})");
                    Block(c.Body);
                }
                if (tr.Finally != null)
                {
                    Line("finally");
                    Block(tr.Finally);
                }
                break;
            case MBlock b:
                Block(b.Body);
                break;
            case MUsing u:
                Line($"using (var {u.Name} = {E(u.Value)})");
                Block(u.Body);
                break;
        }
    }

    static bool Atomic(MExpr e) => e is MCode or MCall or MMember or MInvoke or MIndex or MNew or MNewArray or MTuple or MInterp;

    static string P(MExpr e) => Atomic(e) ? E(e) : "(" + E(e) + ")";

    public static string E(MExpr e)
    {
        switch (e)
        {
            case MCode c:
                return c.Text;
            case MCall c:
                return (c.Target != null ? P(c.Target) + (c.Safe ? "?." : ".") : "") + c.Name + "(" + Join(c.Args) + ")";
            case MMember m:
                return P(m.Target) + (m.Safe ? "?." : ".") + m.Name;
            case MInvoke i:
                return P(i.Target) + "(" + Join(i.Args) + ")";
            case MNew n:
                string init = n.Init == null ? "" : n.Init.Count == 0 ? " { }" : " { " + Join(n.Init) + " }";
                return n.Init != null && n.Args.Count == 0 ? $"new {n.TypeCs}(){init}" : $"new {n.TypeCs}({Join(n.Args)}){init}";
            case MNewArray a:
                if (a.Items != null) return a.ElemCs == "" ? "new[] { " + Join(a.Items) + " }" : $"new {a.ElemCs}[] {{ {Join(a.Items)} }}";
                int br = a.ElemCs.IndexOf('[');
                return br < 0 ? $"new {a.ElemCs}[{E(a.Size!)}]" : $"new {a.ElemCs.Substring(0, br)}[{E(a.Size!)}]{a.ElemCs.Substring(br)}";
            case MIndex ix:
                return P(ix.Target) + "[" + Join(ix.Args) + "]";
            case MBinary b:
                return P(b.L) + " " + b.Op + " " + P(b.R);
            case MUnary u:
                return u.Postfix ? P(u.E) + u.Op : u.Op + P(u.E);
            case MCond c:
                return P(c.C) + " ? " + P(c.A) + " : " + P(c.B);
            case MLambda l:
                {
                    string head = (l.Static ? "static " : "") + "(" + string.Join(", ", l.Params) + ") => ";
                    if (l.ExprBody != null) return head + E(l.ExprBody);
                    var pr = new Printer("", 0) { lastLine = int.MaxValue };
                    pr.Line("{");
                    pr.ind++;
                    pr.Stmts(l.Body!);
                    pr.ind--;
                    pr.sb.Append('}');
                    return head + "\n" + pr.sb;
                }
            case MCast c:
                return "((" + c.TypeCs + ")" + P(c.E) + ")";
            case MIs i:
                return "(" + P(i.E) + " is " + i.TypeCs + ")";
            case MInterp ip:
                {
                    var s = new StringBuilder("string.Create(global::System.Globalization.CultureInfo.InvariantCulture, $\"");
                    for (int k = 0; k < ip.Chunks.Count; k++)
                    {
                        s.Append(ip.Chunks[k].Replace("{", "{{").Replace("}", "}}"));
                        if (k < ip.Holes.Count) s.Append("{(").Append(E(ip.Holes[k])).Append(")}");
                    }
                    return s.Append("\")").ToString();
                }
            case MTuple t:
                return "(" + Join(t.Items) + ")";
            case MPair pp:
                return "{ " + E(pp.K) + ", " + E(pp.V) + " }";
            case MArg a:
                return a.Prefix + E(a.E);
        }
        throw new InvalidOperationException(e.GetType().Name);
    }

    static string Join(List<MExpr> xs) => string.Join(", ", xs.Select(E));
}
