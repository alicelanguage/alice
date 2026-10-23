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
            string zero = ret == "void" ? "return;" : $"return default({ret});";
            var hp = new Backend.Printer("", 2);
            if (ml.ExprBody != null) hp.Root(ret == "void" ? new MExprStmt { E = ml.ExprBody } : new MReturn { Value = ml.ExprBody });
            else foreach (var s in ml.Body!) hp.Root(s);
            Extra.Add($"[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] {{ typeof(global::System.Runtime.CompilerServices.CallConvCdecl) }})]\n"
                + $"private static unsafe {ret} {name}({string.Join(", ", ml.Params)})\n{{\n    try\n    {{\n{hp}    }}\n"
                + $"    catch (global::System.Exception __e)\n    {{\n        global::Alice.Runtime.NativeCallback.Fail(__e);\n        {zero}\n    }}\n}}\n");
            // the address is taken in an unsafe helper so the call site stays safe code
            Extra.Add($"private static unsafe global::Alice.Runtime.NativeFn {name}_fn() => new((nint)({cs})&{name});\n");
            args[i] = new MCode(name + "_fn()");
        }
    }
}
