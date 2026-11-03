using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Wasm;

/// <summary>A lambda's view of its enclosing body: captured names resolve to the enclosing environment.</summary>
internal sealed class LambdaInfo
{
    public WasmBody Outer = null!;
    /// <summary>The lambda's environment parameter (local 0) as a variable.</summary>
    public Var EnvParam = null!;
    public readonly Dictionary<string, Var> Captured = new(StringComparer.Ordinal);

    public Var? Find(string name) => Captured.TryGetValue(name, out var v) ? v : null;
}

// Closures through the function table: a closure is [header][table slot @8][environment @12]; captured locals of a
// body live in one environment object (8 bytes per variable) allocated in the prologue, so the body and its lambdas
// share them (C# capture by reference). A lambda is a function (env, params) -> result called with call_indirect.
public sealed partial class WasmBody
{
    /// <summary>Names each lambda of the body reads or writes that the body itself declares (or `this`).</summary>
    void PrepareCaptures(List<MStmt> stmts)
    {
        var lambdas = new List<MLambda>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in scopes[0].Values) declared.Add(v.Name);
        foreach (var s in stmts) Walk(s, lambdas, declared, inLambda: false);
        if (lambdas.Count == 0) return;
        foreach (var l in lambdas)
        {
            var own = new HashSet<string>(l.Params.Select(p => AirToWasm.Bare(p.Split(' ')[^1])), StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (l.ExprBody != null) Names(l.ExprBody, used, own);
            if (l.Body != null) foreach (var s in l.Body) NamesS(s, used, own);
            foreach (var n in used)
                if (declared.Contains(n) || n == "this" && !isStatic) captured.Add(n);
        }
        if (captured.Count == 0) return;
        envSlots = captured.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => 8 + 8 * x.i, StringComparer.Ordinal);
        env = new Var { Name = "__env", T = WT.Str, Frame = FrameSlot(4, 4) };
        Track();
        code.Get(Fp());
        code.I32(8 + 8 * captured.Count);
        code.I32(AirToWasm.EnvType);
        code.Call(c.Rt.Func("alloc"));
        code.Mem(Op.I32Store, (uint)env.Frame);
    }

    Dictionary<string, int> envSlots = new(StringComparer.Ordinal);

    void BindEnv(Var v)
    {
        if (v.T.IsStruct) throw No("Closure:captured-struct", v.Name);
        v.EnvHolder = env;
        v.EnvOff = envSlots[v.Name];
        v.Local = v.Frame = -1;
    }

    /// <summary>A captured parameter: its value moves into the environment.</summary>
    void MoveToEnv(Var v)
    {
        if (v.T.IsStruct) throw No("Closure:captured-struct", v.Name);
        var src = new Var { Name = v.Name, T = v.T, Local = v.Local, Frame = v.Frame };
        BindEnv(v);
        StoreVar(v, () => LoadVar(src));
    }

    /// <summary>The environment object of a captured variable.</summary>
    void EnvAddr(Var v)
    {
        var holder = v.EnvHolder!;
        if (holder.Local >= 0) code.Get(holder.Local);
        else { code.Get(Fp()); code.Mem(Op.I32Load, (uint)holder.Frame); }
    }

    static void Walk(MStmt s, List<MLambda> lambdas, HashSet<string> declared, bool inLambda)
    {
        switch (s)
        {
            case MDecl d: if (!inLambda) declared.Add(AirToWasm.Bare(d.Name)); if (d.Init != null) WalkE(d.Init, lambdas, declared, inLambda); break;
            case MFor f: if (!inLambda) declared.Add(AirToWasm.Bare(f.Var)); WalkE(f.Start, lambdas, declared, inLambda); WalkE(f.End, lambdas, declared, inLambda); foreach (var x in f.Body) Walk(x, lambdas, declared, inLambda); break;
            case MForeach fe: if (!inLambda) foreach (var v in fe.Vars) declared.Add(AirToWasm.Bare(v)); WalkE(fe.Coll, lambdas, declared, inLambda); foreach (var x in fe.Body) Walk(x, lambdas, declared, inLambda); break;
            case MAssign a: WalkE(a.Target, lambdas, declared, inLambda); WalkE(a.Value, lambdas, declared, inLambda); break;
            case MExprStmt e: WalkE(e.E, lambdas, declared, inLambda); break;
            case MIf i: WalkE(i.Cond, lambdas, declared, inLambda); foreach (var x in i.Then) Walk(x, lambdas, declared, inLambda); if (i.Else != null) foreach (var x in i.Else) Walk(x, lambdas, declared, inLambda); break;
            case MWhile w: WalkE(w.Cond, lambdas, declared, inLambda); foreach (var x in w.Body) Walk(x, lambdas, declared, inLambda); break;
            case MReturn r: if (r.Value != null) WalkE(r.Value, lambdas, declared, inLambda); break;
            case MThrow t: if (t.Value != null) WalkE(t.Value, lambdas, declared, inLambda); break;
            case MTry tr:
                foreach (var x in tr.Body) Walk(x, lambdas, declared, inLambda);
                foreach (var ct in tr.Catches) { if (ct.Name != null && !inLambda) declared.Add(ct.Name); foreach (var x in ct.Body) Walk(x, lambdas, declared, inLambda); }
                if (tr.Finally != null) foreach (var x in tr.Finally) Walk(x, lambdas, declared, inLambda);
                break;
            case MBlock b: foreach (var x in b.Body) Walk(x, lambdas, declared, inLambda); break;
            case AirSwitch sw: WalkE(sw.Value, lambdas, declared, inLambda); foreach (var cs in sw.Cases) foreach (var x in cs) Walk(x, lambdas, declared, inLambda); if (sw.Default != null) foreach (var x in sw.Default) Walk(x, lambdas, declared, inLambda); break;
        }
    }

    static void WalkE(MExpr e, List<MLambda> lambdas, HashSet<string> declared, bool inLambda)
    {
        if (e is MLambda l)
        {
            if (!inLambda) lambdas.Add(l);
            if (l.Body != null) foreach (var s in l.Body) Walk(s, lambdas, declared, true);
            if (l.ExprBody != null) WalkE(l.ExprBody, lambdas, declared, true);
            return;
        }
        if (e is MIs { TypeCs: var tcs } && !inLambda && tcs.Contains(' ')) declared.Add(tcs[(tcs.LastIndexOf(' ') + 1)..]);
        if (e is AirSeq sq) foreach (var s in sq.Pre) Walk(s, lambdas, declared, inLambda);
        foreach (var ch in Children(e)) WalkE(ch, lambdas, declared, inLambda);
    }

    internal static IEnumerable<MExpr> Children(MExpr e) => e switch
    {
        MCall c => (c.Target != null ? [c.Target] : Array.Empty<MExpr>()).Concat(c.Args),
        MMember m => [m.Target],
        MInvoke i => i.Args.Prepend(i.Target),
        MNew n => n.Args.Concat(n.Init ?? []),
        MNewArray na => (na.Size != null ? [na.Size] : Array.Empty<MExpr>()).Concat(na.Items ?? []),
        MIndex ix => ix.Args.Prepend(ix.Target),
        MBinary b => [b.L, b.R],
        MUnary u => [u.E],
        MCond cd => [cd.C, cd.A, cd.B],
        MCast cs => [cs.E],
        MIs i => [i.E],
        AirAs a => [a.E],
        MInterp ip => ip.Holes,
        MTuple t => t.Items,
        MPair p => [p.K, p.V],
        MArg a => [a.E],
        AirSeq sq => [sq.Value],
        AirRent r => r.Args,
        AirArenaExpr ae => ae.Children(),
        AirRelease r => [r.Target],
        AirLength l => [l.Target],
        AirOffset o => [o.Base, o.Offset],
        AirTripCount tc => [tc.End, tc.Start],
        AirRangeLength rl => [rl.Lo, rl.Hi],
        AirPresizeCount pc => [pc.From, pc.To],
        _ => [],
    };

    /// <summary>Free names of a lambda body (locals and parameters it reads or writes), minus its own.</summary>
    static void Names(MExpr e, HashSet<string> used, HashSet<string> own)
    {
        switch (e)
        {
            case AirLocalRef lr: if (!own.Contains(AirToWasm.Bare(lr.Name))) used.Add(AirToWasm.Bare(lr.Name)); return;
            case AirParamRef pr: if (!own.Contains(AirToWasm.Bare(pr.Name))) used.Add(AirToWasm.Bare(pr.Name)); return;
            case AirThis: used.Add("this"); return;
            case AirFieldRef: used.Add("this"); return;
            case MLambda l:
                {
                    var inner = new HashSet<string>(own, StringComparer.Ordinal);
                    foreach (var p in l.Params) inner.Add(AirToWasm.Bare(p.Split(' ')[^1]));
                    if (l.ExprBody != null) Names(l.ExprBody, used, inner);
                    if (l.Body != null) foreach (var s in l.Body) NamesS(s, used, inner);
                    return;
                }
            case AirConst or AirTypeRef or AirNull: return;
            case MCode mc when e.GetType() == typeof(MCode):
                if (!own.Contains(AirToWasm.Bare(mc.Text)) && System.Text.RegularExpressions.Regex.IsMatch(mc.Text, @"^@?[A-Za-z_][A-Za-z0-9_]*$")) used.Add(AirToWasm.Bare(mc.Text));
                return;
            case MCall { Target: null }: used.Add("this"); break;
            case AirSeq sq: foreach (var s in sq.Pre) NamesS(s, used, own); break;
        }
        foreach (var ch in Children(e)) Names(ch, used, own);
    }

    static void NamesS(MStmt s, HashSet<string> used, HashSet<string> own)
    {
        switch (s)
        {
            case MDecl d: own.Add(AirToWasm.Bare(d.Name)); if (d.Init != null) Names(d.Init, used, own); break;
            case MFor f: own.Add(AirToWasm.Bare(f.Var)); Names(f.Start, used, own); Names(f.End, used, own); foreach (var x in f.Body) NamesS(x, used, own); break;
            case MForeach fe: foreach (var v in fe.Vars) own.Add(AirToWasm.Bare(v)); Names(fe.Coll, used, own); foreach (var x in fe.Body) NamesS(x, used, own); break;
            case MAssign a: Names(a.Target, used, own); Names(a.Value, used, own); break;
            case MExprStmt e: Names(e.E, used, own); break;
            case MIf i: Names(i.Cond, used, own); foreach (var x in i.Then) NamesS(x, used, own); if (i.Else != null) foreach (var x in i.Else) NamesS(x, used, own); break;
            case MWhile w: Names(w.Cond, used, own); foreach (var x in w.Body) NamesS(x, used, own); break;
            case MReturn r: if (r.Value != null) Names(r.Value, used, own); break;
            case MThrow t: if (t.Value != null) Names(t.Value, used, own); break;
            case MBlock b: foreach (var x in b.Body) NamesS(x, used, own); break;
            case MTry tr:
                foreach (var x in tr.Body) NamesS(x, used, own);
                foreach (var ct in tr.Catches) { if (ct.Name != null) own.Add(ct.Name); foreach (var x in ct.Body) NamesS(x, used, own); }
                if (tr.Finally != null) foreach (var x in tr.Finally) NamesS(x, used, own);
                break;
            case AirSwitch sw: Names(sw.Value, used, own); foreach (var cs in sw.Cases) foreach (var x in cs) NamesS(x, used, own); if (sw.Default != null) foreach (var x in sw.Default) NamesS(x, used, own); break;
        }
    }

    int lambdaCount;

    /// <summary>A closure value: the lambda becomes a function in the table; the environment is this body's.</summary>
    WT Lambda(MLambda lam)
    {
        var (ps, names, ret) = LambdaSignature(lam);
        if (scratch)
        {
            var ft = WT.FuncOf(ps.ToArray(), ret ?? InferLambdaRet(lam, ps, names));
            code.I32(0);
            return ft;
        }
        ret ??= InferLambdaRet(lam, ps, names);
        if (ret.IsStruct || ps.Any(p => p.IsStruct)) throw No("MLambda:struct-signature");
        var wm = new WMethod { Name = "lambda" + lambdaCount++, Owner = owner, IsStatic = true, Ret = ret };
        wm.Params.Add(WT.Str);
        wm.ParamNames.Add("__env");
        wm.Params.AddRange(ps);
        wm.ParamNames.AddRange(names);
        c.DefineFunc(wm);
        wm.Func.Name = self.Func.Name + "." + wm.Name;
        var info = new LambdaInfo { Outer = this, EnvParam = new Var { Name = "__env", T = WT.Str, Local = 0 } };
        foreach (var n in captured)
            if (CapturedTypeOrNull(n) is { } ct) info.Captured[n] = new Var { Name = n, T = ct, EnvHolder = info.EnvParam, EnvOff = envSlots[n] };
        var lb = new WasmBody(c, wm, info);
        // the env parameter is not a user variable
        lb.scopes[0].Remove("__env");
        var body = lam.Body ?? [ret.K == WK.Void ? new MExprStmt { E = lam.ExprBody! } : new MReturn { Value = lam.ExprBody! }];
        lb.Body(body);
        // [header][slot][env]
        code.I32(16);
        code.I32(AirToWasm.ClosureType);
        code.Call(c.Rt.Func("alloc"));
        int o = Temp(ValType.I32);
        code.Tee(o);
        code.I32(M.Slot(wm.Func));
        code.Mem(Op.I32Store, 8);
        code.Get(o);
        if (env != null) { code.Get(Fp()); code.Mem(Op.I32Load, (uint)env.Frame); } else code.I32(0);
        code.Mem(Op.I32Store, 12);
        code.Get(o);
        return WT.FuncOf(ps.ToArray(), ret);
    }

    /// <summary>The type of a captured name declared so far (a lambda only sees variables declared before it).</summary>
    WT? CapturedTypeOrNull(string n)
    {
        if (n == "this") return isStatic ? null : owner.Type;
        foreach (var s in scopes)
            if (s.TryGetValue(n, out var v)) return v.T;
        if (lambda?.Find(n) is { } lv) return lv.T;
        return null;
    }

    (List<WT> Ps, List<string> Names, WT? Ret) LambdaSignature(MLambda lam)
    {
        var ps = new List<WT>();
        var names = new List<string>();
        WT? ft = lam.Type is AirType.ClrRef cr ? TryAir(cr) : null;
        for (int i = 0; i < lam.Params.Count; i++)
        {
            var parts = lam.Params[i].Trim().Split(' ');
            names.Add(AirToWasm.Bare(parts[^1]));
            if (parts.Length > 1) ps.Add(c.Parse(string.Join(" ", parts[..^1]), span));
            else if (ft != null && i < ft.Params.Length) ps.Add(ft.Params[i]);
            else throw No("MLambda:untyped-parameter", parts[^1]);
        }
        return (ps, names, ft?.Ret);
    }

    WT? TryAir(AirType t)
    {
        try { var w = c.FromAir(t, span); return w.K == WK.Func ? w : null; }
        catch (WasmUnsupportedException) { return null; }
    }

    /// <summary>The result type of an untyped lambda: its expression body typed in a lambda scratch body.</summary>
    WT InferLambdaRet(MLambda lam, List<WT> ps, List<string> names)
    {
        if (lam.ExprBody == null)
        {
            var r = FindReturn(lam.Body!);
            if (r == null) return WT.Void;
            lam = new MLambda { Params = lam.Params, ExprBody = r };
        }
        var wm = new WMethod { Name = "lambda?", Owner = owner, IsStatic = true, Ret = WT.Void, Func = new WasmFunc { Name = "?", Sig = new FuncSig([], []), Code = new WasmCode(new FuncSig([], [])) } };
        var info = new LambdaInfo { Outer = this, EnvParam = new Var { Name = "__env", T = WT.Str, Local = 0 } };
        foreach (var n in captured) if (CapturedTypeOrNull(n) is { } ct) info.Captured[n] = new Var { Name = n, T = ct, EnvHolder = info.EnvParam, EnvOff = envSlots.GetValueOrDefault(n, 8) };
        var lb = new WasmBody(c, wm, info);
        for (int i = 0; i < ps.Count; i++) lb.scopes[0][names[i]] = new Var { Name = names[i], T = ps[i], Local = lb.Local(ps[i].Val) };
        if (!scratch && captured.Count == 0)
        {
            // names the lambda reads from the enclosing body resolve there while typing
            foreach (var sc in scopes) foreach (var kv in sc) info.Captured.TryAdd(kv.Key, kv.Value);
        }
        else foreach (var sc in scopes) foreach (var kv in sc) info.Captured.TryAdd(kv.Key, kv.Value);
        return lb.Ty(lam.ExprBody!);
    }

    static MExpr? FindReturn(List<MStmt> body)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case MReturn { Value: { } v }: return v;
                case MIf i: { var r = FindReturn(i.Then) ?? (i.Else != null ? FindReturn(i.Else) : null); if (r != null) return r; break; }
                case MBlock b: { var r = FindReturn(b.Body); if (r != null) return r; break; }
                case MWhile w: { var r = FindReturn(w.Body); if (r != null) return r; break; }
            }
        }
        return null;
    }

    /// <summary>`f.call(args)` / `f.(args)`: the closure's table slot through call_indirect, the environment first.</summary>
    WT Invoke(MInvoke inv)
    {
        var ft = Ty(inv.Target);
        if (ft.K != WK.Func) throw No("MInvoke:" + ft.Name);
        var clo = FrameTempOrLocal(ft);
        StoreTemp(clo, () => Emit(inv.Target, ft));
        LoadTemp(clo);
        NullCheck();
        code.Mem(Op.I32Load, 12);
        var ops = new List<(Func<WT>, WT, bool)>();
        for (int i = 0; i < ft.Params.Length; i++) ops.Add(Opnd(inv.Args[i], ft.Params[i]));
        Operands(ops);
        LoadTemp(clo);
        code.Mem(Op.I32Load, 8);
        code.CallIndirect(c.ClosureSig(ft));
        return ft.Ret!;
    }
}
