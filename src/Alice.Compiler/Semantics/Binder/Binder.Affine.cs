using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>How a `case` arm on an arena value reads its scrutinee (ADR 0037).</summary>
public enum ArmMode
{
    /// <summary>The scrutinee is owned and never used again on this path: read the fields, then free the node.</summary>
    Take,
    /// <summary>Read the fields in place; the node stays (it is used later, borrowed, or shared).</summary>
    Borrow,
    /// <summary>The scrutinee was moved earlier with a counted second owner (Bend 2's Keep): read only, like Borrow.</summary>
    Keep,
}

/// <summary>What one use of an arena-typed local (a <see cref="NameExpr"/>) does to its value.</summary>
public enum AffineOp
{
    /// <summary>Passed to an Alice parameter, built into a node, returned, or assigned: ownership moves.</summary>
    Move,
    /// <summary>Moved, and the local is read again afterwards (`affine enum` only): the receiver gets a counted
    /// redirect and the local keeps the second count until its last read (then a Drop).</summary>
    Keep,
    /// <summary>Lent to a `borrow` parameter: the callee only reads.</summary>
    BorrowArg,
}

/// <summary>One field of an arena slot, shared by position across the slot variants that agree on its type.</summary>
public sealed class SlotField
{
    public string Name = "";
    public AType Type = AType.Unknown;
    /// <summary>The field holds another arena value: a 64-bit term (`ulong`).</summary>
    public bool IsTerm;
}

/// <summary>A variant of an arena enum: tag (declaration index), packing and where its fields live in the slot.</summary>
public sealed class ArenaVariant
{
    public TypeSym Sym = null!;
    public int Tag;
    /// <summary>No fields, or one field of at most 32 bits: the value is the term word itself, no slot.</summary>
    public bool Packed;
    /// <summary>The packed payload's type (null for a variant without fields).</summary>
    public AType? PackedField;
    /// <summary>Variant field j lives in <see cref="ArenaLayout.Slot"/>[SlotIndex[j]] (empty for packed variants).</summary>
    public int[] SlotIndex = [];
}

/// <summary>The arena form of one sum type (ADR 0037), or why it stays records.</summary>
public sealed class ArenaLayout
{
    public TypeSym Enum = null!;
    /// <summary>`affine enum`: a failed proof is a compile error (AL0240-AL0243) instead of a demotion.</summary>
    public bool Declared;
    /// <summary>Lowered to a struct arena; false = sealed records everywhere (ineligible or demoted).</summary>
    public bool Arena = true;
    /// <summary>Why the type is records: `escapes at file:line: reason` (or `not eligible at`, `used after move at`,
    /// `borrow consumed at`); null while <see cref="Arena"/>.</summary>
    public string? Demotion;
    public List<ArenaVariant> Variants = new();
    /// <summary>The slot record struct: the union by position of every slot variant's fields.</summary>
    public List<SlotField> Slot = new();

    public ArenaVariant? VariantOf(TypeSym v) => Variants.FirstOrDefault(x => x.Sym == v);

    /// <summary>`alice explain` text: `arena`, or `record (demoted: escapes at file:line: reason)`.</summary>
    public string Explain => Arena ? "arena" : $"record (demoted: {Demotion})";
}

/// <summary>
/// Whole-program affine facts (ADR 0037) for the lowering (`Lowering/Lowerer.Affine.cs`): which sum types are arenas,
/// per `case` arm Take / Borrow / Keep, per use Move / Keep / BorrowArg, and where an owned value dies unconsumed
/// (Drop). Facts exist only for arena types; anything not listed is Borrow / no fact (always safe).
/// </summary>
public sealed class AffineFacts
{
    public readonly Dictionary<TypeSym, ArenaLayout> Layouts = new();
    public readonly Dictionary<When, ArmMode> Arms = new();
    public readonly Dictionary<NameExpr, AffineOp> Uses = new();
    /// <summary>Owned arena locals that die here without being consumed. Key <see cref="When"/>: at arm entry, after
    /// the field bindings. Key <see cref="Stmt"/>: after the statement (for a `return` or a tail value: after the value
    /// is computed, before control leaves). Key = first statement of an `if`/`else` branch: see
    /// <see cref="DropsBefore"/>. A Drop frees the node's slot only (shallow): its fields were copied into locals that
    /// own them now, or are reclaimed when the region ends. Ignoring a Drop only leaks until the region resets.</summary>
    public readonly Dictionary<Node, List<LocalSym>> Drops = new();
    /// <summary>Drops at the start of an `if`/`else` branch whose first statement is the key.</summary>
    public readonly Dictionary<Stmt, List<LocalSym>> DropsBefore = new();

