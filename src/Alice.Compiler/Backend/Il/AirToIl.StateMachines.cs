using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// D1.8 consumer: the state machines of Air/Passes/StateMachines (async methods, iterators, async iterators and
/// async lambdas) as nested types of their owner. The pass builds an <see cref="AirTypeDecl"/> and a kickoff body;
/// this file declares the type (fields, methods, properties, MethodImpl rows for <c>Implements</c>), gives it a
/// reflection stand-in that implements its interfaces (so <c>Start&lt;TStateMachine&gt;</c> and
/// <c>AwaitUnsafeOnCompleted&lt;TAwaiter, TStateMachine&gt;</c> close over it by the usual inference) and emits every
/// body through <see cref="IlBody"/>; the jump nodes are labels, br/leave and switch (<see cref="IlBody.StmtExt"/>).
/// </summary>
public sealed partial class AirToIl
{
    int smOrdinal;
    readonly Dictionary<TypeSym, AliceT> smBySym = new();
    ModuleBuilder? smProxies;
    static int smProxyN;

    /// <summary>The descriptor the pass gets for a user method (an inferred-generic instance: its argument types).</summary>
    StateMachineMethod SmMethod(MethodSym m, Instance? inst)
    {
        // `<M>d__N` is looked up by name (MDecl/MNew text): one ordinal per module keeps names unique across owners
        var d = StateMachineMethod.For(m, smOrdinal++);
        if (inst != null)
        {
            int k = d.Params.Count - m.Params.Count;
            for (int i = 0; i < m.Params.Count; i++) d.Params[k + i] = new AirParamDecl(d.Params[k + i].Name, inst.Args[i].ToAir());
            var r = inst.Ret.ToAir();
            if (m.IsAsync && !m.IsAsyncIterator) d.Ret = inst.Ret.IsVoid || inst.Ret.IsUnknown ? SmTypesTask : new AirType.ClrRef("global::System.Threading.Tasks.Task", typeof(Task<>), [r]);
            else d.Ret = r;
            d.TypeParams.Clear();
        }
        return d;
    }

    static readonly AirType SmTypesTask = new AirType.ClrRef("global::System.Threading.Tasks.Task", typeof(Task), []);

    /// <summary>H3a: the CLR return type of an async method or iterator (what Backend/Emitter.cs declares, except the
    /// `Task` of an async def ending in a non-generic await; see StateMachineMethod.For); null for other methods.</summary>
    internal Type? SmReturn(MethodSym m, Instance? inst)
    {
        if (!m.IsAsync && !m.IsIterator) return null;
        var d = StateMachineMethod.For(m, 0);
        if (inst != null)
        {
            var r = inst.Ret.ToAir();
            d.Ret = m.IsAsync && !m.IsAsyncIterator
                ? (inst.Ret.IsVoid || inst.Ret.IsUnknown ? SmTypesTask : new AirType.ClrRef("global::System.Threading.Tasks.Task", typeof(Task<>), [r]))
                : r;
        }
        return AirClr(d.Ret, null);
    }

    /// <summary>H3b: an async method or iterator body becomes its kickoff; the machine (and those of its async lambdas)
    /// is declared nested in the owner and emitted. Other bodies: their async lambdas get machines, the body stays.</summary>
    internal List<MStmt> SmLower(AMethod am, List<MStmt> body)
    {
        var m = am.Sym;
        var opts = new StateMachineOptions { TypeOf = SmTypeOf(am, body) };
        if (m == null || !m.IsAsync && !m.IsIterator)
        {
            if (!SmTree.All(body, lambdas: true).Any(x => x is MUnary { Op: AirUnOp.Await })) return body;
            var lr = StateMachines.AsyncLambdas(body, am.Name, am.Owner.Sym, opts, smOrdinal);
            if (lr.Unsupported.Count > 0) throw new IlUnsupportedException("StateMachines:" + Head(lr.Unsupported[0]), null, string.Join("; ", lr.Unsupported));
            smOrdinal += lr.Lambdas.Count;
            foreach (var t in lr.Lambdas) EmitMachine(t, am.Owner);
            return body;
        }
        var d = SmMethod(m, am.Inst);
        var r = StateMachines.Run(d, body, opts);
        if (r.Unsupported.Count > 0) throw new IlUnsupportedException("StateMachines:" + Head(r.Unsupported[0]), null, m.Name + ": " + string.Join("; ", r.Unsupported));
        if (r.Machine == null) return body;
        smOrdinal += r.Lambdas.Count;
        EmitMachine(r.Machine, am.Owner);
        foreach (var t in r.Lambdas) EmitMachine(t, am.Owner);
        return SmAdapt(r.Kickoff!);
    }

