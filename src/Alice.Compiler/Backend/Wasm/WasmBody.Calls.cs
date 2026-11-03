using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Wasm;

// Calls (Alice methods, virtual dispatch, runtime intrinsics for Prelude/Math/strings/arrays/lists), members,
// indexing, construction, strings and interpolation.
public sealed partial class WasmBody
{
    // ------------------------------------------------------------------ operands

    /// <summary>Pushes operands in order. When a reference (or struct address) is followed by an operand that can
    /// allocate, every operand is evaluated into a temp first (references in frame slots, so the collector sees
    /// them) and then pushed: nothing the collector cannot see sits on the wasm stack across an allocation.</summary>
    void Operands(IReadOnlyList<(Func<WT> Emit, WT T, bool MayAlloc)> ops)
    {
        bool spill = false;
        for (int i = 0; i < ops.Count && !spill; i++)
            if (ops[i].T.IsRef || ops[i].T.IsStruct || ops[i].T.K == WK.Func)
                for (int j = i + 1; j < ops.Count; j++)
                    if (ops[j].MayAlloc) { spill = true; break; }
        if (!spill)
        {
            foreach (var o in ops) o.Emit();
            return;
        }
        var saved = new List<(Var? Frame, int Local, WT T)>();
        foreach (var o in ops)
        {
            if (o.T.IsRef || o.T.K == WK.Func)
            {
                var tmp = FrameTemp(WT.Str);
                int l = Temp(ValType.I32);
                o.Emit();
                code.Set(l);
                code.Get(Fp()); code.Get(l); code.Mem(Op.I32Store, (uint)tmp.Frame);
                saved.Add((tmp, -1, o.T));
            }
            else if (o.T.IsStruct)
            {
                // a copy: the source may be overwritten by a later operand
                var tmp = FrameTemp(o.T);
                FrameAddr(tmp.Frame);
                o.Emit();
                code.I32(o.T.Size);
                code.Call(c.Rt.Func("memcpy"));
                saved.Add((tmp, -1, o.T));
            }
            else
            {
                int l = Temp(o.T.Val);
                o.Emit();
                code.Set(l);
                saved.Add((null, l, o.T));
            }
        }
        foreach (var (f, l, t) in saved)
        {
            if (f == null) code.Get(l);
            else if (t.IsStruct) FrameAddr(f.Frame);
            else { code.Get(Fp()); code.Mem(Op.I32Load, (uint)f.Frame); }
        }
    }

    (Func<WT> Emit, WT T, bool MayAlloc) Opnd(MExpr e, WT want) => (() => Emit(e, want), want, MayAlloc(e));

    // ------------------------------------------------------------------ calls

    WT Call(MCall call, Action? sretDst = null)
    {
        Seen("MCall:" + call.Name);
        if (call.Target == null && call.Name is "unchecked" or "checked" && call.Args.Count == 1)
        {
            if (call.Name == "unchecked") uncheckedDepth++;
            int saved = uncheckedDepth;
            if (call.Name == "checked") uncheckedDepth = 0;
            try { return Raw(call.Args[0]); }
            finally
            {
                if (call.Name == "unchecked") uncheckedDepth--;
                else uncheckedDepth = saved;
            }
        }
        if (call.Safe) throw No("MCall:safe-navigation", call.Name);
        if (call.Args.Any(a => a is MArg)) throw No("MCall:named-or-ref-argument", call.Name);
        if (call.Target == null)
        {
            var m = Resolve(owner, call.Name, call.Args, instance: !isStatic) ?? (owner != c.ProgramC ? Resolve(c.ProgramC, call.Name, call.Args, instance: false) : null);
            if (m == null && lambda != null) m = Resolve(lambda.Outer.owner, call.Name, call.Args, instance: !lambda.Outer.isStatic);
            if (m != null) return CallUser(m, m.IsStatic ? null : new AirThis(), call.Args, sretDst);
            return Intrinsic(null, "Prelude", call);
        }
        var tt = Ty(call.Target);
        if (tt.K == WK.TypeRef)
        {
            if (tt.Cls is { } cls)
            {
                var m = Resolve(cls, call.Name, call.Args, instance: false);
                if (m != null) return CallUser(m, null, call.Args, sretDst);
                if (cls.IsAdt || cls.IsVariant || cls.Sym != null) throw No("MCall:static", cls.Name + "." + call.Name);
            }
            return Intrinsic(null, TypeName(tt.Name), call);
        }
        if (tt.K is WK.Class or WK.Struct && tt.Cls!.Sym != null)
        {
            var m = Resolve(tt.Cls!, call.Name, call.Args, instance: true);
            if (m != null) return CallUser(m, call.Target, call.Args, sretDst, viaBase: call.Target is AirBase);
        }
        return Intrinsic(call.Target, null, call);
    }

    static string TypeName(string n) => n.StartsWith("global::") ? n[8..] : n;

    /// <summary>The best user method named <paramref name="name"/> for the arguments (arity with defaults and
    /// splats, then argument types).</summary>
    WMethod? Resolve(WClass k, string name, List<MExpr> args, bool instance)
    {
        var cands = k.FindMethods(name).Where(m => (instance || m.IsStatic) && Arity(m, args.Count)).ToList();
        if (cands.Count == 0) return null;
        if (cands.Count == 1) return cands[0];
        var ts = args.Select(Ty).ToList();
        WMethod? best = null;
        int bestScore = -1;
        foreach (var m in cands)
        {
            int score = 0;
            bool ok = true;
            for (int i = 0; i < ts.Count && i < m.Params.Count; i++)
            {
                var p = m.Params[i];
                if (ts[i].Same(p)) score += 3;
                else if (Assignable(ts[i], p, args[i])) score += 1;
                else { ok = false; break; }
            }
            if (ok && score > bestScore) { best = m; bestScore = score; }
        }
        return best ?? cands[0];
    }

    static bool Arity(WMethod m, int n)
    {
        int required = m.Defaults.Length == m.Params.Count ? m.Params.Count - m.Defaults.Count(d => d != null) : m.Params.Count;
        bool splat = m.Sym != null && m.Sym.Params.Count > 0 && m.Sym.Params[^1].Splat;
        return n >= required && (n <= m.Params.Count || splat);
    }

    bool Assignable(WT from, WT to, MExpr e)
    {
        if (from.Same(to)) return true;
        if (from.K == WK.Null) return to.IsRef || to.K == WK.Func;
        if (from.K == WK.Class && to.K == WK.Class) return from.Cls!.IsSubclassOf(to.Cls!);
        if (from.IsNumeric && to.IsNumeric)
        {
            if (Literal(e, out var v, out _) && v != null && Fits(v, to)) return true;
            return Implicit(from, to);
        }
        return false;
    }

    static bool Implicit(WT f, WT t)
    {
        if (f.K == WK.Enum || t.K == WK.Enum) return false;
        if (t.IsFloat) return !(f.K == WK.F64 && t.K == WK.F32);
        if (f.IsFloat) return false;
        var (flo, fhi) = Range(f);
        var (tlo, thi) = Range(t);
        if (t.K == WK.U64) return f.IsUnsigned;
        if (f.K == WK.U64) return false;
        return tlo <= flo && fhi <= thi;
    }

    /// <summary>A call of an Alice method: [sret][receiver] arguments (defaults filled, a splat collected), then
    /// the function or the class's dispatcher.</summary>
    WT CallUser(WMethod m, MExpr? receiver, List<MExpr> args, Action? sretDst = null, bool viaBase = false)
    {
        Var? sret = null;
        if (m.Ret.IsStruct)
        {
            if (sretDst != null) sretDst();
            else { sret = FrameTemp(m.Ret); FrameAddr(sret.Frame); }
        }
        var ops = new List<(Func<WT>, WT, bool)>();
        if (!m.IsStatic)
        {
            // a struct receiver is its storage (methods may mutate it), not a copy
            var rt = m.Owner.IsStruct ? WT.I32 : m.Owner.Type;
            if (receiver is AirThis or null && lambda == null)
            {
                ops.Add((() => { var t = This(false); return t; }, rt, false));
            }
            else
            {
                var re = receiver ?? new AirThis();
                ops.Add((() => { var t = Emit(re, null); if (t.K == WK.Class && !m.Owner.IsStruct) NullCheck(); return t; }, rt, MayAlloc(re)));
            }
        }
        bool splat = m.Sym != null && m.Sym.Params.Count > 0 && m.Sym.Params[^1].Splat;
        int fixedN = splat ? m.Params.Count - 1 : m.Params.Count;
        for (int i = 0; i < fixedN; i++)
        {
            var p = m.Params[i];
            if (i < args.Count) ops.Add(Opnd(args[i], p));
            else if (m.Defaults.Length > i && m.Defaults[i] is { } dv) ops.Add(Opnd(dv, p));
            else throw No("MCall:missing-argument", m.ToString());
        }
        if (splat)
        {
            var rest = args.Skip(fixedN).ToList();
            var at = m.Params[^1];
            if (rest.Count == 1 && Ty(rest[0]).Same(at)) ops.Add(Opnd(rest[0], at));
            else ops.Add((() => NewArrayItems(at.Elem!, rest), at, true));
        }
        Operands(ops);
        bool virt = m.IsVirtual && !viaBase && !m.Owner.IsStruct;
        if (virt)
        {
            var recvType = receiver == null || receiver is AirThis ? owner : (Ty(receiver).Cls ?? m.Owner);
            if (!recvType.IsSubclassOf(m.Owner)) recvType = m.Owner;
            code.Call(c.Dispatcher(m, recvType.IsInterface ? recvType : RootFor(recvType, m)));
        }
        else code.Call(m.Func);
        if (m.Ret.IsStruct)
        {
            if (sret != null) FrameAddr(sret.Frame);
            else if (sretDst != null) return WT.Void;
        }
        return m.Ret;
    }