    /// <summary>The arena layout of an enum or variant type, null when it is records.</summary>
    public ArenaLayout? LayoutOf(TypeSym? t)
    {
        var e = t?.Kind == "variant" ? t.Parent : t;
        return e != null && Layouts.TryGetValue(e, out var l) && l.Arena ? l : null;
    }

    public ArenaLayout? LayoutOf(AType? t) => t == null || t.Nullable ? null : LayoutOf(t.Sym);

    public bool IsArena(TypeSym t) => LayoutOf(t) != null;

    public ArmMode Arm(When w) => Arms.TryGetValue(w, out var m) ? m : ArmMode.Borrow;

    public AffineOp? Site(NameExpr n) => Uses.TryGetValue(n, out var op) ? op : null;

    /// <summary>Explicit `borrow p` only (no interprocedural inference yet).</summary>
    public bool BorrowParam(ParamSym p) => p.Borrow && LayoutOf(p.Type) != null;

    public IReadOnlyList<LocalSym> DropsAt(Node n) =>
        n is Stmt s && DropsBefore.TryGetValue(s, out var b) ? b : Drops.TryGetValue(n, out var l) ? l : [];

    /// <summary>One line per sum type, in declaration order: `Tree: arena` / `Tree: record (demoted: ...)`.</summary>
    public IEnumerable<string> Explain() => Layouts.Values.Select(l => $"{l.Enum.Name}: {l.Explain}");
}

/// <summary>
/// Affine arena analysis (ADR 0037), run once after binding, beside <c>Binder.Borrow.cs</c>:
/// <list type="number">
/// <item>Eligibility per sum type: every payload field unmanaged or another eligible sum type (recursion allowed);
/// packing: a variant with no fields or one field of at most 32 bits lives in the term word.</item>
/// <item>Per body, a move-state flow over arena locals (<c>Binder.Affine.Flow.cs</c>): Take / Borrow per `case` arm,
/// Move / Keep / BorrowArg per use, Drops.</item>
/// <item>Whole-program demotion: an escape (field, collection, nullable, block capture, .NET call, `==`/`hash`/
/// `to_s`/interpolation, `rescue`) or a use after a move demotes the type to sealed records everywhere, and demotion
/// propagates along payload edges to a fixpoint. Under `affine enum` the same site is a compile error:
/// AL0240 payload, AL0241 escape, AL0242 use after consume, AL0243 consumed `borrow` parameter.</item>
/// </list>
/// Lowering is unchanged by this pass; <see cref="Affine"/> only records facts.
/// </summary>
public sealed partial class Binder
{
    public readonly AffineFacts Affine = new();

    /// <summary>First demotion site per type (for explain) and the first error under `affine enum`.</summary>
    readonly Dictionary<TypeSym, (string File, Node At, string Reason, string Kind)> demotions = new();

    void CheckAffine()
    {
        var decls = new List<(EnumDecl Decl, TypeSym Sym, string File)>();
        foreach (var f in Files) CollectEnums(f.Items, f.Path, decls);
        if (decls.Count == 0) return;
        foreach (var (d, s, _) in decls)
            if (!Affine.Layouts.ContainsKey(s)) Affine.Layouts[s] = new ArenaLayout { Enum = s, Declared = d.Affine };
        foreach (var (d, s, f) in decls) CheckPayloads(d, s, f);
        PropagateDemotions(decls);
        if (Affine.Layouts.Values.All(l => !l.Arena)) { Finish(); return; }
        ScanTypePositions(decls);
        PropagateDemotions(decls);
        foreach (var (body, f) in AffineBodies()) FlowBody(body, f);
        PropagateDemotions(decls);
        Finish();

        void Finish()
        {
            foreach (var l in Affine.Layouts.Values)
            {
                if (l.Arena) Layout(l);
                else if (demotions.TryGetValue(l.Enum, out var d)) l.Demotion = $"{d.Kind} at {d.File}:{d.At.Line}: {d.Reason}";
            }
            foreach (var w in Affine.Arms.Keys.ToList()) if (!armTypes.TryGetValue(w, out var t) || !t.Arena) Affine.Arms.Remove(w);
            foreach (var n in Affine.Uses.Keys.ToList()) if (Affine.LayoutOf(n.Type) == null) Affine.Uses.Remove(n);
            foreach (var d in new[] { Affine.Drops.Values, (IEnumerable<List<LocalSym>>)Affine.DropsBefore.Values }.SelectMany(x => x))
                d.RemoveAll(l => Affine.LayoutOf(l.Type) == null);
            foreach (var k in Affine.Drops.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) Affine.Drops.Remove(k);
            foreach (var k in Affine.DropsBefore.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) Affine.DropsBefore.Remove(k);
        }
    }

