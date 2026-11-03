using System.Reflection;
using System.Reflection.Metadata;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// D1.5 consumer: closure-converted bodies (Air/Passes/ClosureConvert, ADR 0033 "Closure ABI"). Every display type
/// becomes a private nested class of the method's type (fields typed from the capture or, at creation, from the
/// captured value), every closure method a method on it (or a private static method on the owner for capture-free
/// lambdas, with a lazily cached delegate field). Closure signatures come from the delegate type the call site wants:
/// a lambda argument takes part in overload resolution as <see cref="IlTypes.LambdaArg"/>, generic parameters bound
/// only by the lambda's result are inferred from its expression body. DisplayInfo structs (kernel roles) are demoted to
/// classes here (<see cref="ClosureContext.StructDisplays"/> = false): kernels run as Par.Range/Reduce/Fork delegates.
/// </summary>
public sealed partial class AirToIl
{
    internal sealed class DisplayInfo
    {
        public AirDisplayType D = null!;
        public AliceT T = null!;
        public IlMethodDef Ctor = null!;
    }

    internal sealed class Closure
    {
        public AirClosureMethod M = null!;
        public AMethod Am = null!;
        public Type Delegate = null!;
        public IlFieldDef? Cache;
    }

    readonly Dictionary<AirDisplayType, DisplayInfo> displays = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<AirClosureMethod, Closure> closures = new(ReferenceEqualityComparer.Instance);
    readonly List<Closure> pendingClosures = [];
    int closureN;

    /// <summary>`ALICE_IL_CLOSURES=0`: no closure conversion (lambdas stay unsupported).</summary>
    static readonly bool ClosuresOn = Environment.GetEnvironmentVariable("ALICE_IL_CLOSURES") != "0";

    /// <summary>Closure conversion of one body (after IlLowering), with the method as context.</summary>
    void ConvertClosures(List<MStmt> body, AliceT host, string method, bool instance, IReadOnlyList<(string Name, Type Type)> ps)
    {
        if (!ClosuresOn) return;
        var ctx = new ClosureContext { Method = method + "_" + closureN++, Instance = instance, StructDisplays = false };
        foreach (var (n, _) in ps) ctx.Params.Add((n, null));
        var saved = ClosureConvert.Current;
        ClosureConvert.Current = ctx;
        try
        {
            var cc = new ClosureConvert(ctx);
            cc.Run(body);
            foreach (var u in cc.Unsupported) Stats.Seen("ClosureConvert.left:" + u.Split(':')[0]);
        }
        finally { ClosureConvert.Current = saved; }
        var (ds, _) = ClosureConvert.Declarations(body);
        foreach (var d in ds) DeclareDisplay(d, host);
    }

    internal DisplayInfo DisplayOf(AirDisplayType d) =>
        displays.TryGetValue(d, out var x) ? x : throw new IlUnsupportedException("Closure:undeclared-display", null, d.Name);

    void DeclareDisplay(AirDisplayType d, AliceT host)
    {
        if (displays.ContainsKey(d)) return;
        if (d.Kind != AirDisplayKind.Class) throw new IlUnsupportedException("Closure:display-struct", null, d.Name);
        if (host.Def.GenericParameters.Count > 0) throw new IlUnsupportedException("Closure:generic-owner", null, host.ToString());
        var def = host.Def.DefineNestedType(d.Name, TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            Module.CoreType("Object"));
        var a = new AliceT { Def = def, Sym = host.Sym, Host = host };
        a.Proxy = Types.MakeDisplayProxy(a);
        Types.ByCs[d.Name] = a;
        var ctor = def.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        var il = Body(ctor);
        il.EmitLoadArg(0);
        il.EmitCall(Module.Import(typeof(object).GetConstructor(Type.EmptyTypes)!));
        il.Emit(ILOpCode.Ret);
        var x = new DisplayInfo { D = d, T = a, Ctor = ctor };
        displays[d] = x;
        foreach (var f in d.Fields)
            if (Hint(f) is { } t) DisplayField(x, f.Field, t);
    }

    /// <summary>A capture's declared type, when the pass knew it.</summary>
    internal Type? Hint(AirCapture f)
    {
        if (f.Type is { } at && !ReferenceEquals(at, AirType.Unknown) && Types.TryParse(at.ToCs()) is { } t) return t;
        if (f.TypeCs is { Length: > 0 } cs && cs != "var" && Types.TryParse(cs) is { } t2) return t2;
        return null;
    }

