using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Wasm;

// Statements: declarations, assignments, structured control flow (break/continue through finally), returns
// (struct results through sret), exceptions (wasm EH try_table with one tag; --panic=abort drops the handlers),
// dense switches (br_table).
public sealed partial class WasmBody
{
    sealed class LoopCtx
    {
        public WasmLabel Break = null!, Continue = null!;
        public int Finally;
    }

    readonly List<LoopCtx> loops = [];
    /// <summary>Active `finally` bodies, innermost last: break/continue/return run the ones they leave.</summary>
    readonly List<List<MStmt>> finallies = [];
    /// <summary>The exception of the innermost catch (a bare `raise` rethrows it).</summary>
    readonly List<Var> catches = [];

    internal void Stmts(List<MStmt> stmts)
    {
        foreach (var s in stmts) Stmt(s);
    }

    void Block(List<MStmt> stmts)
    {
        scopes.Add(new(StringComparer.Ordinal));
        Stmts(stmts);
        scopes.RemoveAt(scopes.Count - 1);
    }

    void Stmt(MStmt s)
    {
        if (s.Span != null) span = s.Span;
        else if (s.Line > 0) span = new Span(c.File, s.Line, 1, 0);
        if (s.Line > 0) Mark(s.Line);
        Seen(s.GetType().Name);
        switch (s)
        {
            case MDecl d: Decl(d); break;
            case MAssign a: Assign(a); break;
            case MExprStmt es: ExprStmt(es.E); break;
            case MIf { Cond: AirVectorFits { Form: AirFitsForm.Guard } } vi:
                // no wide vectors in the core profile yet: the guard is false, the vector loop is dead code
                if (vi.Else != null) Block(vi.Else);
                break;
            case MIf i:
                {
                    CondBool(i.Cond);
                    var iff = code.If();
                    Block(i.Then);
                    if (i.Else != null) { code.Else(); Block(i.Else); }
                    code.End(iff);
                    break;
                }
            case MWhile w:
                {
                    var brk = code.Block();
                    var top = code.Loop();
                    CondBool(w.Cond);
                    code.Emit(Op.I32Eqz);
                    code.BrIf(brk);
                    loops.Add(new LoopCtx { Break = brk, Continue = top, Finally = finallies.Count });
                    Block(w.Body);
                    loops.RemoveAt(loops.Count - 1);
                    code.Br(top);
                    code.End(top);
                    code.End(brk);
                    break;
                }
            case MFor f: For(f); break;
            case MForeach fe: Foreach(fe); break;
            case MReturn r: Return(r); break;
            case MBreak: Jump(brk: true); break;
            case MContinue: Jump(brk: false); break;
            case MThrow t: Throw(t); break;
            case MTry tr: Try(tr); break;
            case MBlock b:
                {
                    if (b.Lock != null) { Emit(b.Lock, null); code.Emit(Op.Drop); }   // single-threaded: a lock is its body
                    if (b.Unchecked) uncheckedDepth++;
                    Block(b.Body);
                    if (b.Unchecked) uncheckedDepth--;
                    break;
                }
            case AirSwitch sw: Switch(sw); break;
            case MDeconstruct dc: Deconstruct(dc); break;
            case MUsing: throw No("MUsing");
            default: throw No(s.GetType().Name);
        }
    }

    void ExprStmt(MExpr e)
    {
        if (e is MUnary { Op: AirUnOp.Inc or AirUnOp.Dec } u)
        {
            IncDec(u);
            code.Emit(Op.Drop);
            return;
        }
        var t = Raw(e);
        if (t.K != WK.Void && t.K != WK.TypeRef) code.Emit(Op.Drop);
    }

