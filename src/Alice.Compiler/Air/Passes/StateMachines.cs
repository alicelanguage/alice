using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Air.Passes;

/// <summary>What a method body becomes (backend plan D1.8).</summary>
public enum StateMachineKind { None, Async, Iterator, AsyncIterator }

/// <summary>The user method a state machine is built for: its signature as the IL backend declares it.</summary>
public sealed class StateMachineMethod
{
    /// <summary>The CLR name of the user method (the kickoff method keeps it).</summary>
    public string Name = "";
    /// <summary>N in `&lt;Name&gt;d__N`: unique per owner type (C# uses the method's ordinal).</summary>
    public int Ordinal;
    public bool Static = true;
    /// <summary>The owner as `this` has it (instance methods: stored in `&lt;&gt;4__this`).</summary>
    public AirType? This;
    /// <summary>The Alice owner: resolves bare field reads and implicit-`this` calls of the body.</summary>
    public TypeSym? Owner;
    public List<AirParamDecl> Params = new();
    /// <summary>The method's own type parameters: the state machine is generic over them.</summary>
    public List<string> TypeParams = new();
    /// <summary>The CLR return type of the kickoff method: Task, Task&lt;T&gt;, ValueTask(&lt;T&gt;), IEnumerable(&lt;T&gt;),
    /// IEnumerator(&lt;T&gt;), IAsyncEnumerable&lt;T&gt;, IAsyncEnumerator&lt;T&gt;. `void` with <see cref="Async"/> is rejected.</summary>
    public AirType Ret = SmTypes.Void;
    public bool Async;
    /// <summary>The body yields (`yield x` is `yield return x`).</summary>
    public bool Iterator;
    /// <summary>An async lambda: `void` (an Action) is allowed and uses AsyncVoidMethodBuilder, as C# does.</summary>
    public bool Lambda;

    /// <summary>The descriptor of a bound Alice method; its return type is the one Backend/Emitter.cs declares
    /// (an async def returns Task / Task&lt;T&gt;, an async iterator its stream).</summary>
    public static StateMachineMethod For(MethodSym m, int ordinal)
    {
        var owner = m.Owner;
        bool ext = owner?.Kind == "extension";
        var d = new StateMachineMethod
        {
            Name = m.CsName,
            Ordinal = ordinal,
            Static = m.IsStatic || ext,
            Owner = owner,
            Async = m.IsAsync,
            Iterator = m.IsIterator,
            TypeParams = [.. m.TypeParams],
        };
        if (!d.Static && owner != null) d.This = owner.Type.ToAir();
        if (ext && owner!.ExtTarget is { } et) d.Params.Add(new AirParamDecl("self", et.ToAir()));
        foreach (var p in m.Params) d.Params.Add(new AirParamDecl(p.CsName, p.Type.ToAir()));
        var ret = m.Ret.ToAir();
        if (m.IsAsync && !m.IsAsyncIterator)
            ret = m.Ret.IsVoid || m.Ret.IsUnknown || EndsWithVoidAwait(m) ? SmTypes.Task : SmTypes.Gen(typeof(Task<>), ret);
        d.Ret = ret;
        return d;
    }

    /// <summary>
    /// `async def f / ... / await t / end` where t is a non-generic awaitable (Task, ValueTask, Task.Yield): the
    /// value of the def is void, so it returns Task. The binder types that last expression `dynamic` and the C#
    /// emitter declares Task&lt;dynamic&gt; (reports/book-compiler-issues.md item 17); the IL backend does not copy it.
    /// </summary>
    static bool EndsWithVoidAwait(MethodSym m) =>
        !m.RetDeclared && m.Decl?.Body is [.., Syntax.ExprStmt { Expr: Syntax.AwaitExpr aw }]
        && aw.E.Type.Clr is { } c && !c.IsGenericType
        && c.GetMethod("GetAwaiter", Type.EmptyTypes)?.ReturnType.GetMethod("GetResult", Type.EmptyTypes)?.ReturnType == typeof(void);
}

public sealed class StateMachineOptions
{
    /// <summary>Debug build: async state machines are classes (C# Debug) and every user local is hoisted.</summary>
    public bool Debug;
    /// <summary>The static type of an expression whose <see cref="MExpr.Type"/> is null (the IL emitter's inference).
    /// Used for awaited operands, spilled operands and `var` locals that live across a suspension.</summary>
    public Func<MExpr, AirType?>? TypeOf;
}