    /// <summary>The field of a display, declared on first use with <paramref name="t"/> (null: it must exist).</summary>
    internal AField DisplayField(DisplayInfo d, string name, Type? t)
    {
        if (d.T.Fields.TryGetValue(name, out var f)) return f;
        if (t == null || t == typeof(void)) throw new IlUnsupportedException("Closure:field-type", null, d.D.Name + "." + name);
        var fd = d.T.Def.DefineField(name, Types.Il(t), FieldAttributes.Public);
        return d.T.Fields[name] = new AField { Name = name, Type = t, Owner = d.T, Field = fd };
    }

    /// <summary>The method of a closure, declared on first use with the signature of <paramref name="del"/>.</summary>
    internal Closure DeclareClosure(AirClosureMethod m, Type del, AliceT host)
    {
        if (closures.TryGetValue(m, out var cl))
            return cl.Delegate == del ? cl : throw new IlUnsupportedException("Closure:two-delegate-types", null, m.Name);
        var inv = del.GetMethod("Invoke") ?? throw new IlUnsupportedException("Closure:not-a-delegate", null, del.Name);
        var ps = inv.GetParameters().Select(p => p.ParameterType).ToArray();
        if (ps.Length != m.Params.Count) throw new IlUnsupportedException("Closure:arity", null, m.Name);
        var owner = m.Owner != null ? DisplayOf(m.Owner).T : host;
        if (owner.Def.GenericParameters.Count > 0) throw new IlUnsupportedException("Closure:generic-owner", null, owner.ToString());
        bool st = m.Owner == null;
        var attrs = MethodAttributes.HideBySig | (st ? MethodAttributes.Private | MethodAttributes.Static : MethodAttributes.Assembly);
        var def = owner.Def.DefineMethod(m.Name, attrs, Types.Il(inv.ReturnType), [.. ps.Select(Types.Il)]);
        var names = m.Params.Select(ParamName).ToArray();
        for (int i = 0; i < names.Length; i++) def.DefineParameter(i + 1, names[i]);
        if (m.Hot) def.ImplAttributes |= MethodImplAttributes.AggressiveOptimization;
        var am = new AMethod { Name = m.Name, Owner = owner, Ret = inv.ReturnType, Params = ps, ParamNames = names, IsStatic = st, Def = def };
        cl = new Closure { M = m, Am = am, Delegate = del };
        if (st)
            cl.Cache = owner.Def.DefineField(m.CachedField ?? "<>9__" + m.Name, Types.Il(del), FieldAttributes.Private | FieldAttributes.Static);
        closures[m] = cl;
        pendingClosures.Add(cl);
        return cl;
    }

    internal static string ParamName(string p)
    {
        var parts = p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? p : parts[^1];
    }

    internal static List<MStmt> ClosureBody(AirClosureMethod m, Type ret) =>
        m.Body ?? (ret == typeof(void) ? [new MExprStmt { E = m.ExprBody! }] : [new MReturn { Value = m.ExprBody }]);

    /// <summary>Emits the closure methods declared so far (and the ones their bodies declare).</summary>
    void FlushClosures()
    {
        while (pendingClosures.Count > 0)
        {
            var cl = pendingClosures[0];
            pendingClosures.RemoveAt(0);
            var am = cl.Am;
            var ps = am.ParamNames.Select((n, i) => (n, am.Params[i])).ToList();
            var b = new IlBody(this, am.Owner, am, Body(am.Def), am.Ret, am.IsStatic, ps);
            b.Body(ClosureBody(cl.M, am.Ret));
        }
    }
}

internal sealed partial class IlBody
{
    /// <summary>A display instance: `newobj`, then (not hoisted) every capture copied from its source.</summary>
    Type DisplayNew(AirDisplayNew dn)
    {
        var d = c.DisplayOf(dn.Display);
        PrepareDisplay(dn);
        il.EmitNewObj(d.Ctor);
        if (!dn.Display.Hoisted)
            foreach (var f in dn.Display.Fields)
            {
                var af = c.DisplayField(d, f.Field, null);
                il.Emit(ILOpCode.Dup);
                Emit(f.Source, af.Type);
                il.EmitStoreField(af.Field!);
            }
        return d.T.Proxy;
    }

    /// <summary>Declares the fields of a display about to be created, typed from the captured values.</summary>
    void PrepareDisplay(MExpr? target)
    {
        if (target is not AirDisplayNew { Display: { Hoisted: false } dt }) return;
        var d = c.DisplayOf(dt);
        foreach (var f in dt.Fields)
            if (!d.T.Fields.ContainsKey(f.Field)) c.DisplayField(d, f.Field, c.Hint(f) ?? Infer(f.Source));
    }