    void Decl(MDecl d)
    {
        WT t;
        if (d.TypeCs != null && d.TypeCs != "var") t = c.Parse(d.TypeCs, span);
        else if (d.Init != null) t = Ty(d.Init);
        else throw No("MDecl:untyped", d.Name);
        if (t.K == WK.Null) throw No("MDecl:null-typed", d.Name);
        if (t.K is WK.Void or WK.TypeRef) throw No("MDecl:no-value", d.Name);
        var v = Declare(d.Name, t);
        if (d.Init == null || IsDefaultLit(d.Init) && !t.IsStruct)
        {
            // locals start zeroed in wasm; a reused frame slot (a loop) needs the reset
            if (v.Local < 0 && d.Init != null) StoreVar(v, () => { DefaultValue(t); return t; });
            else if (v.Local >= 0 && d.Init != null) { DefaultValue(t); code.Set(v.Local); }
            return;
        }
        if (t.IsStruct && v.EnvHolder == null) { StructInto(d.Init, () => VarAddr(v), t); return; }
        StoreVar(v, () => Emit(d.Init, t));
    }

    /// <summary>A struct value constructed or copied into the address <paramref name="dst"/> pushes.</summary>
    void StructInto(MExpr e, Action dst, WT t)
    {
        if (e is MNew n && c.Parse(n.TypeCs, span).IsStruct) { New(n, dst); return; }
        if (e is MCall call && Ty(e).IsStruct)
        {
            // the callee copies its struct parameters on entry, so writing the result over one of them is safe
            var r = Call(call, dst);
            if (r.K != WK.Void) { int tmp = Temp(ValType.I32); code.Set(tmp); dst(); code.Get(tmp); code.I32(t.Size); code.Call(c.Rt.Func("memcpy")); }
            return;
        }
        if (IsDefaultLit(e) || e is AirDefault)
        {
            dst(); code.I32(0); code.I32(t.Size); code.Prefixed(Op.MemoryFill); code.B(0);
            return;
        }
        dst();
        var st = Emit(e, t);
        if (!st.IsStruct) throw No("Struct:from", st.Name);
        code.I32(t.Size);
        code.Call(c.Rt.Func("memcpy"));
    }

    static readonly Dictionary<string, AirBinOp> CompoundOps = new()
    {
        ["+="] = AirBinOp.Add, ["-="] = AirBinOp.Sub, ["*="] = AirBinOp.Mul, ["/="] = AirBinOp.Div, ["%="] = AirBinOp.Mod,
        ["&="] = AirBinOp.BitAnd, ["|="] = AirBinOp.BitOr, ["^="] = AirBinOp.BitXor, ["<<="] = AirBinOp.Shl, [">>="] = AirBinOp.Shr,
        [">>>="] = AirBinOp.UShr,
    };

    void Assign(MAssign a)
    {
        if (a.Target is AirDiscard or MCode { Text: "_" }) { ExprStmt(a.Value); return; }
        var tt = Ty(a.Target);
        if (a.Op == "=")
        {
            if (tt.IsStruct && LocalTarget(a.Target) is { } v && v.EnvHolder == null) { StructInto(a.Value, () => VarAddr(v), tt); return; }
            Store(a.Target, () => Emit(a.Value, tt));
            return;
        }
        if (a.Op == "??=")
        {
            Emit(a.Target, tt);
            code.Emit(Op.I32Eqz);
            var iff = code.If();
            Store(a.Target, () => Emit(a.Value, tt));
            code.End(iff);
            return;
        }
        if (!CompoundOps.TryGetValue(a.Op, out var op)) throw No("MAssign:" + a.Op);
        var bin = new MBinary { Op = op, L = a.Target, R = a.Value, Span = a.Value.Span };
        Store(a.Target, () => Raw(bin), explicitConv: true);
    }

    Var? LocalTarget(MExpr t) => t switch
    {
        AirLocalRef lr => Find(lr.Name),
        AirParamRef pr => Find(pr.Name),
        MCode mc when t is not (AirFieldRef or AirThis) && Find(mc.Text) is { } v => v,
        _ => null,
    };

