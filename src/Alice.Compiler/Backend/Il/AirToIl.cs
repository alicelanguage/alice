using System.Reflection;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// Per-module statistics of the IL backend (D1.4): node kinds seen in the lowered bodies, methods emitted, and the
/// unsupported node kinds that blocked a method (the coverage ranking).
/// </summary>
public sealed class IlStats
{
    public readonly SortedDictionary<string, int> Nodes = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, int> Unsupported = new(StringComparer.Ordinal);
    public int Methods, MethodsOk;

    internal void Seen(string kind) => Nodes[kind] = Nodes.GetValueOrDefault(kind) + 1;
    internal void Blocked(string kind) => Unsupported[kind] = Unsupported.GetValueOrDefault(kind) + 1;

    public override string ToString() =>
        $"methods {MethodsOk}/{Methods}" + (Unsupported.Count > 0 ? "; unsupported: " + string.Join(", ", Unsupported.OrderByDescending(k => k.Value).Select(k => k.Key + " x" + k.Value)) : "");
}

/// <summary>
/// The body of one method: walks post-optimizer MIR and emits IL through <see cref="IlBuilder"/> (ADR 0033). MIR is
/// still partly C# text (`var` locals, MCode leaves, calls by name): this emitter types every expression itself
/// (<see cref="Infer"/>), resolving names against locals, parameters, Alice declarations and reflection, as Roslyn
/// does for the C# backend. Expressions are here, statements in AirToIl.Stmts.cs.
/// </summary>
internal sealed partial class IlBody
{
    readonly AirToIl c;
    readonly IlTypes T;
    IlBuilder il;
    readonly AliceT owner;
    readonly AMethod? self;
    readonly bool isStatic;
    readonly Type ret;
    readonly Dictionary<string, (int Index, Type Type)> args = new(StringComparer.Ordinal);
    readonly List<Dictionary<string, (IlLocal L, Type T)>> scopes = [];
    readonly Dictionary<MExpr, Type> inferred = new(ReferenceEqualityComparer.Instance);
    int uncheckedDepth;
    Span? span;

    public IlBody(AirToIl c, AliceT owner, AMethod? self, IlBuilder il, Type ret, bool isStatic, IReadOnlyList<(string Name, Type Type)> ps)
    {
        this.c = c;
        T = c.Types;
        this.owner = owner;
        this.self = self;
        this.il = il;
        this.ret = ret;
        this.isStatic = isStatic;
        int k = isStatic ? 0 : 1;
        for (int i = 0; i < ps.Count; i++) args[Bare(ps[i].Name)] = (i + k, ps[i].Type);
        scopes.Add(new(StringComparer.Ordinal));
    }

    static string Bare(string name) => name.StartsWith('@') ? name[1..] : name;

    bool Checked => uncheckedDepth == 0;

    IlUnsupportedException No(string kind, string? detail = null) => new(kind, span, detail);

    // ------------------------------------------------------------------ typing

    /// <summary>The natural type of <paramref name="e"/>: emitted into a scratch body and discarded (memoized per
    /// node, so the cost is linear in the tree size per level).</summary>
    public Type Infer(MExpr e)
    {
        bool leaf = e is MCode or AirLocalRef or AirConst;
        if (!leaf && inferred.TryGetValue(e, out var t)) return t;
        var saved = il;
        var savedShadow = shadow;
        il = new IlBuilder(true, c.Module);
        shadow = new(ReferenceEqualityComparer.Instance);
        scopes.Add(new(StringComparer.Ordinal));
        int depth = uncheckedDepth;
        try { t = Raw(e); }
        finally
        {
            il = saved;
            shadow = savedShadow;
            scopes.RemoveAt(scopes.Count - 1);
            uncheckedDepth = depth;
        }
        if (!leaf) inferred[e] = t;
        return t;
    }

