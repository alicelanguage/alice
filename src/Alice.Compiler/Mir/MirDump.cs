using System.Text;

namespace Alice.Compiler.Mir;

/// <summary>
/// Stable text form of MIR/AIR bodies (`alice emit --air`): one line per statement, expressions as
/// s-expressions, operators by enum name, `:T` after a node whose <see cref="MExpr.Type"/> is set, `@line` (or
/// `@file:line:col+len` with a <see cref="MStmt.Span"/>) after a statement. Bodies appear in emission order.
/// Tests assert this form instead of C# text.
/// </summary>
public sealed partial class MirDump
{
    [ThreadStatic] static MirDump? active;

    readonly StringBuilder sb = new();
    int bodies;
    int ind;

    /// <summary>Runs <paramref name="compile"/> and returns the dump of every body it lowered, after the MIR passes.</summary>
    public static string Collect(Action compile)
    {
        var prev = active;
        var d = active = new MirDump();
        try { compile(); }
        finally { active = prev; }
        return d.sb.ToString();
    }

    /// <summary>Called by the pass pipeline with each optimized body; a no-op outside <see cref="Collect"/>.</summary>
    public static void Captured(List<MStmt> body)
    {
        if (active is not { } d) return;
        d.sb.Append("body ").Append(++d.bodies).Append('\n');
        d.ind = 1;
        d.Stmts(body);
    }

    /// <summary>The dump of one body (no header).</summary>
    public static string Of(List<MStmt> body)
    {
        var d = new MirDump();
        d.Stmts(body);
        return d.sb.ToString();
    }

    void Stmts(List<MStmt> list)
    {
        foreach (var s in list) Stmt(s);
    }

    void Nested(string head, List<MStmt> body)
    {
        Line(head);
        ind++;
        Stmts(body);
        ind--;
    }

    void Line(string text) => sb.Append(' ', ind * 2).Append(text).Append('\n');

    static string At(MStmt s) =>
        s.Span is { } p ? $" @{p.File}:{p.Line}:{p.Col}+{p.Len}" : s.Line > 0 ? " @" + s.Line : "";

    void Stmt(MStmt s)
    {
        string at = At(s);
        switch (s)
        {
            case MDecl d:
                Line($"decl {d.Name} {d.TypeCs ?? "var"}{(d.RentLength != null ? " rent " + d.RentLength : "")}{(d.Init != null ? " = " + E(d.Init) : "")}{at}");
                break;
            case MDeconstruct dc:
                Line($"deconstruct{(dc.Declare ? " decl" : "")} ({string.Join(", ", dc.Names)}) = {E(dc.Value)}{at}");
                break;
            case MAssign a:
                Line($"assign {E(a.Target)} {a.Op} {E(a.Value)}{at}");
                break;
            case MExprStmt es:
                Line($"expr {E(es.E)}{at}");
                break;
            case MIf i:
                Nested($"if {E(i.Cond)}{at}", i.Then);
                if (i.Else != null) Nested("else", i.Else);
                break;
            case MWhile w:
                Nested($"while {E(w.Cond)}{at}", w.Body);
                break;
            case MFor f:
                Nested($"for {f.Var} {f.VarType ?? "var"} = {E(f.Start)}; {f.Cmp} {E(f.End)}; {f.Step}{(f.MinStart is long m ? " minstart " + m : "")}{at}", f.Body);
                break;
            case MForeach fe:
                Nested($"foreach{(fe.Await ? " await" : "")} ({string.Join(", ", fe.Vars)}) in {E(fe.Coll)}{at}", fe.Body);
                break;
            case MReturn r:
                Line(r.Value != null ? $"return {E(r.Value)}{at}" : "return" + at);
                break;
            case MBreak:
                Line("break" + at);
                break;
            case MContinue:
                Line("continue" + at);
                break;
            case MThrow t:
                Line($"throw {E(t.Value)}{at}");
                break;
            case MTry tr:
                Nested("try" + at, tr.Body);
                foreach (var c in tr.Catches) Nested($"catch {c.TypeCs}{(c.Name != null ? " " + c.Name : "")}", c.Body);
                if (tr.Finally != null) Nested("finally", tr.Finally);
                break;
            case MBlock b:
                Nested("block" + (b.Unchecked ? " unchecked" : "") + (b.Unsafe ? " unsafe" : "") + (b.Lock != null ? " lock " + E(b.Lock) : "") + at, b.Body);
                break;
            case MUsing u:
                Nested($"using{(u.Await ? " await" : "")} {u.Name} = {E(u.Value)}{at}", u.Body);
                break;
            default:
                if (!StmtLower(s, at) && !StmtStateMachines(s, at)) Line(s.GetType().Name + at);
                break;
        }
    }

