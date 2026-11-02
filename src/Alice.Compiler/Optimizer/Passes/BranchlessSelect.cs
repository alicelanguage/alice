using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// `c ? a : b` over Int32/UInt32/Int64/UInt64 becomes `Branchless.U32(c, a, b)` (a setcc and a mask, no jump) when
/// both arms are cheap and cannot throw or have effects: names, literals, wrapping (`unchecked`) + - *, bitwise
/// operators and shifts over them. RyuJIT if-converts a ternary to `cmov` only outside loops, so a data-dependent
/// select in a loop (or in a small helper inlined into one) costs a mispredicted branch: kmeans' `min2`/`dst` (1.85G
/// branch misses -> 0.06G). Left as a ternary:
/// - `c ? 1 : 0`: the JIT already emits setcc.
/// - a conditional update `x = c ? v : x`: its condition is usually stable (an escape flag, a best-so-far), so the
///   predicted branch is cheaper, and a select puts the condition on the loop-carried chain of `x` (mandelbrot's
///   escape freeze `zr = e2 == 0 ? nzr : zr`: 4.3 s -> 8.9 s as a select).
/// Needs the type of the node (<see cref="MExpr.Type"/>); an untyped ternary is left as it is.
/// </summary>
public sealed class BranchlessSelect : MirRewriter, IMirPass
{
    public string Name => "branchless-select";

    /// <summary>Largest arm, in nodes: the select evaluates both arms, so a big arm costs more than the branch saves.</summary>
    const int MaxArm = 8;

    protected override MExpr Rewrite(MExpr e)
    {
        if (e is not MCond { Type: AirType.Prim { Nullable: false, Kind: var k } } c) return e;
        string? fn = k switch { PrimKind.Int => "I32", PrimKind.UInt => "U32", PrimKind.Long => "I64", PrimKind.ULong => "U64", _ => null };
        if (fn == null || Bit(c.A) && Bit(c.B)) return e;
        int na = 0, nb = 0;
        if (!Cheap(c.A, false, ref na) || !Cheap(c.B, false, ref nb)) return e;
        var sel = new MCall { Target = new MCode("global::Alice.Runtime.Branchless"), Name = fn, Args = { c.C, c.A, c.B }, Type = c.Type, Span = c.Span };
        selects[sel] = c;
        return sel;
    }

    readonly Dictionary<MCall, MCond> selects = new(ReferenceEqualityComparer.Instance);

    /// <summary>`x = c ? v : x` (or `x = c ? x : v`) goes back to the ternary: a conditional update.</summary>
    protected override void Visit(MStmt s)
    {
        if (s is MAssign { Op: "=", Target: MCode { Text: var x }, Value: MCall v } a && selects.TryGetValue(v, out var c)
            && (c.A is MCode { Text: var ta } && ta == x || c.B is MCode { Text: var tb } && tb == x))
            a.Value = c;
    }

    /// <summary>A 0 or 1 literal (`1U`, `0L`): `c ? 1 : 0` already compiles to setcc.</summary>
    static bool Bit(MExpr e) => e is MCode { Text: var t } && Literal(t, out long v) && v is 0 or 1;

    /// <summary>Side-effect free and non-throwing: a name, a literal, or wrapping arithmetic and bit operators over them.</summary>
    static bool Cheap(MExpr e, bool wrapping, ref int n)
    {
        if (++n > MaxArm) return false;
        switch (e)
        {
            case MCode { Text: var t }:
                return Ident(t) || Literal(t, out _);
            case MCall { Target: null, Name: "unchecked", Args: [var inner] }:
                return Cheap(inner, true, ref n);
            case MBinary { Op: AirBinOp.BitAnd or AirBinOp.BitOr or AirBinOp.BitXor or AirBinOp.Shl or AirBinOp.Shr or AirBinOp.UShr } b:
                return Cheap(b.L, wrapping, ref n) && Cheap(b.R, wrapping, ref n);
            case MBinary { Op: AirBinOp.Add or AirBinOp.Sub or AirBinOp.Mul } b when wrapping || !b.Checked:
                return Cheap(b.L, true, ref n) && Cheap(b.R, true, ref n);
            case MUnary { Op: AirUnOp.BitNot } u:
                return Cheap(u.E, wrapping, ref n);
            default:
                return false;
        }
    }

    /// <summary>A local, parameter or constant name (no member access, index or call: those can throw).</summary>
    static bool Ident(string t) => t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(ch => char.IsLetterOrDigit(ch) || ch == '_')
        && t is not ("null" or "true" or "false" or "default");

    static bool Literal(string t, out long v) => MirFacts.IntLiteral(new MCode(t.TrimEnd('U', 'u', 'L', 'l')), out v);
}
