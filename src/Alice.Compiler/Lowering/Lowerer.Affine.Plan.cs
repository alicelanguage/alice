using System.Runtime.CompilerServices;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>How a `case` arm reads the node of its scrutinee in the lowering (the binder's Keep lowers as Borrow).</summary>
public enum AffineArm { Take, Borrow }

/// <summary>One variant of an arena enum as the lowering sees it: tag, packing, and the slot word of each field.</summary>
public sealed class AffineVariant
{
    public TypeSym Sym = null!;
    public int Tag;
    public bool Packed;
    /// <summary>Per variant field: (slot word, kind); for a packed variant the one payload field (word 0).</summary>
    public List<(int Word, WordKind Kind)> Fields = new();
    /// <summary>The top byte of its terms (<see cref="AirTermTag"/>).</summary>
    public int Key => Packed ? 0x80 | Tag : Tag;
}

/// <summary>An enum lowered to an arena: its slab and variants.</summary>
public sealed class AffineEnum
{
    public TypeSym Sym = null!;
    public List<AffineVariant> Variants = new();
    public AirSlab Layout = null!;
    /// <summary>Slot words a recursive Drop follows (terms of this same enum); 0: Drops are shallow.</summary>
    public uint DeepWords;
    /// <summary>A Keep use exists (`affine enum` counted redirect, not lowered yet): nothing of this type is freed.</summary>
    public bool NoFree;

    public AffineVariant? Of(TypeSym variant) => Variants.FirstOrDefault(v => v.Sym == variant);
}

/// <summary>
/// The arena lowering's view of the binder's affine facts (<see cref="Binder.Affine"/>, Binder.Affine.cs): which
/// enums are arenas, their slot layout as `ulong` words (one per slot field of the binder's union by position), the
/// read mode per `case` arm, and where owned values die. After the facts are read, every AType of an arena enum and
/// of its slot variants is retyped to `ulong`, so signatures, locals and fork helpers carry term words in both
/// backends (a field-less variant keeps its AType for its dead record declaration; values of it are redirected to the
/// enum's). `ALICE_NO_ARENA=1` (or <see cref="Disable"/> in tests) keeps records; `ALICE_ARENA_DEEP=0` makes Drops
/// shallow (the slot only) instead of Bend's recursive free of the unowned subtree.
/// </summary>
public sealed class AffinePlan
{
    static readonly ConditionalWeakTable<Binder, AffinePlan> Plans = new();

    /// <summary>Tests: force records (true) on this thread regardless of the environment.</summary>
    [ThreadStatic] public static bool? Disable;

    /// <summary>Tests: force shallow (false) or recursive (true) Drops on this thread.</summary>
    [ThreadStatic] public static bool? Deep;

    static bool Disabled => Disable ?? Environment.GetEnvironmentVariable("ALICE_NO_ARENA") == "1";
    static bool DeepDrops => Deep ?? Environment.GetEnvironmentVariable("ALICE_ARENA_DEEP") != "0";

    /// <summary>The plan of a bound program, null when it has no arena enum (or arenas are disabled).</summary>
    public static AffinePlan? For(Binder binder)
    {
        if (Disabled) return null;
        lock (Plans)
        {
            if (!Plans.TryGetValue(binder, out var p))
            {
                p = new AffinePlan(binder, DeepDrops);
                p.Run();
                Plans.Add(binder, p);
            }
            return p.Enums.Count > 0 ? p : null;
        }
    }

    readonly Binder binder;
    readonly bool deep;
    readonly AffineFacts facts;
    public readonly Dictionary<TypeSym, AffineEnum> Enums = new();
    readonly Dictionary<AType, (AffineEnum E, AffineVariant? V)> byType = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, (AffineEnum E, AffineVariant? V)> byCs = new(StringComparer.Ordinal);
    readonly HashSet<BeginStmt> regions = new(ReferenceEqualityComparer.Instance);
    /// <summary>One line per arena enum (layout), for tests and `ALICE_AFFINE_EXPLAIN=1`.</summary>
    public readonly List<string> Explain = new();

    AffinePlan(Binder binder, bool deep)
    {
        this.binder = binder;
        this.deep = deep;
        facts = binder.Affine;
    }

    public AffineArm Arm(When w, AffineEnum e) => !e.NoFree && facts.Arm(w) == ArmMode.Take ? AffineArm.Take : AffineArm.Borrow;

    /// <summary>Owned locals that die at the entry of this arm (after its field bindings).</summary>
    public IReadOnlyList<LocalSym> DropsAtArm(When w) => facts.Drops.TryGetValue(w, out var l) ? Live(l) : [];

    /// <summary>Owned locals that die after this statement (after a return value or tail value is computed).</summary>
    public IReadOnlyList<LocalSym> DropsAfter(Stmt s) => facts.Drops.TryGetValue(s, out var l) ? Live(l) : [];

    /// <summary>Owned locals that die on entering the branch that starts with this statement.</summary>
    public IReadOnlyList<LocalSym> DropsBefore(Stmt s) => facts.DropsBefore.TryGetValue(s, out var l) ? Live(l) : [];

    List<LocalSym> Live(List<LocalSym> l) => l.Where(x => TryType(x.Type, out var e, out _) && !e.NoFree).ToList();

    /// <summary>A `scope ... end` that becomes an arena region.</summary>
    public bool Region(BeginStmt b) => regions.Contains(b);

    /// <summary>The arena enum (and variant, null for the enum itself) of an HIR type, before or after retyping.</summary>
    public bool TryType(AType t, out AffineEnum e, out AffineVariant? v)
    {
        if (byType.TryGetValue(t, out var hit) || (!t.Nullable && t.Kind != "prim" && byCs.TryGetValue(t.Cs, out hit)))
        {
            (e, v) = hit;
            return true;
        }
        e = null!;
        v = null;
        return false;
    }

    void Run()
    {
        // lowerable: the binder's arena layouts with at most 7 slot words whose term fields name lowerable enums
        var lay = facts.Layouts.Values.Where(l => l.Arena && l.Slot.Count <= 7).ToDictionary(l => l.Enum);
        bool shrunk = true;
        while (shrunk)
        {
            shrunk = false;
            foreach (var (t, l) in lay.ToList())
                if (t.Variants.Any(v => v.FieldOrder.Any(f => v.Fields[f].Type.Sym is { } fs && facts.LayoutOf(fs) != null && !lay.ContainsKey(fs.Parent ?? fs))))
                {
                    lay.Remove(t);
                    shrunk = true;
                }
        }
        if (lay.Count == 0) return;
        var keep = facts.Uses.Where(kv => kv.Value == AffineOp.Keep).Select(kv => facts.LayoutOf(kv.Key.Type)?.Enum).OfType<TypeSym>().ToHashSet();
        foreach (var (t, l) in lay)
        {
            var ae = new AffineEnum { Sym = t, NoFree = keep.Contains(t), Layout = new AirSlab { Enum = t.Name, Words = Math.Max(1, l.Slot.Count) } };
            uint cross = 0;
            foreach (var av in l.Variants)
            {
                var v = new AffineVariant { Sym = av.Sym, Tag = av.Tag, Packed = av.Packed };
                var types = av.Sym.FieldOrder.Select(f => av.Sym.Fields[f].Type).ToList();
                for (int j = 0; j < types.Count; j++)
                {
                    bool term = types[j].Sym is { } ts && lay.ContainsKey(ts.Parent ?? ts);
                    var kind = term ? WordKind.Term : AirSlab.KindOf(types[j].Cs) ?? WordKind.U64;
                    int word = av.Packed ? 0 : av.SlotIndex[j];
                    v.Fields.Add((word, kind));
                    if (term && (types[j].Sym!.Parent ?? types[j].Sym) != t) cross |= 1u << word;
                }
                ae.Variants.Add(v);
            }
            for (int k = 0; k < l.Slot.Count; k++)
                if (l.Slot[k].IsTerm)
                {
                    ae.Layout.TermWords |= 1u << k;
                    if (deep && (cross & (1u << k)) == 0) ae.DeepWords |= 1u << k;
                }
            Enums[t] = ae;
            Explain.Add($"enum {t.Name}: arena ({ae.Layout.Words}-word slot; " + string.Join(", ", ae.Variants.Select(v => v.Sym.Name + (v.Packed ? " packed" : " slot")))
                + (ae.DeepWords != 0 ? "; recursive drop" : "") + (ae.NoFree ? "; keep: no frees" : "") + ")");
        }
        CollectRegions();
        if (regions.Count > 0) foreach (var e in Enums.Values) e.Layout.Proof = SlotProof.Unknown;
        Retype();
        if (Environment.GetEnvironmentVariable("ALICE_AFFINE_EXPLAIN") == "1")
            foreach (var l in Explain) Console.Error.WriteLine("affine: " + l);
    }

    bool Direct(AType t) => !t.Nullable && t.Sym is { } s && Enums.ContainsKey(s.Parent ?? s);

    static IEnumerable<Stmt> Items(List<Item> items)
    {
        foreach (var it in items)
            switch (it)
            {
                case StmtItem si: yield return si.Stmt; break;
                case NamespaceItem n: foreach (var s in Items(n.Items)) yield return s; break;
            }
    }

    /// <summary>`scope ... end` in the main program (no concurrent lane) that writes no arena local declared outside it
    /// and does not jump out.</summary>
    void CollectRegions()
    {
        foreach (var f in binder.Files)
            foreach (var st in Items(f.Items))
                AstWalk.Each(st, n =>
                {
                    if (n is not BeginStmt { Scope: true, Rescues.Count: 0, Ensure: null } b) return;
                    bool escapes = false;
                    AstWalk.Each(b.Body, x =>
                    {
                        if (x is Assign { Declares: null, Target: NameExpr { Sym: LocalSym l } } && Direct(l.Type)) escapes = true;
                        if (x is MultiAssign { Declares: false } ma && ma.Locals.Any(l => Direct(l.Type))) escapes = true;
                        if (x is ReturnStmt or BreakStmt or NextStmt) escapes = true;
                    });
                    if (!escapes) regions.Add(b);
                });
    }

    void Retype()
    {
        foreach (var (t, ae) in Enums)
        {
            byType[t.Type] = (ae, null);
            byCs[t.Type.Cs] = (ae, null);
            foreach (var v in ae.Variants)
            {
                byType[v.Sym.Type] = (ae, v);
                byCs[v.Sym.Type.Cs] = (ae, v);
            }
        }
        // the binder copies some ATypes (joined branch types, widened locals): find them all
        var all = new HashSet<AType>(ReferenceEqualityComparer.Instance);
        var unit = new Dictionary<AType, AType>(ReferenceEqualityComparer.Instance);
        bool See(AType? at)
        {
            if (at?.Sym is not { } s || at.Nullable || !Enums.TryGetValue(s.Parent ?? s, out var ae)) return false;
            var v = s.Parent != null ? ae.Of(s) : null;
            byType[at] = (ae, v);
            if (v != null && v.Sym.FieldOrder.Count == 0) { unit[at] = ae.Sym.Type; return true; }
            all.Add(at);
            return false;
        }
        AType Fix(AType at) => See(at) ? unit[at] : at;
        void Local(LocalSym? l) { if (l != null) l.Type = Fix(l.Type); }
        var bodies = new List<List<Stmt>>();
        foreach (var t in binder.Types.Values.Append(binder.Program).Distinct())
        {
            if (t.Kind == "variant" && t.Parent != null && Enums.ContainsKey(t.Parent)) { See(t.Type); continue; }
            foreach (var f in t.Fields.Values) f.Type = Fix(f.Type);
            foreach (var m in t.Methods.Values.SelectMany(x => x))
            {
                m.Ret = Fix(m.Ret);
                foreach (var ps in m.Params) { ps.Type = Fix(ps.Type); Local(ps.Local); }
                if (m.Decl != null) bodies.Add(m.Decl.Body);
            }
        }
        foreach (var (t, ae) in Enums)
        {
            See(t.Type);
            foreach (var v in ae.Variants) See(v.Sym.Type);
        }
        foreach (var f in binder.Files)
        {
            var main = Items(f.Items).ToList();
            if (main.Count > 0) bodies.Add(main);
            AstWalk.Each(f.Items.Cast<Node>(), n => { if (n is MethodDecl md) bodies.Add(md.Body); });
        }
        foreach (var body in bodies.Distinct(ReferenceEqualityComparer.Instance).Cast<List<Stmt>>())
        {
            var patterns = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
            AstWalk.Each(body, n => { if (n is When w) patterns.UnionWith(w.Patterns); });
            AstWalk.Each(body, n =>
            {
                switch (n)
                {
                    case Expr e:
                        if (!patterns.Contains(e)) e.Type = Fix(e.Type);
                        else See(e.Type);
                        if (e.Sym is AType st) See(st);
                        if (e.Sym is LocalSym ls) Local(ls);
                        if (e.Sym is Destructure d) { See(d.Type); foreach (var q in d.Parts) Local(q.Bind); }
                        if (e is BindPatternExpr bp) Local(bp.Local);
                        break;
                    case LocalDecl ld: Local(ld.Local); break;
                    case Assign a: Local(a.Declares); break;
                    case MultiAssign ma: foreach (var l in ma.Locals) Local(l); break;
                    case ForStmt fs: foreach (var l in fs.Locals) Local(l); break;
                }
            });
        }
        foreach (var at in all)
        {
            at.Kind = "prim";
            at.Cs = "ulong";
            at.Clr = typeof(ulong);
            at.Sym = null;
        }
    }
}
