namespace Alice.Compiler.Backend.Wasm;

// Generated runtime helpers that depend on element types or declarations (one function per type, made on first
// use): lists, array folds, record and struct equality, enum names, sequence and inspect formatting, the Alice part
// of the runtime (rt.al) by name.
public sealed partial class AirToWasm
{
    /// <summary>Header type ids of runtime objects (classes start at 16).</summary>
    internal const int StringType = 1, NumArrayType = 2, RefArrayType = 3, StructArrayType = 4, ClosureType = 5, EnvType = 6, ListType = 7;

    readonly Dictionary<string, WasmFunc> helpers = new(StringComparer.Ordinal);

    WasmFunc Helper(string key, FuncSig sig, string?[] names, Action<WasmCode> body)
    {
        if (helpers.TryGetValue(key, out var f)) return f;
        f = M.Define(key, sig, names);
        helpers[key] = f;
        body(f.Code!);
        f.Code!.Finish();
        return f;
    }

    static readonly FuncSig I_I = new([ValType.I32], [ValType.I32]);

    /// <summary>A WMethod of the Alice runtime module (rt.al, `module WasmRt`), or null.</summary>
    internal WMethod? RtAl(string name)
    {
        var rt = classes.FirstOrDefault(k => k.Name == "WasmRt");
        return rt?.Methods.FirstOrDefault(m => m.Name == name);
    }

    // ------------------------------------------------------------------ lists: [header][count @8][items @12]

    internal WasmFunc ListNew(WT elem) => Helper("List<" + elem.Name + ">.new", I_I, ["cap"], c =>
    {
        int o = c.Local(ValType.I32, "o"), fr = c.Local(ValType.I32, "frame");
        c.I32(8); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.I32(16); c.I32(ListType); c.Call(Rt.Func("alloc")); c.Set(o);
        c.Get(fr); c.Get(o); c.Mem(Op.I32Store);
        c.Get(o);
        c.Get(0); c.I32(4); c.Get(0); c.I32(4); c.Emit(Op.I32GtS); c.Emit(Op.Select);
        c.I32(Math.Max(1, elem.Size)); c.I32(elem.ArrayKind); c.Call(Rt.Func("array_new"));
        c.Mem(Op.I32Store, 12);
        c.Get(fr); c.I32(8); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
        c.Get(o);
    });

    /// <summary>`(list, value)`: appends (doubling the items array when full); returns the list when asked.</summary>
    internal WasmFunc ListAdd(WT elem, bool returnList) => Helper("List<" + elem.Name + ">.add" + (returnList ? "!" : ""),
        new FuncSig([ValType.I32, elem.IsStruct ? ValType.I32 : elem.Val], returnList ? [ValType.I32] : []), ["list", "value"], c =>
    {
        int items = c.Local(ValType.I32, "items"), n = c.Local(ValType.I32, "n"), grown = c.Local(ValType.I32, "grown"), fr = c.Local(ValType.I32, "frame");
        c.Get(0); c.Emit(Op.I32Eqz);
        var nul = c.If(); c.I32(ErrNull); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(nul);
        c.Get(0); c.Mem(Op.I32Load, 12); c.Set(items);
        c.Get(0); c.Mem(Op.I32Load, 8); c.Set(n);
        c.Get(n); c.Get(items); c.Mem(Op.I32Load, 4); c.Emit(Op.I32GeU);
        var full = c.If();
        // root the list and the value (a reference) while the new array is allocated
        c.I32(8); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.Get(fr); c.Get(0); c.Mem(Op.I32Store);
        if (elem.IsRef) { c.Get(fr); c.Get(1); c.Mem(Op.I32Store, 4); }
        c.Get(n); c.I32(1); c.Emit(Op.I32Shl); c.I32(4); c.Get(n); c.I32(1); c.Emit(Op.I32Shl); c.I32(4); c.Emit(Op.I32GtS); c.Emit(Op.Select);
        c.I32(Math.Max(1, elem.Size)); c.I32(elem.ArrayKind); c.Call(Rt.Func("array_new")); c.Set(grown);
        c.Get(grown); c.I32(8); c.Emit(Op.I32Add);
        c.Get(items); c.I32(8); c.Emit(Op.I32Add);
        c.Get(n); c.I32(Math.Max(1, elem.Size)); c.Emit(Op.I32Mul);
        c.Call(Rt.Func("memcpy"));
        c.Get(0); c.Get(grown); c.Mem(Op.I32Store, 12);
        c.Get(grown); c.Set(items);
        c.Get(fr); c.I32(8); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
        c.End(full);
        c.Get(items); c.Get(n); c.I32(Math.Max(1, elem.Size)); c.Emit(Op.I32Mul); c.Emit(Op.I32Add);
        if (elem.IsStruct) { c.I32(8); c.Emit(Op.I32Add); c.Get(1); c.I32(elem.Size); c.Call(Rt.Func("memcpy")); }
        else { c.Get(1); c.Mem(elem.Store, 8); }
        c.Get(0); c.Get(n); c.I32(1); c.Emit(Op.I32Add); c.Mem(Op.I32Store, 8);
        if (returnList) c.Get(0);
    });