    /// <summary>The class whose subtree a virtual call can reach: the static receiver class.</summary>
    static WClass RootFor(WClass recv, WMethod m) => recv;

    void NullCheck()
    {
        int t = Temp(ValType.I32);
        code.Tee(t);
        code.Emit(Op.I32Eqz);
        ThrowIf(AirToWasm.ErrNull);
        code.Get(t);
    }

    // ------------------------------------------------------------------ intrinsics

    /// <summary>Calls of runtime functions: Prelude (puts, str, raise...), Math/MathF, Branchless, and methods of
    /// strings, numbers, arrays, lists, string builders and exceptions.</summary>
    WT Intrinsic(MExpr? target, string? staticType, MCall call)
    {
        string n = call.Name;
        var a = call.Args;
        if (staticType != null)
        {
            string st = staticType switch { "System.Math" or "Math" => "Math", "System.MathF" or "MathF" => "MathF", _ => staticType };
            switch (st, n, a.Count)
            {
                case ("System.Diagnostics.Stopwatch", "StartNew" or "start_new", 0):
                    code.I32(1); code.Call(c.Wasi.ClockFunc); return WT.Stopwatch;
                case ("System.Convert", "ToInt64" or "ToInt32", 1) when Ty(a[0]).IsFloat:
                    {
                        // round half to even, as Convert does (out-of-range saturates instead of OverflowException)
                        Emit(a[0], WT.F64); code.Emit(Op.F64Nearest);
                        if (n == "ToInt64") { code.Prefixed(Op.I64TruncSatF64S); return WT.I64; }
                        code.Prefixed(Op.I32TruncSatF64S); return WT.I32;
                    }
                case ("System.Convert", "ToInt64", 1) when Ty(a[0]).IsInt: Emit(a[0], WT.I64); return WT.I64;
                case ("string" or "System.String", "Equals", 2):
                    Operands([Opnd(a[0], WT.Str), Opnd(a[1], WT.Str)]); code.Call(c.Rt.Func("str_eq")); return WT.Bool;
                case ("System.Object" or "object", "ReferenceEquals", 2):
                    {
                        var lt = Ty(a[0]); var rt = Ty(a[1]);
                        if (!lt.IsRef && lt.K != WK.Null || !rt.IsRef && rt.K != WK.Null) throw No("Intrinsic:ReferenceEquals:value", lt.Name);
                        Emit(a[0], lt); Emit(a[1], rt); code.Emit(Op.I32Eq); return WT.Bool;
                    }
                case ("System.GC", "GetTotalAllocatedBytes", 0 or 1):
                    // bytes this module's allocator handed out (headers and size-class rounding included)
                    if (a.Count == 1) { Emit(a[0], WT.Bool); code.Emit(Op.Drop); }
                    code.Call(c.Rt.Func("total_allocated")); return WT.I64;
                case ("System.GC", "GetTotalPauseDuration", 0):
                    // pauses are not timed yet: zero (benchmarks/wasm/README.md)
                    code.I64(0); return WT.TimeSpan;
                case ("System.GC", "Collect", 0): code.Call(c.Rt.Func("gc_collect")); return WT.Void;
                case ("System.GC", "CollectionCount", 1):
                    // one generation: every collection counts for every generation
                    Emit(a[0], WT.I32); code.Emit(Op.Drop); code.Call(c.Rt.Func("gc_count_get")); return WT.I32;
                case ("System.Array", "Sort", 1):
                    {
                        var at = Ty(a[0]);
                        string? sk = at.K == WK.Array ? at.Elem!.K switch { WK.I32 => "i32", WK.U32 => "u32", WK.I64 => "i64", WK.U64 => "u64", WK.F64 => "f64", WK.F32 => "f32", _ => null } : null;
                        if (sk == null) throw No("Intrinsic:System.Array.Sort", at.Name);
                        var m = c.RtAl("sort_" + sk) ?? throw No("Runtime:sort_" + sk);
                        Emit(a[0], at);
                        code.Call(m.Func);
                        return WT.Void;
                    }
                case ("Prelude", "puts", 0): code.I32(c.Literal("")); code.Call(c.Rt.Func("puts")); return WT.Void;
                case ("Prelude", "puts" or "print", 1):
                    {
                        var t = Ty(a[0]);
                        if (t.IsStruct && t.Cls!.IsRecord) { Emit(a[0], t); ToStr(t, csharp: false); }
                        else { Emit(a[0], t); ToStr(t, csharp: false); }
                        code.Call(c.Rt.Func(n == "puts" ? "puts" : "print"));
                        return WT.Void;
                    }
                case ("Prelude", "puts" or "print", _):
                    foreach (var x in a)
                    {
                        var t = Emit(x, null);
                        ToStr(t, csharp: false);
                        code.Call(c.Rt.Func(n == "puts" ? "puts" : "print"));
                    }
                    return WT.Void;
                case ("Prelude", "p", 1):
                    {
                        var t = Ty(a[0]);
                        var v = FrameTempOrLocal(t);
                        StoreTemp(v, () => Emit(a[0], t));
                        LoadTemp(v);
                        Inspect(t);
                        code.Call(c.Rt.Func("puts"));
                        LoadTemp(v);
                        return t;
                    }
                case ("Prelude", "str" or "to_s", 1): { var t = Emit(a[0], null); ToStr(t, csharp: false); return WT.Str; }
                case ("Prelude", "inspect", 1): { var t = Emit(a[0], null); Inspect(t); return WT.Str; }
                case ("Prelude", "raise", 1):
                    {
                        var t = Ty(a[0]);
                        if (t.K == WK.Str) { Emit(a[0], WT.Str); code.Call(c.NewException(c.RuntimeClass("System.Exception"))); return c.RuntimeClass("System.Exception").Type; }
                        return Emit(a[0], null);
                    }
                case ("Prelude", "floor_mod_pos", 2):
                    {
                        var t = Promote(Ty(a[0]), Ty(a[1]), a[0], a[1]);
                        int d = Temp(t.Val), r = Temp(t.Val);
                        DivRem(new MBinary { Op = AirBinOp.Mod, L = a[0], R = a[1] }, t);
                        code.Set(r);
                        Emit(a[1], t);
                        code.Set(d);
                        code.Get(r);
                        if (t.Is64) { code.Get(r); code.I64(63); code.Emit(Op.I64ShrS); code.Get(d); code.Emit(Op.I64And); code.Emit(Op.I64Add); }
                        else { code.Get(r); code.I32(31); code.Emit(Op.I32ShrS); code.Get(d); code.Emit(Op.I32And); code.Emit(Op.I32Add); }
                        return t;
                    }
                case ("Prelude", "floor_mod", 2):
                    {
                        // Ruby modulo: the sign of the divisor
                        var t = Promote(Ty(a[0]), Ty(a[1]), a[0], a[1]);
                        int d = Temp(t.Val), r = Temp(t.Val);
                        Emit(a[1], t);
                        code.Set(d);
                        DivRem(new MBinary { Op = AirBinOp.Mod, L = a[0], R = new TempRef(new Var { T = t, Local = d }) }, t);
                        code.Tee(r);
                        bool w = t.Is64;
                        if (w) { code.I64(0); code.Emit(Op.I64Ne); code.Get(r); code.Get(d); code.Emit(Op.I64Xor); code.I64(0); code.Emit(Op.I64LtS); }
                        else { code.I32(0); code.Emit(Op.I32Ne); code.Get(r); code.Get(d); code.Emit(Op.I32Xor); code.I32(0); code.Emit(Op.I32LtS); }
                        code.Emit(Op.I32And);
                        var adj = code.If(t.Val);
                        code.Get(r); code.Get(d); code.Emit(w ? Op.I64Add : Op.I32Add);
                        code.Else();
                        code.Get(r);
                        code.End(adj);
                        return t;
                    }
                case ("Prelude", "fma", 3):
                    {
                        var m = c.RtAl("fma") ?? throw No("Runtime:fma");
                        Emit(a[0], WT.F64); Emit(a[1], WT.F64); Emit(a[2], WT.F64);
                        code.Call(m.Func);
                        return WT.F64;
                    }
                case ("Prelude", "floor_div" or "floor_div_pos", 2):
                    {
                        // Ruby division: the quotient rounds toward negative infinity
                        var t = Promote(Ty(a[0]), Ty(a[1]), a[0], a[1]);
                        if (t.IsFloat) { Emit(a[0], t); Emit(a[1], t); code.Emit(t.K == WK.F64 ? Op.F64Div : Op.F32Div); code.Emit(t.K == WK.F64 ? Op.F64Floor : Op.F32Floor); return t; }
                        bool w = t.Is64;
                        int x = Temp(t.Val), d = Temp(t.Val), q = Temp(t.Val);
                        Emit(a[0], t); code.Set(x);
                        Emit(a[1], t); code.Set(d);
                        var xv = new TempRef(new Var { T = t, Local = x });
                        var dv = new TempRef(new Var { T = t, Local = d });
                        DivRem(new MBinary { Op = AirBinOp.Div, L = xv, R = dv }, t);
                        code.Set(q);
                        if (t.IsUnsigned) { code.Get(q); return t; }
                        // x != q * d && (x ^ d) < 0
                        code.Get(q);
                        code.Get(q); code.Get(d); code.Emit(w ? Op.I64Mul : Op.I32Mul); code.Get(x); code.Emit(w ? Op.I64Ne : Op.I32Ne);
                        code.Get(x); code.Get(d); code.Emit(w ? Op.I64Xor : Op.I32Xor);
                        if (w) { code.I64(0); code.Emit(Op.I64LtS); } else { code.I32(0); code.Emit(Op.I32LtS); }
                        code.Emit(Op.I32And);
                        if (w) code.Emit(Op.I64ExtendI32U);
                        code.Emit(w ? Op.I64Sub : Op.I32Sub);
                        return t;
                    }
                case ("System.BitConverter", "DoubleToInt64Bits", 1): Emit(a[0], WT.F64); code.Emit(Op.I64ReinterpretF64); return WT.I64;
                case ("System.BitConverter", "Int64BitsToDouble", 1): Emit(a[0], WT.I64); code.Emit(Op.F64ReinterpretI64); return WT.F64;
                case ("System.BitConverter", "SingleToInt32Bits", 1): Emit(a[0], WT.F32); code.Emit(Op.I32ReinterpretF32); return WT.I32;
                case ("System.BitConverter", "Int32BitsToSingle", 1): Emit(a[0], WT.I32); code.Emit(Op.F32ReinterpretI32); return WT.F32;
                case ("Prelude", "append_op", 2):
                    {
                        var lt = Ty(a[0]);
                        if (lt.K != WK.List) throw No("Prelude.append_op", lt.Name);
                        Operands([Opnd(a[0], lt), Opnd(a[1], lt.Elem!)]);
                        code.Call(c.ListAdd(lt.Elem!, returnList: true));
                        return lt;
                    }
                case ("Prelude", "exit", 0 or 1):
                    if (a.Count == 1) Emit(a[0], WT.I32); else code.I32(0);
                    code.Call(c.Rt.Func("exit"));
                    return WT.Void;
                case ("Math" or "MathF", "Sqrt", 1): return F1(a[0], st, Op.F64Sqrt, Op.F32Sqrt);
                case ("Math" or "MathF", "Floor", 1): return F1(a[0], st, Op.F64Floor, Op.F32Floor);
                case ("Math" or "MathF", "Ceiling", 1): return F1(a[0], st, Op.F64Ceil, Op.F32Ceil);
                case ("Math" or "MathF", "Truncate", 1): return F1(a[0], st, Op.F64Trunc, Op.F32Trunc);
                case ("Math" or "MathF", "Round", 1): return F1(a[0], st, Op.F64Nearest, Op.F32Nearest);
                case ("Math" or "MathF", "Abs", 1):
                    {
                        var t = Ty(a[0]);
                        if (t.IsFloat || st == "MathF") return F1(a[0], st, Op.F64Abs, Op.F32Abs);
                        return IntAbs(a[0], t);
                    }
                case ("Math" or "MathF", "Min" or "Max", 2):
                    {
                        var t = st == "MathF" ? WT.F32 : Promote(Ty(a[0]), Ty(a[1]), a[0], a[1]);
                        if (t.IsFloat)
                        {
                            Emit(a[0], t); Emit(a[1], t);
                            code.Emit(t.K == WK.F64 ? (n == "Min" ? Op.F64Min : Op.F64Max) : (n == "Min" ? Op.F32Min : Op.F32Max));
                            return t;
                        }
                        int x = Temp(t.Val), y = Temp(t.Val);
                        Emit(a[0], t); code.Tee(x);
                        Emit(a[1], t); code.Tee(y);
                        code.Get(x); code.Get(y);
                        code.Emit(CompareOp(n == "Min" ? AirBinOp.Lt : AirBinOp.Gt, t));
                        code.Emit(Op.Select);
                        return t;
                    }
                case ("Math" or "MathF", "FusedMultiplyAdd", 3):
                    {
                        // wasm core has no fma: a*b+c rounds twice (documented last-ulp difference, ADR 0034)
                        var t = st == "MathF" ? WT.F32 : WT.F64;
                        Emit(a[0], t); Emit(a[1], t); code.Emit(t.K == WK.F64 ? Op.F64Mul : Op.F32Mul);
                        Emit(a[2], t); code.Emit(t.K == WK.F64 ? Op.F64Add : Op.F32Add);
                        return t;
                    }
                case ("Math" or "MathF", "Sin" or "Cos" or "Tan" or "Exp" or "Log" or "Log2" or "Log10" or "Atan" or "Asin" or "Acos" or "Sinh" or "Cosh" or "Tanh" or "Cbrt", 1):
                    return Libm(n.ToLowerInvariant(), a, st == "MathF");
                case ("Math" or "MathF", "Pow" or "Atan2", 2):
                    return Libm(n.ToLowerInvariant(), a, st == "MathF");
                case ("Alice.Runtime.Branchless", _, 3):
                    {
                        var t = n switch { "U32" => WT.U32, "I32" => WT.I32, "I64" => WT.I64, "U64" => WT.U64, "F64" => WT.F64, "F32" => WT.F32, _ => throw No("Branchless." + n) };
                        Emit(a[1], t); Emit(a[2], t); Emit(a[0], WT.Bool);
                        code.Emit(Op.Select);
                        return t;
                    }
            }
            throw No("Intrinsic:" + st + "." + n, a.Count.ToString());
        }
        var tt = Ty(target!);
        switch (tt.K)
        {
            case WK.Str: return StrMethod(target!, n, a);
            case WK.Array: return ArrayMethod(target!, tt, n, a);
            case WK.List: return ListMethod(target!, tt, n, a);
            case WK.Dict: return DictMethod(target!, tt, n, a);
            case WK.Builder: return BuilderMethod(target!, n, a);
        }
        if (tt.IsNumeric || tt.K is WK.Char or WK.Bool) return NumberMethod(target!, tt, n, a);
        if (tt.K == WK.Struct && c.IsListEnumerator(tt.Cls!)) return EnumeratorMethod(target!, tt, n, a);
        if (tt.K == WK.Class && n == "ToString" && a.Count == 0) { Emit(target!, tt); ToStr(tt, csharp: true); return WT.Str; }
        if (tt.K == WK.Class && c.IsException(tt.Cls!) && n == "ToString") { Emit(target!, tt); ToStr(tt, csharp: true); return WT.Str; }
        if (tt.K == WK.Func && n == "Invoke") return Invoke(new MInvoke { Target = target!, Args = a });
        throw No("Intrinsic:" + tt.Name + "." + n, a.Count.ToString());
    }