    readonly Dictionary<When, ArenaLayout> armTypes = new();

    void CollectEnums(List<Item> items, string file, List<(EnumDecl, TypeSym, string)> into)
    {
        foreach (var it in items)
            switch (it)
            {
                case NamespaceItem n: CollectEnums(n.Items, file, into); break;
                case TypeDecl d: CollectEnums(d.Members, file, into); break;
                case EnumDecl { IsAdt: true, Sym: TypeSym s } e when s.Kind == "adt": into.Add((e, s, file)); break;
                case EnumDecl { Sym: TypeSym s, Underlying.Name: "Int64" or "UInt64" or "Long" or "ULong" }: wideEnums.Add(s); break;
            }
    }

    // ---- candidates ----

    /// <summary>The layout of a sum type still considered for the arena (eligible and not demoted yet).</summary>
    ArenaLayout? Cand(AType? t) => t == null || t.Nullable ? null : Cand(t.Sym);

    ArenaLayout? Cand(TypeSym? s)
    {
        var e = s?.Kind == "variant" ? s.Parent : s;
        return e != null && Affine.Layouts.TryGetValue(e, out var l) && l.Arena ? l : null;
    }

    /// <summary>A candidate nested inside t (element, generic argument, or the type itself made nullable).</summary>
    TypeSym? NestedCand(AType? t, int depth = 0)
    {
        if (t == null || depth > 8) return null;
        if (depth > 0 || t.Nullable)
            if (Cand(t.Sym) is { } l) return l.Enum;
        if (NestedCand(t.Elem, depth + 1) is { } e) return e;
        foreach (var a in t.Args) if (NestedCand(a, depth + 1) is { } x) return x;
        return null;
    }

    /// <summary>A failed proof for `l`'s type: AL error under `affine enum`, else demotion (first site wins).</summary>
    void Violate(ArenaLayout l, string f, Node at, string code, string declared, string inferred)
    {
        if (!l.Arena) return;
        if (l.Declared) throw new CompileException($"{f}:{at.Line}:{Math.Max(1, at.Col)}: error {code}: {declared}");
        l.Arena = false;
        string kind = code switch { "AL0240" => "not eligible", "AL0242" => "used after move", "AL0243" => "borrow consumed", _ => "escapes" };
        demotions.TryAdd(l.Enum, (f, at, inferred, kind));
    }

    void Escape(ArenaLayout l, string f, Node at, string what, string reason) =>
        Violate(l, f, at, "AL0241", $"affine value `{what}` escapes: {reason}; consume it with `case`, move it to an Alice call or return it, or drop `affine` from enum {l.Enum.Name}", reason);

    // ---- 1. eligibility ----

    void CheckPayloads(EnumDecl d, TypeSym s, string f)
    {
        var l = Affine.Layouts[s];
        if (s.Variants.Count > Alice.Runtime.Term.MaxTag + 1)
        {
            Violate(l, f, d, "AL0240", $"affine enum `{s.Name}` has {s.Variants.Count} variants, more than the {Alice.Runtime.Term.MaxTag + 1} a term can tag", $"more than {Alice.Runtime.Term.MaxTag + 1} variants");
            return;
        }
        for (int i = 0; i < d.Names.Count; i++)
        {
            var v = s.Variants.FirstOrDefault(x => x.Name == d.Names[i]);
            if (v == null) continue;
            var ps = d.Fields[i] ?? [];
            for (int j = 0; j < v.FieldOrder.Count; j++)
            {
                var fs = v.Fields[v.FieldOrder[j]];
                if (fs.Type.Sym is { Kind: "adt" or "variant" } && !fs.Type.Nullable) continue;   // edge, checked by propagation
                if (Unmanaged(fs.Type)) continue;
                Node at = j < ps.Count ? ps[j] : d;
                Violate(l, f, at, "AL0240", $"affine enum `{s.Name}`: field `{fs.Name}` of {v.Name} is a reference type ({fs.Type.Cs}) that is not an affine enum; use unmanaged fields or other affine enums, or drop `affine`",
                    $"field `{fs.Name}` of {v.Name} is {fs.Type.Cs}, not unmanaged");
                return;
            }
        }
    }

