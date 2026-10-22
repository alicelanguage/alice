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
        if (m.Decl == null) return unsafeWhy[m] = null;   // compiler-provided methods
        if (Backend.CEmitter.IsNative(m.Decl)) return unsafeWhy[m] = $"`{m.Name}` is @[Native]";
        unsafeWhy[m] = null;
        return unsafeWhy[m] = BodyUnsafe(m.Decl.Body);
    }

    /// <summary>Namespaces whose types grant authority over the machine: creating one is never safe.</summary>
    static readonly string[] Authority = ["System.IO", "System.Net", "System.Diagnostics", "System.Reflection", "System.Runtime.InteropServices", "System.Threading"];

    /// <summary>A .NET static reached by name is ambient authority, except pure math and Alice's own runtime.</summary>
    static bool AmbientClr(Type? t) => t != null && t != typeof(Math) && t != typeof(MathF) && t.Namespace?.StartsWith("Alice.Runtime") != true;

    static string Through(MethodSym callee, string why) => $"it calls `{callee.Name}`, which {why.Replace("it ", "")}";

    string? BodyUnsafe(List<Stmt> body)
    {
        foreach (var s in WalkStmts(body))
            if (s is BeginStmt { Unchecked: true }) return "it uses `unchecked`";
        foreach (var e in Walk(body))
        {
            switch (e)
            {
                case { Res: "dynamic" }:
                    return "it calls through `dynamic`";
                case ConstExpr { Res: "path", Sym: ConstPath { Owner.Clr: var pc } path } when AmbientClr(pc):
                    return $"it reaches .NET `{pc!.Name}.{path.Members[0].Item1}` (ambient authority)";
                case CallExpr { Recv: ConstExpr owner, Res: "clr" or "field" or "dyncall" } sc when AmbientClr(owner.Type.Clr):
                    return $"it reaches .NET `{owner.Type.Clr!.Name}.{sc.Name}` (ambient authority)";
                case NewExpr { Type: { Sym: null, Clr: { } nt } } when Authority.Any(ns => nt.Namespace?.StartsWith(ns) == true):
                    return $"it creates .NET `{nt.Name}` (I/O, network, process, reflection or interop)";
                case CallExpr { Sym: MethodSym callee } when callee != Method && UnsafeReason(callee) is string why:
                    return Through(callee, why);
                case NameExpr { Res: "call", Sym: MethodSym callee } when callee != Method && UnsafeReason(callee) is string why:   // `g` without parens
                    return Through(callee, why);
            }
            if (e is CallExpr { Block: { } blk } && BodyUnsafe(blk.Body) is string inner) return inner;
            if (e is LambdaExpr lam && BodyUnsafe(lam.Body) is string inl) return inl;
        }
        return null;
    }
}