    WT F1(MExpr x, string st, byte op64, byte op32)
    {
        var t = st == "MathF" ? WT.F32 : WT.F64;
        Emit(x, t);
        code.Emit(t.K == WK.F64 ? op64 : op32);
        return t;
    }

    WT IntAbs(MExpr x, WT t)
    {
        t = Under(t).Widened;
        int v = Temp(t.Val);
        Emit(x, t);
        code.Set(v);
        if (!t.IsUnsigned)
        {
            // Math.Abs(MinValue) is OverflowException
            code.Get(v);
            if (t.Is64) { code.I64(long.MinValue); code.Emit(Op.I64Eq); } else { code.I32(int.MinValue); code.Emit(Op.I32Eq); }
            ThrowIf(AirToWasm.ErrOverflow);
            if (t.Is64) code.I64(0); else code.I32(0);
            code.Get(v);
            code.Emit(t.Is64 ? Op.I64Sub : Op.I32Sub);
            code.Get(v);
            code.Get(v);
            if (t.Is64) { code.I64(0); code.Emit(Op.I64LtS); } else { code.I32(0); code.Emit(Op.I32LtS); }
            code.Emit(Op.Select);
        }
        else code.Get(v);
        return t;
    }

    /// <summary>libm functions from the runtime (rt.al, a musl port); MathF goes through double.</summary>
    WT Libm(string name, List<MExpr> a, bool single)
    {
        var m = c.RtAl("m_" + name) ?? throw No("Intrinsic:libm." + name);
        foreach (var x in a) Emit(x, WT.F64);
        code.Call(m.Func);
        if (single) { code.Emit(Op.F32DemoteF64); return WT.F32; }
        return WT.F64;
    }