public sealed class StateMachineResult
{
    public StateMachineKind Kind;
    /// <summary>The state-machine type, nested in the user method's owner; null when <see cref="Kind"/> is None or
    /// <see cref="Unsupported"/> is not empty.</summary>
    public AirTypeDecl? Machine;
    /// <summary>The new body of the user method (creates, initializes and starts the machine). Null on failure.</summary>
    public List<MStmt>? Kickoff;
    /// <summary>Why the method could not be rewritten; the original body is left untouched.</summary>
    public readonly List<string> Unsupported = new();
    /// <summary>State machines of the async lambdas in the body (their MLambda bodies are now kickoffs).</summary>
    public readonly List<AirTypeDecl> Lambdas = new();
}

/// <summary>
/// The IL backend's replacement for Roslyn's async and iterator rewriters (ADR 0032/0033, backend plan D1.8). An async
/// method, an iterator (`yield`) or an async iterator becomes a nested state-machine type in the
/// <see cref="AirTypeDecl"/> model plus a kickoff body, built only from nodes the IL emitter knows and the three jump
/// nodes of Mir/MExpr.StateMachines.cs. Shapes follow C#: `&lt;&gt;1__state` (-1 running, -2 done, 0.. resume points),
/// the method builder in `&lt;&gt;t__builder`, locals that live across a suspension hoisted to fields, one awaiter
/// field per awaiter type, `AwaitUnsafeOnCompleted` with an early return, try/finally across awaits guarded by the
/// state, and iterator finally blocks moved to `&lt;&gt;m__Finally` methods that `Dispose` runs. Runs last in the IL
/// lowering (after closure conversion and Air/Passes/IlLowering); never on the C# path.
/// </summary>
public static class StateMachines
{
    public static StateMachineKind KindOf(StateMachineMethod m)
    {
        if (m.Async) return SmTypes.IsAsyncStream(m.Ret) ? StateMachineKind.AsyncIterator : StateMachineKind.Async;
        return m.Iterator ? StateMachineKind.Iterator : StateMachineKind.None;
    }

    /// <summary>
    /// Builds the state machine of <paramref name="m"/>. <paramref name="body"/> is never modified: on success the
    /// machine's MoveNext holds a rewritten copy and <see cref="StateMachineResult.Kickoff"/> replaces the body.
    /// </summary>
    public static StateMachineResult Run(StateMachineMethod m, List<MStmt> body, StateMachineOptions? options = null)
    {
        var res = new StateMachineResult { Kind = KindOf(m) };
        if (res.Kind == StateMachineKind.None) return res;
        var rw = new StateMachineRewriter(m, options ?? new StateMachineOptions(), res.Kind);
        try
        {
            var work = SmTree.CloneList(body);
            var lambdas = AsyncLambdasInPlace(work, m.Name, m.Owner, options, m.Ordinal);
            rw.Unsupported.AddRange(lambdas.Unsupported);
            res.Lambdas.AddRange(lambdas.Lambdas);
            var (decl, kickoff) = rw.Build(work);
            if (rw.Unsupported.Count == 0)
            {
                res.Machine = decl;
                res.Kickoff = kickoff;
            }
        }
        catch (SmUnsupportedException ex)
        {
            rw.Unsupported.Add(ex.Message);
        }
        res.Unsupported.AddRange(rw.Unsupported.Distinct());
        if (res.Unsupported.Count > 0) res.Lambdas.Clear();
        return res;
    }

    /// <summary>
    /// The async lambdas of any body (an async method's included): each lambda whose own body awaits gets a state
    /// machine and its body becomes the kickoff. Run on every body the IL backend emits; <paramref name="body"/> is
    /// rewritten only when <see cref="StateMachineResult.Unsupported"/> is empty. A lambda that captures outer locals
    /// must have been closure-converted first (its closure method is then an ordinary method for <see cref="Run"/>).
    /// </summary>
    public static StateMachineResult AsyncLambdas(List<MStmt> body, string method, TypeSym? owner, StateMachineOptions? options = null, int firstOrdinal = 0)
    {
        var work = SmTree.CloneList(body);
        var res = AsyncLambdasInPlace(work, method, owner, options, firstOrdinal);
        if (res.Unsupported.Count == 0 && res.Lambdas.Count > 0)
        {
            body.Clear();
            body.AddRange(work);
        }
        return res;
    }