    /// <summary>The pass's fallback typing: the emitter's own inference over a scratch body of the user method. Locals
    /// the expression reads are declared in the scratch scope first, with their AIR type or the type of their
    /// declaration's initializer (the body's MDecls, typed the same way); null when the emitter cannot type it either.</summary>
    Func<MExpr, AirType?> SmTypeOf(AMethod am, List<MStmt> body)
    {
        IlBody? scratch = null;
        var decls = new Dictionary<string, MDecl>(StringComparer.Ordinal);
        foreach (var n in SmTree.All(body, lambdas: true))
            if (n is MDecl d) decls.TryAdd(d.Name, d);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        Type? Local(string name, AirType? at)
        {
            if (at != null && at != AirType.Unknown && at is not AirType.Prim { Kind: PrimKind.Dynamic }) return AirClr(at, null);
            if (!decls.TryGetValue(name, out var d) || !busy.Add(name)) return null;
            try
            {
                if (d.TypeCs is { } cs && cs != "var" && Types.TryParse(cs) is { } tt) return tt;
                return d.Init != null ? Infer(d.Init) : null;
            }
            finally { busy.Remove(name); }
        }
        Type? Infer(MExpr e)
        {
            scratch ??= new IlBody(this, am.Owner, am, new IlBuilder(true, Module), am.Ret, am.IsStatic, am.ParamNames.Select((n, i) => (n, am.Params[i])).ToList());
            // hoisted locals (`this.<x>5__N` of the machine being built) read as scratch locals of their recorded type
            e = SmTree.Clone(e);
            MExpr Hoisted(MExpr x)
            {
                if (x is MMember { Target: AirThis, Type: { } ht } hm && hm.Name.StartsWith('<'))
                    return new AirLocalRef("__sm_" + hm.Name.Replace('<', '_').Replace('>', '_'), ht);
                SmTree.MapExprs(x, Hoisted);
                return x;
            }
            e = Hoisted(e);
            foreach (var n in SmTree.All(e, lambdas: false))
                if (n is AirLocalRef lr && !declared.Contains(lr.Name) && Local(lr.Name, lr.Type) is { } lt && lt != typeof(void))
                {
                    scratch.DeclareScratch(lr.Name, lt);
                    declared.Add(lr.Name);
                }
            var t = scratch.Infer(e);
            return t == typeof(void) ? null : t;
        }
        return e =>
        {
            try { return Infer(e) is { } t ? AirOf(t) : null; }
            catch (Exception x) when (x is IlUnsupportedException or InvalidOperationException or ArgumentException or NotSupportedException or NullReferenceException or KeyNotFoundException or InvalidCastException or IndexOutOfRangeException)
            {
                return null;
            }
        };
    }

    /// <summary>A reflection type as AIR (proxies as their Alice types, generic arguments mapped).</summary>
    internal AirType AirOf(Type t)
    {
        if (Types.Alice(t) is { } a) return new AirType.AliceType(a.Sym, []);
        if (t.IsByRef) return new AirType.ByRef(AirOf(t.GetElementType()!), RefKind.Ref);
        if (t.IsArray) return new AirType.Array(AirOf(t.GetElementType()!), t.GetArrayRank());
        if (t.IsGenericType && Types.HasProxy(t))
        {
            var def = t.GetGenericTypeDefinition();
            string n = def.FullName!;
            return new AirType.ClrRef("global::" + n[..n.IndexOf('`')].Replace('+', '.'), def, [.. t.GetGenericArguments().Select(AirOf)]);
        }
        return SmTypes.Clr(t);
    }

    static string Head(string reason)
    {
        int i = reason.IndexOfAny([':', '(', '`']);
        return (i > 0 ? reason[..i] : reason).Trim();
    }