    WT NumberMethod(MExpr target, WT t, string n, List<MExpr> a)
    {
        if (n.EndsWith("_b") && a.Count == 0)
        {
            // CreateTruncating: integers wrap, floats saturate
            WT? tc = n switch { "to_i_b" or "to_i32_b" => WT.I32, "to_u32_b" => WT.U32, "to_i64_b" => WT.I64, "to_u64_b" => WT.U64, "to_u8_b" => WT.U8, "to_i8_b" => WT.I8, "to_i16_b" => WT.I16, "to_u16_b" => WT.U16, _ => null };
            if (tc != null) { Emit(target, t); Convert(t, tc, explicitCast: true, chk: false); return tc; }
        }
        WT? conv = n switch
        {
            "to_i" or "to_i32" => WT.I32, "to_u32" => WT.U32, "to_i64" => WT.I64, "to_u64" => WT.U64, "to_u8" => WT.U8, "to_i8" => WT.I8,
            "to_i16" => WT.I16, "to_u16" => WT.U16, "to_f" or "to_f64" => WT.F64, "to_f32" => WT.F32,
            _ => null,
        };
        if (conv != null && a.Count == 0)
        {
            Emit(target, t);
            // CreateChecked: overflow and NaN are OverflowException; floats truncate toward zero
            Convert(t, conv, explicitCast: true, chk: true);
            return conv;
        }
        switch (n, a.Count)
        {
            case ("to_s" or "ToString", 0): Emit(target, t); ToStr(t, csharp: n == "ToString"); return WT.Str;
            case ("odd_q" or "even_q", 0) when t.IsInt:
                {
                    var w = Under(t).Widened;
                    Emit(target, w);
                    if (w.Is64) { code.I64(1); code.Emit(Op.I64And); code.Emit(Op.I32WrapI64); } else { code.I32(1); code.Emit(Op.I32And); }
                    if (n == "even_q") code.Emit(Op.I32Eqz);
                    return WT.Bool;
                }
            case ("zero_q", 0): Binary(new MBinary { Op = AirBinOp.Eq, L = target, R = AirConst.Int(0) }); return WT.Bool;
            case ("abs", 0):
                if (t.IsFloat) { Emit(target, t); code.Emit(t.K == WK.F64 ? Op.F64Abs : Op.F32Abs); return t; }
                return IntAbs(target, t);
            case ("sqrt", 0) when t.IsFloat: Emit(target, t); code.Emit(t.K == WK.F64 ? Op.F64Sqrt : Op.F32Sqrt); return t;
            case ("floor", 0) when t.IsFloat: Emit(target, t); code.Emit(t.K == WK.F64 ? Op.F64Floor : Op.F32Floor); return t;
            case ("round", 0 or 1) when t.K == WK.F64:
                {
                    var m = c.RtAl(a.Count == 0 ? "round_away" : "round_digits") ?? throw No("Runtime:round");
                    Emit(target, t);
                    if (a.Count == 1) Emit(a[0], WT.I32);
                    code.Call(m.Func);
                    return t;
                }
            case ("ceil", 0) when t.IsFloat: Emit(target, t); code.Emit(t.K == WK.F64 ? Op.F64Ceil : Op.F32Ceil); return t;
            case ("Equals", 1): return Binary(new MBinary { Op = AirBinOp.Eq, L = target, R = a[0] });
            case ("CompareTo", 1):
                {
                    Emit(target, t); int x = Temp(t.Val); code.Set(x);
                    Emit(a[0], t); int y = Temp(t.Val); code.Set(y);
                    code.Get(x); code.Get(y); code.Emit(CompareOp(AirBinOp.Gt, t));
                    code.Get(x); code.Get(y); code.Emit(CompareOp(AirBinOp.Lt, t));
                    code.Emit(Op.I32Sub);
                    return WT.I32;
                }
            case ("GetHashCode", 0):
                Emit(target, t);
                if (t.Is64) { int x = Temp(ValType.I64); code.Tee(x); code.Emit(Op.I32WrapI64); code.Get(x); code.I64(32); code.Emit(Op.I64ShrU); code.Emit(Op.I32WrapI64); code.Emit(Op.I32Xor); }
                else if (t.IsFloat) throw No("Intrinsic:float.GetHashCode");
                return WT.I32;
        }
        throw No("Intrinsic:" + t.Name + "." + n, a.Count.ToString());
    }

    WT StrMethod(MExpr target, string n, List<MExpr> a)
    {
        switch (n, a.Count)
        {
            case ("to_i" or "to_i32", 0): Emit(target, WT.Str); code.I32(32); code.Call(c.Rt.Func("parse_i64")); code.Emit(Op.I32WrapI64); return WT.I32;
            case ("to_i64", 0): Emit(target, WT.Str); code.I32(64); code.Call(c.Rt.Func("parse_i64")); return WT.I64;
            case ("upcase" or "ToUpper" or "ToUpperInvariant", 0): Emit(target, WT.Str); code.I32(1); code.Call(c.Rt.Func("str_case")); return WT.Str;
            case ("downcase" or "ToLower" or "ToLowerInvariant", 0): Emit(target, WT.Str); code.I32(0); code.Call(c.Rt.Func("str_case")); return WT.Str;
            case ("size" or "length", 0): Emit(target, WT.Str); code.Call(c.Rt.Func("str_len")); return WT.I32;
            case ("to_s" or "ToString", 0): return Emit(target, WT.Str);
            case ("Equals", 1): Operands([Opnd(target, WT.Str), Opnd(a[0], WT.Str)]); code.Call(c.Rt.Func("str_eq")); return WT.Bool;
            case ("CompareTo" or "cmp", 1): Operands([Opnd(target, WT.Str), Opnd(a[0], WT.Str)]); code.Call(c.Rt.Func("str_cmp")); return WT.I32;
            case ("GetHashCode", 0): Emit(target, WT.Str); code.Call(c.Rt.Func("str_hash")); return WT.I32;
            case ("Substring", 1):
                {
                    var s = FrameTempOrLocal(WT.Str);
                    StoreTemp(s, () => Emit(target, WT.Str));
                    int from = Temp(ValType.I32);
                    Emit(a[0], WT.I32); code.Set(from);
                    LoadTemp(s); code.Get(from);
                    LoadTemp(s); code.Call(c.Rt.Func("str_len")); code.Get(from); code.Emit(Op.I32Sub);
                    code.Call(c.Rt.Func("str_sub"));
                    return WT.Str;
                }
            case ("Substring", 2):
                Operands([Opnd(target, WT.Str), Opnd(a[0], WT.I32), Opnd(a[1], WT.I32)]);
                code.Call(c.Rt.Func("str_sub"));
                return WT.Str;
        }
        throw No("Intrinsic:string." + n, a.Count.ToString());
    }

    // ------------------------------------------------------------------ temps