    static StateMachineResult AsyncLambdasInPlace(List<MStmt> body, string method, TypeSym? owner, StateMachineOptions? options, int ordinal)
    {
        var res = new StateMachineResult { Kind = StateMachineKind.Async };
        int n = 0;
        // innermost first: an async lambda inside an async lambda is a kickoff before its parent is rewritten
        var lams = SmTree.All(body, lambdas: true).OfType<MLambda>().Reverse().ToList();
        foreach (var lam in lams)
        {
            var own = lam.Body != null ? SmTree.All(lam.Body) : lam.ExprBody != null ? SmTree.All(lam.ExprBody) : [];
            if (!own.Any(x => x is MUnary { Op: AirUnOp.Await })) continue;
            string name = $"<{method}>b__{ordinal}_{n++}";
            if (!lam.Static)
            {
                res.Unsupported.Add($"async lambda in `{method}` captures outer locals: closure conversion (D1.5) makes it a method, then StateMachines.Run builds its machine");
                continue;
            }
            var sm = new StateMachineMethod { Name = name, Ordinal = 0, Async = true, Lambda = true, Owner = owner };
            var (ps, ret) = LambdaSignature(lam);
            if (ps == null) { res.Unsupported.Add($"async lambda in `{method}`: parameter types unknown"); continue; }
            sm.Params = ps;
            var lbody = lam.Body ?? [lam.ExprBody is MUnary { Op: AirUnOp.Await } ? new MExprStmt { E = lam.ExprBody } : new MReturn { Value = lam.ExprBody }];
            sm.Ret = ret ?? InferLambdaRet(lbody, options);
            var r = Run(sm, lbody, options);
            if (r.Unsupported.Count > 0) { res.Unsupported.AddRange(r.Unsupported); continue; }
            lam.Body = r.Kickoff;
            lam.ExprBody = null;
            res.Lambdas.Add(r.Machine!);
            res.Lambdas.AddRange(r.Lambdas);
        }
        return res;
    }

    /// <summary>Parameters as written (`int x`) or from the delegate type, and the delegate's return type.</summary>
    static (List<AirParamDecl>? Params, AirType? Ret) LambdaSignature(MLambda lam)
    {
        IReadOnlyList<AirType> dargs = [];
        AirType? ret = null;
        if (SmTypes.Generic(lam.Type) is { } g)
        {
            string dn = g.Def.Name;
            if (dn.StartsWith("Func`")) { dargs = g.Args.Take(g.Args.Count - 1).ToList(); ret = g.Args[^1]; }
            else if (dn.StartsWith("Action`")) { dargs = g.Args; ret = SmTypes.Void; }
        }
        else if (lam.Type?.ToCs() == "global::System.Action") ret = SmTypes.Void;
        var ps = new List<AirParamDecl>();
        for (int i = 0; i < lam.Params.Count; i++)
        {
            var parts = lam.Params[i].Trim().Split(' ');
            if (parts.Length >= 2) ps.Add(new AirParamDecl(parts[^1], StateMachineRewriter.FromCs(string.Join(" ", parts[..^1]), null)!));
            else if (i < dargs.Count) ps.Add(new AirParamDecl(parts[0], dargs[i]));
            else return (null, null);
        }
        return (ps, ret);
    }

    /// <summary>No delegate type: Task when nothing returns a value, else Task of the returned value's type.</summary>
    static AirType InferLambdaRet(List<MStmt> body, StateMachineOptions? options)
    {
        var rv = SmTree.All(body).OfType<MReturn>().FirstOrDefault(r => r.Value != null && r.Value is not MUnary { Op: AirUnOp.Await, Type: AirType.Prim { Kind: PrimKind.Void } });
        if (rv == null) return SmTypes.Task;
        var t = rv.Value!.Type ?? options?.TypeOf?.Invoke(rv.Value);
        if (rv.Value is MUnary { Op: AirUnOp.Await } aw && (aw.E.Type ?? options?.TypeOf?.Invoke(aw.E)) is { } at) t = SmTypes.Await(at)?.Result;
        return t == null || t is AirType.Prim { Kind: PrimKind.Void } ? SmTypes.Task : SmTypes.Gen(typeof(Task<>), t);
    }
}