    /// <summary>Stores into an lvalue: a variable, a field (instance or static), an array/list element.</summary>
    void Store(MExpr target, Func<WT> value, bool explicitConv = false)
    {
        Func<WT, Func<WT>> conv = t => () => { var vt = value(); if (explicitConv) { Convert(vt, t, explicitCast: true, chk: Checked); return t; } return vt; };
        if (LocalTarget(target) is { } v) { StoreVar(v, conv(v.T)); return; }
        switch (target)
        {
            case AirFieldRef fr:
                StoreField(FieldOf(owner, fr.Name) ?? FieldOf(c.ProgramC, fr.Name) ?? throw No("Store:field", fr.Name), null, conv);
                return;
            case MCode mc when target is not AirThis:
                {
                    var f = FieldOf(owner, AirToWasm.Bare(mc.Text)) ?? FieldOf(c.ProgramC, AirToWasm.Bare(mc.Text)) ?? throw No("Store:name", mc.Text);
                    StoreField(f, null, conv);
                    return;
                }
            case MMember m:
                {
                    var tt = Ty(m.Target);
                    string n = AirToWasm.Bare(m.Name);
                    WField? f = tt.K == WK.TypeRef ? tt.Cls?.FindField(n) : tt.K is WK.Class or WK.Struct ? tt.Cls!.FindField(n) : null;
                    if (f == null) throw No("Store:member", tt.Name + "." + n);
                    StoreField(f, f.IsStatic ? null : m.Target, conv);
                    return;
                }
            case MIndex ix when ix.Args.Count == 1:
                {
                    var tt = Ty(ix.Target);
                    if (tt.K == WK.Array)
                    {
                        var elem = tt.Elem!;
                        ElementAddr(ix.Target, ix.Args[0]);
                        // the element address is interior: the array itself stays rooted by the local it came from
                        StoreAt(elem, conv(elem), 8);
                        return;
                    }
                    if (tt.K == WK.List)
                    {
                        Operands([Opnd(ix.Target, tt), Opnd(ix.Args[0], WT.I32)]);
                        code.Call(c.ListGetAddr(tt.Elem!));
                        StoreAt(tt.Elem!, conv(tt.Elem!));
                        return;
                    }
                    if (tt.K == WK.Dict)
                    {
                        var vt = tt.Elem!;
                        Operands([Opnd(ix.Target, tt), Opnd(ix.Args[0], tt.KeyT!), (conv(vt), vt, true)]);
                        code.Call(c.DictSet(tt));
                        return;
                    }
                    if (tt.K == WK.Class && tt.Cls!.Sym != null && Resolve(tt.Cls!, "op_IndexSet", [ix.Args[0], ix.Args[0]], instance: true) is { } set)
                    {
                        var tmp = FrameTempOrLocal(set.Params[1]);
                        StoreTemp(tmp, conv(set.Params[1]));
                        CallUser(set, ix.Target, [ix.Args[0], new TempRef(tmp)]);
                        return;
                    }
                    throw No("Store:index", tt.Name);
                }
        }
        throw No("Store:" + target.GetType().Name);
    }

    /// <summary>An already evaluated temp as an expression (an argument evaluated out of order).</summary>
    internal sealed class TempRef(Var v) : MCode("")
    {
        public Var V { get; } = v;
    }

    void StoreField(WField f, MExpr? target, Func<WT, Func<WT>> conv)
    {
        if (f.Literal != null) throw No("Store:literal", f.Name);
        if (f.IsStatic)
        {
            code.I32(f.Address);
            StoreAt(f.Type, conv(f.Type));
            return;
        }
        var value = conv(f.Type);
        if (target != null && target is not AirThis && Ty(target).IsRef)
        {
            // obj.f = v: the object is rooted while v is evaluated
            var tmp = FrameTemp(Ty(target));
            code.Get(Fp());
            FieldBase(target);
            code.Mem(Op.I32Store, (uint)tmp.Frame);
            code.Get(Fp());
            code.Mem(Op.I32Load, (uint)tmp.Frame);
        }
        else FieldBase(target);
        StoreAt(f.Type, value, (uint)f.Offset);
    }