    static bool Unmanaged(AType t, int depth = 0)
    {
        if (t.Nullable || depth > 8) return false;
        if (t.Sym is { } s)
        {
            if (s.Kind == "enum") return true;
            if (s.Kind is "struct" or "record struct" && s.TypeParams.Count == 0 && !s.IsStack)
                return s.Fields.Values.Where(x => !x.IsStatic && x.Kind is "field" or "getter" or "property" or "")
                    .All(x => Unmanaged(x.Type, depth + 1));
            return false;
        }
        return t.Clr is { } c && NoRefs(c, 0);
    }

    /// <summary>A .NET value type without references (C# `unmanaged`).</summary>
    static bool NoRefs(Type c, int depth)
    {
        if (!c.IsValueType || c.ContainsGenericParameters || c == typeof(void) || depth > 8) return false;
        if (c.IsPrimitive || c.IsEnum || c.IsPointer || c == typeof(decimal)) return true;
        return c.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .All(f => NoRefs(f.FieldType, depth + 1));
    }

    /// <summary>Unmanaged and at most 32 bits: packs into the low word of a term.</summary>
    bool Fits32(AType t)
    {
        if (t.Nullable) return false;
        if (t.Sym is { Kind: "enum" } s) return !wideEnums.Contains(s);
        if (t.Clr is not { IsPrimitive: true } c) return t.Clr is { IsEnum: true } e && Fits32(new AType("prim", "", Enum.GetUnderlyingType(e)));
        return Type.GetTypeCode(c) is TypeCode.Boolean or TypeCode.Char or TypeCode.SByte or TypeCode.Byte or TypeCode.Int16
            or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single;
    }

    /// <summary>Plain Alice enums declared over a 64-bit integer (`enum E : Int64`): they do not pack.</summary>
    readonly HashSet<TypeSym> wideEnums = [];

    /// <summary>A demoted (or ineligible) sum type demotes every sum type it holds or that holds it.</summary>
    void PropagateDemotions(List<(EnumDecl Decl, TypeSym Sym, string File)> decls)
    {
        for (bool again = true; again;)
        {
            again = false;
            foreach (var (d, s, f) in decls)
                foreach (var v in s.Variants)
                    foreach (var fn in v.FieldOrder)
                    {
                        var ft = v.Fields[fn].Type;
                        var inner = ft.Sym?.Kind == "variant" ? ft.Sym.Parent : ft.Sym;
                        if (inner?.Kind != "adt" || !Affine.Layouts.TryGetValue(inner, out var il)) continue;
                        var ol = Affine.Layouts[s];
                        Node at = FieldNode(d, v, fn);
                        if (ol.Arena && (!il.Arena || ft.Nullable))
                        {
                            Violate(ol, f, at, "AL0240", $"affine enum `{s.Name}`: field `{fn}` of {v.Name} holds {inner.Name}, which is a record type, not an affine enum{(ft.Nullable ? " (nullable)" : "")}; make {inner.Name} an affine enum or drop `affine`",
                                $"field `{fn}` of {v.Name} holds {inner.Name}{(ft.Nullable ? "?" : "")}, which is records");
                            again = true;
                        }
                        else if (il.Arena && !ol.Arena)
                        {
                            Violate(il, f, at, "AL0241", $"affine value of `{inner.Name}` escapes: stored in field `{fn}` of {v.Name}, and {s.Name} is records; make {s.Name} an affine enum or drop `affine`",
                                $"stored in field `{fn}` of {v.Name} (records)");
                            again = true;
                        }
                    }
        }
    }

    static Node FieldNode(EnumDecl d, TypeSym v, string field)
    {
        int i = d.Names.IndexOf(v.Name);
        return i >= 0 && d.Fields[i]?.FirstOrDefault(p => p.Name == field) is { } p ? p : d;
    }

    // ---- type positions: fields, collections, nullables ----