    string E(MExpr e) => Core(e) + (e.Type is { } t ? ":" + t.ToCs() : "");

    string L(List<MExpr> xs) => string.Concat(xs.Select(x => " " + E(x)));

    string Core(MExpr e)
    {
        switch (e)
        {
            // typed leaves (MExpr.Core.cs) before their MCode base
            case AirConst k: return "(const " + k.Text + ")";
            case AirNull: return "(null)";
            case AirThis t: return t.Extension ? "(self)" : "(this)";
            case AirBase: return "(base)";
            case AirLocalRef l: return "(local " + l.Name + (l.Narrowed ? " narrowed)" : ")");
            case AirParamRef p: return "(param " + p.Name + ")";
            case AirFieldRef f: return "(field " + f.Name + ")";
            case AirArrayLength al: return "(length " + al.Array + ")";
            case AirTypeRef tr: return "(type " + tr.Ref.ToCs() + ")";
            case AirMethodGroup mg: return "(methodgroup " + (mg.Owner?.ToCs() ?? "_") + " " + mg.Name + ")";
            case AirDefault d: return d.Forgiving ? "(default!)" : "(default)";
            case AirTypeOf to: return "(typeof " + to.Of.ToCs() + ")";
            case AirSizeOf so: return "(sizeof " + so.Of.ToCs() + ")";
            case AirDiscard: return "(discard)";
            case AirYieldBreak: return "(yieldbreak)";
            case AirName n: return "(name " + n.Path + ")";
            case AirRawCs raw: return "(cs `" + raw.Text + "`)";
            case MCode c: return "`" + c.Text + "`";
            case MCall c: return $"(call{(c.Safe ? "?" : "")} {c.Name} {(c.Target != null ? E(c.Target) : "_")}{L(c.Args)})";
            case MMember m: return $"(member{(m.Safe ? "?" : "")} {m.Name} {E(m.Target)})";
            case MInvoke i: return $"(invoke {E(i.Target)}{L(i.Args)})";
            case MNew n: return $"(new {n.TypeCs}{L(n.Args)}{(n.Init != null ? " (init" + L(n.Init) + ")" : "")})";
            case MNewArray a: return $"(newarray {(a.ElemCs == "" ? "_" : a.ElemCs)}{(a.Size != null ? " " + E(a.Size) : "")}{(a.Items != null ? " (items" + L(a.Items) + ")" : "")})";
            case MIndex ix: return $"(index {E(ix.Target)}{L(ix.Args)})";
            case MBinary b: return $"({b.Op}{(b.Checked ? "" : " unchecked")} {E(b.L)} {E(b.R)})";
            case MUnary u: return $"({u.Op}{(u.Postfix ? " postfix" : "")}{(u.Checked ? "" : " unchecked")} {E(u.E)})";
            case MCond c: return $"(cond {E(c.C)} {E(c.A)} {E(c.B)})";
            case MCast c: return $"(cast {c.TypeCs} {E(c.E)})";
            case MIs i: return $"(is {E(i.E)} {i.TypeCs})";
            case MInterp ip:
                {
                    var s = new StringBuilder("(interp");
                    for (int k = 0; k < ip.Chunks.Count; k++)
                    {
                        s.Append(' ').Append('"').Append(ip.Chunks[k].Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\n")).Append('"');
                        if (k < ip.Holes.Count) s.Append(' ').Append(E(ip.Holes[k]));
                    }
                    return s.Append(')').ToString();
                }
            case MTuple t: return $"(tuple{L(t.Items)})";
            case MPair p: return $"(pair {E(p.K)} {E(p.V)})";
            case MArg a: return $"(arg \"{a.Prefix}\" {E(a.E)})";
            case MLambda l:
                {
                    string head = "(lambda" + (l.Static ? " static" : "") + (l.Hot ? " hot" : "") + " (" + string.Join(", ", l.Params) + ")";
                    if (l.ExprBody != null) return head + " " + E(l.ExprBody) + ")";
                    var inner = new MirDump { ind = ind + 1 };
                    inner.Stmts(l.Body!);
                    return head + "\n" + inner.sb + new string(' ', ind * 2) + ")";
                }
        }
        if (CoreVector(e) is { } vec) return vec;
        if (CoreLower(e) is { } lo) return lo;
        if (CoreDyn(e) is { } dyn) return dyn;
        if (CoreIterators(e) is { } iter) return iter;
        if (CoreParallel(e) is { } par) return par;
        if (CoreClosures(e) is { } clo) return clo;
        if (CoreDataflow(e) is { } dfl) return dfl;   // ADR 0038 (MirDump.Dataflow.cs)
        return "(" + e.GetType().Name + ")";
    }
}