/// <summary>
/// Stable text of a built state machine for tests and `--air` output: <see cref="AirDeclDump"/> of the machine (and of
/// its async lambdas' machines) then the kickoff body; MirDump spells the jump nodes `label L`, `goto L` / `leave L`
/// and `switch value min: L0, _, L2` (Mir/MirDump.StateMachines.cs).
/// </summary>
public static class StateMachineDump
{
    public static string Of(StateMachineResult r)
    {
        if (r.Machine is not { } m || r.Kickoff is not { } k) return "unsupported: " + string.Join("; ", r.Unsupported) + "\n";
        var sb = new System.Text.StringBuilder(AirDeclDump.Of(m));
        foreach (var l in r.Lambdas) sb.Append(AirDeclDump.Of(l));
        sb.Append("kickoff\n");
        foreach (var line in MirDump.Of(k).Split('\n'))
            if (line.Length > 0) sb.Append("  ").Append(line).Append('\n');
        return sb.ToString();
    }
}

sealed class SmUnsupportedException(string why) : Exception(why);

// ---------------------------------------------------------------- internal statements (never leave the pass)

/// <summary>`Target = await Awaitable` (Target null: the result is discarded) with every operand already spilled.</summary>
sealed class SAwait : MStmt
{
    public MExpr Awaitable = null!;
    public MExpr? Target;
}

/// <summary>`yield return Value`.</summary>
sealed class SYield : MStmt
{
    public MExpr Value = null!;
}

/// <summary>`yield break` (and a `return` in an iterator).</summary>
sealed class SYieldBreak : MStmt { }

// ---------------------------------------------------------------- types

/// <summary>The CLR types state machines are made of, as <see cref="AirType"/>.</summary>
static class SmTypes
{
    public static readonly AirType Void = new AirType.Prim(PrimKind.Void);
    public static readonly AirType Int = new AirType.Prim(PrimKind.Int);
    public static readonly AirType Short = new AirType.Prim(PrimKind.Short);
    public static readonly AirType Bool = new AirType.Prim(PrimKind.Bool);
    public static readonly AirType Obj = new AirType.Prim(PrimKind.Object);
    public static readonly AirType Task = Clr(typeof(Task));
    public static readonly AirType Exception = Clr(typeof(Exception));

    public static AirType Clr(Type t)
    {
        if (t == typeof(void)) return Void;
        if (t.IsGenericType && !t.IsGenericTypeDefinition && Map(t, _ => null) is { } g) return g;
        return ClrTypes.FromClr(t).ToAir();
    }

    /// <summary>A generic CLR type over AIR arguments; the reflection type is closed when every argument has one.</summary>
    public static AirType Gen(Type def, params AirType[] args)
    {
        var closed = args.Select(ClrOf).ToArray();
        Type clr = closed.All(c => c != null) ? def.MakeGenericType(closed!) : def;
        string name = def.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return new AirType.ClrRef("global::" + def.Namespace + "." + name, clr, args);
    }

    /// <summary>The closed reflection type of an AIR type, null when it involves Alice types or type parameters.</summary>
    public static Type? ClrOf(AirType? t)
    {
        if (t == null) return null;
        if (t.Nullable)
        {
            var u = ClrOf(t with { Nullable = false });
            return u == null ? null : u.IsValueType ? typeof(Nullable<>).MakeGenericType(u) : u;
        }
        return t switch
        {
            AirType.Prim p => p.Kind switch
            {
                PrimKind.Void => typeof(void), PrimKind.Bool => typeof(bool), PrimKind.Char => typeof(char),
                PrimKind.SByte => typeof(sbyte), PrimKind.Byte => typeof(byte), PrimKind.Short => typeof(short),
                PrimKind.UShort => typeof(ushort), PrimKind.Int => typeof(int), PrimKind.UInt => typeof(uint),
                PrimKind.Long => typeof(long), PrimKind.ULong => typeof(ulong), PrimKind.Float => typeof(float),
                PrimKind.Double => typeof(double), PrimKind.Decimal => typeof(decimal), PrimKind.String => typeof(string),
                PrimKind.Object => typeof(object), _ => null,
            },
            AirType.ClrRef { Clr: { } c } when !c.ContainsGenericParameters => c,
            AirType.ClrRef { Clr: { IsGenericTypeDefinition: true } d, Args.Count: > 0 } r when r.Args.Select(ClrOf).ToArray() is var a && a.All(x => x != null) && a.Length == d.GetGenericArguments().Length => d.MakeGenericType(a!),
            AirType.Array a when ClrOf(a.Elem) is { } e => a.Rank == 1 ? e.MakeArrayType() : e.MakeArrayType(a.Rank),
            _ => null,
        };
    }