    (IlLocal L, Type T)? FindLocal(string name)
    {
        name = Bare(name);
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(name, out var l))
            {
                // inside Infer the scratch builder gets its own stand-in for every local it reads
                if (shadow != null)
                {
                    if (!shadow.TryGetValue(l.L, out var s)) shadow[l.L] = s = il.DeclareLocal(l.L.Type);
                    return (s, l.T);
                }
                return l;
            }
        return null;
    }

    Dictionary<IlLocal, IlLocal>? shadow;

    bool IsVar(string name) => FindLocal(name) != null || args.ContainsKey(Bare(name));

    IlLocal Declare(string name, Type t)
    {
        var l = il.DeclareLocal(T.Il(t), Bare(name));
        scopes[^1][Bare(name)] = (l, t);
        return l;
    }

    IlLocal Temp(Type t) => il.DeclareLocal(T.Il(t));

    // ------------------------------------------------------------------ entry

    /// <summary>Emits <paramref name="e"/> converted to <paramref name="want"/> (implicit conversions); returns the pushed type.</summary>
    public Type Emit(MExpr e, Type? want)
    {
        if (want == null || want == typeof(void)) return Raw(e);
        if (Literal(e, out var lit) && lit != null && IlTypes.IsNumeric(want) && IlTypes.LiteralFits(lit, want) && IlTypes.IsNumeric(LitType(e)))
        {
            Const(want, lit);
            return want;
        }
        if (Literal(e, out var lf) && lf is float or double && IlTypes.IsFloat(want)) { Const(want, lf); return want; }
        if (IsNullLit(e))
        {
            if (!want.IsValueType || want.IsPointer) { il.EmitLoadNull(); return want; }
            if (IlTypes.IsNullable(want)) { DefaultValue(want); return want; }
        }
        if (IsDefaultLit(e)) { DefaultValue(want); return want; }
        if (e is MCond cd) { Cond(cd, want); return want; }
        var t = Raw(e);
        Convert(t, want, false, implicitOnly: true);
        return want;
    }

    static bool IsNullLit(MExpr e) => e is AirNull or MCode { Text: "null" };
    static bool IsDefaultLit(MExpr e) => e is AirDefault { Type: null } or MCode { Text: "default" or "default!" };

    void DefaultValue(Type t)
    {
        if (t == typeof(void)) return;
        if (!t.IsValueType || t.IsPointer) { if (t.IsPointer) { il.EmitLoadConst(0); il.Emit(ILOpCode.Conv_u); } else il.EmitLoadNull(); return; }
        if (IlTypes.IsNumeric(t) || t == typeof(bool) || t.IsEnum) { Const(t.IsEnum ? Enum.GetUnderlyingType(t) : t, 0); return; }
        var tmp = Temp(t);
        il.EmitLoadLocalAddress(tmp);
        il.EmitInitObj(T.Il(t));
        il.EmitLoadLocal(tmp);
    }

    Type LitType(MExpr e)
    {
        if (e is AirConst { Value: not null } ac) return ac.Value.GetType();
        if (e is MCode mc && IlTypes.TryLiteral(mc.Text, out var t, out _)) return t;
        if (e is MUnary { Op: AirUnOp.Neg, E: MCode c2 } && IlTypes.TryLiteral("-" + c2.Text, out var t2, out _)) return t2;
        return typeof(void);
    }

    bool Literal(MExpr e, out object? v)
    {
        v = null;
        if (e is AirConst { Value: { } cv } && cv is not (string or bool)) { v = cv; return true; }
        if (e is MCode c0 && !IsVar(c0.Text) && IlTypes.TryLiteral(c0.Text, out var lt, out v) && lt != typeof(string) && lt != typeof(bool)) return v != null;
        if (e is MUnary { Op: AirUnOp.Neg, E: MCode c2 } && !IsVar(c2.Text) && IlTypes.TryLiteral("-" + c2.Text, out var t2, out v) && IlTypes.IsNumeric(t2)) return true;
        v = null;
        return false;
    }

    void Const(Type t, object v)
    {
        if (t.IsEnum) t = Enum.GetUnderlyingType(t);
        if (t == typeof(decimal) || v is decimal)
        {
            var dv = System.Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
            if (decimal.IsInteger(dv) && dv >= long.MinValue && dv <= long.MaxValue)
            {
                il.EmitLoadConst((long)dv);
                il.EmitNewObj(c.Module.Import(typeof(decimal).GetConstructor([typeof(long)])!));
                return;
            }
            throw No("Const:decimal", dv.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var big = v switch { int i => (decimal)i, uint u => u, long l => l, ulong ul => ul, short s => s, ushort us => us, byte b => b, sbyte sb => sb, char ch => ch, _ => 0m };
        if (t == typeof(float)) { il.EmitLoadConst(v is float f ? f : v is double d0 ? (float)d0 : (float)big); return; }
        if (t == typeof(double)) { il.EmitLoadConst(v is double d ? d : v is float f2 ? f2 : (double)big); return; }
        if (IlTypes.Is64(t)) { il.EmitLoadConst(t == typeof(ulong) ? unchecked((long)(ulong)big) : (long)big); return; }
        if (t == typeof(nint) || t == typeof(nuint)) { il.EmitLoadConst((long)big); il.Emit(t == typeof(nint) ? ILOpCode.Conv_i : ILOpCode.Conv_u); return; }
        if (t == typeof(bool)) { il.EmitLoadConst(v is bool bb ? bb : big != 0); return; }
        long x = (long)big;
        il.EmitLoadConst(unchecked((int)x));
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>Emits <paramref name="e"/> in its natural type.</summary>
    Type Raw(MExpr e)
    {
        c.Stats.Seen(Kind(e));
        if (e.Span is { } sp) span = sp;
        switch (e)
        {
            case AirConst { Value: not null } ac: return ConstExpr(ac);
            case AirNull: il.EmitLoadNull(); return typeof(object);
            case AirLocalRef { Narrowed: false } lr: return Code(lr.Name, e);
            case AirParamRef pr: return Code(pr.Name, e);
            case AirFieldRef fr: return Code(fr.Name, e);
            case AirThis th: return Code(th.Extension ? "self" : "this", e);
            case AirDefault d:
                {
                    var t = d.Type != null ? T.Parse(d.Type.ToCs(), span) : throw No("AirDefault:untyped");
                    DefaultValue(t);
                    return t;
                }
            case AirTypeOf to:
                il.Emit(ILOpCode.Ldtoken, T.Il(T.Parse(to.Of.ToCs(), span)));
                il.EmitCall(c.Module.Import(typeof(Type).GetMethod("GetTypeFromHandle")!));
                return typeof(Type);
            case AirSizeOf so:
                il.Emit(ILOpCode.Sizeof, T.Il(T.Parse(so.Of.ToCs(), span)));
                return typeof(int);
            case AirStackAlloc sa when sa.Count is AirConst { Value: int n0 } || IlTypes.TryLiteral(sa.Count is MCode cc ? cc.Text : "", out _, out _):
                return StackAlloc(T.Parse(sa.Elem.ToCs(), span), sa.Count is AirConst { Value: int n1 } ? n1 : int.Parse(((MCode)sa.Count).Text, System.Globalization.CultureInfo.InvariantCulture));
            case AirTypeRef tr: throw No("AirTypeRef:value", tr.Text);
            case MCode mc: return Code(mc.Text, e);
            case MBinary b: return Binary(b);
            case MUnary u: return Unary(u);
            case MCond cd: { var t = CondType(cd); Cond(cd, t); return t; }
            case MCast cs: return Cast(cs);
            case MIs i: return Is(i);
            case MCall call: return Call(call);
            case MMember m: return Member(m);
            case MIndex ix: return Index(ix);
            case MNew n: return New(n);
            case MNewArray na: return NewArray(na);
            case MTuple tu: return Tuple(tu);
            case MInterp ip: return Interp(ip);
            case MInvoke inv: return Invoke(inv);
            case AirRent r: return Call(new MCall { Target = new MCode(r.PoolCs), Name = "Rent", Args = r.Args, Span = r.Span });
            case AirRelease r: return Call(new MCall { Target = new MCode(r.PoolCs), Name = "Release", Args = [r.Target], Span = r.Span });
            case AirLength l: return Member(new MMember { Target = l.Target, Name = l.Member, Span = l.Span });
            case AirOffset o: return Binary(new MBinary { Op = AirBinOp.Add, L = o.Base, R = o.Offset });
            case AirTripCount tc:
                {
                    MExpr d = new MBinary { Op = AirBinOp.Sub, L = tc.End, R = tc.Start };
                    if (tc.Inclusive) d = new MBinary { Op = AirBinOp.Add, L = d, R = AirConst.Of(1) };
                    return Raw(d);
                }
            case AirRangeLength rl: return Raw(MathMax0(rl.Lo, rl.Hi, !rl.Exclusive));
            case AirPresizeCount pc: return Raw(MathMax0(pc.From, pc.To, pc.Inclusive));
            case AirForkable: return Member(new MMember { Target = new MCode("global::Alice.Runtime.Par"), Name = "Forkable" });
            case AirSeq sq:
                foreach (var st in sq.Pre) Stmt(st);
                return Raw(sq.Value);
            case AirAs aa:
                {
                    var t = T.Parse(aa.TypeCs, span);
                    var from = Raw(aa.E);
                    if (from.IsValueType) il.EmitBox(T.Il(from));
                    il.EmitIsInst(T.Il(t));
                    return t;
                }
            case AirVectorOp or AirVectorLoad or AirVectorStore or AirVectorReduce or AirVectorFits: return Raw(Vector(e));
            case IlConv cv: { var f = Raw(cv.E); Convert(f, cv.To, cv.Checked); return cv.To; }
            case AirStackBuffer sb: return StackAlloc(T.Parse(sb.ElemCs, span), sb.Length);
        }
        throw No(Kind(e));
    }

    readonly Dictionary<MExpr, MExpr> vectors = new(ReferenceEqualityComparer.Instance);

    /// <summary>The vector nodes (Mir/MExpr.Vector.cs) as the calls and operators they mean (Backend/Printer.Vector.cs):
    /// typed MIR, bound like any other call (System.Numerics.Vector / System.Runtime.Intrinsics.VectorN).</summary>
    MExpr Vector(MExpr e)
    {
        if (vectors.TryGetValue(e, out var done)) return done;
        const string Mm = "global::System.Runtime.InteropServices.MemoryMarshal";
        static MExpr Ty(string cs) => new MCode(cs);
        static MExpr Data(MExpr arr) => new MArg { Prefix = "ref ", E = new MCall { Target = Ty(Mm), Name = "GetArrayDataReference", Args = [arr] } };
        static MExpr Nuint(MExpr i) => new MCall { Name = "unchecked", Args = [new MCast { TypeCs = "nuint", E = i }] };
        MExpr r;
        switch (e)
        {
            case AirVectorOp v:
                {
                    string ns = AirVector.Api(AirVector.Width(v.Vec)), vt = v.Vec.ToCs();
                    var a = v.Args;
                    r = v.Op switch
                    {
                        AirVecOp.Zero or AirVecOp.One or AirVecOp.Indices or AirVecOp.Count => new MMember { Target = Ty(vt), Name = v.Op.ToString() },
                        AirVecOp.IsAccelerated => new MMember { Target = Ty(ns), Name = "IsHardwareAccelerated" },
                        AirVecOp.Splat => AirVector.Width(v.Vec) == 0
                            ? new MNew { TypeCs = vt, Args = [new MCast { TypeCs = AirVector.Lane(v.Vec).ToCs(), E = a[0] }] }
                            : new MCall { Target = Ty(ns), Name = "Create", Args = [new MCast { TypeCs = AirVector.Lane(v.Vec).ToCs(), E = a[0] }] },
                        AirVecOp.Add => new MBinary { Op = AirBinOp.Add, L = a[0], R = a[1], Checked = false },
                        AirVecOp.Sub => new MBinary { Op = AirBinOp.Sub, L = a[0], R = a[1], Checked = false },
                        AirVecOp.Mul => new MBinary { Op = AirBinOp.Mul, L = a[0], R = a[1], Checked = false },
                        AirVecOp.Div => new MBinary { Op = AirBinOp.Div, L = a[0], R = a[1], Checked = false },
                        AirVecOp.Neg => new MCall { Target = Ty(vt), Name = "op_UnaryNegation", Args = [a[0]] },
                        AirVecOp.Sqrt => new MCall { Target = Ty(ns), Name = AirVector.Width(v.Vec) == 0 ? "SquareRoot" : "Sqrt", Args = a },
                        _ => new MCall { Target = Ty(ns), Name = v.Op.ToString(), Args = a },
                    };
                    break;
                }
            case AirVectorLoad ld:
                r = new MCall { Target = Ty(AirVector.Api(AirVector.Width(ld.Vec))), Name = "LoadUnsafe", Args = [Data(ld.Array), Nuint(ld.Index)] };
                break;
            case AirVectorStore st:
                r = new MCall { Target = Ty(AirVector.Api(AirVector.Width(st.Vec))), Name = "StoreUnsafe", Args = [st.Value, Data(st.Array), Nuint(st.Index)] };
                break;
            case AirVectorReduce rd:
                {
                    MExpr call = new MCall { Target = Ty(AirVector.Api(AirVector.Width(rd.Vec))), Name = rd.Op.ToString(), Args = [rd.Arg] };
                    r = rd.CastTo != null ? new MCast { TypeCs = rd.CastTo.ToCs(), E = call } : call;
                    break;
                }
            case AirVectorFits f:
                {
                    MExpr end = f.EndInclusive ? new MBinary { Op = AirBinOp.Add, L = f.End, R = AirConst.Of(1) } : f.End;
                    MExpr count = new MMember { Target = Ty(f.Vec.ToCs()), Name = "Count" };
                    r = f.Form switch
                    {
                        AirFitsForm.Fits => new MBinary { Op = AirBinOp.Le, L = f.Index, R = new MBinary { Op = AirBinOp.Sub, L = end, R = count } },
                        AirFitsForm.Overruns => new MBinary { Op = AirBinOp.Gt, L = f.Index, R = new MBinary { Op = AirBinOp.Sub, L = end, R = count } },
                        _ => new MBinary
                        {
                            Op = AirBinOp.And,
                            L = new MMember { Target = Ty(AirVector.Api(AirVector.Width(f.Vec))), Name = "IsHardwareAccelerated" },
                            R = new MBinary { Op = AirBinOp.Ge, L = new MBinary { Op = AirBinOp.Sub, L = end, R = f.Index }, R = count },
                        },
                    };
                    break;
                }
            default: throw No(Kind(e));
        }
        r.Span = e.Span;
        return vectors[e] = r;
    }

    static MExpr MathMax0(MExpr lo, MExpr hi, bool inclusive)
    {
        MExpr d = new MBinary { Op = AirBinOp.Sub, L = hi, R = lo };
        if (inclusive) d = new MBinary { Op = AirBinOp.Add, L = d, R = AirConst.Of(1) };
        return new MCall { Target = new MCode("global::System.Math"), Name = "Max", Args = [AirConst.Of(0), d] };
    }

    internal static string Kind(MExpr e) => e switch
    {
        MCode => "MCode",
        MCall { Target: null, Name: "unchecked" } => "MCall:unchecked()",
        MCall { Target: null, Name: "checked" } => "MCall:checked()",
        MLambda => "MLambda",
        _ => e.GetType().Name,
    };

    Type ConstExpr(AirConst ac)
    {
        switch (ac.Value)
        {
            case null: il.EmitLoadNull(); return ac.Type != null && ac.Type is not AirType.Prim { Kind: PrimKind.Null } && T.TryParse(ac.Type.ToCs()) is { } nt ? nt : typeof(object);
            case bool b: il.EmitLoadConst(b); return typeof(bool);
            case string s: il.EmitLoadConst(s); return typeof(string);
            case char ch: il.EmitLoadConst((int)ch); return typeof(char);
            case var v: Const(v.GetType(), v); return v.GetType();
        }
    }

    // ---- names

    Type Code(string text, MExpr node)
    {
        text = text.Trim();
        if (FindLocal(text) is { } l) { il.EmitLoadLocal(l.L); return l.T; }
        if (args.TryGetValue(Bare(text), out var a)) { il.EmitLoadArg(a.Index); return a.Type; }
        if (text == "this")
        {
            if (isStatic) throw No("MCode:this-in-static");
            il.EmitLoadArg(0);
            if (owner.IsValueType) { il.EmitLoadObj(T.Il(owner.Proxy)); }
            return owner.Proxy;
        }
        if (IlTypes.TryLiteral(text, out var lt, out var lv))
        {
            if (lt == typeof(bool)) il.EmitLoadConst((bool)lv!);
            else if (lt == typeof(string)) il.EmitLoadConst((string)lv!);
            else if (lt == typeof(char)) il.EmitLoadConst((int)(char)lv!);
            else if (lv == null) il.EmitLoadNull();
            else Const(lt, lv);
            return lt;
        }
        if (text is "default" or "default!") throw No("MCode:default-untyped");
        if (StackallocText.Match(text) is { Success: true } sm) return StackAlloc(T.Parse(sm.Groups[1].Value, span), int.Parse(sm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        if (text.EndsWith('!') && text.Length > 1) return Code(text[..^1], node);
        if (Implicit(text) is { } f) return LoadStaticOrThisMember(f);
        if (StaticPath(text) is { } sp) return LoadStatic(sp);
        if (Reparse(text) is { } parsed) return Raw(parsed);
        throw No("MCode:unresolved", text);
    }

    /// <summary>`stackalloc T[n]` as a Span&lt;T&gt; (at method entry: the eval stack is empty). Not zeroed, as with the C#
    /// backend's [SkipLocalsInit]; the lowering re-initializes the buffer where the source declared it.</summary>
    Type StackAlloc(Type elem, int n)
    {
        il.EmitLoadConst(n);
        il.Emit(ILOpCode.Conv_u);
        il.Emit(ILOpCode.Sizeof, T.Il(elem));
        il.EmitBinary(IlBinOp.Mul, isChecked: true, unsigned: true);
        il.Emit(ILOpCode.Localloc);
        il.EmitLoadConst(n);
        var span = typeof(Span<>).MakeGenericType(elem);
        il.EmitNewObj(T.Method(span.GetConstructor([typeof(void*), typeof(int)])!));
        return span;
    }

    static readonly Regex StackallocText = new(@"^stackalloc\s+(.+)\[(\d+)\]$", RegexOptions.Compiled);

    readonly Dictionary<string, MExpr?> reparsed = new(StringComparer.Ordinal);

    /// <summary>C# text of a leaf as MIR: a member chain on a variable (`c.Length`, `this.x.y`) or a parsed expression.</summary>
    MExpr? Reparse(string text)
    {
        if (DottedVar(text) is { } chain) return chain;
        if (!reparsed.TryGetValue(text, out var e))
        {
            e = CsText.Parse(text, t => !IsVar(t) && T.TryParse(t) != null);
            if (e is MCode mc && mc.Text == text) e = null;
            reparsed[text] = e;
        }
        return e;
    }

    MExpr? DottedVar(string text)
    {
        if (!text.Contains('.') || text.Contains('(') || text.Contains(' ') || text.Contains('<')) return null;
        var parts = text.Split('.');
        if (parts.Any(x => x.Length == 0)) return null;
        if (!(IsVar(parts[0]) || parts[0] == "this" && !isStatic || Implicit(parts[0]) is AField)) return null;
        MExpr e = new MCode(parts[0]);
        foreach (var n in parts.Skip(1)) e = new MMember { Target = e, Name = n };
        return e;
    }

    /// <summary>A bare name that is a member: an Alice field of this type (or Program), or a Prelude static property.</summary>
    object? Implicit(string name)
    {
        name = Bare(name);
        var f = owner.FindField(name);
        if (f != null && (f.IsStatic || !isStatic)) return f;
        if (owner != c.ProgramT && c.ProgramT.FindField(name) is { IsStatic: true } pf) return pf;
        for (var o = owner.Sym.Parent; o != null; o = o.Parent) if (T.BySym.TryGetValue(o, out var ot) && ot.FindField(name) is { IsStatic: true } of) return of;
        if (typeof(Alice.Runtime.Prelude).GetProperty(name, BindingFlags.Public | BindingFlags.Static) is { } pp) return pp;
        if (typeof(Alice.Runtime.Prelude).GetField(name, BindingFlags.Public | BindingFlags.Static) is { } pfi) return pfi;
        return null;
    }

    Type LoadStaticOrThisMember(object m)
    {
        if (m is AField f)
        {
            if (!f.IsStatic) { il.EmitLoadArg(0); return LoadField(f, owner.IsValueType); }
            return LoadField(f, false);
        }
        return LoadStatic((MemberInfo)m);
    }

    /// <summary>A static member path: `global::System.Math.PI`, `int.MaxValue`, `Color.Green`.</summary>
    object? StaticPath(string text)
    {
        int dot = text.LastIndexOf('.');
        if (dot <= 0 || text.Contains('(') || text.Contains(' ')) return null;
        var t = TypeOf(text[..dot]);
        if (t == null) return null;
        return StaticMember(t, text[(dot + 1)..]);
    }

    object? StaticMember(Type t, string name)
    {
        name = Bare(name);
        if (T.Alice(t) is { } a)
        {
            var f = a.FindField(name);
            return f is { IsStatic: true } ? f : null;
        }
        const BindingFlags S = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return (MemberInfo?)t.GetField(name, S) ?? t.GetProperty(name, S);
    }

    Type LoadStatic(object m)
    {
        switch (m)
        {
            case AField af: return LoadField(af, false);
            case FieldInfo { IsLiteral: true } f:
                {
                    var v = f.GetRawConstantValue();
                    if (v is string s) il.EmitLoadConst(s);
                    else if (v is bool bb) il.EmitLoadConst(bb);
                    else if (v is char ch) il.EmitLoadConst((int)ch);
                    else if (v == null) il.EmitLoadNull();
                    else Const(f.FieldType.IsEnum ? Enum.GetUnderlyingType(f.FieldType) : f.FieldType, v);
                    return f.FieldType;
                }
            case FieldInfo f: il.EmitLoadField(T.Field(f)); return f.FieldType;
            case PropertyInfo p: il.EmitCall(T.Method(p.GetGetMethod()!)); return p.PropertyType;
        }
        throw No("MCode:static", m.ToString());
    }

    /// <summary>A type spelled as a member-access target (not a variable).</summary>
    Type? TypeOf(MExpr e) => e switch
    {
        AirTypeRef tr => T.Parse(tr.Text, span),
        MCode mc when !IsVar(mc.Text) && mc.Text != "this" && Implicit(mc.Text) == null => TypeOf(mc.Text),
        _ => null,
    };

    Type? TypeOf(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || IlTypes.TryLiteral(text, out _, out _) || IsVar(text)) return null;
        return T.TryParse(text);
    }

    /// <summary>Instance field or property read; the instance (or its address, for value types) is on the stack.</summary>
    Type LoadField(AField f, bool viaAddress)
    {
        if (f.Literal != null) { Const(f.Type, f.Literal); return f.Type; }
        if (f.Getter != null) { il.EmitCall(f.Getter, virtualCall: !f.IsStatic && !f.Owner.IsValueType); return f.Type; }
        il.EmitLoadField(f.Field!);
        return f.Type;
    }

    // ---- members

    Type Member(MMember m)
    {
        if (m.Safe) return SafeAccess(m.Target, new MMember { Target = new MCode("__safe_recv"), Name = m.Name, Span = m.Span }, m.Name);
        if (TypeOf(m.Target) is { } st)
        {
            var sm = StaticMember(st, m.Name) ?? (st.IsEnum && T.Alice(st) is null ? st.GetField(m.Name) : null)
                ?? throw No("MMember:unresolved-static", st.Name + "." + m.Name);
            return LoadStatic(sm);
        }
        var rt = Infer(m.Target);
        string name = Bare(m.Name);
        if (rt.IsArray && name is "Length" or "LongLength")
        {
            Raw(m.Target);
            il.Emit(ILOpCode.Ldlen);
            il.Emit(name == "Length" ? ILOpCode.Conv_i4 : ILOpCode.Conv_i8);
            return name == "Length" ? typeof(int) : typeof(long);
        }
        if (T.Alice(rt) is { } a)
        {
            var f = a.FindField(name);
            if (f != null && !f.IsStatic)
            {
                bool addr = Receiver(m.Target, rt);
                return LoadField(f, addr);
            }
            var zero = a.FindMethods(name).FirstOrDefault(x => x.Params.Length == 0 && !x.IsStatic);
            if (zero != null) return Call(new MCall { Target = m.Target, Name = m.Name, Span = m.Span });
            if (a.ClrBase == null) throw No("MMember:alice-unresolved", a.Sym.Name + "." + name);
        }
        var fi = FindClrField(rt, name);
        if (fi != null)
        {
            Receiver(m.Target, rt);
            il.EmitLoadField(T.Field(fi));
            return fi.FieldType;
        }
        var p = FindClrProperty(rt, name) ?? throw No("MMember:unresolved", rt.Name + "." + name);
        var g = p.GetGetMethod() ?? throw No("MMember:no-getter", name);
        CallInstance(m.Target, rt, g);
        return Deref(p.PropertyType);
    }

    /// <summary>A `ref T` result (Span indexer, ref-returning property) read as a value.</summary>
    Type Deref(Type t)
    {
        if (!t.IsByRef) return t;
        var el = t.GetElementType()!;
        il.EmitLoadObj(T.Il(el));
        return el;
    }

    FieldInfo? FindClrField(Type rt, string name)
    {
        if (T.Alice(rt) is { } a) rt = a.ClrBase ?? typeof(object);
        try { return rt.GetField(name, BindingFlags.Public | BindingFlags.Instance); } catch (Exception) { return null; }
    }

    PropertyInfo? FindClrProperty(Type rt, string name)
    {
        if (T.Alice(rt) is { } a) rt = a.ClrBase ?? typeof(object);
        foreach (var t in rt.IsInterface ? rt.GetInterfaces().Prepend(rt) : [rt])
        {
            PropertyInfo? p = null;
            try { p = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == name && x.GetIndexParameters().Length == 0).OrderBy(x => x.DeclaringType == t ? 0 : 1).FirstOrDefault(); }
            catch (Exception) { }
            if (p != null) return p;
        }
        return null;
    }

    /// <summary>Pushes the receiver of an instance member: the address for value types (true), the reference otherwise.</summary>
    bool Receiver(MExpr target, Type rt)
    {
        if (rt.IsValueType) { Address(target); return true; }
        Emit(target, rt);
        return false;
    }

    /// <summary>`recv?.member` / `recv?.call()`: <paramref name="inner"/> reads the receiver as `__safe_recv`; null
    /// (or a null Nullable) when the receiver is null.</summary>
    Type SafeAccess(MExpr target, MExpr inner, string what)
    {
        var rt = Infer(target);
        if (rt.IsValueType) throw No("Safe:value-receiver", what);
        Raw(target);
        var recv = Temp(rt);
        il.EmitStoreLocal(recv);
        scopes.Add(new(StringComparer.Ordinal) { ["__safe_recv"] = (recv, rt) });
        try
        {
            var t = Infer(inner);
            var result = t.IsValueType && t != typeof(void) && !IlTypes.IsNullable(t) ? typeof(Nullable<>).MakeGenericType(t) : t;
            var isNull = il.DefineLabel();
            var end = il.DefineLabel();
            il.EmitLoadLocal(recv);
            il.EmitBranch(isNull, IlCond.False);
            Raw(inner);
            Convert(t, result, false, true);
            il.EmitBranch(end);
            il.MarkLabel(isNull);
            DefaultValue(result);
            il.MarkLabel(end);
            return result;
        }
        finally { scopes.RemoveAt(scopes.Count - 1); }
    }

    // ---- addresses

    /// <summary>Pushes the address of a value: locals, arguments, `this`, fields and array elements in place; anything
    /// else through a temp.</summary>
    void Address(MExpr e)
    {
        switch (e)
        {
            case MCode or AirLocalRef:
                {
                    string n = e is MCode mc ? mc.Text.Trim() : ((AirLocalRef)e).Name;
                    if (FindLocal(n) is { } l) { il.EmitLoadLocalAddress(l.L); return; }
                    if (args.TryGetValue(Bare(n), out var a))
                    {
                        if (a.Type.IsByRef) il.EmitLoadArg(a.Index); else il.EmitLoadArgAddress(a.Index);
                        return;
                    }
                    if (n == "this" && !isStatic) { il.EmitLoadArg(0); if (!owner.IsValueType) { var t0 = Temp(owner.Proxy); il.EmitStoreLocal(t0); il.EmitLoadLocalAddress(t0); } return; }
                    if (Implicit(n) is AField { Getter: null, Literal: null } f)
                    {
                        if (f.IsStatic) { il.EmitLoadFieldAddress(f.Field!); return; }
                        il.EmitLoadArg(0);
                        il.EmitLoadFieldAddress(f.Field!);
                        return;
                    }
                    break;
                }
            case MMember { Safe: false } m when TypeOf(m.Target) == null && Infer(m.Target) is var rt && T.Alice(rt)?.FindField(Bare(m.Name)) is { Getter: null, Literal: null, IsStatic: false } af:
                Receiver(m.Target, rt);
                il.EmitLoadFieldAddress(af.Field!);
                return;
            case MIndex { Args.Count: 1 } ix when Infer(ix.Target) is { IsSZArray: true } at:
                Raw(ix.Target);
                Emit(ix.Args[0], typeof(int));
                il.Emit(ILOpCode.Ldelema, T.Il(at.GetElementType()!));
                return;
        }
        var t = e is MCall rc ? Call(rc, keepRef: true) : Raw(e);
        if (t.IsByRef) return;   // `ref MemoryMarshal.GetArrayDataReference(a)`, Span indexers: the reference itself
        var tmp = Temp(t);
        il.EmitStoreLocal(tmp);
        il.EmitLoadLocalAddress(tmp);
    }

    // ---- index

    Type Index(MIndex ix)
    {
        var t = Infer(ix.Target);
        if (t.IsSZArray && ix.Args.Count == 1)
        {
            var et = t.GetElementType()!;
            bool unc = Opt.UncheckedIndex.Proven(ix);
            Raw(ix.Target);
            if (unc) Opt.UncheckedIndex.Head(il, c.Module, T.Il(et));
            Emit(ix.Args[0], typeof(int));
            if (unc) { Opt.UncheckedIndex.Tail(il, c.Module, T.Il(et)); il.EmitLoadObj(T.Il(et)); return et; }
            il.EmitLoadElement(T.Il(et));
            return et;
        }
        if (t.IsArray)
        {
            Raw(ix.Target);
            foreach (var a in ix.Args) Emit(a, typeof(int));
            var get = t.GetMethod("Get")!;
            il.EmitCall(T.Method(get));
            return get.ReturnType;
        }
        if (t == typeof(string) && ix.Args.Count == 1 && Infer(ix.Args[0]) is var it && (it == typeof(int) || IlTypes.ImplicitNumeric(it, typeof(int)) || Literal(ix.Args[0], out _)))
        {
            Raw(ix.Target);
            Emit(ix.Args[0], typeof(int));
            il.EmitCall(c.Module.Import(typeof(string).GetMethod("get_Chars")!), virtualCall: true);
            return typeof(char);
        }
        return Call(new MCall { Target = ix.Target, Name = "get_Item", Args = ix.Args, Span = ix.Span }, indexer: true);
    }

    // ---- new

    Type New(MNew n)
    {
        var t = T.Parse(n.TypeCs, span);
        Type made;
        if (T.Alice(t) is { } a)
        {
            if (a.IsInterface || a.Def.Attributes.HasFlag(TypeAttributes.Abstract)) throw No("MNew:abstract", n.TypeCs);
            if (a.Ctors.Count == 0 && a.IsValueType && n.Args.Count == 0) { DefaultValue(t); made = t; }
            else
            {
                var infos = ArgInfos(n.Args);
                var cand = T.Pick(a.Ctors.Select(IlTypes.FromAlice), infos) ?? throw No("MNew:alice-ctor", n.TypeCs + "/" + n.Args.Count);
                PushArgs(cand, n.Args, 0);
                il.EmitNewObj(cand.Alice!.Def);
                made = t;
            }
        }
        else if (t.IsValueType && n.Args.Count == 0 && t.GetConstructor(Type.EmptyTypes) == null) { DefaultValue(t); made = t; }
        else
        {
            if (t.IsAbstract) throw No("MNew:abstract", n.TypeCs);
            var infos = ArgInfos(n.Args);
            var cand = T.Pick(t.GetConstructors().Select(x => IlTypes.FromClr(x)), infos) ?? throw No("MNew:ctor", n.TypeCs + "/" + n.Args.Count);
            PushArgs(cand, n.Args, 0);
            il.EmitNewObj(T.Method(cand.Clr!));
            made = t;
        }
        if (n.Init != null)
        {
            foreach (var item in n.Init)
            {
                if (made.IsValueType) throw No("MNew:value-initializer", n.TypeCs);
                il.EmitDup();
                var tmpName = "__init_target";
                var tmp = Temp(made);
                il.EmitStoreLocal(tmp);
                scopes[^1][tmpName] = (tmp, made);
                var r = item is MPair pr
                    ? Call(new MCall { Target = new MCode(tmpName), Name = "Add", Args = [pr.K, pr.V] })
                    : Call(new MCall { Target = new MCode(tmpName), Name = "Add", Args = [item] });
                scopes[^1].Remove(tmpName);
                if (r != typeof(void)) il.EmitPop();
            }
        }
        return made;
    }

    Type NewArray(MNewArray na)
    {
        Type et;
        if (na.ElemCs == "")
        {
            if (na.Items is not { Count: > 0 }) throw No("MNewArray:implicit-empty");
            et = Best(na.Items) ?? throw No("MNewArray:implicit", string.Join(",", na.Items.Select(i => Infer(i).Name)));
        }
        else et = T.Parse(na.ElemCs, span);
        var iet = T.Il(et);
        if (na.Items == null)
        {
            Emit(na.Size!, typeof(int));
            il.EmitNewArray(iet);
            return et.MakeArrayType();
        }
        il.EmitLoadConst(na.Items.Count);
        il.EmitNewArray(iet);
        for (int i = 0; i < na.Items.Count; i++)
        {
            il.EmitDup();
            il.EmitLoadConst(i);
            if (et.IsValueType && !IlTypes.IsNumeric(et) && et != typeof(bool) && !et.IsEnum)
            {
                il.Emit(ILOpCode.Ldelema, iet);
                Emit(na.Items[i], et);
                il.EmitStoreObj(iet);
            }
            else
            {
                Emit(na.Items[i], et);
                il.EmitStoreElement(iet);
            }
        }
        return et.MakeArrayType();
    }

    /// <summary>The best common type of `new[] { ... }` items.</summary>
    Type? Best(List<MExpr> items)
    {
        var ts = items.Where(i => !IsNullLit(i)).Select(Infer).Distinct().ToList();
        if (ts.Count == 1) return items.Any(IsNullLit) && ts[0].IsValueType ? typeof(Nullable<>).MakeGenericType(ts[0]) : ts[0];
        foreach (var cand in ts) if (ts.All(x => x == cand || IlTypes.ImplicitNumeric(x, cand) || !x.IsValueType && T.Assignable(x, cand))) return cand;
        return null;
    }

    Type Tuple(MTuple tu)
    {
        var items = tu.Items.Select(Infer).ToArray();
        if (items.Length is < 2 or > 7) throw No("MTuple:arity", items.Length.ToString());
        var tt = Type.GetType("System.ValueTuple`" + items.Length)!.MakeGenericType(items);
        for (int i = 0; i < tu.Items.Count; i++) Emit(tu.Items[i], items[i]);
        il.EmitNewObj(T.Method(tt.GetConstructor(items)!));
        return tt;
    }

    static readonly Type Dish = typeof(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler);

    /// <summary>`string.Create(InvariantCulture, $"...")`: DefaultInterpolatedStringHandler, holes in their static types.</summary>
    Type Interp(MInterp ip)
    {
        var h = Temp(Dish);
        var inv = typeof(System.Globalization.CultureInfo).GetProperty("InvariantCulture")!.GetGetMethod()!;
        int lit = ip.Chunks.Sum(s => s.Length);
        il.EmitLoadLocalAddress(h);
        il.EmitLoadConst(lit);
        il.EmitLoadConst(ip.Holes.Count);
        il.EmitCall(c.Module.Import(inv));
        il.EmitCall(c.Module.Import(Dish.GetConstructor([typeof(int), typeof(int), typeof(IFormatProvider)])!));
        for (int k = 0; k < ip.Chunks.Count; k++)
        {
            if (ip.Chunks[k].Length > 0)
            {
                il.EmitLoadLocalAddress(h);
                il.EmitLoadConst(ip.Chunks[k]);
                il.EmitCall(c.Module.Import(Dish.GetMethod("AppendLiteral", [typeof(string)])!));
            }
            if (k < ip.Holes.Count)
            {
                var ht = Infer(ip.Holes[k]);
                il.EmitLoadLocalAddress(h);
                MethodInfo app;
                if (ht == typeof(string)) app = Dish.GetMethod("AppendFormatted", [typeof(string)])!;
                else if (ht == typeof(ReadOnlySpan<char>)) app = Dish.GetMethod("AppendFormatted", [typeof(ReadOnlySpan<char>)])!;
                else
                {
                    if (ht == typeof(void) || ht.IsByRefLike || ht.IsPointer) throw No("MInterp:hole", ht.Name);
                    app = Dish.GetMethods().Single(m => m.Name == "AppendFormatted" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1).MakeGenericMethod(ht);
                }
                Emit(ip.Holes[k], app.GetParameters()[0].ParameterType);
                il.EmitCall(T.Method(app));
            }
        }
        il.EmitCall(c.Module.Import(inv));
        il.EmitLoadLocalAddress(h);
        il.EmitCall(c.Module.Import(typeof(string).GetMethod("Create", [typeof(IFormatProvider), Dish.MakeByRefType()])!));
        return typeof(string);
    }

    Type Invoke(MInvoke inv)
    {
        var dt = Infer(inv.Target);
        var m = dt.GetMethod("Invoke");
        if (m == null || !typeof(Delegate).IsAssignableFrom(dt)) throw No("MInvoke:non-delegate", dt.Name);
        Raw(inv.Target);
        var cand = IlTypes.FromClr(m);
        PushArgs(cand, inv.Args, 0);
        il.EmitCall(T.Method(m), virtualCall: true);
        return m.ReturnType;
    }

    // ---- is / cast

    Type Is(MIs i)
    {
        string pat = i.TypeCs.Trim();
        var vt = Infer(i.E);
        if (pat is "null" or "not null")
        {
            if (vt.IsValueType && !IlTypes.IsNullable(vt)) { Raw(i.E); il.EmitPop(); il.EmitLoadConst(pat == "not null"); return typeof(bool); }
            if (IlTypes.IsNullable(vt))
            {
                Address(i.E);
                il.EmitCall(T.Method(vt.GetProperty("HasValue")!.GetGetMethod()!));
                if (pat == "null") { il.EmitLoadConst(0); il.Emit(ILOpCode.Ceq); }
                return typeof(bool);
            }
            Raw(i.E);
            il.EmitLoadNull();
            il.Emit(pat == "null" ? ILOpCode.Ceq : ILOpCode.Cgt_un);
            return typeof(bool);
        }
        string? name = null;
        string typeText = pat;
        if (pat.StartsWith("var ") && IsIdent(pat[4..].Trim()))
        {
            Raw(i.E);
            il.EmitStoreLocal(Declare(pat[4..].Trim(), vt));
            il.EmitLoadConst(true);
            return typeof(bool);
        }
        if (pat.StartsWith("not ")) throw No("MIs:pattern", pat);
        int sp0 = LastTopSpace(pat);
        if (sp0 > 0 && T.TryParse(pat[..sp0]) != null && IsIdent(pat[(sp0 + 1)..])) { name = pat[(sp0 + 1)..]; typeText = pat[..sp0]; }
        var tt = T.TryParse(typeText) ?? throw No("MIs:pattern", pat);
        Raw(i.E);
        if (vt.IsValueType) il.EmitBox(T.Il(vt));
        il.EmitIsInst(T.Il(tt));
        if (name == null)
        {
            il.EmitLoadNull();
            il.Emit(ILOpCode.Cgt_un);
            return typeof(bool);
        }
        var obj = Temp(typeof(object));
        il.EmitStoreLocal(obj);
        var local = Declare(name, tt);
        var no = il.DefineLabel();
        var end = il.DefineLabel();
        il.EmitLoadLocal(obj);
        il.EmitBranch(no, IlCond.False);
        il.EmitLoadLocal(obj);
        if (tt.IsValueType) il.EmitUnboxAny(T.Il(tt)); else il.EmitCastClass(T.Il(tt));
        il.EmitStoreLocal(local);
        il.EmitLoadConst(true);
        il.EmitBranch(end);
        il.MarkLabel(no);
        il.EmitLoadConst(false);
        il.MarkLabel(end);
        return typeof(bool);
    }

    static int LastTopSpace(string s)
    {
        int depth = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            char ch = s[i];
            if (ch is '>' or ')' or ']') depth++;
            else if (ch is '<' or '(' or '[') depth--;
            else if (ch == ' ' && depth == 0) return i;
        }
        return -1;
    }

    static bool IsIdent(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_' || s[0] == '@') && s.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    Type Cast(MCast cs)
    {
        var to = T.Parse(cs.TypeCs, span);
        if (Literal(cs.E, out var lit) && lit != null && IlTypes.IsNumeric(to) && IlTypes.IsNumeric(LitType(cs.E)))
        {
            if (IlTypes.IsFloat(to) || IlTypes.LiteralFits(lit, to)) { Const(to, IlTypes.IsFloat(to) ? System.Convert.ToDouble(lit) : lit); return to; }
            if (!Checked && IlTypes.IsIntegral(to) && lit is int or long or uint or ulong)
            {
                Const(LitType(cs.E), lit);
                Convert(LitType(cs.E), to, false);
                return to;
            }
        }
        if (IsNullLit(cs.E)) { DefaultValue(to); return to; }
        var from = Raw(cs.E);
        Convert(from, to, Checked);
        return to;
    }

    // ---- conversions

    /// <summary>Converts the value on the stack: numeric (overflow-checked when <paramref name="chk"/>), enum,
    /// boxing/unboxing, reference casts, Nullable wrapping, user-defined operators.</summary>
    void Convert(Type from, Type to, bool chk, bool implicitOnly = false)
    {
        if (from == to || to == typeof(void)) return;
        var fn = from.IsEnum ? Enum.GetUnderlyingType(from) : from;
        var tn = to.IsEnum ? Enum.GetUnderlyingType(to) : to;
        if (IlTypes.IsNumeric(fn) && IlTypes.IsNumeric(tn) && !(implicitOnly && (from.IsEnum || to.IsEnum)))
        {
            if (implicitOnly && !IlTypes.ImplicitNumeric(fn, tn) && fn != tn) throw No("Conversion:implicit", $"{from.Name}->{to.Name}");
            Numeric(fn, tn, chk);
            return;
        }
        if (to == typeof(decimal) || from == typeof(decimal))
        {
            var op = Operator(from, to, implicitOnly);
            if (op != null) { il.EmitCall(c.Module.Import(op)); return; }
        }
        if (IlTypes.IsNullable(to))
        {
            var u = Nullable.GetUnderlyingType(to)!;
            if (from != u) Convert(from, u, chk, implicitOnly);
            il.EmitNewObj(T.Method(to.GetConstructor([u])!));
            return;
        }
        if (IlTypes.IsNullable(from) && !implicitOnly)
        {
            var u = Nullable.GetUnderlyingType(from)!;
            var tmp = Temp(from);
            il.EmitStoreLocal(tmp);
            il.EmitLoadLocalAddress(tmp);
            il.EmitCall(T.Method(from.GetProperty("Value")!.GetGetMethod()!));
            Convert(u, to, chk, implicitOnly);
            return;
        }
        if (from.IsValueType && !to.IsValueType && T.Assignable(from, to)) { il.EmitBox(T.Il(from)); return; }
        if (!from.IsValueType && !to.IsValueType)
        {
            if (T.Assignable(from, to)) return;
            if (from == typeof(object) && implicitOnly) { il.EmitCastClass(T.Il(to)); return; }
            if (!implicitOnly) { il.EmitCastClass(T.Il(to)); return; }
        }
        if (!from.IsValueType && to.IsValueType && !implicitOnly && T.Assignable(to, from)) { il.EmitUnboxAny(T.Il(to)); return; }
        if (from.IsPointer && to.IsPointer || from.IsPointer && (to == typeof(nint) || to == typeof(nuint)) || to.IsPointer && (from == typeof(nint) || from == typeof(nuint))) return;
        var uop = Operator(from, to, implicitOnly);
        if (uop != null) { il.EmitCall(T.Method(uop)); return; }
        throw No("Conversion", $"{from.Name}->{to.Name}");
    }

    static MethodInfo? Operator(Type from, Type to, bool implicitOnly)
    {
        foreach (var host in new[] { to, from })
        {
            MethodInfo[] ms;
            try { ms = host.GetMethods(BindingFlags.Public | BindingFlags.Static); } catch (Exception) { continue; }
            foreach (var m in ms)
                if ((m.Name == "op_Implicit" || !implicitOnly && m.Name == "op_Explicit") && m.ReturnType == to && m.GetParameters() is [var p] && p.ParameterType == from)
                    return m;
        }
        return null;
    }

    void Numeric(Type from, Type to, bool chk)
    {
        if (from == to) return;
        bool un = IlTypes.IsUnsigned(from) && !IlTypes.IsFloat(from);
        bool fl = IlTypes.IsFloat(from);
        if (to == typeof(float)) { if (un) il.Emit(ILOpCode.Conv_r_un); il.Emit(ILOpCode.Conv_r4); return; }
        if (to == typeof(double)) { if (un) il.Emit(ILOpCode.Conv_r_un); il.Emit(ILOpCode.Conv_r8); return; }
        if (IlTypes.ImplicitNumeric(from, to))
        {
            if (IlTypes.Is64(to) && !IlTypes.Is64(from)) il.Emit(un ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8);
            else if (to == typeof(nint) || to == typeof(nuint)) il.Emit(un ? ILOpCode.Conv_u : ILOpCode.Conv_i);
            return;
        }
        // same-width reinterpretation that cannot overflow is still `conv` in IL for <32-bit targets
        ILOpCode op = (chk && !(fl && false)) switch
        {
            false when to == typeof(int) => ILOpCode.Conv_i4,
            false when to == typeof(uint) => ILOpCode.Conv_u4,
            false when to == typeof(long) => fl ? ILOpCode.Conv_i8 : un ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8,
            false when to == typeof(ulong) => fl ? ILOpCode.Conv_u8 : un ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8,
            false when to == typeof(short) => ILOpCode.Conv_i2,
            false when to == typeof(ushort) || to == typeof(char) => ILOpCode.Conv_u2,
            false when to == typeof(byte) => ILOpCode.Conv_u1,
            false when to == typeof(sbyte) => ILOpCode.Conv_i1,
            false when to == typeof(nint) => ILOpCode.Conv_i,
            false when to == typeof(nuint) => ILOpCode.Conv_u,
            true when to == typeof(int) => un ? ILOpCode.Conv_ovf_i4_un : ILOpCode.Conv_ovf_i4,
            true when to == typeof(uint) => un ? ILOpCode.Conv_ovf_u4_un : ILOpCode.Conv_ovf_u4,
            true when to == typeof(long) => un ? ILOpCode.Conv_ovf_i8_un : ILOpCode.Conv_ovf_i8,
            true when to == typeof(ulong) => un ? ILOpCode.Conv_ovf_u8_un : ILOpCode.Conv_ovf_u8,
            true when to == typeof(short) => un ? ILOpCode.Conv_ovf_i2_un : ILOpCode.Conv_ovf_i2,
            true when to == typeof(ushort) || to == typeof(char) => un ? ILOpCode.Conv_ovf_u2_un : ILOpCode.Conv_ovf_u2,
            true when to == typeof(byte) => un ? ILOpCode.Conv_ovf_u1_un : ILOpCode.Conv_ovf_u1,
            true when to == typeof(sbyte) => un ? ILOpCode.Conv_ovf_i1_un : ILOpCode.Conv_ovf_i1,
            true when to == typeof(nint) => un ? ILOpCode.Conv_ovf_i_un : ILOpCode.Conv_ovf_i,
            true when to == typeof(nuint) => un ? ILOpCode.Conv_ovf_u_un : ILOpCode.Conv_ovf_u,
            _ => throw No("Conversion", $"{from.Name}->{to.Name}"),
        };
        il.Emit(op);
    }

    // ---- conditional

    Type CondType(MCond cd)
    {
        if (cd.Type is { } at && !ReferenceEquals(at, AirType.Unknown) && T.TryParse(at.ToCs()) is { } given) return given;
        var ta = IsNullLit(cd.A) ? null : Infer(cd.A);
        var tb = IsNullLit(cd.B) ? null : Infer(cd.B);
        if (ta == null && tb == null) return typeof(object);
        if (ta == null) return tb!.IsValueType && !IlTypes.IsNullable(tb) ? typeof(Nullable<>).MakeGenericType(tb) : tb;
        if (tb == null) return ta.IsValueType && !IlTypes.IsNullable(ta) ? typeof(Nullable<>).MakeGenericType(ta) : ta;
        if (ta == tb) return ta;
        Literal(cd.A, out var la);
        Literal(cd.B, out var lb);
        if (la != null && IlTypes.LiteralFits(la, tb)) return tb;
        if (lb != null && IlTypes.LiteralFits(lb, ta)) return ta;
        if (IlTypes.ImplicitNumeric(ta, tb)) return tb;
        if (IlTypes.ImplicitNumeric(tb, ta)) return ta;
        if (!ta.IsValueType && T.Assignable(tb, ta)) return ta;
        if (!tb.IsValueType && T.Assignable(ta, tb)) return tb;
        throw No("MCond:types", ta.Name + "/" + tb.Name);
    }

    void Cond(MCond cd, Type t)
    {
        // `c ? 1 : 0` is the comparison value itself, as Roslyn emits it: no branch for unpredictable data (editdist)
        if (IlTypes.IsIntegral(t) && Literal(cd.A, out var a1) && Literal(cd.B, out var b0) && a1 is int or uint or long or ulong && b0 is int or uint or long or ulong
            && System.Convert.ToDecimal(a1) is var av && System.Convert.ToDecimal(b0) is var bv && (av, bv) is (1m, 0m) or (0m, 1m))
        {
            Emit(cd.C, typeof(bool));
            if (av == 0m) { il.EmitLoadConst(0); il.Emit(ILOpCode.Ceq); }
            if (IlTypes.Is64(t)) il.Emit(ILOpCode.Conv_u8);
            return;
        }
        var els = il.DefineLabel();
        var end = il.DefineLabel();
        Jump(cd.C, true, els);
        Emit(cd.B, t);
        il.EmitBranch(end);
        il.MarkLabel(els);
        Emit(cd.A, t);
        il.MarkLabel(end);
    }

    // ---- binary

    Type Binary(MBinary b)
    {
        bool chk = b.Checked && Checked;
        if (b.Op is AirBinOp.And or AirBinOp.Or)
        {
            var shortcut = il.DefineLabel();
            var end = il.DefineLabel();
            Jump(b.L, b.Op == AirBinOp.Or, shortcut);
            Emit(b.R, typeof(bool));
            il.EmitBranch(end);
            il.MarkLabel(shortcut);
            il.EmitLoadConst(b.Op == AirBinOp.Or);
            il.MarkLabel(end);
            return typeof(bool);
        }
        if (b.Op == AirBinOp.Coalesce) return Coalesce(b);
        if (b.Op is AirBinOp.Eq or AirBinOp.Ne && (IsNullLit(b.R) || IsNullLit(b.L)))
        {
            var x = IsNullLit(b.R) ? b.L : b.R;
            return Is(new MIs { E = x, TypeCs = b.Op == AirBinOp.Eq ? "null" : "not null" });
        }
        var lt = Infer(b.L);
        var rt = Infer(b.R);
        if (b.Op is AirBinOp.Shl or AirBinOp.Shr or AirBinOp.UShr)
        {
            var wt = IlTypes.Widen(lt.IsEnum ? Enum.GetUnderlyingType(lt) : lt);
            if (!IlTypes.IsIntegral(wt)) return UserOp(b, lt, rt);
            Emit(b.L, wt);
            ShiftCount(b.R, wt);
            il.Emit(b.Op == AirBinOp.Shl ? ILOpCode.Shl : b.Op == AirBinOp.UShr || IlTypes.IsUnsigned(wt) ? ILOpCode.Shr_un : ILOpCode.Shr);
            return wt;
        }
        if (b.Op == AirBinOp.Add && (lt == typeof(string) || rt == typeof(string)) && !(lt == typeof(char) && rt == typeof(char))) return Concat(b, lt, rt);
        Literal(b.L, out var ll);
        Literal(b.R, out var rl);
        var ln = lt.IsEnum ? Enum.GetUnderlyingType(lt) : lt;
        var rn = rt.IsEnum ? Enum.GetUnderlyingType(rt) : rt;
        Type pt;
        if (IlTypes.IsNumeric(ln) && IlTypes.IsNumeric(rn) && !(lt.IsEnum || rt.IsEnum) || ll != null && IlTypes.IsNumeric(rn) && !rt.IsEnum || rl != null && IlTypes.IsNumeric(ln) && !lt.IsEnum)
            pt = IlTypes.Promote(lt, rt, ll, rl);
        else if (lt.IsEnum && lt == rt) pt = ln;
        else if (lt == typeof(bool) && rt == typeof(bool)) pt = typeof(bool);
        else if (lt == typeof(decimal) || rt == typeof(decimal) || UserOpMethod(b.Op, lt, rt) != null) return UserOp(b, lt, rt);
        else if (b.Op is AirBinOp.Eq or AirBinOp.Ne && !lt.IsValueType && !rt.IsValueType) pt = typeof(object);
        else if (lt.IsPointer && rt.IsPointer && b.Op is AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge) pt = typeof(nuint);
        else throw No("MBinary:operand-types", $"{lt.Name} {b.Op} {rt.Name}");
        if (pt == typeof(decimal)) return UserOp(b, typeof(decimal), typeof(decimal));
        if (pt == typeof(object))
        {
            Raw(b.L);
            Raw(b.R);
        }
        else
        {
            EmitAs(b.L, lt, pt);
            EmitAs(b.R, rt, pt);
        }
        if (Compare(b.Op, pt)) return typeof(bool);
        Arith(b.Op, pt, chk);
        return lt.IsEnum && lt == rt && b.Op is AirBinOp.BitAnd or AirBinOp.BitOr or AirBinOp.BitXor ? lt : pt;
    }

    /// <summary>Emits an operand in the promoted type (enums as their underlying type).</summary>
    void EmitAs(MExpr e, Type natural, Type pt)
    {
        if (natural.IsEnum && Enum.GetUnderlyingType(natural) is var u && (u == pt || IlTypes.ImplicitNumeric(u, pt)))
        {
            Raw(e);
            if (u != pt) Numeric(u, pt, false);
            return;
        }
        Emit(e, pt);
    }

    static readonly Dictionary<AirBinOp, string> OpNames = new()
    {
        [AirBinOp.Add] = "op_Addition", [AirBinOp.Sub] = "op_Subtraction", [AirBinOp.Mul] = "op_Multiply", [AirBinOp.Div] = "op_Division",
        [AirBinOp.Mod] = "op_Modulus", [AirBinOp.Eq] = "op_Equality", [AirBinOp.Ne] = "op_Inequality", [AirBinOp.Lt] = "op_LessThan",
        [AirBinOp.Le] = "op_LessThanOrEqual", [AirBinOp.Gt] = "op_GreaterThan", [AirBinOp.Ge] = "op_GreaterThanOrEqual",
        [AirBinOp.BitAnd] = "op_BitwiseAnd", [AirBinOp.BitOr] = "op_BitwiseOr", [AirBinOp.BitXor] = "op_ExclusiveOr",
        [AirBinOp.Shl] = "op_LeftShift", [AirBinOp.Shr] = "op_RightShift", [AirBinOp.UShr] = "op_UnsignedRightShift",
    };

    object? UserOpMethod(AirBinOp op, Type lt, Type rt)
    {
        if (!OpNames.TryGetValue(op, out var name)) return null;
        var args2 = new[] { new ArgInfo(lt, null, false, ""), new ArgInfo(rt, null, false, "") };
        var cands = new List<Cand>();
        foreach (var t in new[] { lt, rt }.Distinct())
        {
            if (T.Alice(t) is { } a) cands.AddRange(a.FindMethods(name).Where(m => m.IsStatic && m.Params.Length == 2).Select(IlTypes.FromAlice));
            else if (!t.IsPrimitive && t != typeof(string))
                try { cands.AddRange(t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name && m.GetParameters().Length == 2).Select(m => IlTypes.FromClr(m))); }
                catch (Exception) { }
        }
        return cands.Count == 0 ? null : T.Pick(cands, args2);
    }

    Type UserOp(MBinary b, Type lt, Type rt)
    {
        if (lt == typeof(string) && rt == typeof(string) && b.Op is AirBinOp.Eq or AirBinOp.Ne)
        {
            Raw(b.L);
            Raw(b.R);
            il.EmitCall(c.Module.Import(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!));
            if (b.Op == AirBinOp.Ne) { il.EmitLoadConst(0); il.Emit(ILOpCode.Ceq); }
            return typeof(bool);
        }
        if (lt == typeof(decimal) || rt == typeof(decimal))
        {
            if (IlTypes.IsNumeric(lt) && lt != typeof(decimal)) lt = typeof(decimal);
            if (IlTypes.IsNumeric(rt) && rt != typeof(decimal)) rt = typeof(decimal);
        }
        var cand = UserOpMethod(b.Op, lt, rt) as Cand ?? throw No("MBinary:operator", $"{lt.Name} {b.Op} {rt.Name}");
        Emit(b.L, cand.Params[0]);
        Emit(b.R, cand.Params[1]);
        if (cand.Alice != null) il.EmitCall(cand.Alice.Def); else il.EmitCall(T.Method(cand.Clr!));
        return cand.Ret;
    }

    Type Concat(MBinary b, Type lt, Type rt)
    {
        void Part(MExpr e, Type t)
        {
            Raw(e);
            if (t == typeof(string)) return;
            if (t.IsValueType)
            {
                var tmp = Temp(t);
                il.EmitStoreLocal(tmp);
                il.EmitLoadLocalAddress(tmp);
                il.EmitConstrainedPrefix(T.Il(t));
                il.EmitCall(c.Module.Import(typeof(object).GetMethod("ToString")!), virtualCall: true);
                return;
            }
            // a null reference concatenates as ""
            var ok = il.DefineLabel();
            var end = il.DefineLabel();
            il.EmitDup();
            il.EmitBranch(ok, IlCond.True);
            il.EmitPop();
            il.EmitLoadConst("");
            il.EmitBranch(end);
            il.MarkLabel(ok);
            il.EmitCall(c.Module.Import(typeof(object).GetMethod("ToString")!), virtualCall: true);
            il.MarkLabel(end);
        }
        Part(b.L, lt);
        Part(b.R, rt);
        il.EmitCall(c.Module.Import(typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!));
        return typeof(string);
    }

    Type Coalesce(MBinary b)
    {
        var lt = Infer(b.L);
        if (lt.IsValueType && !IlTypes.IsNullable(lt)) { Raw(b.L); return lt; }
        if (IlTypes.IsNullable(lt))
        {
            var u = Nullable.GetUnderlyingType(lt)!;
            var rt0 = Infer(b.R);
            var res = rt0 == lt ? lt : u;
            Raw(b.L);
            var tmp = Temp(lt);
            il.EmitStoreLocal(tmp);
            var none = il.DefineLabel();
            var end = il.DefineLabel();
            il.EmitLoadLocalAddress(tmp);
            il.EmitCall(T.Method(lt.GetProperty("HasValue")!.GetGetMethod()!));
            il.EmitBranch(none, IlCond.False);
            il.EmitLoadLocalAddress(tmp);
            if (res == lt) il.EmitLoadObj(T.Il(lt)); else il.EmitCall(T.Method(lt.GetMethod("GetValueOrDefault", Type.EmptyTypes)!));
            il.EmitBranch(end);
            il.MarkLabel(none);
            Emit(b.R, res);
            il.MarkLabel(end);
            return res;
        }
        var rt = Infer(b.R);
        var t = T.Assignable(rt, lt) || IsNullLit(b.R) ? lt : T.Assignable(lt, rt) ? rt : lt;
        var done = il.DefineLabel();
        Emit(b.L, t);
        il.EmitDup();
        il.EmitBranch(done, IlCond.True);
        il.EmitPop();
        Emit(b.R, t);
        il.MarkLabel(done);
        return t;
    }

    /// <summary>The shift count as Roslyn emits it: masked to the operand width (IL leaves larger counts undefined).</summary>
    void ShiftCount(MExpr count, Type operand)
    {
        int mask = IlTypes.Is64(operand) ? 63 : 31;
        if (Literal(count, out var v) && v is int n) { il.EmitLoadConst(n & mask); return; }
        Emit(count, typeof(int));
        il.EmitLoadConst(mask);
        il.Emit(ILOpCode.And);
    }

    bool Compare(AirBinOp op, Type t)
    {
        bool un = IlTypes.IsUnsigned(t) || t.IsPointer || t == typeof(nuint), fl = IlTypes.IsFloat(t);
        switch (op)
        {
            case AirBinOp.Eq: il.Emit(ILOpCode.Ceq); return true;
            case AirBinOp.Ne: il.Emit(ILOpCode.Ceq); il.EmitLoadConst(0); il.Emit(ILOpCode.Ceq); return true;
            case AirBinOp.Lt: il.Emit(un ? ILOpCode.Clt_un : ILOpCode.Clt); return true;
            case AirBinOp.Gt: il.Emit(un ? ILOpCode.Cgt_un : ILOpCode.Cgt); return true;
            // a >= b is !(a < b), where for floats "<" must be unordered so NaN compares false
            case AirBinOp.Ge: il.Emit(un || fl ? ILOpCode.Clt_un : ILOpCode.Clt); il.EmitLoadConst(0); il.Emit(ILOpCode.Ceq); return true;
            case AirBinOp.Le: il.Emit(un || fl ? ILOpCode.Cgt_un : ILOpCode.Cgt); il.EmitLoadConst(0); il.Emit(ILOpCode.Ceq); return true;
        }
        return false;
    }

    void Arith(AirBinOp op, Type t, bool chk)
    {
        bool un = IlTypes.IsUnsigned(t), integral = IlTypes.IsIntegral(t);
        chk &= integral;
        il.EmitBinary(op switch
        {
            AirBinOp.Add => IlBinOp.Add,
            AirBinOp.Sub => IlBinOp.Sub,
            AirBinOp.Mul => IlBinOp.Mul,
            AirBinOp.Div => IlBinOp.Div,
            AirBinOp.Mod => IlBinOp.Rem,
            AirBinOp.BitAnd => IlBinOp.And,
            AirBinOp.BitOr => IlBinOp.Or,
            AirBinOp.BitXor => IlBinOp.Xor,
            _ => throw No("MBinary:" + op),
        }, chk, un && (integral || op is AirBinOp.Div or AirBinOp.Mod));
    }

    /// <summary>Branch to <paramref name="target"/> when <paramref name="cond"/> == <paramref name="when"/>, fusing
    /// comparisons, `!`, `&amp;&amp;` and `||` into the branch structure as Roslyn does.</summary>
    void Jump(MExpr cond, bool when, IlLabel target)
    {
        switch (cond)
        {
            case MUnary { Op: AirUnOp.Not } n: Jump(n.E, !when, target); return;
            case MBinary { Op: AirBinOp.And or AirBinOp.Or } b:
                {
                    bool and = b.Op == AirBinOp.And;
                    if (and != when) { Jump(b.L, when, target); Jump(b.R, when, target); return; }
                    var skip = il.DefineLabel();
                    Jump(b.L, !when, skip);
                    Jump(b.R, when, target);
                    il.MarkLabel(skip);
                    return;
                }
            case MBinary { Op: AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge } b when Comparable(b, out var pt):
                {
                    var (lt, rt) = (Infer(b.L), Infer(b.R));
                    if (b.Op is AirBinOp.Eq or AirBinOp.Ne && !IlTypes.IsFloat(pt) && (IsZero(b.R) || IsZero(b.L)))
                    {
                        var x = IsZero(b.R) ? b.L : b.R;
                        EmitAs(x, IsZero(b.R) ? lt : rt, pt);
                        il.EmitBranch(target, (b.Op == AirBinOp.Ne) == when ? IlCond.True : IlCond.False);
                        return;
                    }
                    EmitAs(b.L, lt, pt);
                    EmitAs(b.R, rt, pt);
                    bool un = IlTypes.IsUnsigned(pt), fl = IlTypes.IsFloat(pt);
                    var cc = (b.Op, when) switch
                    {
                        (AirBinOp.Eq, true) or (AirBinOp.Ne, false) => IlCond.Eq,
                        (AirBinOp.Ne, true) or (AirBinOp.Eq, false) => IlCond.NotEq,
                        (AirBinOp.Lt, true) => un ? IlCond.LtUn : IlCond.Lt,
                        (AirBinOp.Le, true) => un ? IlCond.LeUn : IlCond.Le,
                        (AirBinOp.Gt, true) => un ? IlCond.GtUn : IlCond.Gt,
                        (AirBinOp.Ge, true) => un ? IlCond.GeUn : IlCond.Ge,
                        // negated: unordered (NaN) must take the "false" branch, so floats use the .un forms
                        (AirBinOp.Lt, false) => un || fl ? IlCond.GeUn : IlCond.Ge,
                        (AirBinOp.Le, false) => un || fl ? IlCond.GtUn : IlCond.Gt,
                        (AirBinOp.Gt, false) => un || fl ? IlCond.LeUn : IlCond.Le,
                        (AirBinOp.Ge, false) => un || fl ? IlCond.LtUn : IlCond.Lt,
                        _ => throw new InvalidOperationException(),
                    };
                    il.EmitBranch(target, cc);
                    return;
                }
        }
        Emit(cond, typeof(bool));
        il.EmitBranch(target, when ? IlCond.True : IlCond.False);
    }

    bool IsZero(MExpr e) => Literal(e, out var v) && v is int or uint or long or ulong && System.Convert.ToDecimal(v) == 0m || e is MCode { Text: "false" } or AirConst { Value: false };

    /// <summary>A numeric comparison, or ==/!= of bools or enums: one IL compare-and-branch in the promoted type.</summary>
    bool Comparable(MBinary b, out Type pt)
    {
        pt = typeof(void);
        if (IsNullLit(b.L) || IsNullLit(b.R)) return false;
        var lt = Infer(b.L);
        var rt = Infer(b.R);
        Literal(b.L, out var ll);
        Literal(b.R, out var rl);
        var ln = lt.IsEnum ? Enum.GetUnderlyingType(lt) : lt;
        var rn = rt.IsEnum ? Enum.GetUnderlyingType(rt) : rt;
        if (lt.IsEnum || rt.IsEnum)
        {
            if (lt != rt) return false;
            pt = ln;
            return true;
        }
        if (IlTypes.IsNumeric(ln) && IlTypes.IsNumeric(rn) || ll != null && IlTypes.IsNumeric(rn) || rl != null && IlTypes.IsNumeric(ln))
        {
            pt = IlTypes.Promote(lt, rt, ll, rl);
            return pt != typeof(decimal);
        }
        if (lt == typeof(bool) && rt == typeof(bool) && b.Op is AirBinOp.Eq or AirBinOp.Ne) { pt = typeof(bool); return true; }
        return false;
    }

    // ---- unary

    Type Unary(MUnary u)
    {
        switch (u.Op)
        {
            case AirUnOp.NotNull: return Raw(u.E);
            case AirUnOp.Not:
                {
                    var t0 = Infer(u.E);
                    if (t0 != typeof(bool)) throw No("MUnary:Not", t0.Name);
                    Emit(u.E, typeof(bool));
                    il.EmitLoadConst(0);
                    il.Emit(ILOpCode.Ceq);
                    return typeof(bool);
                }
            case AirUnOp.BitNot:
                {
                    var t0 = Infer(u.E);
                    var t = IlTypes.Widen(t0.IsEnum ? Enum.GetUnderlyingType(t0) : t0);
                    EmitAs(u.E, t0, t);
                    il.Emit(ILOpCode.Not);
                    return t0.IsEnum ? t0 : t;
                }
            case AirUnOp.Neg:
                {
                    if (Literal(u, out var lit) && lit != null) { var lt0 = LitType(u); Const(lt0, lit); return lt0; }
                    var t = IlTypes.Widen(Infer(u.E));
                    if (t == typeof(uint)) t = typeof(long);
                    if (t == typeof(decimal)) { Emit(u.E, t); il.EmitCall(c.Module.Import(typeof(decimal).GetMethod("op_UnaryNegation")!)); return t; }
                    if (!IlTypes.IsNumeric(t)) throw No("MUnary:Neg", t.Name);
                    if (u.Checked && Checked && IlTypes.IsIntegral(t)) { Const(t, 0); Emit(u.E, t); il.EmitBinary(IlBinOp.Sub, true, false); }
                    else { Emit(u.E, t); il.Emit(ILOpCode.Neg); }
                    return t;
                }
            case AirUnOp.Inc or AirUnOp.Dec:
                {
                    var t = Infer(u.E);
                    string op = u.Op == AirUnOp.Inc ? "+=" : "-=";
                    if (u.Postfix)
                    {
                        Raw(u.E);
                        var tmp = Temp(t);
                        il.EmitStoreLocal(tmp);
                        Assign(u.E, op, AirConst.Of(1), u.Checked);
                        il.EmitLoadLocal(tmp);
                    }
                    else
                    {
                        Assign(u.E, op, AirConst.Of(1), u.Checked);
                        Raw(u.E);
                    }
                    return t;
                }
        }
        throw No("MUnary:" + u.Op);
    }
}

/// <summary>An explicit conversion to a reflection type (compound assignment through an indexer: `x[i] = (T)(x[i] op v)`).</summary>
internal sealed class IlConv : MExpr
{
    public Type To = null!;
    public MExpr E = null!;
    public bool Checked;
}

internal static class IlBuilderTyped
{
    /// <summary>`ldind.*` / `ldobj T` for the element at the address on the stack.</summary>
    public static void EmitLoadObj(this IlBuilder il, IlType t)
    {
        var op = Ind(t, load: true);
        if (op is { } o) il.Emit(o); else il.Emit(ILOpCode.Ldobj, t);
    }

    /// <summary>`stind.*` / `stobj T`.</summary>
    public static void EmitStoreObj(this IlBuilder il, IlType t)
    {
        var op = Ind(t, load: false);
        if (op is { } o) il.Emit(o); else il.Emit(ILOpCode.Stobj, t);
    }

    static ILOpCode? Ind(IlType t, bool load) => t switch
    {
        IlType.PrimType p => p.Code switch
        {
            PrimitiveTypeCode.SByte => load ? ILOpCode.Ldind_i1 : ILOpCode.Stind_i1,
            PrimitiveTypeCode.Byte or PrimitiveTypeCode.Boolean => load ? ILOpCode.Ldind_u1 : ILOpCode.Stind_i1,
            PrimitiveTypeCode.Int16 => load ? ILOpCode.Ldind_i2 : ILOpCode.Stind_i2,
            PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char => load ? ILOpCode.Ldind_u2 : ILOpCode.Stind_i2,
            PrimitiveTypeCode.Int32 => load ? ILOpCode.Ldind_i4 : ILOpCode.Stind_i4,
            PrimitiveTypeCode.UInt32 => load ? ILOpCode.Ldind_u4 : ILOpCode.Stind_i4,
            PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => load ? ILOpCode.Ldind_i8 : ILOpCode.Stind_i8,
            PrimitiveTypeCode.Single => load ? ILOpCode.Ldind_r4 : ILOpCode.Stind_r4,
            PrimitiveTypeCode.Double => load ? ILOpCode.Ldind_r8 : ILOpCode.Stind_r8,
            PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => load ? ILOpCode.Ldind_i : ILOpCode.Stind_i,
            PrimitiveTypeCode.String or PrimitiveTypeCode.Object => load ? ILOpCode.Ldind_ref : ILOpCode.Stind_ref,
            _ => null,
        },
        IlType.SzArrayType or IlType.ArrayType => load ? ILOpCode.Ldind_ref : ILOpCode.Stind_ref,
        IlType.NamedType { IsValueType: false } or IlType.GenericInstType { IsValueType: false } => load ? ILOpCode.Ldind_ref : ILOpCode.Stind_ref,
        _ => null,
    };
}
