using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// `case` without Roslyn. The lowerer turns `case` into an if/else-if chain over one subject local; a chain whose
/// arms test only `subject == constant` (or an `||` of them) becomes:
/// - integers and chars: an <see cref="AirSwitch"/> jump table when at least 3 labels cover at most twice as many
///   slots (dense); otherwise the compare chain stays;
/// - strings (at least 4 labels): a null check, a switch on the length, a `string.Equals` chain per length that
///   stores the arm index, then a dense switch on that index (deterministic, no hashing; arm bodies appear once);
/// - types (`subject is T x` arms): the chain stays an isinst chain, which PatternTemps makes explicit.
/// An arm that tests anything else ends the switchable prefix; the rest of the chain becomes the default.
/// </summary>
public sealed class SwitchLower(IlLowerContext? ctx = null) : IlPass(ctx)
{
    public override string Name => "il-switch";

    const int MinIntLabels = 3, MinStringLabels = 4, MaxTable = 4096;

    readonly HashSet<MIf> tails = new(ReferenceEqualityComparer.Instance);

    /// <summary>Only chain heads are lowered: mark every `if` that is the sole statement of another one's else.</summary>
    protected override void Before(List<MStmt> body) => new TailMarker(tails).Run(body);

    sealed class TailMarker(HashSet<MIf> tails) : MirRewriter
    {
        protected override void Visit(MStmt s)
        {
            if (s is MIf { Else: [MIf next] }) tails.Add(next);
        }
    }

    sealed record Arm(List<MExpr> Labels, List<MStmt> Body);

    protected override List<MStmt>? Lower(MStmt s)
    {
        if (s is not MIf head || tails.Contains(head)) return null;
        string? subj = null;
        AirType? subjType = null;
        var arms = new List<Arm>();
        List<MStmt>? dflt = null;
        for (MIf? cur = head; cur != null;)
        {
            var labels = new List<MExpr>();
            if (!Labels(cur.Cond, ref subj, ref subjType, labels))
            {
                dflt = [cur];
                break;
            }
            arms.Add(new Arm(labels, cur.Then));
            if (cur.Else is [MIf next]) cur = next;
            else { dflt = cur.Else; cur = null; }
        }
        if (subj == null || arms.Count == 0) return null;
        // a fresh node per use: passes rewrite nodes in place, so the tree never shares one
        MExpr Subject() => Local(subj, subjType);
        if (IlTypes.IsIntegral(subjType)) return Ints(Subject, arms, dflt);
        if (subjType is AirType.Prim { Kind: PrimKind.String }) return Strings(Subject, arms, dflt);
        return null;
    }

    /// <summary>Collects the constants of `subj == K || subj == K2 ...`; false when the condition has another shape.</summary>
    static bool Labels(MExpr cond, ref string? subj, ref AirType? subjType, List<MExpr> labels)
    {
        if (cond is MBinary { Op: AirBinOp.Or } or)
            return Labels(or.L, ref subj, ref subjType, labels) && Labels(or.R, ref subj, ref subjType, labels);
        if (cond is not MBinary { Op: AirBinOp.Eq } eq) return false;
        var (s, k) = SubjectName(eq.L) != null ? (eq.L, eq.R) : (eq.R, eq.L);
        if (SubjectName(s) is not { } n || !IlTypes.Known(s.Type)) return false;
        if (IlTypes.IntLiteral(k) == null && IlTypes.StringLiteral(k) == null) return false;
        if (subj != null && (subj != n || !subjType!.Same(s.Type!))) return false;
        subj = n;
        subjType = s.Type;
        labels.Add(k);
        return true;
    }

    static string? SubjectName(MExpr e) => e switch
    {
        AirLocalRef l => l.Name,
        MCode c when IsIdent(c.Text) => c.Text,
        _ => null,
    };

    List<MStmt>? Ints(Func<MExpr> subject, List<Arm> arms, List<MStmt>? dflt)
    {
        var slots = new SortedDictionary<long, int>();
        for (int a = 0; a < arms.Count; a++)
            foreach (var l in arms[a].Labels)
                if (IlTypes.IntLiteral(l) is not { } v) return null;
                else slots.TryAdd(v, a);   // a repeated label belongs to the first arm, as in the chain
        if (slots.Count < MinIntLabels) return null;
        long min = slots.Keys.First(), span = slots.Keys.Last() - min + 1;
        if (span > 2L * slots.Count || span > MaxTable) return null;
        var sw = new AirSwitch { Value = subject(), Min = min, Cases = arms.Select(a => a.Body).ToList(), Default = dflt };
        for (long v = min; v < min + span; v++) sw.Table.Add(slots.TryGetValue(v, out int a) ? a : -1);
        return [sw];
    }

    List<MStmt>? Strings(Func<MExpr> subject, List<Arm> arms, List<MStmt>? dflt)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var byLength = new SortedDictionary<int, List<(string Lit, MExpr Label, int Arm)>>();
        for (int a = 0; a < arms.Count; a++)
            foreach (var l in arms[a].Labels)
            {
                if (IlTypes.StringLiteral(l) is not { } s) return null;
                if (!seen.Add(s)) continue;
                if (!byLength.TryGetValue(s.Length, out var g)) byLength[s.Length] = g = new();
                g.Add((s, l, a));
            }
        if (seen.Count < MinStringLabels) return null;
        string k = Ctx.Temp("arm");
        MExpr Len() => Member(subject(), "Length", IlTypes.Int);

        // per length: if (string.Equals(s, "ab")) arm = 0; else if (...) arm = 1; ...
        List<MStmt> Group(List<(string Lit, MExpr Label, int Arm)> g)
        {
            List<MStmt>? chain = null;
            for (int i = g.Count - 1; i >= 0; i--)
            {
                var test = Call(new AirTypeRef(IlTypes.Str), "Equals", IlTypes.Bool, subject(), g[i].Label);
                chain = [new MIf { Cond = test, Then = [new MAssign { Target = Local(k, IlTypes.Int), Value = Const(g[i].Arm) }], Else = chain }];
            }
            return chain!;
        }

        var groups = byLength.ToList();
        List<MStmt> dispatch;
        long span = groups[^1].Key - groups[0].Key + 1;
        if (groups.Count >= MinIntLabels && span <= 2L * groups.Count)
        {
            var lsw = new AirSwitch { Value = Len(), Min = groups[0].Key };
            var index = groups.Select((g, i) => (g.Key, i)).ToDictionary(p => p.Key, p => p.i);
            for (long v = groups[0].Key; v <= groups[^1].Key; v++) lsw.Table.Add(index.TryGetValue((int)v, out int i) ? i : -1);
            lsw.Cases = groups.Select(g => Group(g.Value)).ToList();
            dispatch = [lsw];
        }
        else
        {
            List<MStmt>? chain = null;
            for (int i = groups.Count - 1; i >= 0; i--)
                chain = [new MIf { Cond = new MBinary { Op = AirBinOp.Eq, L = Len(), R = Const(groups[i].Key), Type = IlTypes.Bool }, Then = Group(groups[i].Value), Else = chain }];
            dispatch = chain!;
        }
        var notNull = new MBinary { Op = AirBinOp.Ne, L = subject(), R = Const(null), Type = IlTypes.Bool };
        var sw = new AirSwitch { Value = Local(k, IlTypes.Int), Min = 0, Cases = arms.Select(a => a.Body).ToList(), Default = dflt };
        for (int a = 0; a < arms.Count; a++) sw.Table.Add(a);
        return [Decl(k, IlTypes.Int, Const(-1)), new MIf { Cond = notNull, Then = dispatch }, sw];
    }
}