    void For(MFor f)
    {
        scopes.Add(new(StringComparer.Ordinal));
        var t = f.VarType != null && f.VarType != "var" ? c.Parse(f.VarType, span) : Ty(f.Start);
        if (t.K == WK.Null || !t.IsNumeric) throw No("MFor:type", t.Name);
        var v = Declare(f.Var, t);
        StoreVar(v, () => Emit(f.Start, t));
        var brk = code.Block();
        var top = code.Loop();
        var cmp = f.Cmp switch
        {
            "<" => AirBinOp.Lt, "<=" => AirBinOp.Le, ">" => AirBinOp.Gt, ">=" => AirBinOp.Ge, "!=" => AirBinOp.Ne, "==" => AirBinOp.Eq,
            _ => throw No("MFor:cmp", f.Cmp),
        };
        var name = new AirLocalRef(f.Var);
        CondBool(new MBinary { Op = cmp, L = name, R = f.End });
        code.Emit(Op.I32Eqz);
        code.BrIf(brk);
        var cont = code.Block();
        loops.Add(new LoopCtx { Break = brk, Continue = cont, Finally = finallies.Count });
        Block(f.Body);
        loops.RemoveAt(loops.Count - 1);
        code.End(cont);
        Step(name, f.Step);
        code.Br(top);
        code.End(top);
        code.End(brk);
        scopes.RemoveAt(scopes.Count - 1);
    }

    void Step(MExpr v, AirForStep step)
    {
        switch (step.Op)
        {
            case "++": ExprStmt(new MUnary { Op = AirUnOp.Inc, E = v, Postfix = true }); return;
            case "--": ExprStmt(new MUnary { Op = AirUnOp.Dec, E = v, Postfix = true }); return;
            case "+=" or "-=" or "*=" or "<<=" or ">>=" or "=" when step.Value != null:
                Assign(new MAssign { Target = v, Op = step.Op, Value = step.Value });
                return;
        }
        throw No("MFor:step", step.Op);
    }

    /// <summary>`a, b = x, y`: every value into a temporary first (so `a, b = b, a` swaps), then the names.</summary>
    void Deconstruct(MDeconstruct dc)
    {
        if (dc.Value is not MTuple tu || tu.Items.Count != dc.Names.Count) throw No("MDeconstruct", string.Join(",", dc.Names));
        var temps = new List<(Var V, WT T)>();
        for (int i = 0; i < tu.Items.Count; i++)
        {
            var t = Ty(tu.Items[i]);
            var tmp = Declare("__dc" + i + "_" + code.Length, t);
            StoreVar(tmp, () => Emit(tu.Items[i], t));
            temps.Add((tmp, t));
        }
        for (int i = 0; i < dc.Names.Count; i++)
        {
            if (dc.Names[i] == "_") continue;
            if (dc.Declare) Declare(dc.Names[i], temps[i].T);
            Assign(new MAssign { Target = new AirLocalRef(dc.Names[i]), Op = "=", Value = new AirLocalRef(temps[i].V.Name) });
        }
    }

    /// <summary>`foreach` left by the lowering: an array (or string) walked by index.</summary>
    void Foreach(MForeach fe)
    {
        if (fe.Await) throw No("MForeach:await");
        var ct = Ty(fe.Coll);
        if (ct.K != WK.Array || fe.Vars.Count != 1) throw No("MForeach:" + ct.Name);
        scopes.Add(new(StringComparer.Ordinal));
        var arr = Declare("__fe_arr" + code.Length, ct);
        StoreVar(arr, () => Emit(fe.Coll, ct));
        var idx = Declare("__fe_i" + code.Length, WT.I32);
        var brk = code.Block();
        var top = code.Loop();
        LoadVar(idx); LoadVar(arr); code.Mem(Op.I32Load, 4); code.Emit(Op.I32GeS); code.BrIf(brk);
        scopes.Add(new(StringComparer.Ordinal));
        var ev = Declare(fe.Vars[0], ct.Elem!);
        StoreVar(ev, () => Raw(new MIndex { Target = new AirLocalRef(arr.Name), Args = [new AirLocalRef(idx.Name)] }));
        var cont = code.Block();
        loops.Add(new LoopCtx { Break = brk, Continue = cont, Finally = finallies.Count });
        Block(fe.Body);
        loops.RemoveAt(loops.Count - 1);
        code.End(cont);
        scopes.RemoveAt(scopes.Count - 1);
        LoadVar(idx); code.I32(1); code.Emit(Op.I32Add); code.Set(idx.Local);
        code.Br(top);
        code.End(top);
        code.End(brk);
        scopes.RemoveAt(scopes.Count - 1);
    }

