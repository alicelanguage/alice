using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>C# for the parallel and ownership nodes (Mir/MExpr.Parallel.cs): the exact text the lowerer used to build.</summary>
public sealed partial class Printer
{
    const string Par = "global::Alice.Runtime.Par";

    static string? EParallel(MExpr e)
    {
        switch (e)
        {
            case AirPar p:
                {
                    string head = p.Kind == AirParKind.Range ? Par + ".Range(" : Par + ".Reduce<" + p.ResultCs + ">(";
                    string s = head + E(p.Count) + ", " + E(p.Kernel);
                    if (p.Combine != null) s += ", " + E(p.Combine);
                    if (p.Grain is int g) s += ", " + g.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return s + ")";
                }
            case AirParGuard g:
                return E(g.Count) + " >= " + g.Min.ToString(System.Globalization.CultureInfo.InvariantCulture) + " && " + Par + ".Workers > 1"
                    + string.Concat(g.Distinct.Select(d => " && " + E(d)));
            case AirReferenceEqualsGuard r:
                return "!global::System.Object.ReferenceEquals(" + E(r.W) + ", " + E(r.R) + ")";
            case AirParProbe pr:
                {
                    if (pr.Kind == AirProbeKind.Timestamp) return "global::System.Diagnostics.Stopwatch.GetTimestamp()";
                    string i = E(pr.I!), st = E(pr.Start!);
                    if (pr.Kind == AirProbeKind.Second) return $"{i} == {st} + 1";
                    return $"{i} - {st} >= 2 && (({i} - {st}) & ({i} - {st} - 1)) == 0 && {Par}.Offload({E(pr.T0!)}, {i} - {st} - 1, {E(pr.Count!)} - ({i} - {st}))";
                }
            case AirForkJoin f:
                return Par + ".Fork(" + Join(f.Branches) + ")";
            case AirForkable:
                return Par + ".Forkable";
            case AirRent r:
                return r.PoolCs + ".Rent(" + Join(r.Args) + ")";
            case AirRelease r:
                return r.PoolCs + ".Release(" + E(r.Target) + ")";
            case AirLength l:
                return E(l.Target) + "." + l.Member;
            case AirTripCount t:
                return (t.EndAsWritten ? E(t.End) : TripEnd(t.End)) + " - " + E(t.Start) + (t.Inclusive ? " + 1" : "");
            case AirOffset o:
                return E(o.Base) + " + " + E(o.Offset);
            case AirPresizeCount c:
                return "global::System.Math.Max(0, (" + E(c.To) + ") - (" + E(c.From) + ")" + (c.Inclusive ? " + 1" : "") + ")";
        }
        return null;
    }

    /// <summary>The range end left of `- start`: an additive or multiplicative operand keeps its C# (left-associative), anything
    /// looser (`c ? a : b`, `a ?? b`, comparisons, shifts) is parenthesized.</summary>
    static string TripEnd(MExpr e) =>
        e is MBinary { Op: AirBinOp.Add or AirBinOp.Sub or AirBinOp.Mul or AirBinOp.Div or AirBinOp.Mod } ? E(e) : P(e);

    /// <summary>
    /// C# for a typed helper member (Mir/AirDecls.Helpers.cs), spelled as the lowering used to write it:
    /// `private [static ]Ret name(T a, ...)` + body, or `internal static readonly T name = init;`.
    /// </summary>
    public static string Helper(AirHelperDecl d)
    {
        if (d.Field is { } f)
            return $"{Vis(f.Vis)} {(f.Static ? "static " : "")}{(f.ReadOnly ? "readonly " : "")}{f.Type.ToCs()} {f.Name} = {E(d.FieldInit!)};\n";
        var m = d.Method!;
        string attrs = string.Concat(d.Attributes.Select(a => "[" + a.Type.ToCs()
            + (a.Args.Count + a.Named.Count > 0 ? "(" + string.Join(", ", a.Args.Select(E).Concat(a.Named.Select(nv => nv.Name + " = " + E(nv.Value)))) + ")" : "") + "]\n"));
        string ps = string.Join(", ", m.Params.Select(x => x.Type.ToCs() + " " + x.Name));
        string head = $"{attrs}{Vis(m.Vis)} {(m.Is(AirMethodFlags.Static) ? "static " : "")}{(d.Unsafe ? "unsafe " : "")}{m.Ret.ToCs()} {m.Name}({ps})";
        if (d.ExprBody != null) return head + " => " + E(d.ExprBody) + ";\n";
        var p = new Printer("", 1);
        p.Stmts(m.Body!);
        return $"{head}\n{{\n{p}}}\n";
    }

    static string Vis(AirVis v) => v.ToString().ToLowerInvariant();
}
