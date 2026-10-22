using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// Range analysis of typed counted loops whose body never writes the counter. The counter i of
/// `for (T i = start; i cmp end; i++)` lies in [start, hi], where hi is end - 1 for `&lt;` and end for `&lt;=`,
/// and an `.Length` / `.Count` bound is at most Array.MaxLength. Inside that range:
/// - the step i + 1 cannot overflow, so it drops `add.ovf`;
/// - `i + k` and `i - k` (literal k) that provably stay in T drop their check too (`row[j - 1]`, `prev[j + 1]`).
/// Every other integer operation stays checked (ADR 0013).
/// </summary>
public sealed class InductionVariables : MirRewriter, IMirPass
{
    public string Name => "induction";

    /// <summary>Array.MaxLength: the largest `.Length` / `.Count` of any array, string or list.</summary>
    const long MaxLength = 0x7FFFFFC7;

    protected override void Visit(MStmt s)
    {
        if (s is not MFor { Step: "++", Cmp: "<" or "<=", VarType: "int" or "long" } f || MirFacts.Writes(f.Body, f.Var)) return;
        long min = f.VarType == "int" ? int.MinValue : long.MinValue, max = f.VarType == "int" ? int.MaxValue : long.MaxValue;
        long? lo = MirFacts.IntLiteral(f.Start, out long st) ? st : null;
        long? bound = MirFacts.IntLiteral(f.End, out long en) ? en : f.End is MMember { Name: "Length" or "Count" } ? MaxLength : null;
        long hi = f.Cmp == "<" ? (bound ?? max) - 1 : bound ?? max;
        if (hi < max) f.Step = $" = unchecked({f.Var} + 1)";
        new Offsets(f.Var, lo ?? min, hi, min, max).Run(f.Body);
    }

    /// <summary>Rewrites `i ± k` inside the loop body when [lo, hi] ± k stays inside the counter type.</summary>
    sealed class Offsets(string var, long lo, long hi, long min, long max) : MirRewriter
    {
        protected override MExpr Rewrite(MExpr e)
        {
            if (e is MBinary { Op: "+" or "-", L: MCode { Text: var t } } b && t == var && MirFacts.IntLiteral(b.R, out long k) && k >= 0)
            {
                bool safe = b.Op == "+" ? hi <= max - k : lo >= min + k;
                if (safe) return new MCall { Name = "unchecked", Args = { b } };
            }
            return e;
        }
    }
}
