using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Idiom rewrites with identical semantics but less work than the code a typical C# developer writes:
/// - `d[k] = d.GetValueOrDefault(k) + e` -> one hash probe (CollectionsMarshal.GetValueRefOrAddDefault)
/// - `d[k] += e`                         -> one hash probe, still throws KeyNotFoundException
/// - `for w in s.Split(c)`               -> span split (no string[]); when `w` is only a dictionary key,
///                                          the .NET 9 alternate lookup avoids allocating a substring per word
/// </summary>
public sealed partial class Lowerer
{
    const string CM = "global::System.Runtime.InteropServices.CollectionsMarshal";

    sealed class SpanKey
    {
        public LocalSym Word = null!;
        public string SpanVar = "";
        public Dictionary<string, string> Alt = new();   // dictionary C# expr -> alternate-lookup local
    }

    SpanKey? spanKey;

    static bool IsDictionary(AType t, out AType key, out AType val)
    {
        key = val = AType.Unknown;
        if (t.Clr is not { IsGenericType: true } c || c.GetGenericTypeDefinition() != typeof(Dictionary<,>) || t.Args.Count != 2) return false;
        key = t.Args[0];
        val = t.Args[1];
        return true;
    }

    static bool SameExpr(Syntax.Expr a, Syntax.Expr b) => (a, b) switch
    {
        (NameExpr x, NameExpr y) => x.Name == y.Name && x.Sym == y.Sym,
        (IvarExpr x, IvarExpr y) => x.Name == y.Name,
        (LitExpr x, LitExpr y) => x.Kind == y.Kind && x.Text == y.Text,
        _ => false,
    };

    static bool Pure(Syntax.Expr e) => e is NameExpr { Res: "local" or "field" } or IvarExpr or LitExpr;

    /// <summary>`d[k] = d.GetValueOrDefault(k) (+|-) e` -> (dict, key, op, delta).</summary>
    static (IndexExpr Ix, string Op, Syntax.Expr Delta)? CounterUpdate(Assign a)
    {
        if (a.Target is not IndexExpr ix || ix.Args.Count != 1 || !IsDictionary(ix.Target.Type, out _, out var v) || !v.IsNumeric) return null;
        if (!Pure(ix.Target) || !Pure(ix.Args[0].Value!)) return null;
        if (a.Op is "+=" or "-=") return (ix, a.Op, a.Value);
        if (a.Op != "=" || a.Value is not BinaryExpr { Op: "+" or "-" } b) return null;
        if (b.L is not CallExpr { Name: "GetValueOrDefault", Args.Count: 1 } g || g.Recv == null || !SameExpr(g.Recv, ix.Target) || !SameExpr(g.Args[0].Value!, ix.Args[0].Value!)) return null;
        return (ix, b.Op + "=", b.R);
    }

    bool TryCounterUpdate(List<MStmt> o, Assign a)
    {
        var cu = CounterUpdate(a);
        if (cu == null) return false;
        var (ix, op, delta) = cu.Value;
        var dict = Expr(ix.Target);
        string dictCs = Backend.Printer.E(dict);
        var key = ix.Args[0].Value!;
        IsDictionary(ix.Target.Type, out _, out var vt);
        if (a.Op == "=")
        {
            // missing key starts at default(V): exactly GetValueOrDefault semantics, one probe
            string target = spanKey != null && key is NameExpr kn && kn.Sym == spanKey.Word && spanKey.Alt.TryGetValue(dictCs, out var alt)
                ? $"{CM}.GetValueRefOrAddDefault({alt}, {spanKey.SpanVar}, out _)"
                : $"{CM}.GetValueRefOrAddDefault({dictCs}, {Backend.Printer.E(Expr(key))}, out _)";
            o.Add(new MAssign { Target = new MCode(target), Op = op, Value = Expr(delta) });
            return true;
        }
        string slot = Temp("slot");
        o.Add(new MDecl { Name = slot, TypeCs = "ref " + vt.Cs, Init = new MCode($"ref {CM}.GetValueRefOrNullRef({dictCs}, {Backend.Printer.E(Expr(key))})") });
        o.Add(new MIf { Cond = new MCode($"global::System.Runtime.CompilerServices.Unsafe.IsNullRef(ref {slot})"), Then = { new MThrow { Value = new MNew { TypeCs = "global::System.Collections.Generic.KeyNotFoundException" } } } });
        o.Add(new MAssign { Target = new MCode(slot), Op = op, Value = Expr(delta) });
        return true;
    }

