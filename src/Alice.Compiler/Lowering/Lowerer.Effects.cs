using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Declared effects: `pure def` and `safe def` state what inference would otherwise guess, and a failed proof is a
/// compile error instead of a silently slower or less trusted method.
/// <list type="bullet">
/// <item>`pure`: the interprocedural purity gate that decides forks and parallel loops (AL0210).</item>
/// <item>`safe`: secure execution. The method and every Alice method it calls run only Alice code: no ambient authority
/// (.NET statics other than `Math`/`Alice.Runtime`, I/O, network, process, reflection or interop types), no dynamic dispatch, no `@[Native]`, no `unchecked` (AL0212).</item>
/// </list>
/// </summary>
public sealed partial class Lowerer
{
    readonly Dictionary<MethodSym, string?> unsafeWhy = new();

    /// <summary>A literal constant of a .NET type (`Int32::MAX`, `Float64::INFINITY`, `Math::PI`): reads no state.</summary>
    static bool LiteralConst(ConstExpr ce) =>
        ce is { Res: "path", Sym: ConstPath { OwnerText: null, Members: [(var n, false)] } cp }
        && cp.Owner.Clr?.GetField(n, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) is { IsLiteral: true };

    /// <summary>A Prelude extension call (`s.upcase`) is pure when it is on the numeric list or every Prelude overload of
    /// that name for the receiver is marked [Pure] by the runtime.</summary>
    static bool PureExtCall(CallExpr x)
    {
        string name = Binder.Mangle(x.Name);
        if (PureExt.Contains(name)) return true;
        if (x.Recv?.Type.Clr is not { } rt) return false;
        lock (pureExts)
        {
            if (pureExts.TryGetValue((name, rt), out bool known)) return known;
            var ms = typeof(Alice.Runtime.Prelude).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == name && m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                    && m.GetParameters()[0].ParameterType is { IsGenericParameter: false } p0 && p0.IsAssignableFrom(rt)).ToList();
            return pureExts[(name, rt)] = ms.Count > 0 && ms.All(m => m.CustomAttributes.Any(a => a.AttributeType.FullName == "Alice.Runtime.PureAttribute"));
        }
    }

    static readonly Dictionary<(string, Type), bool> pureExts = new();

    void CheckDeclaredEffects(int line)
    {
        if (Method?.Decl is not { } d) return;
        string name = Method.Name;
        if (d.IsPure && !MethodPure(Method))
            throw new CompileException($"{File}:{line}:1: `{name}` is not pure: it writes state it did not allocate, does I/O, or calls code not proven pure");
        for (int k = 0; k < Method.Params.Count; k++)
            if (Method.Params[k].Borrow && !Borrows(Method, k, new()))
                throw new CompileException($"{File}:{line}:1: borrowed value `{Method.Params[k].Name}` escapes `{name}`: a borrow may only be indexed, sized, iterated or lent to another borrowing parameter; it must not be assigned, stored, returned, captured by a block or passed where it may be kept");
        if (d.IsSafe && UnsafeReason(Method) is string why)
            throw new CompileException($"{File}:{line}:1: `{name}` is not safe: {why}");
    }

    /// <summary>Why a method can escape Alice's checks, or null. Recursion is assumed safe while it is being checked.</summary>
    string? UnsafeReason(MethodSym m)
    {
        if (unsafeWhy.TryGetValue(m, out var known)) return known;
        if (m.Decl == null)
            // compiler-provided: a delegate's Invoke and @[SoA] store methods run Alice code; a `lib` fun is C
            return unsafeWhy[m] = m.Owner?.Kind is "delegate" or "soa" ? null : $"it is a foreign function (`{m.Owner?.Name}.{m.Name}`)";
        if (Backend.CEmitter.IsNative(m.Decl)) return unsafeWhy[m] = $"`{m.Name}` is @[Native]";
        unsafeWhy[m] = null;
        return unsafeWhy[m] = BodyUnsafe(m.Decl.Body);
    }

    // ---- the allow-list (security audit F5, F6) ----
    // Allowed: Alice code (checked transitively), members of values the method was handed (capability style),
    // the .NET statics below, the Alice runtime except its ambient entry points, the Prelude functions below.
    // Everything else is rejected, whatever the shape of the expression that reaches it.

    /// <summary>Prelude functions callable by name from safe code: computation only, no I/O, environment or process.</summary>
    static readonly HashSet<string> SafeFree =
    [
        "format", "sprintf", "str", "inspect", "hash", "raise", "rand", "pow", "IPow", "fma", "floor_div", "floor_mod",
        "floor_fmod", "floor_div_pos", "floor_mod_pos", "arr_and", "arr_or", "arr_plus", "append_op", "filled", "range",
        "regex", "match_index", "no_match", "checked", "assert", "assert_equal", "to_json", "parse_json", "FormatFloat",
    ];

    /// <summary>.NET types whose statics only compute: math, numbers, strings, collections helpers.</summary>
    static readonly HashSet<Type> SafeClrStatics =
    [
        typeof(Math), typeof(MathF), typeof(string), typeof(char), typeof(decimal), typeof(Half), typeof(Int128), typeof(UInt128),
        typeof(System.Numerics.BigInteger), typeof(System.Numerics.BitOperations), typeof(System.Numerics.Vector), typeof(Array),
        typeof(Enumerable), typeof(Convert), typeof(BitConverter), typeof(TimeSpan), typeof(StringComparer), typeof(Tuple),
        typeof(ValueTuple), typeof(MemoryExtensions), typeof(System.Buffers.Binary.BinaryPrimitives),
    ];

    /// <summary>Alice runtime types that reach the machine (native libraries, files, the loaded program, the test host).</summary>
    static readonly HashSet<string> AmbientRuntimeTypes =
        ["NativeLibs", "NativeCallback", "MappedFile", "ObjectSpace", "TestRunner", "ReplHost", "Utf8Arg", "EventAdapters", "KernelPool", "Specs", "Prelude"];

    /// <summary>Prelude members marked [Ambient] (reflection, dynamic dispatch): the runtime is the source of truth.</summary>
    static readonly HashSet<string> AmbientPrelude = typeof(Alice.Runtime.Prelude)
        .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(m => m.IsDefined(typeof(Alice.Runtime.AmbientAttribute), false)).Select(m => m.Name).ToHashSet();

    static bool IsRuntime(Type t) => t.Namespace?.StartsWith("Alice.Runtime") == true;

    /// <summary>A static member of `t` reached by name: allowed for pure .NET helpers and the non-ambient runtime.</summary>
    static bool SafeStatic(Type t, string member)
    {
        if (t.IsGenericType) t = t.GetGenericTypeDefinition();
        if (t.IsPrimitive || SafeClrStatics.Contains(t)) return true;
        if (t == typeof(Alice.Runtime.Prelude)) return SafeFree.Contains(Binder.Mangle(member)) && !AmbientPrelude.Contains(Binder.Mangle(member));
        if (IsRuntime(t)) return !AmbientRuntimeTypes.Contains(t.Name) && !t.IsDefined(typeof(Alice.Runtime.AmbientAttribute), false)
            && !t.GetMember(member).Any(m => m.IsDefined(typeof(Alice.Runtime.AmbientAttribute), false));
        return false;
    }

    /// <summary>Creating a .NET object: collections, text, numbers and the non-ambient runtime are plain data.</summary>
    static bool SafeNew(Type t)
    {
        if (t.IsGenericType) t = t.GetGenericTypeDefinition();
        if (Reflective(t)) return false;
        if (IsRuntime(t)) return !AmbientRuntimeTypes.Contains(t.Name) && !t.IsDefined(typeof(Alice.Runtime.AmbientAttribute), false);
        string ns = t.Namespace ?? "";
        if (ns is "System.Collections.Generic" or "System.Collections.Immutable" or "System.Collections.Concurrent" or "System.Numerics"
            || ns == "System.Text" || ns == "System.Text.RegularExpressions") return true;
        return ns == "System" && (t.IsValueType || t == typeof(object) || t == typeof(string) || t == typeof(Random) || typeof(Exception).IsAssignableFrom(t));
    }

    /// <summary>Reflection, interop and code-loading types: holding one is reaching the machine.</summary>
    static bool Reflective(Type? t)
    {
        if (t == null) return false;
        if (t.IsArray) return Reflective(t.GetElementType());
        if (t.IsGenericType && t.GetGenericArguments().Any(Reflective)) return true;
        string ns = t.Namespace ?? "";
        return typeof(System.Reflection.MemberInfo).IsAssignableFrom(t) || typeof(System.Reflection.Assembly).IsAssignableFrom(t)
            || t == typeof(AppDomain) || t == typeof(Activator) || t == typeof(Delegate) || t == typeof(MulticastDelegate)
            || ns.StartsWith("System.Reflection") || ns.StartsWith("System.Runtime.InteropServices") || ns.StartsWith("System.Runtime.Loader");
    }

    static string Through(MethodSym callee, string why) => $"it calls `{callee.Name}`, which {why.Replace("it ", "")}";

    /// <summary>Every node of the body is checked (using, spawn, if-as-value, blocks, lambdas and inlined rewrites).</summary>
    string? BodyUnsafe(IEnumerable<Node> body)
    {
        string? why = null;
        void Visit(Node n)
        {
            if (why != null) return;
            why = NodeUnsafe(n);
            if (why == null && n is Syntax.Expr { Sym: Node rewritten }) AstWalk.Each(rewritten, Visit);   // instance_eval, generic `.new`
        }
        AstWalk.Each(body, Visit);
        return why;
    }

    string? NodeUnsafe(Node n)
    {
        switch (n)
        {
            case BeginStmt { Unchecked: true }:
                return "it uses `unchecked`";
            case Syntax.Expr { Res: "dynamic" or "dyncall" }:
                return "it calls through `dynamic`";
            case CallExpr { Res: "special" } sp when sp.Name != "to_s":
                return $"it calls `{sp.Name}` (dynamic dispatch or reflection)";
            case CallExpr { Res: "defmethod" or "classq" } r:
                return $"it uses `{r.Name}` (runtime reflection)";
            case Syntax.Expr e when Reflective(e.Type.Clr):
                return $"it handles a reflection object (`{e.Type.Clr!.Name}`)";
            case ConstExpr { Res: "path", Sym: ConstPath { Owner.Clr: { } pc } path } when !SafeStatic(pc, path.Members[0].Item1):
                return $"it reaches .NET `{pc.Name}.{path.Members[0].Item1}` (ambient authority)";
            case CallExpr { Res: "clr" or "field", Sym: System.Reflection.MethodBase { IsStatic: true } sm } sc when sm.DeclaringType is { } dt && !SafeStatic(dt, sm.Name):
                return $"it reaches .NET `{dt.Name}.{sc.Name}` (ambient authority)";
            case CallExpr { Recv: ConstExpr { Res: "type" } owner, Res: "clr" or "field" } sc when owner.Type.Clr is { } oc && !SafeStatic(oc, sc.Name):
                return $"it reaches .NET `{oc.Name}.{sc.Name}` (ambient authority)";
            case CallExpr { Recv: null, Res: "clr" } inh:
                return $"it calls the inherited .NET member `{inh.Name}`";
            case CallExpr { Res: "ext" } x when AmbientPrelude.Contains(Binder.Mangle(x.Name)):
                return $"it calls `{x.Name}` (runtime reflection or dynamic dispatch)";
            case CallExpr { Recv: null, Res: "free" or "call", Sym: not MethodSym } f when !SafeFree.Contains(Binder.Mangle(f.Name)):
                return $"it calls `{f.Name}` (I/O, environment, process or reflection: not on the safe allow-list)";
            case CallExpr { Recv: not null, Res: "call" or "unknown-member", Sym: not MethodSym } u:
                return $"it calls `{u.Name}` on a value of unknown type";
            case NameExpr { Res: "call" or "unknown", Sym: not MethodSym and not LocalSym } ne when !SafeFree.Contains(Binder.Mangle(ne.Name)):
                return $"it calls `{ne.Name}` (I/O, environment, process or reflection: not on the safe allow-list)";
            case NewExpr { Type: { Sym: null, Clr: { } nt } } when !SafeNew(nt):
                return $"it creates .NET `{nt.Name}` (I/O, network, process, reflection or interop)";
            case NewExpr { Type.Sym: TypeSym ts, Args: var nargs } when CtorUnsafe(ts, nargs.Count) is string cwhy:
                return $"it creates `{ts.Name}`, whose initializer {cwhy.Replace("it ", "")}";
            case CallExpr { Sym: MethodSym callee } when callee != Method && UnsafeReason(callee) is string why:
                return Through(callee, why);
            case NameExpr { Res: "call", Sym: MethodSym callee } when callee != Method && UnsafeReason(callee) is string why:   // `g` without parens
                return Through(callee, why);
        }
        return null;
    }

    /// <summary>`T.new`: its `initialize` and its field initializers run too.</summary>
    string? CtorUnsafe(TypeSym ts, int argc)
    {
        for (var s = ts; s != null; s = s.BaseClass)
        {
            if (s.FindMethod("initialize", argc) is { } ctor && ctor != Method && UnsafeReason(ctor) is string why) return why;
            if (s.Decl != null)
                foreach (var m in s.Decl.Members)
                    if (m is FieldDecl { Init: { } fi } && BodyUnsafe([fi]) is string fwhy) return fwhy;
                    else if (m is AccessorDecl { Init: { } ai } && BodyUnsafe([ai]) is string awhy) return awhy;
        }
        return null;
    }
}