    /// <summary>Declares a machine nested in <paramref name="outer"/> and emits its bodies.</summary>
    void EmitMachine(AirTypeDecl decl, AliceT outer)
    {
        if (decl.TypeParams.Count > 0) throw new IlUnsupportedException("StateMachines:generic-machine", null, decl.Name);
        decl.Sym.Parent ??= outer.Sym;
        var a = new AliceT { Sym = decl.Sym, IsValueType = decl.Kind == AirTypeKind.Struct };
        var attrs = TypeAttributes.NestedPrivate | TypeAttributes.BeforeFieldInit | TypeAttributes.Sealed
            | (a.IsValueType ? TypeAttributes.SequentialLayout : TypeAttributes.Class);
        a.Def = outer.Def.DefineNestedType(decl.Name, attrs);
        a.Def.BaseType = a.IsValueType ? Module.CoreType("ValueType") : Module.CoreType("Object");
        var ifaces = decl.Interfaces.Select(i => AirClr(i, null)).ToList();
        foreach (var i in ifaces) a.Def.Interfaces.Add(Types.Il(i));
        a.Proxy = MachineProxy(decl.Name, a.IsValueType, ifaces);
        // not in Types.BySym: EmitBodies walks BySym for constructors without a declaration (SyntheticCtor)
        Types.ByProxy[a.Proxy] = a;
        smBySym[decl.Sym] = a;
        Types.ByCs[decl.Name] = a;
        Types.ByCs[decl.Self.ToCs()] = a;

        foreach (var f in decl.Fields)
        {
            var ft = AirClr(f.Type, null);
            var fa = (f.Vis == AirVis.Public ? FieldAttributes.Public : FieldAttributes.Private) | (f.Static ? FieldAttributes.Static : 0) | (f.ReadOnly ? FieldAttributes.InitOnly : 0);
            var fd = a.Def.DefineField(f.Name, Types.Il(ft), fa);
            a.Fields[f.Name] = new AField { Name = f.Name, Type = ft, IsStatic = f.Static, Owner = a, Field = fd };
        }
        var defs = new List<(AirMethodDecl D, AMethod M)>();
        foreach (var md in decl.Methods)
        {
            bool ctor = md.Is(AirMethodFlags.Ctor);
            bool st = md.Is(AirMethodFlags.Static);
            var ps = md.Params.Select(p => AirClr(p.Type, null)).ToArray();
            var ret = ctor ? typeof(void) : AirClr(md.Ret, null);
            var ma = Vis(md.Vis) | MethodAttributes.HideBySig
                | (st ? MethodAttributes.Static : 0)
                | (md.Is(AirMethodFlags.Virtual) || md.Is(AirMethodFlags.Override) || md.Is(AirMethodFlags.Abstract) ? MethodAttributes.Virtual : 0)
                | (md.Is(AirMethodFlags.Virtual) && !md.Is(AirMethodFlags.Override) ? MethodAttributes.NewSlot : 0)
                | (md.Is(AirMethodFlags.Final) ? MethodAttributes.Final : 0)
                | (md.Is(AirMethodFlags.Abstract) ? MethodAttributes.Abstract : 0)
                | (md.Is(AirMethodFlags.SpecialName) ? MethodAttributes.SpecialName : 0);
            var def = ctor
                ? a.Def.DefineConstructor(Vis(md.Vis) | MethodAttributes.HideBySig, [.. ps.Select(Types.Il)], isStatic: st)
                : a.Def.DefineMethod(md.Name, ma, Types.Il(ret), [.. ps.Select(Types.Il)]);
            for (int i = 0; i < md.Params.Count; i++) def.DefineParameter(i + 1, md.Params[i].Name);
            var am = new AMethod
            {
                Name = ctor ? ".ctor" : md.Name, Owner = a, Ret = ret, Params = ps, ParamNames = [.. md.Params.Select(p => p.Name)],
                IsStatic = st, IsCtor = ctor, IsVirtual = (ma & MethodAttributes.Virtual) != 0, Def = def,
            };
            if (ctor) a.Ctors.Add(am); else a.Methods.Add(am);
            if (md.Implements is { } impl)
            {
                var it = AirClr(impl.Interface, null);
                var im = InterfaceMethod(it, impl.Method, ps) ?? throw new IlUnsupportedException("StateMachines:implements", null, it.Name + "." + impl.Method);
                a.Def.MethodOverrides.Add((def, Types.Method(im)));
            }
            defs.Add((md, am));
        }
        foreach (var p in decl.Properties)
        {
            var get = p.Get != null ? defs.First(x => x.D == p.Get).M.Def : null;
            var set = p.Set != null ? defs.First(x => x.D == p.Set).M.Def : null;
            a.Def.DefineProperty(p.Name, Types.Il(AirClr(p.Type, null)), get, set);
        }
        foreach (var (md, am) in defs)
        {
            if (md.Body == null) continue;
            var body = SmAdapt(md.Body);
            Guard(a + "::" + am.Name, () =>
            {
                var b = new IlBody(this, a, am, Body(am.Def), am.Ret, am.IsStatic, am.ParamNames.Select((n, i) => (n, am.Params[i])).ToList());
                b.Body(body);
            });
        }
        foreach (var n in decl.Nested) EmitMachine(n, a);
    }