    void Jump(bool brk)
    {
        if (loops.Count == 0) throw No(brk ? "MBreak:outside-loop" : "MContinue:outside-loop");
        var l = loops[^1];
        RunFinallies(l.Finally);
        code.Br(brk ? l.Break : l.Continue);
    }

    /// <summary>Inlines the finally bodies entered since depth <paramref name="downTo"/> (innermost first).</summary>
    void RunFinallies(int downTo)
    {
        var saved = finallies.ToList();
        for (int i = finallies.Count - 1; i >= downTo; i--)
        {
            var f = finallies[i];
            finallies.RemoveRange(i, finallies.Count - i);
            Block(f);
        }
        finallies.Clear();
        finallies.AddRange(saved);
    }

    void Return(MReturn r)
    {
        if (self.IsCtor && r.Value == null) { RunFinallies(0); Epilogue(); code.Emit(Op.Return); return; }
        if (r.Value == null)
        {
            RunFinallies(0);
            Epilogue();
            code.Emit(Op.Return);
            return;
        }
        var rt = self.Ret;
        if (rt.IsStruct)
        {
            StructInto(r.Value, () => code.Get(sretLocal), rt);
            RunFinallies(0);
            Epilogue();
            code.Emit(Op.Return);
            return;
        }
        if (rt.K == WK.Void) { ExprStmt(r.Value); RunFinallies(0); Epilogue(); code.Emit(Op.Return); return; }
        if (finallies.Count == 0 && (rt.Val != ValType.I32 || !rt.IsRef))
        {
            Emit(r.Value, rt);
            Epilogue();
            code.Emit(Op.Return);
            return;
        }
        // the value outlives the finally bodies (and, for a reference, the frame: the caller roots it)
        int tmp = Temp(rt.Val);
        Emit(r.Value, rt);
        code.Set(tmp);
        RunFinallies(0);
        Epilogue();
        code.Get(tmp);
        code.Emit(Op.Return);
    }

    void Throw(MThrow t)
    {
        if (t.Value == null || t.Value is MCode { Text: "" })
        {
            if (catches.Count == 0) throw No("MThrow:rethrow-outside-catch");
            LoadVar(catches[^1]);
        }
        else
        {
            var et = Emit(t.Value, null);
            if (et.K != WK.Class || !c.IsException(et.Cls!)) throw No("MThrow:type", et.Name);
        }
        c.EmitThrow(code);
    }