    /// <summary>The generic definition and arguments of a constructed CLR type (closed or over Alice types).</summary>
    public static (Type Def, IReadOnlyList<AirType> Args)? Generic(AirType? t)
    {
        if (t is not AirType.ClrRef { Clr: { IsGenericType: true } c } r || t.Nullable) return null;
        var def = c.IsGenericTypeDefinition ? c : c.GetGenericTypeDefinition();
        if (r.Args.Count == def.GetGenericArguments().Length) return (def, r.Args);
        if (!c.IsGenericTypeDefinition && !c.ContainsGenericParameters) return (def, c.GetGenericArguments().Select(Clr).ToList());
        return null;
    }

    /// <summary>A reflection type seen through generic arguments: <paramref name="param"/> maps a generic parameter.</summary>
    public static AirType? Map(Type t, Func<Type, AirType?> param)
    {
        if (t.IsGenericParameter) return param(t);
        if (t == typeof(void)) return Void;
        if (t.IsByRef) return Map(t.GetElementType()!, param);
        if (t.IsArray) return Map(t.GetElementType()!, param) is { } el ? new AirType.Array(el, t.GetArrayRank()) : null;
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def.IsNested && def.DeclaringType!.IsGenericType) return t.ContainsGenericParameters ? null : ClrTypes.FromClr(t).ToAir();
            var args = new List<AirType>();
            foreach (var a in t.GetGenericArguments())
                if (Map(a, param) is { } aa) args.Add(aa);
                else return null;
            if (def == typeof(Nullable<>)) return args[0] with { Nullable = true };
            return Gen(def, [.. args]);
        }
        return t.ContainsGenericParameters ? null : ClrTypes.FromClr(t).ToAir();
    }

    public static bool IsDef(AirType? t, Type def) => Generic(t) is { } g && g.Def == def;

    public static bool IsAsyncStream(AirType t) => IsDef(t, typeof(IAsyncEnumerable<>)) || IsDef(t, typeof(IAsyncEnumerator<>));

    public static bool? IsValueType(AirType? t) => t switch
    {
        null => null,
        { Nullable: true } => IsValueType(t with { Nullable = false }) == true,
        AirType.Prim p => p.Kind is not (PrimKind.String or PrimKind.Object or PrimKind.Dynamic or PrimKind.Null or PrimKind.Void),
        AirType.ClrRef { Clr: { } c } => c.IsValueType,
        AirType.AliceType a => a.Sym.Kind is "struct" or "enum",
        AirType.Tuple => true,
        AirType.Array => false,
        _ => null,
    };

    /// <summary>The awaiter type, the result type (void: <see cref="Void"/>) and whether the awaiter is an
    /// ICriticalNotifyCompletion, for an awaitable of type <paramref name="t"/>; null when unknown.</summary>
    public static (AirType Awaiter, AirType Result, bool Critical)? Await(AirType? t)
    {
        if (t == null) return null;
        if (ClrOf(t) is { } clr)
        {
            var ga = clr.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (ga == null) return null;
            var aw = ga.ReturnType;
            var gr = aw.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (gr == null) return null;
            return (Clr(aw), Clr(gr.ReturnType), typeof(ICriticalNotifyCompletion).IsAssignableFrom(aw));
        }
        if (Generic(t) is { } g)
        {
            var ga = g.Def.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (ga == null) return null;
            var awOpen = ga.ReturnType;
            var awAir = Map(awOpen, p => p.GenericParameterPosition < g.Args.Count ? g.Args[p.GenericParameterPosition] : null);
            if (awAir == null) return null;
            var awDef = awOpen.IsGenericType ? awOpen.GetGenericTypeDefinition() : awOpen;
            var gr = awDef.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (gr == null) return null;
            var awArgs = Generic(awAir)?.Args ?? [];
            var res = Map(gr.ReturnType, p => p.GenericParameterPosition < awArgs.Count ? awArgs[p.GenericParameterPosition] : null);
            if (res == null) return null;
            return (awAir, res, typeof(ICriticalNotifyCompletion).IsAssignableFrom(awDef));
        }
        return null;
    }

    /// <summary>The return type of the public method <paramref name="name"/> with <paramref name="argc"/> arguments
    /// on <paramref name="recv"/> (static when <paramref name="isStatic"/>), through the receiver's type arguments;
    /// a generic method's type parameters are inferred from arguments typed exactly as one of them.</summary>
    public static AirType? MethodRet(AirType recv, string name, IReadOnlyList<AirType?> args, bool isStatic)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        Type? owner = ClrOf(recv);
        IReadOnlyList<AirType> targs = [];
        if (owner == null && Generic(recv) is { } g) { owner = g.Def; targs = g.Args; }
        if (owner == null && recv is AirType.ClrRef { Clr: { } c }) owner = c;
        if (owner == null) return null;
        var cands = owner.GetMethods(flags).Where(mi => mi.Name == name && mi.GetParameters().Length == args.Count).ToList();
        if (owner.IsInterface && cands.Count == 0)
            cands = owner.GetInterfaces().SelectMany(i => i.GetMethods(flags)).Where(mi => mi.Name == name && mi.GetParameters().Length == args.Count).ToList();
        foreach (var mi in cands)
        {
            var ps = mi.GetParameters();
            var margs = new Dictionary<int, AirType>();
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt.IsGenericParameter && pt.DeclaringMethod != null)
                {
                    if (args[i] is { } at) margs[pt.GenericParameterPosition] = at;
                }
                else if (ClrOf(args[i]) is { } ac && !pt.ContainsGenericParameters && !pt.IsAssignableFrom(ac) && !(pt.IsByRef && pt.GetElementType()!.IsAssignableFrom(ac)))
                    ok = false;
            }
            if (!ok && cands.Count > 1) continue;
            var r = Map(mi.ReturnType, p => p.DeclaringMethod != null ? margs.GetValueOrDefault(p.GenericParameterPosition) : p.GenericParameterPosition < targs.Count ? targs[p.GenericParameterPosition] : null);
            if (r != null) return r;
        }
        return null;
    }

    public static AirType? PropertyType(AirType recv, string name, bool isStatic)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        Type? owner = ClrOf(recv);
        IReadOnlyList<AirType> targs = [];
        if (owner == null && Generic(recv) is { } g) { owner = g.Def; targs = g.Args; }
        if (owner == null) return null;
        var pi = owner.GetProperty(name, flags) ?? (owner.IsInterface ? owner.GetInterfaces().Select(i => i.GetProperty(name, flags)).FirstOrDefault(p => p != null) : null);
        if (pi != null) return Map(pi.PropertyType, p => p.GenericParameterPosition < targs.Count ? targs[p.GenericParameterPosition] : null);
        var fi = owner.GetField(name, flags);
        return fi != null ? Map(fi.FieldType, p => p.GenericParameterPosition < targs.Count ? targs[p.GenericParameterPosition] : null) : null;
    }
}