    Type DisplayFieldLoad(AirDisplayField df)
    {
        var af = c.DisplayField(c.DisplayOf(df.Owner), df.Field, null);
        Raw(df.Display);
        il.EmitLoadField(af.Field!);
        return af.Type;
    }

    void DisplayFieldAddress(AirDisplayField df)
    {
        var af = c.DisplayField(c.DisplayOf(df.Owner), df.Field, null);
        Raw(df.Display);
        il.EmitLoadFieldAddress(af.Field!);
    }

    void DisplayFieldStore(AirDisplayField df, string op, MExpr value, bool nodeChecked)
    {
        if (op != "=")
        {
            DisplayFieldStore(df, "=", new MBinary { Op = CompoundOp(op, this), L = df, R = value, Checked = nodeChecked, Span = value.Span }, nodeChecked);
            return;
        }
        var d = c.DisplayOf(df.Owner);
        var hint = df.Owner.Fields.FirstOrDefault(f => f.Field == df.Field) is { } cap ? c.Hint(cap) : null;
        var af = c.DisplayField(d, df.Field, d.T.Fields.ContainsKey(df.Field) ? null : hint ?? Infer(value));
        Raw(df.Display);
        Emit(value, af.Type);
        il.EmitStoreField(af.Field!);
    }

    /// <summary>A delegate of type <paramref name="want"/> over a closure method.</summary>
    Type MakeDelegate(AirDelegateCreate dc, Type want)
    {
        if (!typeof(System.Delegate).IsAssignableFrom(want) || want == typeof(System.Delegate) || want == typeof(MulticastDelegate))
            throw No("Closure:target-type", want.Name);
        PrepareDisplay(dc.Target);
        var cl = c.DeclareClosure(dc.Method, want, owner);
        var ctor = T.Method(want.GetConstructor([typeof(object), typeof(IntPtr)]) ?? throw No("Closure:delegate-ctor", want.Name));
        if (cl.Cache is { } cache)
        {
            var done = il.DefineLabel();
            il.EmitLoadField(cache);
            il.Emit(ILOpCode.Dup);
            il.EmitBranch(done, IlCond.True);
            il.Emit(ILOpCode.Pop);
            il.EmitLoadNull();
            il.Emit(ILOpCode.Ldftn, cl.Am.Def);
            il.EmitNewObj(ctor);
            il.Emit(ILOpCode.Dup);
            il.EmitStoreField(cache);
            il.MarkLabel(done);
            return want;
        }
        if (dc.ByRefTarget) throw No("Closure:byref-target", dc.Method.Name);
        Raw(dc.Target ?? throw No("Closure:no-target", dc.Method.Name));
        il.Emit(ILOpCode.Ldftn, cl.Am.Def);
        il.EmitNewObj(ctor);
        return want;
    }