    /// <summary>try/catch/finally. The exception arrives on the tag's payload; the shadow stack pointer is reset to
    /// this function's at the handler (frames of the unwound callees are dropped).</summary>
    void Try(MTry tr)
    {
        if (c.Opt.PanicAbort)
        {
            // no handlers: the body, then the finally on the normal path
            Block(tr.Body);
            if (tr.Finally != null) Block(tr.Finally);
            return;
        }
        int savedSp = Temp(ValType.I32);
        code.GGet(c.Rt.Global("sp"));
        code.Set(savedSp);
        WasmLabel? finEnd = null, finEsc = null, finTry = null;
        if (tr.Finally != null)
        {
            finEnd = code.Block();
            finEsc = code.Block(ValType.I32);
            finTry = code.TryTable(null, c.Exn, finEsc);
            finallies.Add(tr.Finally);
        }
        if (tr.Catches.Count > 0)
        {
            var end = code.Block();
            var h = code.Block(ValType.I32);
            var t = code.TryTable(null, c.Exn, h);
            Block(tr.Body);
            code.End(t);
            code.Br(end);
            code.End(h);
            // the exception: rooted in a frame slot while the handler runs
            var ev = FrameTemp(c.RuntimeClass("System.Exception").Type);
            int e = Temp(ValType.I32);
            code.Set(e);
            code.Get(savedSp);
            code.GSet(c.Rt.Global("sp"));
            code.Get(Fp()); code.Get(e); code.Mem(Op.I32Store, (uint)ev.Frame);
            catches.Add(ev);
            foreach (var ct in tr.Catches)
            {
                var cls = c.Parse(ct.TypeCs, span);
                if (cls.K != WK.Class || !c.IsException(cls.Cls!)) throw No("MCatch:type", ct.TypeCs);
                bool all = cls.Cls!.Name == "System.Exception";
                WasmLabel? test = null;
                if (!all)
                {
                    code.Get(e);
                    TypeTest(cls.Cls!);
                    test = code.If();
                }
                scopes.Add(new(StringComparer.Ordinal));
                if (ct.Name != null)
                {
                    var nv = Declare(ct.Name, cls);
                    StoreVar(nv, () => { code.Get(e); return cls; });
                }
                Stmts(ct.Body);
                scopes.RemoveAt(scopes.Count - 1);
                code.Br(end);
                if (test != null) code.End(test);
                if (all) break;
            }
            catches.RemoveAt(catches.Count - 1);
            // no handler matched: rethrow
            code.Get(e);
            c.EmitThrow(code);
            code.End(end);
        }
        else Block(tr.Body);
        if (tr.Finally != null)
        {
            finallies.RemoveAt(finallies.Count - 1);
            code.End(finTry!);
            Block(tr.Finally);
            code.Br(finEnd!);
            code.End(finEsc!);
            // escaping exception: finally, then on
            var ev = FrameTemp(c.RuntimeClass("System.Exception").Type);
            int e = Temp(ValType.I32);
            code.Set(e);
            code.Get(savedSp);
            code.GSet(c.Rt.Global("sp"));
            code.Get(Fp()); code.Get(e); code.Mem(Op.I32Store, (uint)ev.Frame);
            Block(tr.Finally);
            code.Get(Fp()); code.Mem(Op.I32Load, (uint)ev.Frame);
            c.EmitThrow(code);
            code.End(finEnd!);
        }
    }

    void Switch(AirSwitch sw)
    {
        var vt = Under(Ty(sw.Value)).Widened;
        if (!vt.IsInt) throw No("AirSwitch:type", vt.Name);
        var end = code.Block();
        var dflt = code.Block();
        var cases = new List<WasmLabel>();
        for (int i = 0; i < sw.Cases.Count; i++) cases.Add(code.Block());
        Emit(sw.Value, vt);
        if (vt.Is64)
        {
            int v = Temp(ValType.I64);
            code.I64(sw.Min);
            code.Emit(Op.I64Sub);
            code.Tee(v);
            code.I64(sw.Table.Count);
            code.Emit(Op.I64GeU);
            code.BrIf(dflt);
            code.Get(v);
            code.Emit(Op.I32WrapI64);
        }
        else if (sw.Min != 0)
        {
            code.I32(unchecked((int)sw.Min));
            code.Emit(Op.I32Sub);
        }
        var table = sw.Table.Select(k => k < 0 ? dflt : cases[k]).ToList();
        code.BrTable(table, dflt);
        for (int i = sw.Cases.Count - 1; i >= 0; i--)
        {
            code.End(cases[i]);
            Block(sw.Cases[i]);
            code.Br(end);
        }
        code.End(dflt);
        if (sw.Default != null) Block(sw.Default);
        code.End(end);
    }
}