    /// <summary>`(list, i) -> element address`, ArgumentOutOfRangeException outside [0, Count).</summary>
    internal WasmFunc ListGetAddr(WT elem) => Helper("List<" + elem.Name + ">.at", new FuncSig([ValType.I32, ValType.I32], [ValType.I32]), ["list", "i"], c =>
    {
        c.Get(0); c.Emit(Op.I32Eqz);
        var nul = c.If(); c.I32(ErrNull); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(nul);
        c.Get(1); c.Get(0); c.Mem(Op.I32Load, 8); c.Emit(Op.I32GeU);
        var bad = c.If(); c.I32(ErrRange); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(bad);
        c.Get(0); c.Mem(Op.I32Load, 12); c.I32(8); c.Emit(Op.I32Add);
        c.Get(1); c.I32(Math.Max(1, elem.Size)); c.Emit(Op.I32Mul); c.Emit(Op.I32Add);
    });

    internal WasmFunc ListToArray(WT elem) => Helper("List<" + elem.Name + ">.to_a", I_I, ["list"], c =>
    {
        int r = c.Local(ValType.I32, "r"), fr = c.Local(ValType.I32, "frame");
        c.I32(8); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.Get(fr); c.Get(0); c.Mem(Op.I32Store);
        c.Get(0); c.Mem(Op.I32Load, 8); c.I32(Math.Max(1, elem.Size)); c.I32(elem.ArrayKind); c.Call(Rt.Func("array_new")); c.Set(r);
        c.Get(r); c.I32(8); c.Emit(Op.I32Add);
        c.Get(0); c.Mem(Op.I32Load, 12); c.I32(8); c.Emit(Op.I32Add);
        c.Get(0); c.Mem(Op.I32Load, 8); c.I32(Math.Max(1, elem.Size)); c.Emit(Op.I32Mul);
        c.Call(Rt.Func("memcpy"));
        c.Get(fr); c.I32(8); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
        c.Get(r);
    });

    readonly Dictionary<string, (WClass C, WT Elem)> listEnums = new(StringComparer.Ordinal);

    /// <summary>`List&lt;T&gt;.Enumerator`: a struct [list, index].</summary>
    internal WClass ListEnumerator(WT elem)
    {
        string key = elem.Key;
        if (listEnums.TryGetValue(key, out var e)) return e.C;
        var k = new WClass { Name = "System.Collections.Generic.List<" + elem.Name + ">.Enumerator", IsStruct = true };
        k.AddField("list", WT.ListOf(elem));
        k.AddField("index", WT.I32);
        k.Lay();
        listEnums[key] = (k, elem);
        return k;
    }

    internal bool IsListEnumerator(WClass k) => listEnums.Values.Any(x => x.C == k);
    internal WT ListEnumeratorElem(WClass k) => listEnums.Values.First(x => x.C == k).Elem;

    // ------------------------------------------------------------------ arrays