    Var FrameTempOrLocal(WT t) => t.IsRef || t.IsStruct || t.K == WK.Func ? FrameTemp(t) : new Var { T = t, Local = Temp(t.Val) };

    void StoreTemp(Var v, Func<WT> value)
    {
        if (v.Local >= 0) { Convert(value(), v.T, false); code.Set(v.Local); return; }
        StoreVar(v, value);
    }

    void LoadTemp(Var v) => LoadVar(v);

    // ------------------------------------------------------------------ strings

    /// <summary>Converts the value on the stack to a string: Alice `str` (puts: `true`, `1.0`) or C# ToString
    /// (interpolation and `+`: `True`, `1`).</summary>
    internal void ToStr(WT t, bool csharp)
    {
        switch (t.K)
        {
            case WK.Str:
                if (!csharp)
                {
                    // puts nil prints an empty line
                    int s = Temp(ValType.I32);
                    code.Tee(s);
                    var iff = code.If(ValType.I32);
                    code.Get(s);
                    code.Else();
                    code.I32(c.Literal(""));
                    code.End(iff);
                }
                return;
            case WK.Null: code.Emit(Op.Drop); code.I32(c.Literal("")); return;
            case WK.Bool:
                if (csharp)
                {
                    var iff = code.If(ValType.I32);
                    code.I32(c.Literal("True")); code.Else(); code.I32(c.Literal("False"));
                    code.End(iff);
                }
                else code.Call(c.Rt.Func("bool_to_str"));
                return;
            case WK.Char: code.Call(c.Rt.Func("str_from_char")); return;
            case WK.I8 or WK.I16 or WK.I32: code.Call(c.Rt.Func("i32_to_str")); return;
            case WK.U8 or WK.U16 or WK.U32: code.Call(c.Rt.Func("u32_to_str")); return;
            case WK.I64: code.Call(c.Rt.Func("i64_to_str")); return;
            case WK.U64: code.I32(0); code.Call(c.Rt.Func("u64_to_str")); return;
            case WK.F64 or WK.F32:
                {
                    if (t.K == WK.F32) code.Emit(Op.F64PromoteF32);
                    code.I32(t.K == WK.F32 ? 1 : 0);
                    code.I32(csharp ? 0 : 1);
                    code.Call((c.RtAl("fmt_float") ?? throw No("Runtime:fmt_float")).Func);
                    return;
                }
            case WK.Enum:
                code.Call(c.EnumToString(t.Cls!));
                return;
            case WK.Class or WK.Struct:
                {
                    var ts = t.Cls!.Sym != null ? AirToWasm.Implementation(t.Cls!, "ToString", 0) : null;
                    if (ts != null)
                    {
                        if (t.IsStruct || !ts.IsVirtual) code.Call(ts.Func);
                        else code.Call(c.Dispatcher(ts, t.Cls!));
                        return;
                    }
                    if (t.K == WK.Class && c.IsException(t.Cls!)) { code.Call(c.ExceptionToString); return; }
                    code.Emit(Op.Drop);
                    code.I32(c.Literal(t.Cls!.Name));
                    return;
                }
            case WK.Array or WK.List:
                code.Call(c.SeqToString(t));
                return;
        }
        throw No("ToStr", t.Name);
    }

    /// <summary>`inspect`: strings quoted, other values as `str`.</summary>
    void Inspect(WT t)
    {
        if (t.K == WK.Str) { code.Call(c.InspectStr); return; }
        if (t.K == WK.Null) { code.Emit(Op.Drop); code.I32(c.Literal("nil")); return; }
        if (t.K == WK.Char)
        {
            code.Call(c.Rt.Func("str_from_char"));
            int s = Temp(ValType.I32);
            code.Set(s);
            code.I32(c.Literal("'"));
            code.Get(s);
            code.Call(c.Rt.Func("str_concat"));
            code.I32(c.Literal("'"));
            code.Call(c.Rt.Func("str_concat"));
            return;
        }
        ToStr(t, csharp: false);
    }

    /// <summary>Interpolation: each hole formatted as C# does (invariant culture), the chunks concatenated.</summary>
    WT Interp(MInterp ip)
    {
        var acc = FrameTemp(WT.Str);
        code.Get(Fp());
        code.I32(c.Literal(ip.Chunks.Count > 0 ? Unescape(ip.Chunks[0]) : ""));
        code.Mem(Op.I32Store, (uint)acc.Frame);
        for (int i = 0; i < ip.Holes.Count; i++)
        {
            code.Get(Fp());
            code.Get(Fp());
            code.Mem(Op.I32Load, (uint)acc.Frame);
            var t = Emit(ip.Holes[i], null);
            ToStr(t, csharp: true);
            code.Call(c.Rt.Func("str_concat"));
            if (i + 1 < ip.Chunks.Count && ip.Chunks[i + 1].Length > 0)
            {
                code.I32(c.Literal(Unescape(ip.Chunks[i + 1])));
                code.Call(c.Rt.Func("str_concat"));
            }
            code.Mem(Op.I32Store, (uint)acc.Frame);
        }
        code.Get(Fp());
        code.Mem(Op.I32Load, (uint)acc.Frame);
        return WT.Str;
    }

    /// <summary>`DefaultInterpolatedStringHandler` / `StringBuilder` held in a string local: appends concatenate.</summary>
    WT BuilderMethod(MExpr target, string n, List<MExpr> a)
    {
        switch (n, a.Count)
        {
            case ("AppendLiteral" or "AppendFormatted" or "Append", 1):
                {
                    bool chain = n == "Append";
                    Store(target, () =>
                    {
                        var tmp = FrameTemp(WT.Str);
                        code.Get(Fp());
                        Emit(target, WT.Builder);
                        code.Mem(Op.I32Store, (uint)tmp.Frame);
                        code.Get(Fp());
                        code.Mem(Op.I32Load, (uint)tmp.Frame);
                        var t = Emit(a[0], null);
                        ToStr(t, csharp: true);
                        code.Call(c.Rt.Func("str_concat"));
                        return WT.Builder;
                    });
                    if (chain) return Emit(target, WT.Builder);
                    return WT.Void;
                }
            case ("ToStringAndClear" or "ToString", 0):
                Emit(target, WT.Builder);
                return WT.Str;
        }
        throw No("Intrinsic:StringBuilder." + n, a.Count.ToString());
    }

    // ------------------------------------------------------------------ arrays

    WT Length(MExpr target)
    {
        var t = Emit(target, null);
        switch (t.K)
        {
            case WK.Array: NullCheck(); code.Mem(Op.I32Load, 4); return WT.I32;
            case WK.Str: code.Call(c.Rt.Func("str_len")); return WT.I32;
            case WK.List: NullCheck(); code.Mem(Op.I32Load, 8); return WT.I32;
        }
        throw No("Length", t.Name);
    }

    WT ArrayMethod(MExpr target, WT t, string n, List<MExpr> a)
    {
        switch (n, a.Count)
        {
            case ("size" or "length" or "count", 0): return Length(target);
            case ("max" or "min" or "sum", 0):
                Emit(target, t);
                code.Call(c.ArrayFold(t.Elem!, n));
                return n == "sum" && t.Elem!.Widened.K == WK.I32 && t.Elem.K != WK.I32 ? WT.I32 : t.Elem!;
            case ("par_sum" or "sum", 1):
                return SumMap(target, t, a[0]);
            case ("Clear", 0):
                {
                    Emit(target, t);
                    int arr = Temp(ValType.I32);
                    code.Tee(arr); code.I32(8); code.Emit(Op.I32Add);
                    code.I32(0);
                    code.Get(arr); code.Mem(Op.I32Load, 4); code.I32(t.Elem!.Size); code.Emit(Op.I32Mul);
                    code.Prefixed(Op.MemoryFill); code.B(0);
                    return WT.Void;
                }
            case ("to_s" or "ToString", 0): Emit(target, t); ToStr(t, csharp: false); return WT.Str;
        }
        throw No("Intrinsic:array." + n, a.Count.ToString());
    }

