using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    AType BindExpr(Expr e)
    {
        e.Type = BindCore(e);
        return e.Type;
    }

    AType BindCore(Expr e)
    {
        switch (e)
        {
            case LitExpr l:
                return l.Kind switch
                {
                    "nil" => AType.Null,
                    _ => LiteralType(l),
                };
            case InterpExpr ie:
                foreach (var h in ie.Holes) BindExpr(h);
                return AType.String;
            case NameExpr n:
                return BindName(n);
            case IvarExpr iv:
                {
                    UseThis();
                    var f = curType.FindField(iv.Name);
                    iv.Sym = f;
                    return f?.Type ?? AType.Unknown;
                }
            case SelfExpr:
                UseThis();
                return curType.Type;
            case SuperExpr sup:
                {
                    UseThis();
                    if (sup.Args != null) BindArgs(sup.Args);
                    var bm = curType.BaseClass?.FindMethod(curMethod?.Name ?? "", sup.Args?.Count ?? curMethod?.Params.Count ?? 0);
                    if (bm != null) return bm.Ret;
                    // `super` in an override of a .NET virtual (MonoGame's Update/Draw): the base method's result
                    return curMethod != null && (sup.Args == null || sup.Args.Count == curMethod.Params.Count) && ClrOverride(curMethod, curType) is { } cb
                        ? ClrTypes.FromClr(cb.ReturnType) : AType.Unknown;
                }
            case ConstExpr ce:
                return BindConst(ce);
            case CallExpr c:
                return BindCall(c);
            case NewExpr ne:
                {
                    if (ne.TypeRef.Args.Count == 0 && !ne.TypeRef.Name.Contains('.') && TypeByName(ne.TypeRef.Name, ne.TypeRef.Args) == null)
                    {
                        ne.AsCall ??= new CallExpr { Name = ne.TypeRef.Name, Args = ne.Args, Block = ne.Block, Parens = true, Line = ne.Line };
                        return BindExpr(ne.AsCall);
                    }
                    var t = ResolveType(ne.TypeRef);
                    if (IsConcurrentType(t)) MarkConcurrentArgs(ne.Args);
                    CheckActorNew(ne, t);
                    BindArgs(ne.Args);
                    if (ne.TypeRef.Name == "Hash" && t.Args.Count == 2 && t.Clr is { IsGenericType: true } ht && ht.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                        && (ne.Block == null && ne.Args is [{ Name: null, Value: { } dv }] && !dv.Type.IsUnknown && dv.Type.Clr is not { IsGenericType: true }
                            || ne.Block != null && ne.Args.Count == 0))
                    {
                        // Ruby `Hash.new(0)` / `Hash.new { |h, k| ... }`: missing keys read as the default (ADR: CTO decision)
                        if (ne.Block != null)
                        {
                            // `{ |h, k| h[k] = v }`: the assignment's value is the block's (Ruby), read back from h
                            if (ne.Block.Body is [.., Assign { Op: "=", Target: IndexExpr st }]) ne.Block.Body.Add(new ExprStmt { Expr = st, Line = st.Line });
                            BindLambda(ne.Block, [t, t.Args[0]]);
                        }
                        return DefaultHashOf(t);
                    }
                    if (ne.Block != null) BindLambda(ne.Block, t.Kind == "array" ? [AType.Int] : new List<AType>());
                    return t;
                }
            case IndexExpr ix:
                return BindIndex(ix);
            case BinaryExpr b:
                return BindBinary(b);
            case UnaryExpr u:
                {
                    var t = BindExpr(u.E);
                    if (u.Op == "!") return AType.Bool;
                    if (u.Op == "&") return new AType("prim", t.Cs + "*");
                    return t;
                }
            case TernaryExpr te:
                BindExpr(te.C);
                return Unify([BindExpr(te.A), BindExpr(te.B)]);
            case RangeExpr r:
                BindExpr(r.A);
                BindExpr(r.B);
                return Builtins.Generic(typeof(IEnumerable<>), Unify([r.A.Type, r.B.Type]).IsKnown ? Unify([r.A.Type, r.B.Type]) : AType.Int);
            case ArrayLit a:
                {
                    var its = a.Items.Select(BindExpr).ToList();
                    if (a.Of != null) return Builtins.Generic(typeof(List<>), ResolveType(a.Of));
                    var el = Unify(its);
                    return el.IsKnown ? AType.Array(el) : AType.Unknown;
                }
            case HashLit h:
                {
                    var ks = h.Keys.Select(BindExpr).ToList();
                    var vs = h.Values.Select(BindExpr).ToList();
                    var kt = h.K != null ? ResolveType(h.K) : Unify(ks);
                    var vt = h.V != null ? ResolveType(h.V) : Unify(vs);
                    return kt.IsKnown && vt.IsKnown ? Builtins.Generic(typeof(Dictionary<,>), kt, vt) : AType.Unknown;
                }
            case TupleLit tl:
                {
                    var tt = Tuple(tl.Items.Select(BindExpr).ToList());
                    if (tl.Names == null) return tt;
                    return NamedTuple(tt, tl.Names);
                }
            case LambdaExpr lam:
                pendingConcurrent = concurrentLambdas.Contains(lam);
                BindLambda(lam, new List<AType>());
                return lam.ReturnsValue && lam.Result.IsKnown
                    ? Func(lam.ParamSyms.Select(p => p.Type).ToList(), lam.Result)
                    : lam.ParamSyms.All(p => p.Type.IsKnown) && !lam.ReturnsValue ? Func(lam.ParamSyms.Select(p => p.Type).ToList(), AType.Void) : AType.Unknown;
            case TypeTestExpr tt:
                return BindTypeTest(tt);
            case InvokeExpr inv:
                {
                    var t = BindExpr(inv.Target);
                    BindArgs(inv.Args);
                    return t.Kind == "func" && t.Args.Count > 0 ? t.Args[^1] : AType.Unknown;
                }
            case RegexExpr:
                return ClrTypes.FromClr(typeof(System.Text.RegularExpressions.Regex));
            case AwaitExpr aw:
                {
                    var t = BindExpr(aw.E);
                    if (t.Clr is { IsGenericType: true } g && (g.GetGenericTypeDefinition() == typeof(Task<>) || g.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                        return t.Args.Count == 1 ? t.Args[0] : ClrTypes.FromClr(g.GetGenericArguments()[0]);
                    if (t.Clr == typeof(Task) || t.Clr == typeof(ValueTask)) return AType.Void;
                    return t.Kind == "dynamic" ? t : AType.Unknown;
                }
            case SpawnExpr sp:
                {
                    spawnDepth++;
                    BindLambda(sp.Body, new List<AType>());
                    spawnDepth--;
                    return sp.Body.ReturnsValue && sp.Body.Result.IsKnown
                        ? Builtins.Generic(typeof(Task<>), sp.Body.Result)
                        : ClrTypes.FromClr(typeof(Task));
                }
            case YieldExpr y:
                {
                    BindArgs(y.Args);
                    if (curMethod != null) curMethod.UsesYield = true;
                    var bp = curMethod?.Params.FirstOrDefault(p => p.Block);
                    if (bp == null && curMethod != null && IteratorElement(curMethod.Ret) != null) return AType.Void;   // generator: `yield return`
                    if (bp == null) throw Err(y, "yield needs a block parameter (def name(&block : A -> R)), or a return type IEnumerable(T) / IEnumerator(T) / IAsyncEnumerable(T) for an iterator");
                    y.Sym = bp;
                    return bp.Type.Kind == "func" ? bp.Type.Args[^1] : AType.Unknown;
                }
            case StmtExpr se:
                {
                    BindStmt(se.Stmt);
                    var body = new List<Stmt> { se.Stmt };
                    return TailType(body) ?? AType.Unknown;
                }
        }
        return AType.Unknown;
    }

    AType BindName(NameExpr n)
    {
        var l = FindLocal(n.Name);
        if (l != null)
        {
            UseLocal(l);
            CheckIsolatedRead(n, l);
            n.Res = "local";
            n.Sym = l;
            if (narrowed.ContainsKey(l))
            {
                n.Narrowed = true;
                return NonNullValue(l.Type);
            }
            n.Narrowed = false;
            return l.Type;
        }
        var f = curType.FindField(n.Name);
        if (f != null)
        {
            if (!f.IsStatic) UseThis();
            n.Res = "field";
            n.Sym = f;
            return f.Type;
        }
        var m = curType.FindMethod(n.Name, 0) ?? Program.FindMethod(n.Name, 0);
        if (m != null)
        {
            if (!m.IsStatic) UseThis();
            SelfCallEffects(n, m);
            n.Res = "call";
            n.Sym = m;
            return Async(m, m.Ret);
        }
        if (Builtins.FreeFunctions.Contains(n.Name))
        {
            n.Res = "call";
            return Builtins.FreeReturn(n.Name, new List<AType>());
        }
        if (Builtins.PreludeZeroArg.TryGetValue(n.Name, out var pz))
        {
            n.Res = "call";
            return ClrTypes.FromClr(pz);
        }
        if (BaseClr(curType) is Type bc && ClrTypes.Lookup(bc, n.Name, 0, false, false) is { } inherited)
        {
            UseThis();
            n.Res = inherited.Kind == "method" ? "call" : "field";
            return inherited.Type;
        }
        if (curType.Kind == "extension")
        {
            var self = new CallExpr { Recv = new SelfExpr { Line = n.Line }, Name = n.Name, Line = n.Line };
            n.Res = "selfcall";
            n.Sym = self;
            return BindExpr(self);
        }
        if (closed.ContainsKey(n.Name)) Hoist(n.Name);
        n.Res = "unknown";
        return AType.Unknown;
    }

    /// <summary>The .NET base class of an Alice class (`class Game1 &lt; Game`), if any.</summary>
    static Type? BaseClr(TypeSym t)
    {
        for (var s = t; s != null; s = s.BaseClass)
            foreach (var b in s.Bases)
                if (b.Clr != null && !b.Clr.IsInterface) return b.Clr;
        return null;
    }

    AType BindConst(ConstExpr ce)
    {
        string name = ce.Ref.Name;
        // C# "Color Color" rule: an inherited instance member wins over a type of the same name
        var head = name.Split('.')[0];
        if (ce.Ref.Args.Count == 0 && curMethod is { IsStatic: false } && BaseClr(curType) is Type bt
            && ClrTypes.Lookup(bt, head, 0, false, false) is { Kind: not "method" } mem)
        {
            UseThis();
            var path = new ConstPath { Owner = mem.Type, OwnerText = head };
            AType cur = mem.Type;
            foreach (var seg in name.Split('.').Skip(1))
            {
                bool call;
                (cur, call) = MemberType(cur, seg, false);
                path.Members.Add((seg, call));
            }
            ce.Res = "path";
            ce.Sym = path;
            return cur;
        }
        var f = curType.FindField(name) ?? Program.FindField(name);
        if (f != null && ce.Ref.Args.Count == 0)
        {
            ce.Res = "const";
            ce.Sym = f;
            return f.Type;
        }
        var t = TypeByName(name, ce.Ref.Args);
        if (t != null)
        {
            ce.Res = "type";
            ce.Sym = t;
            return t;
        }
        // Longest type prefix, then static member chain: Math.PI, Color.Green, DateTime.UnixEpoch.Year
        var segs = name.Split('.');
        for (int k = segs.Length - 1; k >= 1; k--)
        {
            var owner = TypeByName(string.Join(".", segs.Take(k)), new List<TypeRef>());
            if (owner == null) continue;
            ce.Res = "path";
            var path = new ConstPath { Owner = owner };
            AType cur = owner;
            bool isStatic = true;
            for (int j = k; j < segs.Length; j++)
            {
                bool call;
                if (isStatic && owner.Clr is { IsPrimitive: true } && NumericLimit(segs[j]) is { } limit) segs[j] = limit;
                (cur, call) = MemberType(cur, segs[j], isStatic);
                path.Members.Add((segs[j], call));
                isStatic = false;
            }
            ce.Sym = path;
            return cur;
        }
        ce.Res = "unknown";
        if (ce.Ref.Args.Count > 0) return AType.Unknown;
        // the program arguments (the Main parameter; Prelude.ARGV inside methods)
        if (name == "ARGV") return AType.Array(AType.String);
        // a member chain on a constant value (`ARGV.Length`, `MASK.Length`): emitted as written, typed here
        AType? headType = segs[0] == "ARGV" ? AType.Array(AType.String) : (curType.FindField(segs[0]) ?? Program.FindField(segs[0]))?.Type;
        if (segs.Length > 1 && headType is { IsKnown: true })
        {
            AType at = headType;
            foreach (var seg in segs.Skip(1))
            {
                (at, _) = at.Kind == "array" && at.Clr == null && ClrTypes.Lookup(typeof(Array), seg, 0, false, false) is { Kind: not "method" } am
                    ? (am.Type, false) : MemberType(at, seg, false);
                if (!at.IsKnown) break;
            }
            return at;
        }
        return AType.Unknown;
    }

    /// <summary>Crystal-style numeric limits: Int32::MAX, UInt8::MIN, Float64::INFINITY, Float64::NAN.</summary>
    static string? NumericLimit(string name) => name switch
    {
        "MAX" => "MaxValue",
        "MIN" => "MinValue",
        "INFINITY" => "PositiveInfinity",
        "NAN" => "NaN",
        _ => null,
    };

    (AType, bool) MemberType(AType owner, string member, bool isStatic)
    {
        if (owner.Sym != null)
        {
            var f = owner.Sym.FindField(member);
            if (f != null) return (f.Type, false);
            var m = owner.Sym.FindMethod(member, 0);
            if (m != null) return (m.Ret, true);
        }
        if (owner.Clr != null)
        {
            var mem = ClrTypes.Lookup(owner.Clr, member, 0, false, isStatic);
            if (mem != null) return (mem.Type, mem.Kind == "method");
        }
        return (AType.Unknown, false);
    }

    AType BindIndex(IndexExpr ix)
    {
        var t = BindExpr(ix.Target);
        BindArgs(ix.Args);
        // t[:a] on a named tuple: the element, resolved at compile time
        if (t.TupleNames is { } tn && ix.Args is [{ Value: LitExpr { Kind: "str" } key }])
        {
            int at = tn.IndexOf(key.Text.TrimStart(':'));
            if (at < 0) throw Err(ix, $"named tuple has no element `{key.Text.TrimStart(':')}` (elements: {string.Join(", ", tn)})");
            ix.TupleElement = Mangle(tn[at]);
            return t.Args[at];
        }
        if (ix.Args is [{ Value: LitExpr { Kind: "int" } at0 }] && int.TryParse(at0.Text, out int pos))
        {
            if (t.Kind == "tuple" && pos >= 0 && pos < t.Args.Count && pos < 7)
            {
                ix.TupleElement = t.TupleNames is { } names ? Mangle(names[pos]) : "Item" + (pos + 1);
                return t.Args[pos];
            }
            if (t.Clr is { IsGenericType: true } kvt && kvt.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) && pos is 0 or 1 && t.Args.Count == 2)
            {
                ix.TupleElement = pos == 0 ? "Key" : "Value";
                return t.Args[pos];
            }
        }
        // Ruby slices and negative indexes on arrays, lists and strings
        bool list = t.Clr is { IsGenericType: true } lg && lg.GetGenericTypeDefinition() == typeof(List<>) || t.Clr == null && t.OpenClr == typeof(List<>);
        if (t.Kind == "array" && t.Elem != null && t.Cs.EndsWith("[]") || t.IsString || list)
        {
            AType sliced = t.Kind == "array" ? AType.Array(t.Elem!) : t;
            if (ix.Args is [{ Mod: "", Value: RangeExpr }]) { ix.Slice = "range"; return sliced; }
            if (ix.Args is [{ Mod: "", Value: { Type.IsInteger: true } }, { Mod: "", Value: { Type.IsInteger: true } }]) { ix.Slice = "len"; return sliced; }
            // a literal negative index counts from the end; the target is read twice, so only a plain local/field/@ivar
            if (ix.Args is [{ Mod: "", Value: LitExpr { Kind: "int" } neg }] && int.TryParse(neg.Text, out int k) && k < 0
                && ix.Target is NameExpr { Res: "local" or "field" } or IvarExpr or ConstExpr { Res: "const" })
                ix.FromEnd = -k;
        }
        if (t.Kind == "array" && t.Elem != null) return t.Elem;
        if (t.IsString) return AType.Char;
        if (t.Sym != null)
        {
            var m = t.Sym.FindMethod("[]", ix.Args.Count);
            if (m != null) return m.Ret;
        }
        if (t.Clr != null)
        {
            foreach (var pr in t.Clr.GetProperties())
                if (pr.GetIndexParameters().Length == ix.Args.Count && pr.Name == "Item") return ClrTypes.FromClr(pr.PropertyType);
        }
        return t.Kind == "dynamic" ? t : AType.Unknown;
    }

    /// <summary>`Alice.Runtime.DefaultHash(K, V)`: a Dictionary subclass whose indexer reads the default. The binder sees
    /// a Dictionary (Clr), the C# declarations spell DefaultHash so `h[k]` and `h[k] += 1` go through its indexer.</summary>
    static AType DefaultHashOf(AType dict) =>
        new("class", $"global::Alice.Runtime.DefaultHash<{dict.Args[0].Cs}, {dict.Args[1].Cs}>", dict.Clr) { Args = dict.Args, Sym = dict.Sym };

    static bool IsDefaultHash(AType t) => t.Cs.StartsWith("global::Alice.Runtime.DefaultHash<");

    static bool IsPlainHash(AType t) =>
        !IsDefaultHash(t) && t.Args.Count == 2 && t.Clr is { IsGenericType: true } g && g.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    /// <summary>
    /// A field or parameter declared `Hash(K, V)` that is only ever given DefaultHash values (`Hash.new(0)`) takes the
    /// DefaultHash type, so its reads honor the default; one plain Dictionary value anywhere keeps it a Dictionary.
    /// Returns the slot's type for this binding pass (the flags only grow, so the fixpoint settles).
    /// </summary>
    AType FlagHash(AType slot, ref AType? declared, ref bool sawDefault, ref bool sawPlain, AType value)
    {
        var decl = declared ?? slot;
        if (!IsPlainHash(decl) || value.Clr != decl.Clr) return slot;
        if (IsDefaultHash(value)) sawDefault = true;
        else if (IsPlainHash(value)) sawPlain = true;
        else return slot;
        declared = decl;
        var want = sawDefault && !sawPlain ? DefaultHashOf(decl) : decl;
        if (!want.Same(slot)) changed = true;
        return want;
    }

    AType BindBinary(BinaryExpr b)
    {
        var l = BindExpr(b.L);
        var r = BindExpr(b.R);
        b.Res = "";
        // List(T) of a type parameter (`class Shelf(T)`) has only the open definition
        bool listy(AType t) => t.Kind == "array" || t.Clr is { IsGenericType: true } g && g.GetGenericTypeDefinition() == typeof(List<>) || t.Clr == null && t.OpenClr == typeof(List<>);
        switch (b.Op)
        {
            case "||" when !l.IsBool && l.Kind is not ("unknown" or "dynamic"):
                // Ruby `a || b` on a nil-able value: the first non-nil (C# ??)
                b.Res = "coalesce";
                return r.IsKnown ? r : ResolveNonNull(l);
            case "=~" or "!~":
                b.Res = b.Op == "=~" ? "match" : "nomatch";
                return b.Op == "=~" ? AType.Int.AsNullable() : AType.Bool;
            case "%" when l.IsString:
                b.Res = "format";
                return AType.String;
            case "*" when l.IsString && r.IsInteger:
                b.Res = "repeat";
                return AType.String;
            case "<<" when listy(l) || l.Clr == typeof(System.Text.StringBuilder):
                b.Res = "append";
                // `xs << v` mutates xs: self's state (F9) or, from concurrent code, a shared list
                NoteSelfWrite(b.L);
                if (IsShared(b.L) && !ThreadSafe(l)) throw Race(b, $"`<<` appends to a shared {l.Cs}");
                return l;
            case "*" when listy(l) && (r.IsInteger || r.IsString):
                b.Res = "arrmul";
                return r.IsString ? AType.String : Builtins.ElemOf(l) is { IsKnown: true } me ? AType.Array(me) : AType.Unknown;
            case "+" or "-" or "&" or "|" when listy(l) && listy(r):
                b.Res = "set" + b.Op;
                var el = Builtins.ElemOf(l);
                return el.IsKnown ? AType.Array(el) : AType.Unknown;
            case "&&":
            case "||":
            case "==":
            case "!=":
            case "<":
            case ">":
            case "<=":
            case ">=":
                if (l.Sym != null && b.Op is not ("&&" or "||") && l.Sym.FindMethod(b.Op, 1) is MethodSym om) return om.Ret;
                return AType.Bool;
            case "<=>":
                if (l.Sym?.FindMethod("<=>", 1) is MethodSym cmp) { b.Res = "usercmp"; return cmp.Ret; }
                return AType.Int;
            case "<<" or ">>" when l.IsInteger && r.IsInteger && r.Cs != "int":
                b.Res = "shiftcount";   // the count is converted to Int32 (C# defines shifts for an int count only)
                return l.NumericRank < AType.Int.NumericRank ? AType.Int : l;
            case "**":
                if (l.IsInteger && r.IsInteger) return l.Cs == "long" ? AType.Long : AType.Int;
                return AType.Double;
        }
        if (l.Sym != null && l.Sym.FindMethod(b.Op, 1) is MethodSym m) return m.Ret;
        if (b.Op == "+" && (l.IsString || r.IsString)) return AType.String;
        if (l.IsNumeric && r.IsNumeric)
        {
            var hi = l.NumericRank >= r.NumericRank ? l : r;
            return hi.NumericRank < AType.Int.NumericRank ? AType.Int : hi;
        }
        if (l.IsNumeric && r.IsUnknown) return l;
        if (r.IsNumeric && l.IsUnknown) return r;
        if (l.Kind == "dynamic" || r.Kind == "dynamic") return ReferenceEquals(l, AType.Any) || ReferenceEquals(r, AType.Any) ? AType.Any : AType.Dynamic;
        if (l.Clr != null && l.Clr.IsEnum) return l;
        return AType.Unknown;
    }

    AType BindTypeTest(TypeTestExpr tt)
    {
        if (tt.E != null) BindExpr(tt.E);
        AType? rt = tt.Ref != null ? ResolveType(tt.Ref) : null;
        tt.Sym = rt;
        return tt.Kind switch
        {
            "is" or "nil?" => AType.Bool,
            "as" or "default" => rt!,
            "typeof" => ClrTypes.FromClr(typeof(Type)),
            "sizeof" => AType.Int,
            "not_nil!" => tt.E!.Type.Nullable ? ResolveNonNull(tt.E.Type) : tt.E.Type,
            _ => AType.Unknown,
        };
    }

    /// <summary>Element type of an iterator return type, null when the type is not one.</summary>
    static Type? IteratorElement(AType t)
    {
        if (t.Clr is not { IsGenericType: true } c) return t.Clr == typeof(System.Collections.IEnumerable) || t.Clr == typeof(System.Collections.IEnumerator) ? typeof(object) : null;
        var g = c.GetGenericTypeDefinition();
        return g == typeof(IEnumerable<>) || g == typeof(IEnumerator<>) || g == typeof(IAsyncEnumerable<>) || g == typeof(IAsyncEnumerator<>) ? c.GetGenericArguments()[0] : null;
    }

    /// <summary>`->name`: parameters and body from the referenced Alice method; returns their types.</summary>
    List<AType> ExpandMethodRef(LambdaExpr lam, Expr? recv, string name, List<AType> expected)
    {
        TypeSym? owner = recv switch
        {
            null => curType.FindMethod(name, -1) != null || curType.Methods.ContainsKey(name) ? curType : Program,
            ConstExpr ce => TypeByName(ce.Ref.Name, ce.Ref.Args)?.Sym,
            _ => BindExpr(recv).Sym,
        };
        var cands = owner != null && owner.Methods.TryGetValue(name, out var list) ? list : [];
        if (expected.Count > 0 && cands.Count > 1) cands = cands.Where(m => m.Params.Count(p => !p.Block) == expected.Count).ToList();
        if (cands.Count == 0) throw Err(lam, $"->{name}: no method `{name}`{(owner != null ? " in " + owner.Name : "")}");
        if (cands.Count > 1) throw Err(lam, $"->{name} is overloaded; give the parameter types: ->{name}(T1, T2)");
        var m = cands[0];
        var ps = m.Params.Where(p => !p.Block).ToList();
        lam.Params = ps.Select((p, i) => new Param { Name = "__m" + i, Line = lam.Line }).ToList();
        lam.Body = [new ExprStmt { Line = lam.Line, Expr = new CallExpr { Recv = recv, Name = name, Line = lam.Line, Args = lam.Params.Select(p => new Arg { Value = new NameExpr { Name = p.Name, Line = lam.Line } }).ToList() } }];
        return ps.Select(p => p.Type).ToList();
    }

    static AType ResolveNonNull(AType t) =>
        new(t.Kind, t.Cs.EndsWith("?") ? t.Cs[..^1] : t.Cs, t.Clr) { Sym = t.Sym, Args = t.Args, Elem = t.Elem };

    void BindArgs(List<Arg> args)
    {
        foreach (var a in args)
        {
            if (a.OutName != null)
            {
                a.OutLocal = Declare(a, a.OutName, AType.Unknown);
                continue;
            }
            if (a.Value != null) BindExpr(a.Value);
            CheckRefArg(a);
        }
    }

    /// <summary>Binds a lambda/block. Expected parameter types come from the callee (e.g. Prelude iterators).</summary>
    void BindLambda(LambdaExpr lam, List<AType> expected)
    {
        bool conc = pendingConcurrent;
        pendingConcurrent = false;
        if (conc) spawnDepth++;
        EnterIsolation(lam, conc);
        try { BindLambdaCore(lam, expected, conc); }
        finally { LeaveIsolation(); if (conc) spawnDepth--; }
    }

    void BindLambdaCore(LambdaExpr lam, List<AType> expected, bool conc)
    {
        if (lam.MethodRef is { } mr) expected = lam.MethodRefTypes ??= ExpandMethodRef(lam, mr.Recv, mr.Name, expected);
        if (!lam.BranchLocalsPromoted)
        {
            // an assignment inside a branch of the block to a variable of the enclosing method assigns that variable
            // (Ruby closures); only names unknown outside become block locals. Before, `best = v if v > best` in
            // `h.each { }` declared a new `best` and the method's stayed 0
            var known = lam.Params.Select(p => p.Name).ToHashSet();
            foreach (var scope in scopes) known.UnionWith(scope.Keys);
            PromoteBranchLocals(lam.Body, known);
            lam.BranchLocalsPromoted = true;
        }
        lam.Captures = false;
        lam.ParamSyms = new List<LocalSym>();
        lambdas.Add(lam);
        var savedReturns = returns;
        returns = new List<AType>();
        PushScope();
        for (int i = 0; i < lam.Params.Count; i++)
        {
            var p = lam.Params[i];
            AType t = p.Type != null ? ResolveType(p.Type) : i < expected.Count ? expected[i] : AType.Unknown;
            lam.ParamSyms.Add(Declare(p, p.Name, t));
        }
        if (conc) disjoint.Add(DisjointParams(lam));
        BindBlock(lam.Body);
        PopScope();
        var tail = TailType(lam.Body);
        lam.ReturnsValue = tail != null || returns.Count > 0;
        lam.Result = lam.ReturnsValue ? Unify(returns.Concat(tail != null ? [tail] : Array.Empty<AType>())) : AType.Void;
        if (lam.ReturnsValue && lam.Result.IsUnknown) lam.Result = AType.Unknown;
        returns = savedReturns;
        if (conc) disjoint.RemoveAt(disjoint.Count - 1);
        lambdas.RemoveAt(lambdas.Count - 1);
    }
}