    /// <summary>`for w in s.Split(c)` over a string with a one-char separator.</summary>
    MStmt? TrySplitLoop(ForStmt f)
    {
        if (Method?.IsAsync == true) return null;   // spans cannot live across await
        if (f.Names.Count != 1 || f.Iter is not CallExpr { Name: "Split", Args.Count: 1, Block: null } c || c.Recv == null || !c.Recv.Type.IsString) return null;
        string? sep = c.Args[0].Value switch
        {
            LitExpr { Kind: "char" } l => l.Text,
            LitExpr { Kind: "str" } sl when sl.Text.Length == 1 && sl.Text != "\\" => "'" + (sl.Text == "'" ? "\\'" : sl.Text) + "'",
            _ => null,
        };
        if (sep == null) return null;
        var w = f.Locals[0];
        var outp = new List<MStmt>();
        var str = Expr(c.Recv);
        if (str is not MCode sc || !IsSimple(sc.Text))
        {
            string tmp = Temp("str");
            pre.Add(new MDecl { Name = tmp, Init = str });
            str = new MCode(tmp);
        }
        string s = Backend.Printer.E(str);
        string range = Temp("rg");
        var uses = new List<NameExpr>();
        Collect(f.Body, w, uses);
        var counters = new List<Assign>();
        CollectCounters(f.Body, w, counters);
        int keyUses = counters.Count;
        bool spanOnly = uses.Count > 0 && uses.Count == keyUses * 2 - counters.Count(a => a.Op != "=") && counters.All(a => IsDictionary(((IndexExpr)a.Target).Target.Type, out var k, out _) && k.IsString);
        var body = new List<MStmt>();
        var saved = spanKey;
        if (spanOnly)
        {
            var sk = new SpanKey { Word = w, SpanVar = Temp("w") };
            foreach (var a in counters)
            {
                string d = Backend.Printer.E(Expr(((IndexExpr)a.Target).Target));
                if (sk.Alt.ContainsKey(d)) continue;
                string alt = Temp("alt");
                pre.Add(new MDecl { Name = alt, Init = new MCode($"{d}.GetAlternateLookup<global::System.ReadOnlySpan<char>>()") });
                sk.Alt[d] = alt;
            }
            body.Add(new MDecl { Name = sk.SpanVar, TypeCs = "global::System.ReadOnlySpan<char>", Init = new MCode($"{s}.AsSpan()[{range}]") });
            spanKey = sk;
        }
        else body.Add(new MDecl { Name = w.CsName, Init = new MCode($"{s}[{range}]") });
        ctx.Add("loop");
        body.AddRange(Stmts(f.Body, Tail.None, null));
        ctx.RemoveAt(ctx.Count - 1);
        spanKey = saved;
        return new MForeach { Vars = { range }, Coll = new MCode($"{s}.AsSpan().Split({sep})"), Body = body };
    }

    static void Collect(List<Stmt> body, LocalSym w, List<NameExpr> into)
    {
        foreach (var n in Walk(body)) if (n is NameExpr ne && ne.Sym == w) into.Add(ne);
    }

    static void CollectCounters(List<Stmt> body, LocalSym w, List<Assign> into)
    {
        foreach (var s in WalkStmts(body))
            if (s is Assign a && a.Op == "=" && CounterUpdate(a) is { } cu && cu.Ix.Args[0].Value is NameExpr k && k.Sym == w) into.Add(a);
    }

    static IEnumerable<Stmt> WalkStmts(List<Stmt> body)
    {
        foreach (var s in body)
        {
            yield return s;
            IEnumerable<List<Stmt>> kids = s switch
            {
                IfStmt i => i.Bodies.Concat(i.Else != null ? [i.Else] : []),
                WhileStmt wl => [wl.Body],
                ForStmt fr => [fr.Body],
                CaseStmt cs => cs.Whens.Select(x => x.Body).Concat(cs.Else != null ? [cs.Else] : []),
                BeginStmt b => new[] { b.Body }.Concat(b.Rescues.Select(r => r.Body)).Concat(b.Ensure != null ? [b.Ensure] : []),
                _ => [],
            };
            foreach (var k in kids)
                foreach (var x in WalkStmts(k)) yield return x;
        }
    }

    /// <summary>All expression nodes reachable from statements (used for simple use-analysis).</summary>
    static IEnumerable<Syntax.Expr> Walk(List<Stmt> body)
    {
        foreach (var s in WalkStmts(body))
        {
            IEnumerable<Syntax.Expr?> roots = s switch
            {
                ExprStmt es => [es.Expr],
                Assign a => [a.Target, a.Value],
                LocalDecl d => [d.Value],
                MultiAssign m => m.Values,
                IfStmt i => i.Conds,
                WhileStmt wl => [wl.Cond],
                ForStmt fr => [fr.Iter],
                CaseStmt cs => cs.Whens.SelectMany(x => x.Patterns).Prepend(cs.Subject),
                ReturnStmt r => [r.Value],
                NextStmt n => [n.Value],
                RaiseStmt rs => [rs.Value],
                _ => [],
            };
            foreach (var r in roots)
                if (r != null)
                    foreach (var e in WalkExpr(r)) yield return e;
        }
    }

    static IEnumerable<Syntax.Expr> WalkExpr(Syntax.Expr e)
    {
        yield return e;
        IEnumerable<Syntax.Expr?> kids = e switch
        {
            CallExpr c => c.Args.Select(a => a.Value).Prepend(c.Recv),
            NewExpr n => n.Args.Select(a => a.Value),
            IndexExpr ix => ix.Args.Select(a => a.Value).Prepend(ix.Target),
            BinaryExpr b => [b.L, b.R],
            UnaryExpr u => [u.E],
            TernaryExpr t => [t.C, t.A, t.B],
            RangeExpr r => [r.A, r.B],
            ArrayLit a => a.Items,
            HashLit h => h.Keys.Concat(h.Values),
            TupleLit tl => tl.Items,
            InterpExpr ie => ie.Holes,
            TypeTestExpr tt => [tt.E],
            InvokeExpr inv => inv.Args.Select(a => a.Value).Prepend(inv.Target),
            YieldExpr y => y.Args.Select(a => a.Value),
            _ => [],
        };
        foreach (var k in kids)
            if (k != null)
                foreach (var x in WalkExpr(k)) yield return x;
        if (e is LambdaExpr lam)
            foreach (var x in Walk(lam.Body)) yield return x;
        if (e is CallExpr { Block: { } blk })
            foreach (var x in Walk(blk.Body)) yield return x;
    }
}