    /// <summary>`xs.sum { |x| f(x) }` / `par_sum` (serial: one thread): the closure over each element, summed.</summary>
    WT SumMap(MExpr target, WT at, MExpr f)
    {
        var ft = Ty(f);
        if (ft.K != WK.Func || ft.Params.Length != 1) throw No("Intrinsic:sum-closure", ft.Name);
        var r = ft.Ret!;
        var arr = FrameTemp(at);
        var clo = FrameTemp(ft);
        StoreVar(arr, () => Emit(target, at));
        StoreVar(clo, () => Emit(f, ft));
        var acc = new Var { T = r, Local = Temp(r.Val) };
        int i = Temp(ValType.I32);
        var done = code.Block();
        var loop = code.Loop();
        code.Get(i); LoadVar(arr); code.Mem(Op.I32Load, 4); code.Emit(Op.I32GeS); code.BrIf(done);
        var item = new TempRef(new Var { T = WT.I32, Local = i });
        var call = new MInvoke { Target = new TempRef(clo), Args = [new MIndex { Target = new TempRef(arr), Args = [item] }] };
        StoreTemp(acc, () => Binary(new MBinary { Op = AirBinOp.Add, L = new TempRef(acc), R = call }));
        code.Get(i); code.I32(1); code.Emit(Op.I32Add); code.Set(i);
        code.Br(loop);
        code.End(loop);
        code.End(done);
        code.Get(acc.Local);
        return r;
    }

    /// <summary>A new zeroed array of <paramref name="elem"/> with <paramref name="count"/> elements.</summary>
    WT NewArrayOf(WT elem, MExpr count)
    {
        Emit(count, WT.I32);
        code.I32(Math.Max(1, elem.Size));
        code.I32(elem.ArrayKind);
        code.Call(c.Rt.Func("array_new"));
        return WT.ArrayOf(elem);
    }

    WT NewArray(MNewArray na)
    {
        var elem = c.Parse(na.ElemCs, span);
        if (na.Items != null) return NewArrayItems(elem, na.Items);
        if (na.Size == null) throw No("MNewArray:size");
        return NewArrayOf(elem, na.Size);
    }

    /// <summary>`[a, b, c]`: the array is rooted in a frame temp while the items are stored.</summary>
    WT NewArrayItems(WT elem, List<MExpr> items)
    {
        var arr = FrameTemp(WT.ArrayOf(elem));
        code.Get(Fp());
        code.I32(items.Count);
        code.I32(Math.Max(1, elem.Size));
        code.I32(elem.ArrayKind);
        code.Call(c.Rt.Func("array_new"));
        code.Mem(Op.I32Store, (uint)arr.Frame);
        for (int i = 0; i < items.Count; i++)
        {
            code.Get(Fp());
            code.Mem(Op.I32Load, (uint)arr.Frame);
            code.I32(8 + i * elem.Size);
            code.Emit(Op.I32Add);
            StoreAt(elem, () => Emit(items[i], elem));
        }
        code.Get(Fp());
        code.Mem(Op.I32Load, (uint)arr.Frame);
        return WT.ArrayOf(elem);
    }

    /// <summary>Stores a value of <paramref name="t"/> at the address on the stack (a struct is copied).</summary>
    void StoreAt(WT t, Func<WT> value, uint offset = 0)
    {
        if (t.IsStruct)
        {
            if (offset != 0) { code.I32((int)offset); code.Emit(Op.I32Add); }
            var vt = value();
            if (!vt.IsStruct) throw No("Store:struct-from", vt.Name);
            code.I32(t.Size);
            code.Call(c.Rt.Func("memcpy"));
            return;
        }
        Convert(value(), t, false);
        code.Mem(t.Store, offset);
    }

    WT Rent(AirRent r)
    {
        // Own<T>.Rent(len[, fill]): a fresh array (released explicitly; the collector reclaims a leaked one)
        string pool = r.PoolCs;
        int lt = pool.IndexOf('<');
        var elem = c.Parse(pool[(lt + 1)..^1], span);
        if (r.Args.Count == 0) throw No("AirRent:args");
        var at = NewArrayOf(elem, r.Args[0]);
        if (r.Args.Count == 2)
        {
            int arr = Temp(ValType.I32), i = Temp(ValType.I32), v = Temp(elem.Val);
            code.Set(arr);
            Emit(r.Args[1], elem);
            code.Set(v);
            var done = code.Block();
            var loop = code.Loop();
            code.Get(i); code.Get(arr); code.Mem(Op.I32Load, 4); code.Emit(Op.I32GeS); code.BrIf(done);
            code.Get(arr); code.Get(i); code.I32(elem.Size); code.Emit(Op.I32Mul); code.Emit(Op.I32Add);
            code.Get(v);
            code.Mem(elem.Store, 8);
            code.Get(i); code.I32(1); code.Emit(Op.I32Add); code.Set(i);
            code.Br(loop);
            code.End(loop);
            code.End(done);
            code.Get(arr);
        }
        return at;
    }

    /// <summary>Pushes the address of element <paramref name="index"/> of the array, bounds-checked
    /// (IndexOutOfRangeException).</summary>
    WT ElementAddr(MExpr array, MExpr index)
    {
        var at = Ty(array);
        if (at.K != WK.Array) throw No("Index:array", at.Name);
        var elem = at.Elem!;
        int arr = Temp(ValType.I32), i = Temp(ValType.I32);
        Operands([Opnd(array, at), Opnd(index, WT.I32)]);
        code.Set(i);
        code.Tee(arr);
        code.Emit(Op.I32Eqz);
        ThrowIf(AirToWasm.ErrNull);
        code.Get(i);
        code.Get(arr);
        code.Mem(Op.I32Load, 4);
        code.Emit(Op.I32GeU);
        ThrowIf(AirToWasm.ErrIndex);
        code.Get(arr);
        code.Get(i);
        if (elem.Size != 1)
        {
            if ((elem.Size & (elem.Size - 1)) == 0) { code.I32(System.Numerics.BitOperations.Log2((uint)elem.Size)); code.Emit(Op.I32Shl); }
            else { code.I32(elem.Size); code.Emit(Op.I32Mul); }
        }
        code.Emit(Op.I32Add);
        return elem;
    }

    WT Index(MIndex ix)
    {
        if (ix.Args.Count != 1) throw No("MIndex:args", ix.Args.Count.ToString());
        var tt = Ty(ix.Target);
        switch (tt.K)
        {
            case WK.Array:
                {
                    var elem = ElementAddr(ix.Target, ix.Args[0]);
                    if (elem.IsStruct) { code.I32(8); code.Emit(Op.I32Add); return elem; }
                    code.Mem(elem.Load, 8);
                    return elem;
                }
            case WK.Str:
                Operands([Opnd(ix.Target, WT.Str), Opnd(ix.Args[0], WT.I32)]);
                code.Call(c.Rt.Func("str_at"));
                return WT.Char;
            case WK.List:
                Operands([Opnd(ix.Target, tt), Opnd(ix.Args[0], WT.I32)]);
                code.Call(c.ListGetAddr(tt.Elem!));
                if (tt.Elem!.IsStruct) return tt.Elem;
                code.Mem(tt.Elem.Load);
                return tt.Elem;
            case WK.Dict:
                Operands([Opnd(ix.Target, tt), Opnd(ix.Args[0], tt.KeyT!)]);
                code.Call(c.DictGetAddr(tt));
                if (tt.Elem!.IsStruct) return tt.Elem;
                code.Mem(tt.Elem.Load);
                return tt.Elem;
            case WK.Class when tt.Cls!.Sym != null:
                {
                    var m = Resolve(tt.Cls!, "get_Item", ix.Args, instance: true) ?? throw No("MIndex:indexer", tt.Name);
                    return CallUser(m, ix.Target, ix.Args);
                }
        }
        throw No("MIndex:" + tt.Name);
    }

    // ------------------------------------------------------------------ members

    WT Member(MMember m)
    {
        if (m.Safe) throw No("MMember:safe-navigation", m.Name);
        string n = AirToWasm.Bare(m.Name);
        var tt = Ty(m.Target);
        if (tt.K == WK.TypeRef)
        {
            if (tt.Cls is { } cls)
            {
                var f = cls.FindField(n);
                if (f != null) return LoadField(f, null);
                if (cls.Sym != null && Resolve(cls, n, [], instance: false) is { } sm) return CallUser(sm, null, []);
                throw No("MMember:static", cls.Name + "." + n);
            }
            return StaticMember(TypeName(tt.Name), n);
        }
        if (ReferenceEquals(tt, WT.Stopwatch) || ReferenceEquals(tt, WT.TimeSpan)) return TimeMember(m.Target, tt, n);
        switch (tt.K)
        {
            case WK.Array when n is "Length" or "Count": return Length(m.Target);
            case WK.Str when n == "Length": return Length(m.Target);
            case WK.List when n == "Count": return Length(m.Target);
            case WK.Dict when n == "Count": Emit(m.Target, tt); NullCheck(); code.Mem(Op.I32Load, 8); return WT.I32;
        }
        if (tt.K == WK.Struct && c.IsListEnumerator(tt.Cls!) && n == "Current") return EnumeratorCurrent(m.Target, tt);
        if (tt.K is WK.Class or WK.Struct)
        {
            var f = tt.Cls!.FindField(n) ?? tt.Cls!.FindField("@" + n);
            if (f != null && !f.IsStatic) return LoadField(f, m.Target);
            if (f != null) return LoadField(f, null);
            if (tt.Cls!.Sym != null && Resolve(tt.Cls!, n, [], instance: true) is { } gm) return CallUser(gm, m.Target, []);
            if (n == "Message" && c.IsException(tt.Cls!)) return LoadField(c.RuntimeClass("System.Exception").FieldsByName["Message"], m.Target);
        }
        throw No("MMember:" + tt.Name + "." + n);
    }

