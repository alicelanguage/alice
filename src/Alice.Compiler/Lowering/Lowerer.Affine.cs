using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Affine arena lowering (ADR 0037 pipeline step 2), driven by the binder's facts (<see cref="AffinePlan"/> over
/// Binder.Affine): an arena enum is a `ulong` term word; a packed variant (no fields, or one field of at most 32 bits)
/// is <see cref="AirTermPack"/>, any other variant <see cref="AirArenaAlloc"/> into the enum's slab; `case`
/// dispatches on <see cref="AirTermTag"/> and reads a slot variant's fields with <see cref="AirArenaTake"/> (the slot
/// is freed when this lane owns it) or <see cref="AirArenaBorrow"/>, per arm. An owned value that dies unconsumed is
/// an <see cref="AirArenaDrop"/>: recursive (its unowned subtree is freed too, Bend-style) unless `ALICE_ARENA_DEEP=0`.
/// A Take arm drops the term fields it does not bind. `scope ... end` in the main body becomes an
/// <see cref="AirArenaRegion"/>. Forks are unchanged: every thread allocates in its own lane.
/// </summary>
public sealed partial class Lowerer
{
    AffinePlan? affine;
    /// <summary>Borrowing arms being lowered: scrutinee name, and the slot words their patterns bound to locals.</summary>
    readonly List<(string Term, uint Bound)> borrowArms = new();

    static readonly AirType ArenaTermT = new AirType.Prim(PrimKind.ULong);
    static readonly AirType ArenaIntT = new AirType.Prim(PrimKind.Int);

    /// <summary>The arena plan of this program (null: no arena enum).</summary>
    public AffinePlan? Arena => affine;

    /// <summary>Hook (Lowerer constructor): reads the binder's affine facts once per binder and retypes arena enums.</summary>
    void AffineInit() => affine = AffinePlan.For(binder);

    /// <summary>Hook (Lowerer.Expressions.cs, `NewExpr`): `Type::Variant(args)` of an arena enum.</summary>
    MExpr? AffineNew(NewExpr ne)
    {
        if (affine == null || !affine.TryType(ne.Type, out var ae, out var v) || v == null) return null;
        var args = ne.Args.Select(a => Expr(a.Value!)).ToList();
        if (v.Packed)
            return At(new AirTermPack { Variant = v.Sym.Name, Tag = v.Tag, Payload = args.Count > 0 ? args[0] : null, Kind = v.Fields.Count > 0 ? v.Fields[0].Kind : WordKind.U32, Type = ArenaTermT }, ne);
        return At(new AirArenaAlloc
        {
            Layout = ae.Layout, Variant = v.Sym.Name, Tag = v.Tag, Fields = args,
            Kinds = v.Fields.Select(f => f.Kind).ToList(), Words = v.Fields.Select(f => f.Word).ToList(), Type = ArenaTermT,
        }, ne);
    }

    /// <summary>Hook (Lowerer.LoopPar.cs `PureRead`, Lowerer.Fusion.cs): constructing a variant of an arena enum only
    /// stores its arguments in this lane's slab (or packs them), like a record constructor; its AType was retyped to
    /// `ulong`, so the record test cannot see the variant.</summary>
    bool AffineNewPure(NewExpr ne) => affine != null && affine.TryType(ne.Type, out _, out var v) && v != null;

    /// <summary>Hook (Lowerer.Expressions.cs, `Const`): a field-less variant `Type::Variant` is a packed constant.</summary>
    MExpr? AffineConst(ConstExpr ce)
    {
        if (affine == null || ce.Res != "type" || ce.Sym is not AType t || !affine.TryType(t, out _, out var v) || v == null) return null;
        return At(new AirTermPack { Variant = v.Sym.Name, Tag = v.Tag, Type = ArenaTermT }, ce);
    }

    /// <summary>Hook (Lowerer.Statements.cs, plain `begin`): `scope ... end` as an arena region, reset at every exit.</summary>
    MStmt AffineRegion(BeginStmt b, MBlock block)
    {
        if (affine == null || !affine.Region(b)) return block;
        string r = Temp("region");
        return new MBlock
        {
            Body =
            {
                new MDecl { Name = r, TypeCs = "global::Alice.Runtime.ArenaRegion", Init = new AirArenaRegion() },
                new MTry { Body = { block }, Finally = [new MExprStmt { E = new AirArenaRegionEnd { Region = new AirLocalRef(r) } }] },
            },
        };
    }