    /// <summary>`xs.max` / `xs.min` (InvalidOperationException when empty) and `xs.sum` (checked for integers).</summary>
    internal WasmFunc ArrayFold(WT elem, string op)
    {
        var acc = op == "sum" ? elem.Widened : elem;
        return Helper(elem.Name + "[]." + op, new FuncSig([ValType.I32], [acc.Val]), ["xs"], c =>
        {
            int i = c.Local(ValType.I32, "i"), n = c.Local(ValType.I32, "n"), a = c.Local(acc.Val, "acc"), x = c.Local(acc.Val, "x");
            c.Get(0); c.Emit(Op.I32Eqz);
            var nul = c.If(); c.I32(ErrNull); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(nul);
            c.Get(0); c.Mem(Op.I32Load, 4); c.Set(n);
            if (op != "sum")
            {
                c.Get(n); c.Emit(Op.I32Eqz);
                var empty = c.If(); c.I32(ErrState); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(empty);
                c.Get(0); c.Mem(elem.Load, 8); c.Set(a);
                c.I32(1); c.Set(i);
            }
            var done = c.Block();
            var loop = c.Loop();
            c.Get(i); c.Get(n); c.Emit(Op.I32GeS); c.BrIf(done);
            c.Get(0); c.Get(i); c.I32(elem.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.Mem(elem.Load, 8); c.Set(x);
            bool w = acc.Is64, u = acc.IsUnsigned;
            if (op == "sum")
            {
                if (acc.IsFloat) { c.Get(a); c.Get(x); c.Emit(acc.K == WK.F64 ? Op.F64Add : Op.F32Add); c.Set(a); }
                else
                {
                    int r = c.Local(acc.Val, "r");
                    c.Get(a); c.Get(x); c.Emit(w ? Op.I64Add : Op.I32Add); c.Set(r);
                    if (u) { c.Get(r); c.Get(a); c.Emit(w ? Op.I64LtU : Op.I32LtU); }
                    else
                    {
                        c.Get(a); c.Get(r); c.Emit(w ? Op.I64Xor : Op.I32Xor); c.Get(x); c.Get(r); c.Emit(w ? Op.I64Xor : Op.I32Xor);
                        c.Emit(w ? Op.I64And : Op.I32And);
                        if (w) { c.I64(0); c.Emit(Op.I64LtS); } else { c.I32(0); c.Emit(Op.I32LtS); }
                    }
                    var ov = c.If(); c.I32(ErrOverflow); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(ov);
                    c.Get(r); c.Set(a);
                }
            }
            else
            {
                byte cmp = acc.Val switch
                {
                    ValType.F64 => op == "max" ? Op.F64Gt : Op.F64Lt,
                    ValType.F32 => op == "max" ? Op.F32Gt : Op.F32Lt,
                    ValType.I64 => op == "max" ? (u ? Op.I64GtU : Op.I64GtS) : (u ? Op.I64LtU : Op.I64LtS),
                    _ => op == "max" ? (u ? Op.I32GtU : Op.I32GtS) : (u ? Op.I32LtU : Op.I32LtS),
                };
                c.Get(x); c.Get(a); c.Get(x); c.Get(a); c.Emit(cmp); c.Emit(Op.Select); c.Set(a);
            }
            c.Get(i); c.I32(1); c.Emit(Op.I32Add); c.Set(i);
            c.Br(loop);
            c.End(loop);
            c.End(done);
            c.Get(a);
        });
    }

    // ------------------------------------------------------------------ equality

    /// <summary>Record (class) value equality: same reference, or both non-null of the same type with equal fields.</summary>
    internal WasmFunc RecordEquals(WClass k) => Helper(k.Name + ".Equals", new FuncSig([ValType.I32, ValType.I32], [ValType.I32]), ["a", "b"], c =>
    {
        c.Get(0); c.Get(1); c.Emit(Op.I32Eq);
        var same = c.If(); c.I32(1); c.Emit(Op.Return); c.End(same);
        c.Get(0); c.Emit(Op.I32Eqz); c.Get(1); c.Emit(Op.I32Eqz); c.Emit(Op.I32Or);
        var nul = c.If(); c.I32(0); c.Emit(Op.Return); c.End(nul);
        c.Get(0); c.Mem(Op.I32Load); c.Get(1); c.Mem(Op.I32Load); c.Emit(Op.I32Ne);
        var diff = c.If(); c.I32(0); c.Emit(Op.Return); c.End(diff);
        FieldsEqual(c, k, 0, 1);
    });

    internal WasmFunc StructEquals(WClass k) => Helper(k.Name + ".Equals(struct)", new FuncSig([ValType.I32, ValType.I32], [ValType.I32]), ["a", "b"], c => FieldsEqual(c, k, 0, 1));

    void FieldsEqual(WasmCode c, WClass k, int a, int b)
    {
        var fields = new List<WField>();
        for (var x = k; x != null; x = x.Base) fields.InsertRange(0, x.Fields.Where(f => !f.IsStatic && f.Literal == null));
        c.I32(1);
        foreach (var f in fields)
        {
            var t = f.Type;
            if (t.IsStruct)
            {
                c.Get(a); c.I32(f.Offset); c.Emit(Op.I32Add);
                c.Get(b); c.I32(f.Offset); c.Emit(Op.I32Add);
                c.Call(StructEquals(t.Cls!));
            }
            else
            {
                c.Get(a); c.Mem(t.Load, (uint)f.Offset);
                c.Get(b); c.Mem(t.Load, (uint)f.Offset);
                switch (t.K)
                {
                    case WK.Str: c.Call(Rt.Func("str_eq")); break;
                    case WK.Class when t.Cls!.IsRecord: c.Call(RecordEquals(t.Cls!)); break;
                    default:
                        c.Emit(t.Val switch { ValType.I64 => Op.I64Eq, ValType.F32 => Op.F32Eq, ValType.F64 => Op.F64Eq, _ => Op.I32Eq });
                        break;
                }
            }
            c.Emit(Op.I32And);
        }
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>An enum value's member name (the number when no member has it), as C# Enum.ToString.</summary>
    internal WasmFunc EnumToString(WClass k) => Helper(k.Name + ".ToString", new FuncSig([k.EnumUnderlying!.Val], [ValType.I32]), ["v"], c =>
    {
        foreach (var f in k.Fields.Where(f => f.Literal != null))
        {
            c.Get(0);
            if (k.EnumUnderlying!.Is64) { c.I64(System.Convert.ToInt64(f.Literal)); c.Emit(Op.I64Eq); }
            else { c.I32(unchecked((int)System.Convert.ToInt64(f.Literal))); c.Emit(Op.I32Eq); }
            var hit = c.If(); c.I32(Literal(f.Name)); c.Emit(Op.Return); c.End(hit);
        }
        c.Get(0);
        if (k.EnumUnderlying!.Is64) c.Call(Rt.Func("i64_to_str")); else c.Call(Rt.Func("i32_to_str"));
    });

    WasmFunc? exceptionToString;

    /// <summary>`Type: message` (Exception.ToString without a stack trace).</summary>
    internal WasmFunc ExceptionToString => exceptionToString ??= Helper("Exception.ToString", I_I, ["e"], c =>
    {
        c.Get(0); c.Call(TypeNameFunc);
        c.I32(Literal(": ")); c.Call(Rt.Func("str_concat"));
        c.Get(0); c.Mem(Op.I32Load, (uint)RuntimeClass("System.Exception").FieldsByName["Message"].Offset);
        c.Call(Rt.Func("str_concat"));
    });

    WasmFunc? inspectStr;

    /// <summary>`"text"` with `\\`, `"` and newlines escaped (Alice inspect).</summary>
    internal WasmFunc InspectStr => inspectStr ??= Helper("String.inspect", I_I, ["s"], c =>
    {
        int r = c.Local(ValType.I32, "r"), i = c.Local(ValType.I32, "i"), n = c.Local(ValType.I32, "n"), ch = c.Local(ValType.I32, "ch"), fr = c.Local(ValType.I32, "frame");
        c.Get(0); c.Emit(Op.I32Eqz);
        var nul = c.If(); c.I32(Literal("nil")); c.Emit(Op.Return); c.End(nul);
        c.I32(8); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.Get(fr); c.Get(0); c.Mem(Op.I32Store);
        c.I32(Literal("\"")); c.Set(r);
        c.Get(0); c.Call(Rt.Func("str_len")); c.Set(n);
        var done = c.Block();
        var loop = c.Loop();
        c.Get(i); c.Get(n); c.Emit(Op.I32GeU); c.BrIf(done);
        c.Get(0); c.Get(i); c.Emit(Op.I32Add); c.Mem(Op.I32Load8U, 8); c.Set(ch);
        c.Get(fr); c.Get(r); c.Mem(Op.I32Store, 4);
        c.Get(r);
        c.Get(ch); c.I32('\\'); c.Emit(Op.I32Eq);
        var bs = c.If(ValType.I32); c.I32(Literal("\\\\"));
        c.Else();
        c.Get(ch); c.I32('"'); c.Emit(Op.I32Eq);
        var q = c.If(ValType.I32); c.I32(Literal("\\\""));
        c.Else();
        c.Get(ch); c.I32('\n'); c.Emit(Op.I32Eq);
        var nl = c.If(ValType.I32); c.I32(Literal("\\n"));
        c.Else();
        c.Get(0); c.Get(i); c.I32(1); c.Call(Rt.Func("str_sub"));
        c.End(nl);
        c.End(q);
        c.End(bs);
        c.Call(Rt.Func("str_concat")); c.Set(r);
        c.Get(i); c.I32(1); c.Emit(Op.I32Add); c.Set(i);
        c.Br(loop);
        c.End(loop);
        c.End(done);
        c.Get(r); c.I32(Literal("\"")); c.Call(Rt.Func("str_concat")); c.Set(r);
        c.Get(fr); c.I32(8); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
        c.Get(r);
    });

    /// <summary>`[1, 2, 3]`: an array or list as Alice `str` prints it (elements as `inspect`).</summary>
    internal WasmFunc SeqToString(WT t)
    {
        var elem = t.Elem!;
        if (elem.IsStruct || elem.K is WK.Class or WK.Array or WK.List or WK.Func) throw new WasmUnsupportedException("ToStr:sequence-of", null, elem.Name);
        return Helper(t.Name + ".to_s", I_I, ["xs"], c =>
        {
            int r = c.Local(ValType.I32, "r"), i = c.Local(ValType.I32, "i"), n = c.Local(ValType.I32, "n"), items = c.Local(ValType.I32, "items"), fr = c.Local(ValType.I32, "frame");
            c.I32(8); c.Call(Rt.Func("frame_enter")); c.Set(fr);
            c.Get(fr); c.Get(0); c.Mem(Op.I32Store);
            if (t.K == WK.List) { c.Get(0); c.Mem(Op.I32Load, 12); c.Set(items); c.Get(0); c.Mem(Op.I32Load, 8); c.Set(n); }
            else { c.Get(0); c.Set(items); c.Get(0); c.Mem(Op.I32Load, 4); c.Set(n); }
            c.I32(Literal("[")); c.Set(r);
            var done = c.Block();
            var loop = c.Loop();
            c.Get(i); c.Get(n); c.Emit(Op.I32GeS); c.BrIf(done);
            c.Get(fr); c.Get(r); c.Mem(Op.I32Store, 4);
            c.Get(i);
            var sep = c.If(); c.Get(r); c.I32(Literal(", ")); c.Call(Rt.Func("str_concat")); c.Set(r); c.Get(fr); c.Get(r); c.Mem(Op.I32Store, 4); c.End(sep);
            c.Get(r);
            c.Get(items); c.Get(i); c.I32(elem.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.Mem(elem.Load, 8);
            FormatElem(c, elem);
            c.Call(Rt.Func("str_concat")); c.Set(r);
            c.Get(i); c.I32(1); c.Emit(Op.I32Add); c.Set(i);
            c.Br(loop);
            c.End(loop);
            c.End(done);
            c.Get(r); c.I32(Literal("]")); c.Call(Rt.Func("str_concat")); c.Set(r);
            c.Get(fr); c.I32(8); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
            c.Get(r);
        });
    }

    void FormatElem(WasmCode c, WT t)
    {
        switch (t.K)
        {
            case WK.Str: c.Call(InspectStr); return;
            case WK.Bool: c.Call(Rt.Func("bool_to_str")); return;
            case WK.Char: c.Call(Rt.Func("str_from_char")); return;
            case WK.I8 or WK.I16 or WK.I32: c.Call(Rt.Func("i32_to_str")); return;
            case WK.U8 or WK.U16 or WK.U32: c.Call(Rt.Func("u32_to_str")); return;
            case WK.I64: c.Call(Rt.Func("i64_to_str")); return;
            case WK.U64: c.I32(0); c.Call(Rt.Func("u64_to_str")); return;
            case WK.F64 or WK.F32:
                if (t.K == WK.F32) c.Emit(Op.F64PromoteF32);
                c.I32(t.K == WK.F32 ? 1 : 0); c.I32(1);
                c.Call((RtAl("fmt_float") ?? throw new WasmUnsupportedException("Runtime:fmt_float")).Func);
                return;
            case WK.Enum: c.Call(EnumToString(t.Cls!)); return;
        }
        throw new WasmUnsupportedException("ToStr:element", null, t.Name);
    }

    // ------------------------------------------------------------------ closures

    readonly Dictionary<WMethod, WasmFunc> thunks = new();

    /// <summary>`(env, params) -> ret` calling a static method: a method group as a closure.</summary>
    internal WasmFunc StaticThunk(WMethod m)
    {
        if (thunks.TryGetValue(m, out var f)) return f;
        if (m.Ret.IsStruct || m.Params.Any(p => p.IsStruct)) throw new WasmUnsupportedException("AirMethodGroup:struct-signature", null, m.ToString());
        var ps = new List<ValType> { ValType.I32 };
        ps.AddRange(m.Params.Select(p => p.Val));
        f = M.Define("thunk " + m, new FuncSig(ps.ToArray(), m.Func.Sig.Results));
        var c = f.Code!;
        for (int i = 0; i < m.Params.Count; i++) c.Get(i + 1);
        c.Call(m.Func);
        c.Finish();
        thunks[m] = f;
        return f;
    }

    /// <summary>The call_indirect signature of a closure: (env, params) -> ret.</summary>
    internal int ClosureSig(WT f)
    {
        var ps = new List<ValType> { ValType.I32 };
        ps.AddRange(f.Params.Select(p => p.Val));
        return M.TypeIndex(new FuncSig(ps.ToArray(), f.Ret!.K == WK.Void ? [] : [f.Ret.Val]));
    }
}
