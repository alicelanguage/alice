using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    void BindBlock(List<Stmt> body)
    {
        foreach (var s in body) BindStmt(s);
    }

    void Scoped(List<Stmt> body)
    {
        PushScope();
        BindBlock(body);
        PopScope();
    }

    void BindStmt(Stmt s)
    {
        switch (s)
        {
            case ExprStmt es:
                BindExpr(es.Expr);
                break;
            case LocalDecl d:
                {
                    AType vt = d.Value != null ? BindExpr(d.Value) : AType.Unknown;
                    AType t = d.Type != null ? ResolveType(d.Type) : vt;
                    d.Local = Declare(d, d.Name, t, d.IsVal);
                    break;
                }
            case Assign a:
                BindAssign(a);
                break;
            case MultiAssign ma:
                BindMultiAssign(ma);
                break;
            case IfStmt i:
                HoistBranchLocals(i.Bodies.Append(i.Else));
                foreach (var c in i.Conds) BindExpr(c);
                foreach (var b in i.Bodies) Scoped(b);
                if (i.Else != null) Scoped(i.Else);
                break;
            case WhileStmt w:
                BindExpr(w.Cond);
                Scoped(w.Body);
                break;
            case ForStmt f:
                BindFor(f);
                break;
            case CaseStmt c:
                HoistBranchLocals(c.Whens.Select(w => w.Body).Append(c.Else));
                BindCase(c);
                break;
            case BeginStmt b:
                Scoped(b.Body);
                foreach (var r in b.Rescues)
                {
                    PushScope();
                    r.Resolved = r.Type != null ? ResolveType(r.Type) : ClrTypes.FromClr(typeof(Exception));
                    if (r.Name != null) r.Local = Declare(r, r.Name, r.Resolved);
                    BindBlock(r.Body);
                    PopScope();
                }
                if (b.Ensure != null) Scoped(b.Ensure);
                break;
            case ReturnStmt r:
                if (r.Value != null)
                {
                    var t = BindExpr(r.Value);
                    if (lambdas.Count == 0) returns.Add(t);
                }
                break;
            case NextStmt n:
                if (n.Value != null) BindExpr(n.Value);
                break;
            case RaiseStmt rs:
                BindExpr(rs.Value);
                break;
            case BreakStmt:
                break;
            case UsingStmt us:
                {
                    var vt = BindExpr(us.Value);
                    PushScope();
                    us.Local = Declare(us, us.Name, vt);
                    BindBlock(us.Body);
                    PopScope();
                    break;
                }
        }
    }

    void BindAssign(Assign a)
    {
        if (a.Target is NameExpr n)
        {
            var l = FindLocal(n.Name);
            if (l == null && a.Op == "=")
            {
                AType vt = BindExpr(a.Value);
                a.Declares = Declare(a, n.Name, vt);
                n.Sym = a.Declares;
                n.Res = "local";
                n.Type = vt;
                return;
            }
            if (l == null)
            {
                Hoist(n.Name);
                BindExpr(a.Value);
                n.Res = "unknown";
                return;
            }
            CheckWrite(n, l);
            n.Sym = l;
            n.Res = "local";
            n.Type = l.Type;
            UseLocal(l);
            AType v = BindExpr(a.Value);
            if (!l.Type.IsKnown && v.IsKnown && a.Op == "=") l.Type = v;
            return;
        }
        if (a.Target is IvarExpr iv && curType.Kind is "class" or "struct" or "record" && a.Op == "=")
        {
            // Ruby: assigning @x declares the field; its type comes from the value (attr_accessor reuses it)
            var vt0 = BindExpr(a.Value);
            var f = curType.FindField(iv.Name);
            if (f == null)
            {
                curType.Fields[iv.Name] = new FieldSym { Name = iv.Name, CsName = Mangle(iv.Name), Type = vt0.IsKnown ? vt0 : AType.Dynamic, Kind = "inferred", Owner = curType };
                changed = true;
            }
            else if (!f.Type.IsKnown && vt0.IsKnown)
            {
                f.Type = vt0;
                changed = true;
            }
        }
        BindExpr(a.Target);
        BindExpr(a.Value);
        if (curMethod != null && lambdas.Count == 0 && a.Target is IvarExpr or NameExpr { Res: "field" } or CallExpr { Res: "field", Recv: SelfExpr }) curMethod.Mutates = true;
        CheckSharedTargetWrite(a.Target);
    }

    /// <summary>Rust-style write rules: `val` locals are immutable; spawned tasks may not write captured locals.</summary>
    void CheckWrite(Node at, LocalSym l)
    {
        if (l.IsVal) throw Err(at, $"cannot assign to immutable '{l.Name}' (declared with val)");
        if (l.SpawnDepth < spawnDepth)
            throw Err(at, $"data race: spawned task writes captured variable '{l.Name}'; send it through a Channel or use an Actor");
    }

    /// <summary>Ruby: a local first assigned inside an if/case branch belongs to the method (visible after it).</summary>
    void HoistBranchLocals(IEnumerable<List<Stmt>?> bodies)
    {
        if (lambdas.Count > 0) return;
        foreach (var b in bodies)
            if (b != null)
                foreach (var st in b)
                    if (st is Assign { Target: NameExpr n, Op: "=" } && FindLocal(n.Name) == null) Hoist(n.Name);
    }

    void Hoist(string name)
    {
        if (hoist.Add(name)) changed = true;
    }

    void BindMultiAssign(MultiAssign ma)
    {
        var vts = ma.Values.Select(BindExpr).ToList();
        if (ma.Values.Count == 1 && vts[0].Kind == "tuple" && vts[0].Args.Count == ma.Names.Count) vts = vts[0].Args;
        ma.Locals = new List<LocalSym>();
        ma.Declares = false;
        var existing = ma.Names.Select(FindLocal).ToList();
        if (existing.All(e => e != null))
        {
            foreach (var l in existing) { CheckWrite(ma, l!); UseLocal(l!); }
            ma.Locals = existing.Select(e => e!).ToList();
            return;
        }
        if (existing.Any(e => e != null)) throw Err(ma, "mixing new and existing variables in multiple assignment");
        ma.Declares = true;
        for (int i = 0; i < ma.Names.Count; i++)
            ma.Locals.Add(Declare(ma, ma.Names[i], i < vts.Count ? vts[i] : AType.Unknown));
    }

    void BindFor(ForStmt f)
    {
        AType elem;
        if (f.Iter is RangeExpr r)
        {
            var a = BindExpr(r.A);
            var b = BindExpr(r.B);
            elem = Unify([a, b]);
            if (!elem.IsKnown) elem = AType.Int;
            r.Type = elem;
        }
        else if (f.Await)
        {
            var st = BindExpr(f.Iter);
            var ae = st.Clr?.GetInterfaces().Append(st.Clr).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
            if (ae == null && st.IsKnown) throw Err(f, $"for await needs an IAsyncEnumerable (got {st.Cs})");
            if (curMethod != null && !curMethod.IsAsync) throw Err(f, "for await is only allowed inside an async def");
            elem = ae != null ? ClrTypes.FromClr(ae.GetGenericArguments()[0]) : AType.Unknown;
        }
        else elem = Builtins.ElemOf(BindExpr(f.Iter));
        PushScope();
        f.Locals = new List<LocalSym>();
        if (f.Names.Count == 1) f.Locals.Add(Declare(f, f.Names[0], elem));
        else
        {
            var parts = elem.Args.Count == f.Names.Count ? elem.Args : null;
            for (int i = 0; i < f.Names.Count; i++) f.Locals.Add(Declare(f, f.Names[i], parts?[i] ?? AType.Unknown));
        }
        BindBlock(f.Body);
        PopScope();
    }

    void BindCase(CaseStmt c)
    {
        AType subj = c.Subject != null ? BindExpr(c.Subject) : AType.Unknown;
        var covered = new HashSet<TypeSym>();
        foreach (var w in c.Whens)
        {
            PushScope();
            foreach (var p in w.Patterns)
            {
                if (c.Subject != null && p is BindPatternExpr bp)
                {
                    if (bp.Name == "_") p.Res = "wild";
                    else { bp.Local = Declare(bp, bp.Name, subj); p.Res = "bindpat"; }
                    p.Type = subj;
                    continue;
                }
                if (c.Subject != null && p is HashPatternExpr hp)
                {
                    if (!Lowering.Lowerer.IsDict(subj, out var kt, out var vt)) throw Err(p, "hash patterns need a Dictionary subject");
                    hp.Binds = new List<LocalSym?>();
                    for (int k = 0; k < hp.Keys.Count; k++)
                    {
                        BindExpr(hp.Keys[k]);
                        if (hp.Values[k] is BindPatternExpr hb && hb.Name != "_") hp.Binds.Add(Declare(hb, hb.Name, vt));
                        else
                        {
                            if (hp.Values[k] is not BindPatternExpr) BindExpr(hp.Values[k]);
                            hp.Binds.Add(null);
                        }
                    }
                    p.Res = "hashpat";
                    p.Type = subj;
                    continue;
                }
                if (c.Subject != null && p is ArrayPatternExpr ap)
                {
                    var et = Builtins.ElemOf(subj);
                    foreach (var lit in ap.Literals) if (lit != null) BindExpr(lit);
                    ap.Binds = ap.Names.Select(n => n == "_" ? null! : Declare(ap, n, et)).ToList();
                    ap.RestSym = ap.Rest != null ? Declare(ap, ap.Rest, AType.Array(et)) : null;
                    p.Res = "arraypat";
                    p.Type = subj;
                    continue;
                }
                if (c.Subject != null && p is NewExpr ne && ResolveType(ne.TypeRef) is AType dt && dt.Sym is { FieldOrder.Count: > 0 } ds)
                {
                    if (ne.Args.Count != ds.FieldOrder.Count) throw Err(p, $"{ds.Name} has {ds.FieldOrder.Count} fields, pattern has {ne.Args.Count}");
                    var d = new Destructure { Type = dt };
                    bool total = true;
                    for (int i = 0; i < ne.Args.Count; i++)
                    {
                        var field = ds.Fields[ds.FieldOrder[i]];
                        var arg = ne.Args[i].Value!;
                        if (arg is NameExpr n && n.Name == "_") d.Parts.Add((field.CsName, null, null));
                        else if (arg is NameExpr bn && FindLocal(bn.Name) == null) d.Parts.Add((field.CsName, Declare(arg, bn.Name, field.Type), null));
                        else
                        {
                            BindExpr(arg);
                            d.Parts.Add((field.CsName, null, arg));
                            total = false;
                        }
                    }
                    if (w.Patterns.Count > 1 && d.Parts.Any(x => x.Bind != null)) throw Err(p, "a `when` with several patterns cannot bind variables");
                    p.Res = "destructure";
                    p.Sym = d;
                    p.Type = dt;
                    if (total) covered.Add(ds);
                }
                else if (c.Subject != null && p is ConstExpr ce && TypeByName(ce.Ref.Name, ce.Ref.Args) is AType pt && !IsConstValue(ce))
                {
                    p.Res = "type";
                    p.Type = pt;
                    if (pt.Sym != null) covered.Add(pt.Sym);
                }
                else if (p is RangeExpr r)
                {
                    BindExpr(r.A);
                    BindExpr(r.B);
                }
                else BindExpr(p);
            }
            if (w.Guard != null) BindExpr(w.Guard);
            BindBlock(w.Body);
            PopScope();
        }
        if (c.Else != null) Scoped(c.Else);
        if (c.Else == null && c.Whens.Any(w => w.Guard == null && w.Patterns.Any(p => p.Res is "wild" or "bindpat"))) { c.Exhaustive = true; return; }
        // Rust-style exhaustiveness for sum types
        c.Exhaustive = false;
        if (c.Else == null && subj.Sym is { Kind: "enum", Decl: null } en && Types.Values.FirstOrDefault(t => t == en) is { } enumSym)
        {
            var names = enumSym.Fields.Values.Where(f => f.Kind == "const").Select(f => f.Name).ToList();
            var hit = c.Whens.SelectMany(w => w.Patterns).Select(p => p is ConstExpr ce ? ce.Ref.Name.Split('.')[^1] : null).Where(n => n != null).ToHashSet();
            var missing = names.Where(n => !hit.Contains(n)).ToList();
            if (missing.Count > 0 && hit.Count > 0) throw Err(c, $"non-exhaustive match on {enumSym.Name}: missing {string.Join(", ", missing)}");
            c.Exhaustive = missing.Count == 0;
        }
        // array patterns: exhaustive when every length below the shortest *rest pattern has its own pattern
        var aps = c.Whens.SelectMany(w => w.Patterns).OfType<ArrayPatternExpr>().ToList();
        if (c.Else == null && aps.Count > 0)
        {
            var withRest = aps.Where(a => a.Rest != null).Select(a => a.Names.Count).DefaultIfEmpty(-1).Min();
            var exact = aps.Where(a => a.Rest == null).Select(a => a.Names.Count).ToHashSet();
            if (withRest >= 0 && Enumerable.Range(0, withRest).All(exact.Contains)) c.Exhaustive = true;
        }
        if (c.Else == null && subj.Sym is { Kind: "adt" } adt)
        {
            var missing = adt.Variants.Where(v => !covered.Contains(v)).Select(v => v.Name).ToList();
            if (missing.Count > 0) throw Err(c, $"non-exhaustive match on {adt.Name}: missing {string.Join(", ", missing)}");
            c.Exhaustive = true;
        }
    }

    bool IsConstValue(ConstExpr ce) => curType.FindField(ce.Ref.Name) != null || Program.Fields.ContainsKey(ce.Ref.Name);
}