    void ScanTypePositions(List<(EnumDecl Decl, TypeSym Sym, string File)> decls)
    {
        foreach (var t in Types.Values.Distinct())
        {
            if (t.Kind is "adt" or "variant") continue;
            foreach (var fs in t.Fields.Values)
            {
                var c = Cand(fs.Type)?.Enum ?? NestedCand(fs.Type);
                if (c == null) continue;
                Node at = (Node?)t.Decl ?? decls[0].Decl;
                Escape(Affine.Layouts[c], FileOfType(t), at, fs.Name, $"stored in field `{fs.Name}` of {t.Name}");
            }
        }
        foreach (var (body, f) in AffineBodies())
            AstWalk.Each(body, n =>
            {
                if (n is LocalDecl { Local: { } dl } && NestedCand(dl.Type) is { } dc)
                    Escape(Affine.Layouts[dc], f, n, dl.Name, dl.Type.Nullable ? $"nullable `{dl.Type.Cs}`" : $"stored in a collection or generic value (`{dl.Type.Cs}`)");
                if (n is Expr e && NestedCand(e.Type) is { } c)
                    Escape(Affine.Layouts[c], f, e, Describe(e), e is LambdaExpr || e.Type.Kind == "func" ? $"captured by a block or lambda (`{e.Type.Cs}`)"
                        : e.Type.Nullable && Cand(e.Type.Sym) != null ? $"nullable `{e.Type.Cs}`" : $"stored in a collection or generic value (`{e.Type.Cs}`)");
            });
        foreach (var m in AllMethods())
            foreach (var p in m.Params)
                if (NestedCand(p.Type) is { } c && m.Decl != null)
                    Escape(Affine.Layouts[c], FileOf(m.Decl), m.Decl, p.Name, $"parameter `{p.Name}` of {m.Name} is `{p.Type.Cs}`");
    }

    string FileOfType(TypeSym t)
    {
        static bool Has(List<Item> items, TypeSym t) => items.Any(it => it switch
        {
            TypeDecl d => d.Sym == t || Has(d.Members, t),
            NamespaceItem n => Has(n.Items, t),
            _ => false,
        });
        return Files.FirstOrDefault(f => Has(f.Items, t))?.Path ?? (Files.Count > 0 ? Files[0].Path : "");
    }

    IEnumerable<MethodSym> AllMethods() =>
        Types.Values.Append(Program).Distinct().SelectMany(t => t.Methods.Values.SelectMany(x => x)).Distinct();

    static string Describe(Expr e) => e switch
    {
        NameExpr n => n.Name,
        CallExpr c => c.Name,
        _ => e.Type.Sym?.Name ?? "value",
    };

    /// <summary>Every body the analysis reads: each file's top-level statements and each method with a body.</summary>
    IEnumerable<(Body Body, string File)> AffineBodies()
    {
        foreach (var f in Files)
        {
            var top = f.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList();
            if (top.Count > 0) yield return (new Body(top, null), f.Path);
            foreach (var m in Methods(f.Items)) yield return (new Body(m.Body, m), FileOf(m));
        }
    }

    static IEnumerable<MethodDecl> Methods(List<Item> items)
    {
        foreach (var it in items)
            switch (it)
            {
                case NamespaceItem n: foreach (var m in Methods(n.Items)) yield return m; break;
                case TypeDecl d: foreach (var m in Methods(d.Members)) yield return m; break;
                case MethodDecl { IsTemplate: false, HasBody: true } m: yield return m; break;
            }
    }

    /// <summary>A body to analyse: statements plus the method (null for top-level code).</summary>
    sealed record Body(List<Stmt> Stmts, MethodDecl? Method) : IEnumerable<Node>
    {
        public IEnumerator<Node> GetEnumerator() => Stmts.Cast<Node>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---- layout ----

    void Layout(ArenaLayout l)
    {
        l.Variants.Clear();
        l.Slot.Clear();
        for (int tag = 0; tag < l.Enum.Variants.Count; tag++)
        {
            var v = l.Enum.Variants[tag];
            var fields = v.FieldOrder.Select(n => v.Fields[n].Type).ToList();
            var av = new ArenaVariant { Sym = v, Tag = tag };
            if (fields.Count == 0) av.Packed = true;
            else if (fields.Count == 1 && Cand(fields[0]) == null && Fits32(fields[0])) { av.Packed = true; av.PackedField = fields[0]; }
            else
            {
                av.SlotIndex = new int[fields.Count];
                var used = new HashSet<int>();
                for (int j = 0; j < fields.Count; j++)
                {
                    bool term = Cand(fields[j]) != null;
                    int k = Enumerable.Range(0, l.Slot.Count).FirstOrDefault(i => !used.Contains(i) && SameSlot(l.Slot[i], fields[j], term), -1);
                    if (k < 0)
                    {
                        k = l.Slot.Count;
                        l.Slot.Add(new SlotField { Name = "f" + k, Type = term ? l.Enum.Type : fields[j], IsTerm = term });
                    }
                    used.Add(k);
                    av.SlotIndex[j] = k;
                }
            }
            l.Variants.Add(av);
        }
    }

    static bool SameSlot(SlotField s, AType t, bool term) => term ? s.IsTerm : !s.IsTerm && s.Type.Cs == t.Cs;
}