    /// <summary>The delegate type of a lambda outside a call: its AIR type, else Func/Action over its declared
    /// parameter types and inferred result.</summary>
    Type? LambdaType(AirDelegateCreate dc)
    {
        if (dc.Type is { } at && !ReferenceEquals(at, AirType.Unknown) && T.TryParse(at.ToCs()) is { } dt && typeof(System.Delegate).IsAssignableFrom(dt)) return dt;
        var ps = new List<Type>();
        foreach (var p in dc.Method.Params)
        {
            var parts = p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || T.TryParse(string.Join(' ', parts[..^1])) is not { } pt) return null;
            ps.Add(pt);
        }
        var ret = dc.Method.Body is { } body && !body.Any(s => s is MReturn { Value: not null }) ? typeof(void) : LambdaReturn(dc, [.. ps]);
        if (ret == null) return null;
        return System.Linq.Expressions.Expression.GetDelegateType([.. ps, ret]);
    }

    /// <summary>The result type of a lambda given its parameter types (generic inference); null when unknown.</summary>
    internal Type? LambdaReturn(object lambda, Type[] ps)
    {
        if (lambda is not AirDelegateCreate dc) return null;
        var m = dc.Method;
        var e = m.ExprBody ?? (m.Body is [.., MReturn { Value: { } v }] ? v : null);
        if (e == null || m.Body != null && m.Body.Count != 1) return null;
        try
        {
            PrepareDisplay(dc.Target);
            var o = m.Owner != null ? c.DisplayOf(m.Owner).T : owner;
            var names = m.Params.Select(AirToIl.ParamName).ToArray();
            var b = new IlBody(c, o, null, new IlBuilder(true, c.Module), typeof(object), m.Owner == null, [.. names.Select((n, i) => (n, ps[i]))]);
            return b.Infer(e);
        }
        catch (IlUnsupportedException) { return null; }
    }

    static readonly MCode ParType = new("global::Alice.Runtime.Par");

    static MBinary Bin(AirBinOp op, MExpr l, MExpr r) => new() { Op = op, L = l, R = r };
    static MCall StaticCall(string type, string name, params MExpr[] args) => new() { Target = new MCode(type), Name = name, Args = [.. args] };

    /// <summary>A parallel-lowering or iterator node as the MIR of what Backend/Printer.Parallel.cs and
    /// Printer.Iterators.cs print for it.</summary>
    MExpr Spelled(MExpr e)
    {
        switch (e)
        {
            case AirParGuard g:
                {
                    MExpr r = Bin(AirBinOp.And, Bin(AirBinOp.Ge, g.Count, AirConst.Of(g.Min)),
                        Bin(AirBinOp.Gt, new MMember { Target = ParType, Name = "Workers" }, AirConst.Of(1)));
                    foreach (var d in g.Distinct) r = Bin(AirBinOp.And, r, d);
                    return r;
                }
            case AirReferenceEqualsGuard q:
                return new MUnary { Op = AirUnOp.Not, E = StaticCall("global::System.Object", "ReferenceEquals", q.W, q.R) };
            case AirParProbe pr:
                {
                    if (pr.Kind == AirProbeKind.Timestamp) return StaticCall("global::System.Diagnostics.Stopwatch", "GetTimestamp");
                    var i = pr.I!;
                    var st = pr.Start!;
                    if (pr.Kind == AirProbeKind.Second) return Bin(AirBinOp.Eq, i, Bin(AirBinOp.Add, st, AirConst.Of(1)));
                    MExpr Done() => Bin(AirBinOp.Sub, i, st);
                    return Bin(AirBinOp.And, Bin(AirBinOp.And,
                            Bin(AirBinOp.Ge, Done(), AirConst.Of(2)),
                            Bin(AirBinOp.Eq, Bin(AirBinOp.BitAnd, Done(), Bin(AirBinOp.Sub, Done(), AirConst.Of(1))), AirConst.Of(0))),
                        new MCall { Target = ParType, Name = "Offload", Args = [pr.T0!, Bin(AirBinOp.Sub, Done(), AirConst.Of(1)), Bin(AirBinOp.Sub, pr.Count!, Done())] });
                }
            case AirAlternateLookup al:
                return new MCall { Target = al.Dict, Name = "GetAlternateLookup<" + al.KeyTypeCs + ">" };
            case AirSpanSplit sp:
                return new MCall { Target = new MCall { Target = sp.Str, Name = "AsSpan" }, Name = "Split", Args = [sp.Sep] };
            case AirRefOrAddDefault a:
                return StaticCall("global::System.Runtime.InteropServices.CollectionsMarshal", "GetValueRefOrAddDefault", a.Dict, a.Key, new MArg { Prefix = "out ", E = new MCode("_") });
            case AirRefOrNullRef n:
                return StaticCall("global::System.Runtime.InteropServices.CollectionsMarshal", "GetValueRefOrNullRef", n.Dict, n.Key);
        }
        throw No(e.GetType().Name);
    }

    /// <summary>A parallel range or reduction as the call the C# backend prints (Par.Range / Par.Reduce&lt;T&gt;).</summary>
    MCall ParCall(AirPar p)
    {
        var call = new MCall { Target = ParType, Span = p.Span };
        call.Args.Add(p.Count);
        call.Args.Add(p.Kernel);
        if (p.Kind == AirParKind.Range) call.Name = "Range";
        else
        {
            call.Name = p.ResultCs is { Length: > 0 } r ? "Reduce<" + r + ">" : "Reduce";
            call.Args.Add(p.Combine ?? throw No("AirPar:no-combine"));
        }
        if (p.Grain is int g) call.Args.Add(AirConst.Of(g));
        return call;
    }

    /// <summary>A fork-join of 2 or 3 branches: Par.Fork&lt;A, B[, C]&gt;(Func&lt;A&gt;, ...).</summary>
    MCall ForkCall(AirForkJoin f)
    {
        var call = new MCall { Target = ParType, Name = "Fork", Span = f.Span };
        if (f.Type is AirType.Tuple tt && tt.Items.Count == f.Branches.Count)
            call.Name = "Fork<" + string.Join(", ", tt.Items.Select(i => i.ToCs())) + ">";
        call.Args.AddRange(f.Branches);
        return call;
    }
}
