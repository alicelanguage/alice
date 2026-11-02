namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the IL-lowering helper nodes (Mir/MExpr.Lower.cs).</summary>
public sealed partial class MirDump
{
    /// <summary>`(seq` + the statements one level deeper + `) value)`; `(as T e)`.</summary>
    string? CoreLower(MExpr e)
    {
        switch (e)
        {
            case AirSeq q:
                {
                    var inner = new MirDump { ind = ind + 1 };
                    inner.Stmts(q.Pre);
                    return "(seq\n" + inner.sb + new string(' ', ind * 2) + ") " + E(q.Value) + ")";
                }
            case AirAs a:
                return $"(as {a.TypeCs} {E(a.E)})";
        }
        return null;
    }

    /// <summary>`switch value min N table [..]`, then `case k` / `default` bodies.</summary>
    bool StmtLower(MStmt s, string at)
    {
        if (s is not AirSwitch sw) return false;
        Line($"switch {E(sw.Value)} min {sw.Min} table [{string.Join(", ", sw.Table)}]{at}");
        for (int k = 0; k < sw.Cases.Count; k++) Nested("case " + k, sw.Cases[k]);
        if (sw.Default != null) Nested("default", sw.Default);
        return true;
    }
}
