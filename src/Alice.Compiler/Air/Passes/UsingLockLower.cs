using Alice.Compiler.Mir;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// `using` and `lock` without Roslyn. `using (var r = v) body` is a scope `{ decl r = v; try { body } finally {
/// dispose } }`: a class resource is null-checked, a struct one gets a direct (constrained, unboxed) Dispose call.
/// `lock (o) body` is `Monitor.Enter(o, ref taken)` inside the try and `if (taken) Monitor.Exit(o)` in the finally,
/// with o evaluated once; a `System.Threading.Lock` uses its `EnterScope()` ref struct as C# 13 does.
/// `await using` and resources of unknown type stay and are reported.
/// </summary>
public sealed class UsingLockLower(IlLowerContext? ctx = null) : IlPass(ctx)
{
    public override string Name => "il-using-lock";

    static readonly AirType MonitorType = new AirType.ClrRef("global::System.Threading.Monitor", typeof(System.Threading.Monitor), []);
    static readonly AirType ScopeType = new AirType.ClrRef("global::System.Threading.Lock.Scope", typeof(System.Threading.Lock.Scope), []);

    protected override List<MStmt>? Lower(MStmt s) => s switch
    {
        MUsing u => Using(u),
        MBlock { Lock: not null } b => Lock(b),
        _ => null,
    };

    List<MStmt>? Using(MUsing u)
    {
        if (u.Await)
        {
            Unsupported(u, "`await using` is not lowered for the IL backend yet");
            return null;
        }
        var t = u.Value.Type;
        if (IlTypes.IsValueType(t) is not { } isValue || IlTypes.IsNullableValue(t))
        {
            Unsupported(u, "using of a resource of unknown or Nullable<T> type");
            return null;
        }
        var r = Local(u.Name, t);
        MStmt dispose = Do(Call(r, "Dispose", IlTypes.Void));
        if (!isValue) dispose = new MIf { Cond = new MBinary { Op = AirBinOp.Ne, L = Local(u.Name, t), R = Const(null), Type = IlTypes.Bool }, Then = [dispose] };
        return [new MBlock { Body = [Decl(u.Name, t!, u.Value), new MTry { Body = u.Body, Finally = [dispose] }] }];
    }

    List<MStmt> Lock(MBlock b)
    {
        var target = b.Lock!;
        b.Lock = null;
        List<MStmt> body;
        if (target.Type is AirType.ClrRef { Name: "global::System.Threading.Lock", Nullable: false })
        {
            string scope = Ctx.Temp("scope");
            body =
            [
                Decl(scope, ScopeType, Call(target, "EnterScope", ScopeType)),
                new MTry { Body = b.Body, Finally = [Do(Call(Local(scope, ScopeType), "Dispose", IlTypes.Void))] },
            ];
        }
        else
        {
            var lt = IlTypes.IsValueType(target.Type) == false ? target.Type! : IlTypes.Obj;
            string o = Ctx.Temp("lock"), taken = Ctx.Temp("taken");
            var enter = Do(Call(new AirTypeRef(MonitorType), "Enter", IlTypes.Void,
                Local(o, lt), new MArg { Prefix = "ref ", E = Local(taken, IlTypes.Bool) }));
            var exit = Do(Call(new AirTypeRef(MonitorType), "Exit", IlTypes.Void, Local(o, lt)));
            body =
            [
                Decl(o, lt, target),
                Decl(taken, IlTypes.Bool, Const(false)),
                new MTry { Body = [enter, .. b.Body], Finally = [new MIf { Cond = Local(taken, IlTypes.Bool), Then = [exit] }] },
            ];
        }
        b.Body = body;
        return [b];
    }
}
