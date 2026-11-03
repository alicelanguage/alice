using Alice.Compiler.Backend.Il.Opt;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;
using Options = Alice.Compiler.Backend.Il.Opt.IlOpt;

namespace Alice.Compiler.Air.Passes.IlOpt;

/// <summary>
/// Loop-carried store-to-load forwarding (IL backend only, after <see cref="InBounds"/>): in a `while` loop whose
/// counter `j` steps by a constant `k` at the end of the body, a load `a[e]` that reads the element the previous
/// iteration stored (`a[e'] = v` with e'(j - k) == e(j), both linear in the locals) is replaced by a local carrying
/// v. The DP recurrence `rows[co + j + 1] = min(rows[co + j] + 1, ...)` (editdist) then runs through a register
/// instead of a store-forwarding round trip through memory on every iteration. The store itself stays.
/// <para>Proof obligations, all checked or the loop is left alone: `a` is a local initialized once with a fresh array
/// (`Own.Rent`, `new T[n]`) and used only as an index target, `.Length` or the pool release (no alias, no escape,
/// so no call can write it); the body has exactly one store to `a`, unconditional and at its top level, and no
/// other array store; no `continue`, nested loop, lambda, `ref` argument or node this pass does not know; the
/// locals in e other than `j` are not assigned in the body and `j` only by the final step; index arithmetic is
/// unchecked (linear equality holds mod 2^32); the replaced load was proven in bounds by InBounds, so the pre-loop
/// load of the first element (guarded by the side-effect-free loop condition) cannot throw where the original
/// would not.</para>
/// </summary>
public sealed class StoreForward : IMirPass
{
    public string Name => "il-store-forward";

    /// <summary>Loads replaced by the last <see cref="Run"/>.</summary>
    public int Forwarded { get; private set; }

    int counter;
    HashSet<string> arrays = [];

    public void Run(List<MStmt> body)
    {
        if (!Options.On(IlOptPass.Forward)) return;
        var scan = new Scan();
        if (!scan.Method(body)) return;
        arrays = scan.Fresh();
        if (arrays.Count == 0) return;
        Visit(body);
    }