// ---------------------------------------------------------------- generic tree access

/// <summary>Reflection-based access to the children of any MIR node, so nodes added by other passes (AirSeq,
/// AirSwitch, closure nodes...) are traversed and cloned without this pass knowing them.</summary>
static class SmTree
{
    enum SlotKind { Expr, ExprList, Stmt, StmtList, CatchList, StmtLists }

    sealed record Slot(FieldInfo Field, SlotKind Kind);

    static readonly ConcurrentDictionary<Type, Slot[]> slots = new();
    static readonly MethodInfo memberwise = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!;

    static Slot[] Slots(Type t) => slots.GetOrAdd(t, static t =>
    {
        if (typeof(MCode).IsAssignableFrom(t)) return [];
        var list = new List<Slot>();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.IsInitOnly) continue;
            var ft = f.FieldType;
            if (typeof(MExpr).IsAssignableFrom(ft)) list.Add(new(f, SlotKind.Expr));
            else if (typeof(MStmt).IsAssignableFrom(ft)) list.Add(new(f, SlotKind.Stmt));
            else if (ft == typeof(List<MExpr>)) list.Add(new(f, SlotKind.ExprList));
            else if (ft == typeof(List<MStmt>)) list.Add(new(f, SlotKind.StmtList));
            else if (ft == typeof(List<MCatch>)) list.Add(new(f, SlotKind.CatchList));
            else if (ft == typeof(List<List<MStmt>>)) list.Add(new(f, SlotKind.StmtLists));
        }
        return [.. list];
    });

    /// <summary>The expressions a node holds directly (not those inside its statement lists).</summary>
    public static IEnumerable<MExpr> Exprs(object node)
    {
        foreach (var s in Slots(node.GetType()))
            switch (s.Kind)
            {
                case SlotKind.Expr when s.Field.GetValue(node) is MExpr e: yield return e; break;
                case SlotKind.ExprList when s.Field.GetValue(node) is List<MExpr> xs:
                    foreach (var x in xs) yield return x;
                    break;
            }
    }

    /// <summary>The statement lists a node holds directly (catch bodies included; a lone statement child as a list).</summary>
    public static IEnumerable<List<MStmt>> Lists(object node)
    {
        foreach (var s in Slots(node.GetType()))
            switch (s.Kind)
            {
                case SlotKind.StmtList when s.Field.GetValue(node) is List<MStmt> l: yield return l; break;
                case SlotKind.CatchList when s.Field.GetValue(node) is List<MCatch> cs:
                    foreach (var c in cs) yield return c.Body;
                    break;
                case SlotKind.StmtLists when s.Field.GetValue(node) is List<List<MStmt>> ls:
                    foreach (var l in ls) yield return l;
                    break;
                case SlotKind.Stmt when s.Field.GetValue(node) is MStmt one: yield return [one]; break;
            }
    }

    /// <summary>Replaces each expression a node holds directly by <paramref name="f"/> of it.</summary>
    public static void MapExprs(object node, Func<MExpr, MExpr> f)
    {
        foreach (var s in Slots(node.GetType()))
            switch (s.Kind)
            {
                case SlotKind.Expr when s.Field.GetValue(node) is MExpr e: s.Field.SetValue(node, f(e)); break;
                case SlotKind.ExprList when s.Field.GetValue(node) is List<MExpr> xs:
                    for (int i = 0; i < xs.Count; i++) xs[i] = f(xs[i]);
                    break;
            }
    }

    /// <summary>Every node below <paramref name="node"/> (pre-order), lambda bodies excluded unless <paramref name="lambdas"/>.</summary>
    public static IEnumerable<object> All(object node, bool lambdas = false)
    {
        var stack = new Stack<object>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n is MLambda && !lambdas && !ReferenceEquals(n, node)) continue;
            var kids = new List<object>();
            kids.AddRange(Exprs(n));
            foreach (var l in Lists(n)) kids.AddRange(l);
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }

    public static IEnumerable<object> All(List<MStmt> list, bool lambdas = false) => list.SelectMany(s => All(s, lambdas));

    public static T Clone<T>(T node) where T : class
    {
        if (node is MCode) return node;   // leaves are immutable in practice; shared
        var c = (T)memberwise.Invoke(node, null)!;
        foreach (var s in Slots(node.GetType()))
        {
            var v = s.Field.GetValue(node);
            if (v == null) continue;
            s.Field.SetValue(c, s.Kind switch
            {
                SlotKind.Expr => Clone((MExpr)v),
                SlotKind.Stmt => Clone((MStmt)v),
                SlotKind.ExprList => ((List<MExpr>)v).Select(Clone).ToList(),
                SlotKind.StmtList => CloneList((List<MStmt>)v),
                SlotKind.CatchList => ((List<MCatch>)v).Select(k => new MCatch { TypeCs = k.TypeCs, Name = k.Name, Body = CloneList(k.Body) }).ToList(),
                SlotKind.StmtLists => ((List<List<MStmt>>)v).Select(CloneList).ToList(),
                _ => v,
            });
        }
        return c;
    }

    public static List<MStmt> CloneList(List<MStmt> body) => body.Select(Clone).ToList();
}
