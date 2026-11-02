using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// A lambda passed to a `Callback(...)` parameter of a `lib` `fun` becomes a C function pointer (ADR 0018): the
/// lambda is lifted into a static `[UnmanagedCallersOnly]` method and passed as `&amp;method`. No delegate, no GC
/// handle, nothing to keep alive. Captures are a compile error (C cannot hold a closure). Exceptions are caught
/// in the callback and rethrown by the `fun` wrapper after C returns.
/// </summary>
public sealed partial class Lowerer
{
    const string UnmanagedCdecl = "delegate* unmanaged[Cdecl]<";

    void LiftCallbacks(CallExpr c, MethodSym m, List<MExpr> args)
    {
        for (int i = 0; i < m.Params.Count && i < c.Args.Count && i < args.Count; i++)
        {
            string cs = m.Params[i].Type.Cs;
            if (!cs.StartsWith(UnmanagedCdecl) || c.Args[i].Value is not LambdaExpr lam || args[i] is not MLambda ml) continue;
            if (lam.Captures)
                throw new CompileException($"{File}:{c.Line}:{Math.Max(1, c.Col)}: error AL0215: a callback passed to native code cannot capture variables (C keeps only a function pointer); pass state through the C function's user-data argument");
            string ret = cs[UnmanagedCdecl.Length..^1].Split(", ")[^1];
            string name = $"__cb_{++helperN}";
            var retA = ret == "void" ? AirLeaf.Void : new AirType.ClrRef(ret, null, []);
            // the body is optimized statement by statement, as it always was; the catch hands the exception to the fun wrapper
            List<MStmt> body = ml.ExprBody != null ? [ret == "void" ? new MExprStmt { E = ml.ExprBody } : new MReturn { Value = ml.ExprBody }] : ml.Body!;
            foreach (var s in body) Optimizer.Pipeline.Run([s]);
            var guarded = new MTry { Body = body };
            guarded.Catches.Add(new MCatch
            {
                TypeCs = "global::System.Exception", Name = "__e",
                Body =
                {
                    new MExprStmt { E = new MCall { Target = AirTypeRef.Named("global::Alice.Runtime.NativeCallback"), Name = "Fail", Args = { new AirLocalRef("__e") } } },
                    ret == "void" ? new MReturn() : new MReturn { Value = new AirDefault(retA) },
                },
            });
            AddHelper(new AirHelperDecl
            {
                Method = new AirMethodDecl { Name = name, Vis = AirVis.Private, Flags = AirMethodFlags.Static, Params = ml.Params.Select(ParamOf).ToList(), Ret = retA, Body = [guarded] },
                Unsafe = true,
                Attributes =
                {
                    new AirAttribute
                    {
                        Type = new AirType.ClrRef("global::System.Runtime.InteropServices.UnmanagedCallersOnly", null, []),
                        Named = { ("CallConvs", new MNewArray { Items = [new AirTypeOf(new AirType.ClrRef("global::System.Runtime.CompilerServices.CallConvCdecl", null, []))] }) },
                    },
                },
            });
            // the address is taken in an unsafe helper so the call site stays safe code
            AddHelper(new AirHelperDecl
            {
                Method = new AirMethodDecl { Name = name + "_fn", Vis = AirVis.Private, Flags = AirMethodFlags.Static, Ret = new AirType.ClrRef("global::Alice.Runtime.NativeFn", null, []) },
                Unsafe = true,
                // new((nint)(delegate* unmanaged[Cdecl]<..>)&name)
                ExprBody = new MNew { Args = { new MCast { Bare = true, TypeCs = "nint", E = new MCast { Bare = true, TypeCs = cs, E = new MUnary { Op = AirUnOp.AddressOf, E = new AirMethodGroup(null, name) } } } } },
            });
            args[i] = new MCall { Name = name + "_fn" };
        }
    }
}
