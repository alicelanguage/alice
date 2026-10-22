using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Row flattening (BendRT's flat blocks, at the source level). An array of fixed-width literal rows
/// <code>
/// points = Array(Array(Int32)).new(n)
/// for i in 0...n
///   points[i] = [x, y]
/// end
/// </code>
/// whose rows never escape is stored as one `int[n * 2]`: `points[i][j]` reads `points[i * 2 + j]`.
/// One allocation instead of n + 1, rows contiguous in memory, no pointer chasing, nothing for the GC to trace.
///
/// Sound only when the jagged and flat forms cannot be told apart:
/// - rows are written once each by a loop over exactly `0...n` right after the allocation (no nil rows),
///   all with the same literal width k;
/// - every other use is `points[i][j]` (read, write, compound) or `points.size`;
/// - the column index stays checked against k, so `points[i][k]` raises instead of reading the next row.
/// </summary>
public sealed partial class Lowerer
{
    const int MaxFlatWidth = 16;

    sealed record FlatRow(int Width, string Elem);

    Dictionary<LocalSym, FlatRow> flatRows = new();

    /// <summary>C# type of a local as it is stored (flattened rows are `T[]`, not `T[][]`).</summary>
    string LocalTypeCs(LocalSym l) => flatRows.TryGetValue(l, out var f) ? f.Elem + "[]" : TypeCs(l.Type);

    void FindFlatRows(List<Stmt> body)
    {
        flatRows = new();
        if (Method is { IsAsync: true } || Method is { UsesYield: true }) return;
        for (int k = 0; k + 1 < body.Count; k++)
        {
            if (body[k] is not Assign { Declares: { } l, Op: "=" } decl || Unwrap(decl.Value) is not NewExpr { Block: null, Args.Count: 1 } alloc
                || alloc.Type is not { Kind: "array", Elem: { Kind: "array", Elem: { } el } } || !Unmanaged.Contains(el.Cs)) continue;
            if (body[k + 1] is not ForStmt { Iter: RangeExpr { Excl: true, A: LitExpr { Text: "0" } } range, Locals.Count: 1 } init || !SameSize(range.B, alloc.Args[0].Value!, body)) continue;
            var row = init.Body.OfType<Assign>().FirstOrDefault(a => a.Op == "=" && a.Target is IndexExpr { Target: NameExpr { Sym: LocalSym t }, Args: [{ Value: NameExpr { Sym: LocalSym iv } }] } && t == l && iv == init.Locals[0]);
            if (row?.Value is not ArrayLit { Items.Count: > 0 and <= MaxFlatWidth } lit) continue;
            if (Walk(init.Body).OfType<NameExpr>().Count(n => n.Sym == l) != 1) continue;   // the row write is the loop's only use
            if (FlatUses(body, l, decl, row)) flatRows[l] = new FlatRow(lit.Items.Count, el.Cs);
        }
        foreach (var l in flatRows.Keys)   // `n = points.size` must not alias the flat length
            foreach (var alias in lengthAlias.Where(kv => kv.Value == l.CsName + ".Length").Select(kv => kv.Key).ToList())
                lengthAlias.Remove(alias);
    }

    /// <summary>The loop bound is the allocation size: the same literal, or the same local never reassigned.</summary>
    static bool SameSize(Syntax.Expr bound, Syntax.Expr size, List<Stmt> body) => (bound, size) switch
    {
        (LitExpr a, LitExpr b) => a.Kind == "int" && a.Text == b.Text,
        (NameExpr { Sym: LocalSym a }, NameExpr { Sym: LocalSym b }) => a == b
            && WalkStmts(body).Count(s => s is Assign { Declares: { } d } && d == a || s is Assign { Target: NameExpr { Sym: LocalSym t } } && t == a) <= 1,
        _ => false,
    };

    bool FlatUses(List<Stmt> body, LocalSym l, Assign decl, Assign row)
    {
        int mentions = 0, allowed = 0;
        foreach (var e in Walk(body))
        {
            switch (e)
            {
                case NameExpr { Sym: LocalSym x } when x == l: mentions++; break;
                case IndexExpr { Args.Count: 1, Target: IndexExpr { Args.Count: 1, Target: NameExpr { Sym: LocalSym x } } } when x == l: allowed++; break;
                case CallExpr { Res: "ext", Args.Count: 0, Block: null, Recv: NameExpr { Sym: LocalSym x } } c when x == l && Binder.Mangle(c.Name) is "size" or "length": allowed++; break;
                case LambdaExpr lam when WalkExpr(lam).OfType<NameExpr>().Any(n => n.Sym == l): return false;
                case CallExpr { Block: { } blk } when Walk(blk.Body).OfType<NameExpr>().Any(n => n.Sym == l): return false;
            }
        }
        foreach (var s in WalkStmts(body))
        {
            if (s is Assign { Target: NameExpr { Sym: LocalSym t } } a && t == l && a != decl) return false;   // reassigned
            if (s == decl || s == row) allowed++;
        }
        return mentions == allowed;
    }

    /// <summary>`points = Array(Array(T)).new(n)` becomes one `T[n * width]`.</summary>
    bool TryFlatDecl(List<MStmt> o, Assign a)
    {
        if (a.Declares is not { } l || !flatRows.TryGetValue(l, out var f) || Unwrap(a.Value) is not NewExpr ne) return false;
        var n = Expr(ne.Args[0].Value!);
        o.Add(new MDecl { Name = l.CsName, TypeCs = f.Elem + "[]", Init = new MNewArray { ElemCs = f.Elem, Size = new MBinary { Op = "*", L = n, R = new MCode(f.Width.ToString()) } } });
        return true;
    }

    /// <summary>`points[i] = [e0, e1]` writes the row's cells in order.</summary>
    bool TryFlatRowWrite(List<MStmt> o, Assign a)
    {
        if (a.Target is not IndexExpr { Target: NameExpr { Sym: LocalSym l }, Args: [var idx] } || !flatRows.TryGetValue(l, out var f) || a.Value is not ArrayLit lit) return false;
        string baseIx = Temp("row");
        o.Add(new MDecl { Name = baseIx, TypeCs = "int", Init = new MBinary { Op = "*", L = Expr(idx.Value!), R = new MCode(f.Width.ToString()) } });
        for (int c = 0; c < lit.Items.Count; c++)
            o.Add(new MAssign { Target = new MIndex { Target = new MCode(l.CsName), Args = { new MBinary { Op = "+", L = new MCode(baseIx), R = new MCode(c.ToString()) } } }, Value = Expr(lit.Items[c]) });
        return true;
    }

    /// <summary>`points[i][j]` reads cell i * width + j; a non-literal column is checked against the width.</summary>
    MExpr? TryFlatIndex(IndexExpr ix)
    {
        if (ix is not { Args: [var col], Target: IndexExpr { Target: NameExpr { Sym: LocalSym l }, Args: [var rowIx] } } || !flatRows.TryGetValue(l, out var f)) return null;
        MExpr column = col.Value is LitExpr { Kind: "int" } cl && int.TryParse(cl.Text, out int c) && c >= 0 && c < f.Width
            ? new MCode(c.ToString())
            : new MCall { Target = new MCode("Prelude"), Name = "column", Args = { Expr(col.Value!), new MCode(f.Width.ToString()) } };
        var cell = new MBinary { Op = "+", L = new MBinary { Op = "*", L = Expr(rowIx.Value!), R = new MCode(f.Width.ToString()) }, R = column };
        return new MIndex { Target = new MCode(l.CsName), Args = { cell } };
    }

    /// <summary>`points.size` is the row count.</summary>
    MExpr? TryFlatSize(CallExpr c) =>
        c.Recv is NameExpr { Sym: LocalSym l } && flatRows.TryGetValue(l, out var f)
            ? new MBinary { Op = "/", L = new MMember { Target = new MCode(l.CsName), Name = "Length" }, R = new MCode(f.Width.ToString()) }
            : null;
}