    static MethodAttributes Vis(AirVis v) => v switch
    {
        AirVis.Public => MethodAttributes.Public,
        AirVis.Protected => MethodAttributes.Family,
        AirVis.Internal => MethodAttributes.Assembly,
        _ => MethodAttributes.Private,
    };

    /// <summary>The interface method a MethodImpl row names (by name and parameter types).</summary>
    static MethodInfo? InterfaceMethod(Type iface, string name, Type[] ps)
    {
        var all = iface.GetMethods().Where(m => m.Name == name && m.GetParameters().Length == ps.Length).ToList();
        return all.FirstOrDefault(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(ps)) ?? all.FirstOrDefault();
    }

    /// <summary>A reflection stand-in that implements <paramref name="ifaces"/> (bodies throw; only typing uses it),
    /// so generic constraints such as `TStateMachine : IAsyncStateMachine` hold during overload resolution.</summary>
    Type MachineProxy(string name, bool valueType, List<Type> ifaces)
    {
        smProxies ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AliceIlMachines" + Interlocked.Increment(ref smProxyN)), AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule("machines");
        var tb = smProxies.DefineType("M" + smOrdinal + "_" + smProxies.GetTypes().Length + "_" + name.Replace('<', '_').Replace('>', '_'),
            TypeAttributes.Public | TypeAttributes.Sealed | (valueType ? TypeAttributes.SequentialLayout : TypeAttributes.Class),
            valueType ? typeof(ValueType) : typeof(object));
        var closure = new HashSet<Type>();
        foreach (var i in ifaces) { closure.Add(i); foreach (var j in i.GetInterfaces()) closure.Add(j); }
        int n = 0;
        foreach (var i in closure)
        {
            tb.AddInterfaceImplementation(i);
            foreach (var im in i.GetMethods())
            {
                if (im.IsStatic || !im.IsAbstract) continue;
                var ps = im.GetParameters().Select(p => p.ParameterType).ToArray();
                var mb = tb.DefineMethod("I" + n++ + "_" + im.Name, MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot, im.ReturnType, ps);
                var g = mb.GetILGenerator();
                g.Emit(OpCodes.Ldnull);
                g.Emit(OpCodes.Throw);
                tb.DefineMethodOverride(mb, im);
            }
        }
        return tb.CreateType();
    }

    /// <summary>An AIR type as the reflection type the emitter types with (Alice types: their proxies).</summary>
    internal Type AirClr(AirType t, Span? span)
    {
        if (t.Nullable)
        {
            var u = AirClr(t with { Nullable = false }, span);
            return u.IsValueType && !IlTypes.IsNullable(u) ? typeof(Nullable<>).MakeGenericType(u) : u;
        }
        switch (t)
        {
            case AirType.AliceType at when at.Args.Count == 0 && (Types.BySym.TryGetValue(at.Sym, out var a) || smBySym.TryGetValue(at.Sym, out a)): return a.Proxy;
            case AirType.Array ar: { var e = AirClr(ar.Elem, span); return ar.Rank == 1 ? e.MakeArrayType() : e.MakeArrayType(ar.Rank); }
            case AirType.ByRef br: return AirClr(br.Elem, span).MakeByRefType();
            case AirType.Pointer pt: return AirClr(pt.Elem, span).MakePointerType();
            case AirType.ClrRef { Clr: { } c } cr when cr.Args.Count > 0 && c.IsGenericType:
                {
                    var def = c.IsGenericTypeDefinition ? c : c.GetGenericTypeDefinition();
                    if (def.GetGenericArguments().Length == cr.Args.Count)
                        return def.MakeGenericType([.. cr.Args.Select(x => AirClr(x, span))]);
                    break;
                }
            case AirType.ClrRef { Clr: { ContainsGenericParameters: false } c }: return c;
            case AirType.Prim p when SmPrim(p.Kind) is { } pk: return pk;
        }
        return Types.Parse(t.ToCs(), span);
    }