    /// <summary>Hook (Lowerer.cs, `Into`): the binder's Drops around one statement. Before: on entering the branch
    /// the statement starts (ahead of its side statements). After: after the statement, or after a return value is
    /// computed and before it leaves.</summary>
    void AffineStmtDrops(Stmt s, List<MStmt> pre, List<MStmt> produced)
    {
        if (affine == null) return;
        var before = affine.DropsBefore(s);
        if (before.Count > 0)
        {
            var d = new List<MStmt>();
            DropLocals(d, before);
            pre.InsertRange(0, d);
        }
        var after = affine.DropsAfter(s);
        if (after.Count == 0) return;
        var drops = new List<MStmt>();
        DropLocals(drops, after);
        if (produced.Count > 0 && produced[^1] is MReturn ret)
        {
            produced.RemoveAt(produced.Count - 1);
            if (ret.Value != null)
            {
                string tmp = Temp("ret");
                produced.Add(new MDecl { Name = tmp, Init = ret.Value, Line = ret.Line });
                produced.AddRange(drops);
                produced.Add(new MReturn { Value = new AirLocalRef(tmp), Line = ret.Line });
            }
            else
            {
                produced.AddRange(drops);
                produced.Add(ret);
            }
            return;
        }
        produced.AddRange(drops);
    }

    void DropLocals(List<MStmt> into, IReadOnlyList<LocalSym> locals)
    {
        foreach (var x in locals)
            if (affine!.TryType(x.Type, out var xe, out _))
                into.Add(new MExprStmt { E = Drop(xe, new AirLocalRef(x.CsName, ArenaTermT), x.CsName) });
    }

    /// <summary>A Drop of an owned term: recursive over the words no enclosing borrowing arm on it bound to locals.</summary>
    AirArenaDrop Drop(AffineEnum e, MExpr term, string? name)
    {
        uint bound = 0;
        if (name != null) foreach (var (t, b) in borrowArms) if (t == name) bound |= b;
        return new AirArenaDrop { Layout = e.Layout, Term = term, Top = e.DeepWords & ~bound, All = e.DeepWords };
    }