    /// <summary>`sw.Elapsed` / `sw.ElapsedMilliseconds` / `sw.ElapsedTicks` and `ts.TotalMilliseconds` /
    /// `TotalSeconds` / `TotalMicroseconds` / `Ticks` (100 ns, as .NET) over the nanosecond i64.</summary>
    WT TimeMember(MExpr target, WT tt, string n)
    {
        Emit(target, tt);
        if (ReferenceEquals(tt, WT.Stopwatch))
        {
            int start = Temp(ValType.I64);
            code.Set(start);
            code.I32(1); code.Call(c.Wasi.ClockFunc); code.Get(start); code.Emit(Op.I64Sub);
            switch (n)
            {
                case "Elapsed" or "elapsed": return WT.TimeSpan;
                case "ElapsedMilliseconds" or "elapsed_milliseconds": code.I64(1_000_000); code.Emit(Op.I64DivS); return WT.I64;
                case "ElapsedTicks" or "elapsed_ticks": code.I64(100); code.Emit(Op.I64DivS); return WT.I64;
            }
            throw No("MMember:Stopwatch." + n);
        }
        switch (n)
        {
            case "Ticks" or "ticks": code.I64(100); code.Emit(Op.I64DivS); return WT.I64;
            case "TotalMilliseconds" or "total_milliseconds": code.Emit(Op.F64ConvertI64S); code.F64(1e6); code.Emit(Op.F64Div); return WT.F64;
            case "TotalSeconds" or "total_seconds": code.Emit(Op.F64ConvertI64S); code.F64(1e9); code.Emit(Op.F64Div); return WT.F64;
            case "TotalMicroseconds" or "total_microseconds": code.Emit(Op.F64ConvertI64S); code.F64(1e3); code.Emit(Op.F64Div); return WT.F64;
        }
        throw No("MMember:TimeSpan." + n);
    }

    WT StaticMember(string type, string n)
    {
        switch (type, n)
        {
            case ("System.Math" or "Math", "PI"): code.F64(Math.PI); return WT.F64;
            case ("System.Math" or "Math", "E"): code.F64(Math.E); return WT.F64;
            case ("System.MathF" or "MathF", "PI"): code.F32(MathF.PI); return WT.F32;
            case ("int" or "System.Int32", "MaxValue"): code.I32(int.MaxValue); return WT.I32;
            case ("int" or "System.Int32", "MinValue"): code.I32(int.MinValue); return WT.I32;
            case ("long" or "System.Int64", "MaxValue"): code.I64(long.MaxValue); return WT.I64;
            case ("long" or "System.Int64", "MinValue"): code.I64(long.MinValue); return WT.I64;
            case ("uint" or "System.UInt32", "MaxValue"): code.I32(-1); return WT.U32;
            case ("double" or "System.Double", "PositiveInfinity"): code.F64(double.PositiveInfinity); return WT.F64;
            case ("double" or "System.Double", "NegativeInfinity"): code.F64(double.NegativeInfinity); return WT.F64;
            case ("double" or "System.Double", "NaN"): code.F64(double.NaN); return WT.F64;
            case ("double" or "System.Double", "MaxValue"): code.F64(double.MaxValue); return WT.F64;
            case ("double" or "System.Double", "Epsilon"): code.F64(double.Epsilon); return WT.F64;
            case ("float" or "System.Single", "MaxValue"): code.F32(float.MaxValue); return WT.F32;
            case ("float" or "System.Single", "PositiveInfinity"): code.F32(float.PositiveInfinity); return WT.F32;
        }
        throw No("MMember:static " + type + "." + n);
    }

    /// <summary>Loads a field: an instance field of <paramref name="target"/> (null: `this`), a static field, or a
    /// literal (enum member, const).</summary>
    WT LoadField(WField f, MExpr? target)
    {
        if (f.Literal != null) { Const(f.Type, f.Literal); return f.Type; }
        if (f.IsStatic)
        {
            if (f.Type.IsStruct) { code.I32(f.Address); return f.Type; }
            code.I32(f.Address);
            code.Mem(f.Type.Load);
            return f.Type;
        }
        FieldBase(target);
        if (f.Type.IsStruct) { code.I32(f.Offset); code.Emit(Op.I32Add); return f.Type; }
        code.Mem(f.Type.Load, (uint)f.Offset);
        return f.Type;
    }

    /// <summary>The address an instance field is relative to: the object, or a struct's address (null-checked
    /// unless it is `this`).</summary>
    void FieldBase(MExpr? target)
    {
        if (target == null || target is AirThis) { This(false); return; }
        var t = Emit(target, null);
        if (t.K == WK.Class) NullCheck();
    }

    /// <summary>A method group as a closure value (a static method; an instance method of `this`).</summary>
    WT MethodGroup(AirMethodGroup mg)
    {
        var k = mg.Owner != null ? c.ClassNamed(mg.Owner.ToCs()) ?? c.ProgramC : owner;
        var m = k.FindMethods(mg.Name).FirstOrDefault() ?? c.ProgramC.FindMethods(mg.Name).FirstOrDefault() ?? throw No("AirMethodGroup", mg.Name);
        if (!m.IsStatic) throw No("AirMethodGroup:instance", mg.Name);
        var thunk = c.StaticThunk(m);
        // [header][slot][env 0]
        code.I32(16);
        code.I32(AirToWasm.ClosureType);
        code.Call(c.Rt.Func("alloc"));
        int o = Temp(ValType.I32);
        code.Tee(o);
        code.I32(M.Slot(thunk));
        code.Mem(Op.I32Store, 8);
        code.Get(o);
        return WT.FuncOf(m.Params.ToArray(), m.Ret);
    }

    // ------------------------------------------------------------------ construction

    WT New(MNew n, Action? dst = null)
    {
        var t = c.Parse(n.TypeCs, span);
        if (n.Init != null && n.Init.Count > 0)
        {
            // collection initializer: the new list or dictionary rooted in a frame temp, then Add per item
            if (t.K is not (WK.List or WK.Dict)) throw No("MNew:initializer", n.TypeCs);
            var obj = FrameTemp(t);
            StoreVar(obj, () => New(new MNew { TypeCs = n.TypeCs, Args = n.Args }));
            foreach (var it in n.Init)
            {
                if (t.K == WK.List) { Operands([(() => LoadVar(obj), t, false), Opnd(it, t.Elem!)]); code.Call(c.ListAdd(t.Elem!, returnList: false)); }
                else if (it is MPair pr) { Operands([(() => LoadVar(obj), t, false), Opnd(pr.K, t.KeyT!), Opnd(pr.V, t.Elem!)]); code.Call(c.DictSet(t)); }
                else throw No("MNew:initializer-item", it.GetType().Name);
            }
            return LoadVar(obj);
        }
        switch (t.K)
        {
            case WK.List:
                if (n.Args.Count > 0) Emit(n.Args[0], WT.I32); else code.I32(4);
                code.Call(c.ListNew(t.Elem!));
                return t;
            case WK.Dict:
                if (n.Args.Count > 0) throw No("MNew:Dictionary-arguments");
                code.Call(c.DictNew(t));
                return t;
            case WK.Builder:
                code.I32(c.Literal(""));
                return t;
            case WK.Class when c.IsException(t.Cls!) && t.Cls!.Sym == null:
                if (n.Args.Count > 0) Emit(n.Args[0], WT.Str);
                else code.I32(c.Literal(AirToWasm.ErrorCodes.FirstOrDefault(x => x.Type == t.Cls!.Name).Message ?? "Exception of type '" + t.Cls!.Name + "' was thrown."));
                code.Call(c.NewException(t.Cls!));
                return t;
            case WK.Class:
                {
                    var cls = t.Cls!;
                    if (cls.IsAbstract || cls.IsAdt && !cls.IsVariant) throw No("MNew:abstract", cls.Name);
                    var ctor = PickCtor(cls, n.Args);
                    // the object first (rooted in a frame temp), then the arguments, then the constructor
                    var obj = FrameTemp(t);
                    code.Get(Fp());
                    code.I32(Math.Max(16, cls.Size));
                    code.I32(cls.Id);
                    code.Call(c.Rt.Func("alloc"));
                    code.Mem(Op.I32Store, (uint)obj.Frame);
                    CallCtor(ctor, () => { code.Get(Fp()); code.Mem(Op.I32Load, (uint)obj.Frame); return t; }, n.Args);
                    code.Get(Fp());
                    code.Mem(Op.I32Load, (uint)obj.Frame);
                    return t;
                }
            case WK.Struct:
                {
                    var cls = t.Cls!;
                    if (dst != null)
                    {
                        if (n.Args.Count == 0 && cls.Ctors.All(k => k.Params.Count > 0))
                        {
                            dst(); code.I32(0); code.I32(cls.Size); code.Prefixed(Op.MemoryFill); code.B(0);
                            return WT.Void;
                        }
                        CallCtor(PickCtor(cls, n.Args), () => { dst(); return t; }, n.Args, zero: true);
                        return WT.Void;
                    }
                    var tmp = FrameTemp(t);
                    if (n.Args.Count == 0 && cls.Ctors.All(k => k.Params.Count > 0)) { ZeroFrame(tmp.Frame, cls.Size); FrameAddr(tmp.Frame); return t; }
                    CallCtor(PickCtor(cls, n.Args), () => { FrameAddr(tmp.Frame); return t; }, n.Args, zero: true);
                    FrameAddr(tmp.Frame);
                    return t;
                }
        }
        throw No("MNew:" + t.Name);
    }