    static Type? SmPrim(PrimKind k) => k switch
    {
        PrimKind.Void => typeof(void), PrimKind.Bool => typeof(bool), PrimKind.Char => typeof(char),
        PrimKind.SByte => typeof(sbyte), PrimKind.Byte => typeof(byte), PrimKind.Short => typeof(short),
        PrimKind.UShort => typeof(ushort), PrimKind.Int => typeof(int), PrimKind.UInt => typeof(uint),
        PrimKind.Long => typeof(long), PrimKind.ULong => typeof(ulong), PrimKind.Float => typeof(float),
        PrimKind.Double => typeof(double), PrimKind.Decimal => typeof(decimal), PrimKind.String => typeof(string),
        PrimKind.Object => typeof(object), _ => null,
    };

    /// <summary>Rewrites what the emitter spells differently: `base..ctor()` becomes <see cref="IlBaseCtor"/>.</summary>
    static List<MStmt> SmAdapt(List<MStmt> body)
    {
        var r = new List<MStmt>(body.Count);
        foreach (var s in body) r.Add(SmAdapt(s));
        return r;
    }

    static MStmt SmAdapt(MStmt s)
    {
        if (s is MExprStmt { E: MCall { Target: AirBase, Name: ".ctor" } bc }) return new IlBaseCtor { Args = bc.Args, Line = s.Line, Span = s.Span };
        if (s is MAssign { Target: AirThis, Op: "=", Value: AirDefault } th) return new IlInitThis { Line = th.Line, Span = th.Span };
        foreach (var node in SmTree.All(s))
            foreach (var l in SmTree.Lists(node))
                for (int i = 0; i < l.Count; i++)
                    if (l[i] is MExprStmt { E: MCall { Target: AirBase, Name: ".ctor" } c }) l[i] = new IlBaseCtor { Args = c.Args, Line = l[i].Line, Span = l[i].Span };
        return s;
    }
}

/// <summary>`this = default` in a struct constructor (`initobj` through the `this` byref).</summary>
internal sealed class IlInitThis : MStmt { }

/// <summary>`base..ctor(args)` of a synthesized constructor (object or a CLR/Alice base).</summary>
internal sealed class IlBaseCtor : MStmt
{
    public List<MExpr> Args = new();
}

internal sealed partial class IlBody
{
    Dictionary<string, IlLabel>? smLabels;

    /// <summary>A local of a typing-only body (<see cref="AirToIl.SmTypeOf"/>).</summary>
    internal void DeclareScratch(string name, Type t) => Declare(name, t);

    IlLabel SmLabel(string name)
    {
        smLabels ??= new(StringComparer.Ordinal);
        if (!smLabels.TryGetValue(name, out var l)) smLabels[name] = l = il.DefineLabel();
        return l;
    }

    /// <summary>H2: statements the core switch does not know (jump nodes of the state machines, base constructor calls).</summary>
    internal bool StmtExt(MStmt s)
    {
        switch (s)
        {
            case AirLabel lb:
                il.MarkLabel(SmLabel(lb.Name));
                return true;
            case AirGoto g:
                if (g.Leave) il.EmitLeave(SmLabel(g.Label)); else il.EmitBranch(SmLabel(g.Label));
                return true;
            case AirGotoTable gt:
                {
                    Emit(gt.Value, typeof(int));
                    if (gt.Min != 0) { il.EmitLoadConst(gt.Min); il.EmitBinary(IlBinOp.Sub); }
                    var fall = il.DefineLabel();
                    il.EmitSwitch([.. gt.Labels.Select(n => n == null ? fall : SmLabel(n))]);
                    il.MarkLabel(fall);
                    return true;
                }
            case IlBaseCtor bc:
                BaseCtor(bc);
                return true;
            case IlInitThis when owner.IsValueType && !isStatic:
                il.EmitLoadArg(0);
                il.Emit(ILOpCode.Initobj, T.Il(owner.Proxy));
                return true;
        }
        return false;
    }

    void BaseCtor(IlBaseCtor bc)
    {
        il.EmitLoadArg(0);
        var infos = ArgInfos(bc.Args);
        if (owner.Base != null)
        {
            var cand = T.Pick(owner.Base.Ctors.Select(IlTypes.FromAlice), infos) ?? throw No("Ctor:base", owner.Base.ToString());
            PushArgs(cand, bc.Args, 0);
            il.EmitCall(cand.Alice!.Def);
            return;
        }
        var bt = owner.ClrBase ?? typeof(object);
        var c2 = T.Pick(bt.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(x => !x.IsPrivate).Select(x => IlTypes.FromClr(x)), infos)
            ?? throw No("Ctor:base", bt.Name);
        PushArgs(c2, bc.Args, 0);
        il.EmitCall(T.Method(c2.Clr!));
    }
}