    /// <summary>Hook (Lowerer.Statements.cs, `Case`): a `case` over an arena enum. False: not an arena subject.</summary>
    bool AffineCase(List<MStmt> o, CaseStmt c, Tail tail, string? target)
    {
        if (affine == null || c.Subject == null || !affine.TryType(c.Subject.Type, out var ae, out _)) return false;
        var v0 = Expr(c.Subject);
        string termName;
        bool named = true;
        if (v0 is AirLocalRef { Narrowed: false } lr) termName = lr.Name;
        else if (v0 is AirParamRef pr) termName = pr.Name;
        else
        {
            termName = Temp("case");
            named = false;
            o.Add(new MDecl { Name = termName, TypeCs = "ulong", Init = v0 });
        }
        MExpr Term() => new AirLocalRef(termName, ArenaTermT);
        string key = Temp("tag");
        o.Add(new MDecl { Name = key, TypeCs = "int", Init = new AirTermTag { Term = Term(), Type = ArenaIntT } });
        // the else (lowered at each use: a guard duplicates the rest of the chain)
        List<MStmt>? Els() => c.Else != null ? Stmts(c.Else, tail, target)
            : c.Exhaustive ? [new MThrow { Value = new MNew { TypeCs = "global::System.Diagnostics.UnreachableException" } }] : null;
        // arms from index i as an if-chain; a guarded arm binds (it borrows: the binder never takes under a guard),
        // tests its guard, and on false continues with the rest of the chain (lowered again in that branch)
        List<MStmt>? Arms(int i)
        {
            if (i == c.Whens.Count) return Els();
            var w = c.Whens[i];
            MExpr? cond = null;
            var then = new List<MStmt>();
            // a temp subject is owned and dead after the case: every unguarded arm takes it
            var mode = w.Guard != null ? AffineArm.Borrow : named ? affine.Arm(w, ae) : ae.NoFree ? AffineArm.Borrow : AffineArm.Take;
            uint bound = 0;
            foreach (var p in w.Patterns)
            {
                AffineVariant? v = null;
                if (p is { Res: "destructure", Sym: Destructure d0 } && affine.TryType(d0.Type, out _, out var dv)) v = dv;
                else if (p.Res == "type" && affine.TryType(p.Type, out _, out var tv)) v = tv;
                MExpr one = v == null ? AirConst.Bool(true) : new MBinary { Op = AirBinOp.Eq, L = new AirLocalRef(key, ArenaIntT), R = AirConst.Int(v.Key) };
                cond = cond == null ? one : new MBinary { Op = AirBinOp.Or, L = cond, R = one };
                if (v == null || w.Patterns.Count > 1) continue;
                if (p is { Sym: Destructure d } && d.Parts.Any(x => x.Bind != null)) bound = Binds(then, ae, v, Term, d, mode);
                else if (!v.Packed && mode == AffineArm.Take) then.Add(new MExprStmt { E = Drop(ae, Term(), null) });
            }
            var body = then;
            if (w.Guard != null)
            {
                body = new List<MStmt>();
                then.Add(new MIf { Cond = Expr(w.Guard), Then = body, Else = Arms(i + 1) });
            }
            DropLocals(body, affine.DropsAtArm(w));
            bool borrowing = mode == AffineArm.Borrow && named;
            if (borrowing) borrowArms.Add((termName, bound));
            try { body.AddRange(Stmts(w.Body, tail, target)); }
            finally { if (borrowing) borrowArms.RemoveAt(borrowArms.Count - 1); }
            return [new MIf { Cond = cond!, Then = then, Else = Arms(i + 1) }];
        }
        var chain = Arms(0);
        if (chain != null) o.AddRange(chain);
        return true;
    }

    /// <summary>The bound fields of one variant pattern: a packed payload, or the slot read once (Take/Borrow) and its
    /// words. A Take drops the term fields the pattern leaves unbound. Returns the slot words bound to locals.</summary>
    uint Binds(List<MStmt> then, AffineEnum ae, AffineVariant v, Func<MExpr> term, Destructure d, AffineArm mode)
    {
        if (v.Packed)
        {
            if (d.Parts.Count == 1 && d.Parts[0].Bind is { } pb)
                Bind(then, pb, new AirTermPayload { Term = term(), Kind = v.Fields[0].Kind });
            return 0;
        }
        string s = Temp("slot");
        MExpr read = mode == AffineArm.Take
            ? new AirArenaTake { Layout = ae.Layout, Term = term(), Frees = true }
            : new AirArenaBorrow { Layout = ae.Layout, Term = term() };
        then.Add(new MDecl { Name = s, TypeCs = ae.Layout.SlotCs, Init = read });
        uint bound = 0;
        for (int i = 0; i < d.Parts.Count && i < v.Fields.Count; i++)
        {
            var (word, kind) = v.Fields[i];
            if (d.Parts[i].Bind is { } b)
            {
                bound |= 1u << word;
                Bind(then, b, new AirSlotWord { Layout = ae.Layout, Slot = new AirLocalRef(s), Word = word, Kind = kind });
            }
            else if (mode == AffineArm.Take && kind == WordKind.Term && (ae.DeepWords & (1u << word)) != 0)
                then.Add(new MExprStmt { E = new AirArenaDrop { Layout = ae.Layout, Term = new AirSlotWord { Layout = ae.Layout, Slot = new AirLocalRef(s), Word = word, Kind = kind }, Top = ae.DeepWords, All = ae.DeepWords } });
        }
        return bound;
    }

    void Bind(List<MStmt> into, LocalSym b, MExpr value)
    {
        if (hoisted.Contains(b)) into.Add(new MAssign { Target = new AirLocalRef(b.CsName, b.Type.ToAir()), Value = value });
        else into.Add(new MDecl { Name = b.CsName, TypeCs = TypeCs(b.Type), Init = value });
    }
}