    void Visit(List<MStmt> stmts)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            switch (stmts[i])
            {
                case MWhile w: Visit(w.Body); break;
                case MFor f: Visit(f.Body); break;
                case MIf mi: Visit(mi.Then); if (mi.Else != null) Visit(mi.Else); break;
                case MBlock b: Visit(b.Body); break;
                case MTry t: Visit(t.Body); break;
            }
            if (stmts[i] is MWhile loop && Forward(loop) is { } pre)
            {
                stmts.InsertRange(i, pre);
                i += pre.Count;
            }
        }
    }

    // ---------------------------------------------------------------- one loop

    List<MStmt>? Forward(MWhile w)
    {
        var body = w.Body;
        if (body.Count < 2 || !Pure(w.Cond)) return null;
        // the counter: the last statement is `j = j + k` / `j += k` with a constant k != 0
        if (Step(body[^1]) is not (string j, long k) || k == 0) return null;
        var assigned = new HashSet<string>();
        int stores = 0;
        MAssign? store = null;
        foreach (var s in body.Take(body.Count - 1))
        {
            if (!Simple(s, assigned, ref stores, ref store, top: true)) return null;
        }
        if (stores != 1 || store == null || assigned.Contains(j)) return null;
        var target = (MIndex)store.Target;
        string a = Local(target.Target)!;
        if (store.Op != "=" || target.Args.Count != 1 || Lin(target.Args[0]) is not { } es) return null;
        if (es.Vars.Keys.Any(v => v != j && assigned.Contains(v))) return null;
        if (es.Vars.GetValueOrDefault(j) is not (1 or -1)) return null;
        // the loads that read the previous iteration's element: e(j) == e'(j - k)
        var shifted = es.Shift(j, -k);
        var loads = new List<MIndex>();
        foreach (var s in body.Take(body.Count - 1)) Collect(s, a, shifted, loads);
        if (loads.Count == 0 || !loads.All(UncheckedIndex.Proven)) return null;
        var elem = (target.Target.Type as AirType.Array)?.Elem;
        if (elem is not AirType.Prim) return null;

        string carry = "__fwd" + counter, next = "__fwdv" + counter;
        counter++;
        var load = loads[0];
        foreach (var s in body) Replace(s, loads, carry, elem);
        // `a[e'] = v` -> `next = v; a[e'] = next`, and before the step `carry = next`
        int at = body.IndexOf(store);
        body.Insert(at, new MDecl { Name = next, TypeCs = elem.ToCs(), Init = store.Value, Span = store.Span });
        store.Value = new AirLocalRef(next, elem);
        body.Insert(body.Count - 1, new MAssign { Target = new AirLocalRef(carry, elem), Value = new AirLocalRef(next, elem) });
        var first = new MIndex { Target = Clone(load.Target), Args = [Clone(load.Args[0])], Type = load.Type, Span = load.Span };
        UncheckedIndex.Prove(first);
        Forwarded += loads.Count;
        return
        [
            new MDecl { Name = carry, TypeCs = elem.ToCs(), Init = new AirDefault(elem) },
            new MIf { Cond = Clone(w.Cond), Then = [new MAssign { Target = new AirLocalRef(carry, elem), Value = first }] },
        ];
    }

    /// <summary>`j = j + k`, `j = j - k`, `j += k`, `j -= k` on a local, k an integer literal.</summary>
    static (string, long)? Step(MStmt s)
    {
        if (s is not MAssign { Target: var t } asg || Local(t) is not { } j) return null;
        if (asg.Op is "+=" or "-=" && Int(asg.Value) is { } k1) return (j, asg.Op == "+=" ? k1 : -k1);
        if (asg.Op == "=" && asg.Value is MBinary { Op: AirBinOp.Add or AirBinOp.Sub } b && Local(b.L) == j && Int(b.R) is { } k2)
            return (j, b.Op == AirBinOp.Add ? k2 : -k2);
        return null;
    }

    /// <summary>A body statement this pass can reason about; collects assigned locals and array stores.</summary>
    bool Simple(MStmt s, HashSet<string> assigned, ref int stores, ref MAssign? store, bool top)
    {
        switch (s)
        {
            case MDecl d:
                assigned.Add(d.Name);
                return d.Init == null || Known(d.Init);
            case MExprStmt es:
                return Known(es.E);
            case MAssign asg:
                if (!Known(asg.Value)) return false;
                if (Local(asg.Target) is { } n) { assigned.Add(n); return true; }
                if (asg.Target is MIndex ix && Known(ix))
                {
                    stores++;
                    if (!top || Local(ix.Target) is not { } arr || !arrays.Contains(arr)) return false;
                    store = asg;
                    return true;
                }
                return false;
            case MIf mi:
                if (!Known(mi.Cond)) return false;
                foreach (var x in mi.Then) if (!Simple(x, assigned, ref stores, ref store, top: false)) return false;
                foreach (var x in mi.Else ?? []) if (!Simple(x, assigned, ref stores, ref store, top: false)) return false;
                return true;
            case MBreak:
                return true;
            case MReturn r:
                return r.Value == null || Known(r.Value);
            default:
                return false;   // continue, loops, try, blocks, anything else
        }
    }

    /// <summary>Expression kinds the analysis sees through completely (no hidden children, no writes).</summary>
    static bool Known(MExpr e) => e switch
    {
        AirLocalRef { Narrowed: false } or AirParamRef or AirConst or AirDefault => true,
        MCode c => c.GetType() == typeof(MCode) && IsIdent(c.Text),
        MBinary b => Known(b.L) && Known(b.R),
        MUnary u => u.Op is AirUnOp.Neg or AirUnOp.Not or AirUnOp.BitNot && Known(u.E),
        MCond c => Known(c.C) && Known(c.A) && Known(c.B),
        MIndex ix => Known(ix.Target) && ix.Args.All(Known),
        MMember m => Known(m.Target),
        MCast c => Known(c.E),
        MCall c => (c.Target == null || Known(c.Target) || TypeName(c.Target)) && c.Args.All(a => a is not MArg && Known(a)),
        _ => false,
    };

    /// <summary>Side-effect-free condition (evaluated once more before the loop).</summary>
    static bool Pure(MExpr e) => e switch
    {
        AirLocalRef { Narrowed: false } or AirParamRef or AirConst => true,
        MCode c => c.GetType() == typeof(MCode) && IsIdent(c.Text),
        MBinary b => Pure(b.L) && Pure(b.R),
        MUnary u => u.Op is AirUnOp.Not or AirUnOp.Neg or AirUnOp.BitNot && Pure(u.E),
        MMember { Name: "Length" } m => Local(m.Target) != null,
        _ => false,
    };

    void Collect(MStmt s, string a, LinForm want, List<MIndex> into)
    {
        void E(MExpr e)
        {
            if (e is MIndex ix && Local(ix.Target) == a && ix.Args.Count == 1 && Lin(ix.Args[0]) is { } l && l.Same(want)) into.Add(ix);
            foreach (var c in MirFacts.Children(e)) E(c);
        }
        switch (s)
        {
            case MDecl d: if (d.Init != null) E(d.Init); break;
            case MExprStmt es: E(es.E); break;
            case MAssign asg: E(asg.Value); if (asg.Target is MIndex t) foreach (var x in t.Args) E(x); break;
            case MIf mi: E(mi.Cond); foreach (var x in mi.Then) Collect(x, a, want, into); foreach (var x in mi.Else ?? []) Collect(x, a, want, into); break;
            case MReturn { Value: { } v }: E(v); break;
        }
    }

    static void Replace(MStmt s, List<MIndex> loads, string carry, AirType elem)
    {
        new Replacer(loads, carry, elem).Run([s]);
    }

    sealed class Replacer(List<MIndex> loads, string carry, AirType elem) : MirRewriter
    {
        protected override MExpr Rewrite(MExpr e) => e is MIndex ix && loads.Contains(ix) ? new AirLocalRef(carry, elem) { Span = ix.Span } : e;
    }

    static MExpr Clone(MExpr e) => e switch
    {
        AirLocalRef l => new AirLocalRef(l.Name, l.Type) { Span = l.Span },
        AirParamRef p => new AirParamRef(p.Name, p.Type) { Span = p.Span },
        AirConst c => c,   // immutable leaf
        MCode c when c.GetType() == typeof(MCode) => new MCode(c.Text) { Type = c.Type, Span = c.Span },
        MBinary b => new MBinary { Op = b.Op, Checked = b.Checked, L = Clone(b.L), R = Clone(b.R), Type = b.Type, Span = b.Span },
        MUnary u => new MUnary { Op = u.Op, Checked = u.Checked, E = Clone(u.E), Postfix = u.Postfix, Type = u.Type, Span = u.Span },
        MMember m => new MMember { Target = Clone(m.Target), Name = m.Name, Safe = m.Safe, Type = m.Type, Span = m.Span },
        MCall { Name: "unchecked", Target: null, Args: [var x] } c => new MCall { Name = "unchecked", Args = { Clone(x) }, Type = c.Type, Span = c.Span },
        _ => throw new InvalidOperationException("StoreForward.Clone: " + e.GetType().Name),
    };

    // ---------------------------------------------------------------- linear index forms

    /// <summary>Σ coef·var + c over int locals, from unchecked `+ -` (and `* literal`); null for anything else.</summary>
    sealed record LinForm(Dictionary<string, long> Vars, long C)
    {
        public LinForm Shift(string v, long by) => new(new(Vars), C + Vars.GetValueOrDefault(v) * by);
        public bool Same(LinForm o) => C == o.C && Vars.Count(kv => kv.Value != 0) == o.Vars.Count(kv => kv.Value != 0)
            && Vars.All(kv => kv.Value == 0 || o.Vars.GetValueOrDefault(kv.Key) == kv.Value);
    }

    static LinForm? Lin(MExpr e)
    {
        switch (e)
        {
            case MCall { Name: "unchecked", Target: null, Args: [var x] }: return Lin(x);
            case AirConst when Int(e) is { } k: return new(new(), k);
            case MBinary { Checked: false, Op: AirBinOp.Add or AirBinOp.Sub } b when Lin(b.L) is { } l && Lin(b.R) is { } r:
                {
                    long sign = b.Op == AirBinOp.Add ? 1 : -1;
                    var d = new Dictionary<string, long>(l.Vars);
                    foreach (var (v, c) in r.Vars) d[v] = d.GetValueOrDefault(v) + sign * c;
                    return new(d, l.C + sign * r.C);
                }
            case MBinary { Checked: false, Op: AirBinOp.Mul } m when Int(m.R) is { } f && Lin(m.L) is { } ml:
                return new(ml.Vars.ToDictionary(kv => kv.Key, kv => kv.Value * f), ml.C * f);
        }
        if (Local(e) is { } n && IsIntType(e.Type)) return new(new() { [n] = 1 }, 0);
        return null;
    }

    static bool IsIntType(AirType? t) => t is AirType.Prim { Kind: PrimKind.Int };

    static long? Int(MExpr e) => e is AirConst { Kind: AirConstKind.Int } c && long.TryParse(c.Literal.TrimEnd('L', 'l', 'U', 'u'), out var v) ? v : null;

    static string? Local(MExpr e) => e switch
    {
        AirLocalRef { Narrowed: false } l => l.Name,
        AirParamRef p => p.Name,
        MCode c when c.GetType() == typeof(MCode) && IsIdent(c.Text) => c.Text,
        _ => null,
    };

    /// <summary><paramref name="name"/> as a whole word in opaque text.</summary>
    static bool Word(string text, string name)
    {
        for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            int j = i + name.Length;
            bool before = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
            bool after = j >= text.Length || !(char.IsLetterOrDigit(text[j]) || text[j] == '_');
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>A static call target spelled as a type name (`global::Alice.Runtime.Branchless`).</summary>
    static bool TypeName(MExpr e) => e is MCode c && c.GetType() == typeof(MCode)
        && c.Text.Length > 0 && c.Text.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or ':' or '<' or '>' or ',' or ' ');

    static bool IsIdent(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    // ---------------------------------------------------------------- method scan: fresh, non-escaping arrays

    /// <summary>Which array locals are fresh and never escape; bails (false) on node kinds it cannot see into.</summary>
    sealed class Scan
    {
        readonly Dictionary<string, int> decls = new();
        readonly HashSet<string> fresh = [], bad = [];

        public HashSet<string> Fresh() => [.. fresh.Where(n => !bad.Contains(n) && decls.GetValueOrDefault(n) == 1)];

        public bool Method(List<MStmt> body)
        {
            try { Stmts(body); return true; }
            catch (InvalidOperationException) { return false; }
        }

        void Stmts(List<MStmt>? b) { foreach (var s in b ?? []) Stmt(s); }

        void Stmt(MStmt s)
        {
            switch (s)
            {
                case MDecl d:
                    decls[d.Name] = decls.GetValueOrDefault(d.Name) + 1;
                    if (d.Init is AirRent or MNewArray { Items: null } || d.Init is MCall { Name: "Rent", Target: MCode { Text: var tx } } && tx.StartsWith("global::Alice.Runtime.Own<", StringComparison.Ordinal))
                    {
                        fresh.Add(d.Name);
                        if (d.Init is AirRent r) foreach (var x in r.Args) E(x);
                        else if (d.Init is MNewArray na && na.Size != null) E(na.Size);
                        else if (d.Init is MCall rc) foreach (var x in rc.Args) E(x);
                    }
                    else if (d.Init != null) E(d.Init);
                    break;
                case MAssign a:
                    if (Local(a.Target) is { } n) bad.Add(n);   // reassigned: not this pass's array
                    else if (a.Target is MIndex ix) { Target(ix.Target); foreach (var x in ix.Args) E(x); }
                    else E(a.Target);
                    E(a.Value);
                    break;
                case MExprStmt es: E(es.E); break;
                case MIf i: E(i.Cond); Stmts(i.Then); Stmts(i.Else); break;
                case MWhile w: E(w.Cond); Stmts(w.Body); break;
                case MFor f: E(f.Start); E(f.End); Stmts(f.Body); break;
                case MReturn r: if (r.Value != null) E(r.Value); break;
                case MBlock b: Stmts(b.Body); break;
                case MBreak or MContinue: break;
                case MTry t: Stmts(t.Body); foreach (var c in t.Catches) Stmts(c.Body); Stmts(t.Finally); break;
                case MThrow th: E(th.Value); break;
                default: throw new InvalidOperationException();
            }
        }

        /// <summary>An index target: a plain local is fine (not an escape).</summary>
        void Target(MExpr t)
        {
            if (Local(t) == null) E(t);
        }

        void E(MExpr e)
        {
            switch (e)
            {
                case MIndex ix: Target(ix.Target); foreach (var x in ix.Args) E(x); return;
                case MMember { Name: "Length" or "size" } m when Local(m.Target) != null: return;
                case AirRelease r when Local(r.Target) != null: return;
                case AirLocalRef or AirParamRef:
                    bad.Add(Local(e)!);   // any other use of the name: may escape or alias
                    return;
                case MCode c when c.GetType() == typeof(MCode):
                    if (IsIdent(c.Text)) bad.Add(c.Text);
                    else foreach (var n in fresh) if (Word(c.Text, n)) bad.Add(n);
                    return;
                case AirConst or AirDefault or AirNull: return;
                case MBinary or MUnary or MCond or MCall or MMember or MCast or MIs or MTuple or MInterp or MNew or MNewArray or MPair or MArg or MInvoke:
                    foreach (var c in MirFacts.Children(e)) E(c);
                    return;
                default:
                    throw new InvalidOperationException();   // lambdas, parallel, arena, closures: not modelled
            }
        }
    }
}