    WMethod PickCtor(WClass cls, List<MExpr> args)
    {
        var cands = cls.Ctors.Where(k => Arity(k, args.Count)).ToList();
        if (cands.Count == 0) throw No("MNew:no-constructor", cls.Name + "/" + args.Count);
        if (cands.Count == 1) return cands[0];
        var ts = args.Select(Ty).ToList();
        return cands.FirstOrDefault(k => ts.Select((t, i) => Assignable(t, k.Params[i], args[i])).All(x => x)) ?? cands[0];
    }

    /// <summary>Calls a constructor on the object (or struct address) <paramref name="self"/> pushes.</summary>
    void CallCtor(WMethod ctor, Func<WT> self, List<MExpr> args, bool zero = false)
    {
        if (zero)
        {
            // a struct is zeroed before its constructor runs (fields without an initializer are default)
            self(); code.I32(0); code.I32(ctor.Owner.Size); code.Prefixed(Op.MemoryFill); code.B(0);
        }
        // the object (or the struct's storage) itself, never a copy
        var ops = new List<(Func<WT>, WT, bool)> { (self, ctor.Owner.IsStruct ? WT.I32 : ctor.Owner.Type, false) };
        for (int i = 0; i < ctor.Params.Count; i++)
        {
            if (i < args.Count) ops.Add(Opnd(args[i], ctor.Params[i]));
            else if (ctor.Defaults.Length > i && ctor.Defaults[i] is { } dv) ops.Add(Opnd(dv, ctor.Params[i]));
            else throw No("MNew:missing-argument", ctor.ToString());
        }
        Operands(ops);
        code.Call(ctor.Func);
    }

    WT OperatorMethod(MBinary b, WT l, WT r)
    {
        if (l.K is WK.Class or WK.Struct && l.Cls!.Sym != null)
        {
            string name = b.Op switch
            {
                AirBinOp.Add => "op_Add", AirBinOp.Sub => "op_Sub", AirBinOp.Mul => "op_Mul", AirBinOp.Div => "op_Div", AirBinOp.Mod => "op_Mod",
                AirBinOp.Eq => "op_Eq", AirBinOp.Ne => "op_Ne", AirBinOp.Lt => "op_Lt", AirBinOp.Gt => "op_Gt", AirBinOp.Le => "op_Le", AirBinOp.Ge => "op_Ge",
                AirBinOp.Shl => "op_Shl", AirBinOp.Shr => "op_Shr", AirBinOp.BitAnd => "op_BitAnd", AirBinOp.BitOr => "op_BitOr", AirBinOp.BitXor => "op_Xor",
                _ => "",
            };
            var m = name.Length > 0 ? Resolve(l.Cls!, name, [b.R], instance: true) : null;
            if (m != null) return CallUser(m, b.L, [b.R]);
        }
        throw No("Binary:" + b.Op, l.Name + ", " + r.Name);
    }

    // ------------------------------------------------------------------ lists

    WT DictMethod(MExpr target, WT t, string n, List<MExpr> a)
    {
        switch (n, a.Count)
        {
            case ("ContainsKey" or "has_key_q" or "key_q" or "include_q", 1):
                Operands([Opnd(target, t), Opnd(a[0], t.KeyT!)]);
                code.Call(c.DictFind(t));
                code.I32(0); code.Emit(Op.I32GeS);
                return WT.Bool;
            case ("Remove" or "delete", 1):
                Operands([Opnd(target, t), Opnd(a[0], t.KeyT!)]);
                code.Call(c.DictRemove(t));
                return WT.Bool;
            case ("Count" or "size" or "length", 0):
                Emit(target, t); NullCheck(); code.Mem(Op.I32Load, 8);
                return WT.I32;
            case ("Add" or "store", 2):
                Operands([Opnd(target, t), Opnd(a[0], t.KeyT!), Opnd(a[1], t.Elem!)]);
                code.Call(c.DictSet(t));
                return WT.Void;
        }
        throw No("Intrinsic:Dictionary." + n, a.Count.ToString());
    }

    WT EnumeratorMethod(MExpr target, WT t, string n, List<MExpr> a)
    {
        switch (n, a.Count)
        {
            case ("MoveNext", 0):
                {
                    // ++index < list.Count
                    Emit(target, t);
                    int en = Temp(ValType.I32), i = Temp(ValType.I32);
                    code.Tee(en);
                    code.Get(en); code.Mem(Op.I32Load, 4); code.I32(1); code.Emit(Op.I32Add); code.Tee(i);
                    code.Mem(Op.I32Store, 4);
                    code.Get(i);
                    code.Get(en); code.Mem(Op.I32Load); code.Mem(Op.I32Load, 8);
                    code.Emit(Op.I32LtS);
                    return WT.Bool;
                }
            case ("Dispose", 0): return WT.Void;
        }
        throw No("Intrinsic:List.Enumerator." + n);
    }

    WT EnumeratorCurrent(MExpr target, WT t)
    {
        var elem = c.ListEnumeratorElem(t.Cls!);
        Emit(target, t);
        int en = Temp(ValType.I32);
        code.Tee(en);
        code.Mem(Op.I32Load);
        code.Get(en);
        code.Mem(Op.I32Load, 4);
        code.Call(c.ListGetAddr(elem));
        if (elem.IsStruct) return elem;
        code.Mem(elem.Load);
        return elem;
    }

    WT ListMethod(MExpr target, WT t, string n, List<MExpr> a)
    {
        var e = t.Elem!;
        switch (n, a.Count)
        {
            case ("Add" or "push" or "append_op", 1):
                Operands([Opnd(target, t), Opnd(a[0], e)]);
                code.Call(c.ListAdd(e, returnList: false));
                return WT.Void;
            case ("Count" or "size" or "length", 0): return Length(target);
            case ("Clear", 0):
                Emit(target, t); NullCheck();
                code.I32(0); code.Mem(Op.I32Store, 8);
                return WT.Void;
            case ("GetEnumerator", 0):
                {
                    // List<T>.Enumerator: a struct [list, index] in a frame temp
                    var et = c.ListEnumerator(e).Type;
                    var tmp = FrameTemp(et);
                    code.Get(Fp());
                    Emit(target, t);
                    code.Mem(Op.I32Store, (uint)tmp.Frame);
                    code.Get(Fp());
                    code.I32(-1);
                    code.Mem(Op.I32Store, (uint)tmp.Frame + 4);
                    FrameAddr(tmp.Frame);
                    return et;
                }
            case ("ToArray", 0):
                Emit(target, t);
                code.Call(c.ListToArray(e));
                return WT.ArrayOf(e);
            case ("max" or "min" or "sum", 0):
                Emit(target, t);
                code.Call(c.ListToArray(e));
                code.Call(c.ArrayFold(e, n));
                return e;
        }
        throw No("Intrinsic:List." + n, a.Count.ToString());
    }
}
