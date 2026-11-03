namespace Alice.Compiler.Backend.Wasm;

// Dictionary<K, V> for value-hashed keys: [header][count @8][capacity @12][keys @16][values @20][states @24],
// open addressing with linear probing (state 0 empty, 1 full, 2 deleted), capacity a power of two, grown at 3/4.
public sealed partial class AirToWasm
{
    internal const int DictType = 8;

    static string DKey(WT d) => d.KeyT!.Name + "," + d.Elem!.Name;

    internal WasmFunc DictNew(WT d) => Helper("Dictionary<" + DKey(d) + ">.new", new FuncSig([], [ValType.I32]), [], c =>
    {
        int o = c.Local(ValType.I32, "o"), fr = c.Local(ValType.I32, "frame");
        c.I32(8); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.I32(32); c.I32(DictType); c.Call(Rt.Func("alloc")); c.Set(o);
        c.Get(fr); c.Get(o); c.Mem(Op.I32Store);
        DictArrays(c, d, o, () => c.I32(8));
        c.Get(fr); c.I32(8); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
        c.Get(o);
    });

    /// <summary>Fresh key, value and state arrays of the capacity <paramref name="cap"/> pushes, stored into <paramref name="o"/>.</summary>
    void DictArrays(WasmCode c, WT d, int o, Action cap)
    {
        c.Get(o); cap(); c.Mem(Op.I32Store, 12);
        c.Get(o); cap(); c.I32(Math.Max(1, d.KeyT!.Size)); c.I32(d.KeyT.ArrayKind); c.Call(Rt.Func("array_new")); c.Mem(Op.I32Store, 16);
        c.Get(o); cap(); c.I32(Math.Max(1, d.Elem!.Size)); c.I32(d.Elem.ArrayKind); c.Call(Rt.Func("array_new")); c.Mem(Op.I32Store, 20);
        c.Get(o); cap(); c.I32(1); c.I32(NumArrayType); c.Call(Rt.Func("array_new")); c.Mem(Op.I32Store, 24);
    }

    /// <summary>The hash of the key on the stack (an i32).</summary>
    void Hash(WasmCode c, WT k)
    {
        switch (k.Val)
        {
            case ValType.I64: { int t = c.Local(ValType.I64); c.Tee(t); c.Emit(Op.I32WrapI64); c.Get(t); c.I64(32); c.Emit(Op.I64ShrU); c.Emit(Op.I32WrapI64); c.Emit(Op.I32Xor); break; }
            case ValType.F64: { c.Emit(Op.I64ReinterpretF64); int t = c.Local(ValType.I64); c.Tee(t); c.Emit(Op.I32WrapI64); c.Get(t); c.I64(32); c.Emit(Op.I64ShrU); c.Emit(Op.I32WrapI64); c.Emit(Op.I32Xor); break; }
            case ValType.F32: c.Emit(Op.I32ReinterpretF32); break;
            default: if (k.K == WK.Str) { c.Call(Rt.Func("str_hash")); return; } break;
        }
        // mix (murmur3 finalizer)
        int h = c.Local(ValType.I32);
        c.Tee(h); c.Get(h); c.I32(16); c.Emit(Op.I32ShrU); c.Emit(Op.I32Xor); c.I32(unchecked((int)0x85EBCA6B)); c.Emit(Op.I32Mul);
        c.Tee(h); c.Get(h); c.I32(13); c.Emit(Op.I32ShrU); c.Emit(Op.I32Xor);
    }

    void KeyEq(WasmCode c, WT k)
    {
        if (k.K == WK.Str) { c.Call(Rt.Func("str_eq")); return; }
        c.Emit(k.Val switch { ValType.I64 => Op.I64Eq, ValType.F64 => Op.F64Eq, ValType.F32 => Op.F32Eq, _ => Op.I32Eq });
    }

    /// <summary>`(dict, key) -> slot`, -1 when absent.</summary>
    internal WasmFunc DictFind(WT d) => Helper("Dictionary<" + DKey(d) + ">.find", new FuncSig([ValType.I32, d.KeyT!.Val], [ValType.I32]), ["d", "key"], c =>
    {
        var k = d.KeyT!;
        int h = c.Local(ValType.I32, "h"), mask = c.Local(ValType.I32, "mask"), st = c.Local(ValType.I32, "st");
        c.Get(0); c.Emit(Op.I32Eqz);
        var nul = c.If(); c.I32(ErrNull); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(nul);
        c.Get(0); c.Mem(Op.I32Load, 12); c.I32(1); c.Emit(Op.I32Sub); c.Set(mask);
        c.Get(1); Hash(c, k); c.Get(mask); c.Emit(Op.I32And); c.Set(h);
        var done = c.Block();
        var loop = c.Loop();
        c.Get(0); c.Mem(Op.I32Load, 24); c.Get(h); c.Emit(Op.I32Add); c.Mem(Op.I32Load8U, 8); c.Tee(st);
        c.Emit(Op.I32Eqz); c.BrIf(done);
        c.Get(st); c.I32(1); c.Emit(Op.I32Eq);
        var full = c.If();
        c.Get(0); c.Mem(Op.I32Load, 16); c.Get(h); c.I32(k.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.Mem(k.Load, 8);
        c.Get(1);
        KeyEq(c, k);
        var hit = c.If(); c.Get(h); c.Emit(Op.Return); c.End(hit);
        c.End(full);
        c.Get(h); c.I32(1); c.Emit(Op.I32Add); c.Get(mask); c.Emit(Op.I32And); c.Set(h);
        c.Br(loop);
        c.End(loop);
        c.End(done);
        c.I32(-1);
    });

    /// <summary>`(dict, key, value)`: insert or replace; grows (rehash) at 3/4 load.</summary>
    internal WasmFunc DictSet(WT d) => Helper("Dictionary<" + DKey(d) + ">.set", new FuncSig([ValType.I32, d.KeyT!.Val, d.Elem!.IsStruct ? ValType.I32 : d.Elem.Val], []), ["d", "key", "value"], c =>
    {
        var k = d.KeyT!;
        var v = d.Elem!;
        int slot = c.Local(ValType.I32, "slot"), mask = c.Local(ValType.I32, "mask"), h = c.Local(ValType.I32, "h"), st = c.Local(ValType.I32, "st"), fr = c.Local(ValType.I32, "frame");
        c.Get(0); c.Get(1); c.Call(DictFind(d)); c.Tee(slot);
        c.I32(0); c.Emit(Op.I32GeS);
        var found = c.If();
        c.Get(0); c.Mem(Op.I32Load, 20); c.Get(slot); c.I32(v.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add);
        if (v.IsStruct) { c.I32(8); c.Emit(Op.I32Add); c.Get(2); c.I32(v.Size); c.Call(Rt.Func("memcpy")); } else { c.Get(2); c.Mem(v.Store, 8); }
        c.Emit(Op.Return);
        c.End(found);
        // grow when (count + 1) * 4 > capacity * 3 (deleted slots count as used: rehash drops them)
        c.Get(0); c.Mem(Op.I32Load, 8); c.I32(1); c.Emit(Op.I32Add); c.I32(2); c.Emit(Op.I32Shl);
        c.Get(0); c.Mem(Op.I32Load, 12); c.I32(3); c.Emit(Op.I32Mul);
        c.Emit(Op.I32GtU);
        var grow = c.If();
        c.I32(16); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.Get(fr); c.Get(0); c.Mem(Op.I32Store);
        if (k.IsRef) { c.Get(fr); c.Get(1); c.Mem(Op.I32Store, 4); }
        if (v.IsRef) { c.Get(fr); c.Get(2); c.Mem(Op.I32Store, 8); }
        c.Get(0); c.Call(DictRehash(d));
        c.Get(fr); c.I32(16); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
        c.End(grow);
        // first empty or deleted slot
        c.Get(0); c.Mem(Op.I32Load, 12); c.I32(1); c.Emit(Op.I32Sub); c.Set(mask);
        c.Get(1); Hash(c, k); c.Get(mask); c.Emit(Op.I32And); c.Set(h);
        var done = c.Block();
        var loop = c.Loop();
        c.Get(0); c.Mem(Op.I32Load, 24); c.Get(h); c.Emit(Op.I32Add); c.Mem(Op.I32Load8U, 8); c.I32(1); c.Emit(Op.I32Ne); c.BrIf(done);
        c.Get(h); c.I32(1); c.Emit(Op.I32Add); c.Get(mask); c.Emit(Op.I32And); c.Set(h);
        c.Br(loop);
        c.End(loop);
        c.End(done);
        c.Get(0); c.Mem(Op.I32Load, 24); c.Get(h); c.Emit(Op.I32Add); c.I32(1); c.Mem(Op.I32Store8, 8);
        c.Get(0); c.Mem(Op.I32Load, 16); c.Get(h); c.I32(k.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.Get(1); c.Mem(k.Store, 8);
        c.Get(0); c.Mem(Op.I32Load, 20); c.Get(h); c.I32(v.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add);
        if (v.IsStruct) { c.I32(8); c.Emit(Op.I32Add); c.Get(2); c.I32(v.Size); c.Call(Rt.Func("memcpy")); } else { c.Get(2); c.Mem(v.Store, 8); }
        c.Get(0); c.Get(0); c.Mem(Op.I32Load, 8); c.I32(1); c.Emit(Op.I32Add); c.Mem(Op.I32Store, 8);
        _ = st;
    });

    /// <summary>Doubles the capacity and reinserts every full slot (the dictionary is rooted by the caller).</summary>
    WasmFunc DictRehash(WT d) => Helper("Dictionary<" + DKey(d) + ">.rehash", new FuncSig([ValType.I32], []), ["d"], c =>
    {
        var k = d.KeyT!;
        var v = d.Elem!;
        int ok = c.Local(ValType.I32, "keys"), ov = c.Local(ValType.I32, "vals"), os = c.Local(ValType.I32, "states"), cap = c.Local(ValType.I32, "cap"), i = c.Local(ValType.I32, "i"), fr = c.Local(ValType.I32, "frame");
        c.Get(0); c.Mem(Op.I32Load, 16); c.Set(ok);
        c.Get(0); c.Mem(Op.I32Load, 20); c.Set(ov);
        c.Get(0); c.Mem(Op.I32Load, 24); c.Set(os);
        c.Get(0); c.Mem(Op.I32Load, 12); c.Set(cap);
        // the old arrays stay rooted in a frame while the new ones are allocated
        c.I32(16); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.Get(fr); c.Get(ok); c.Mem(Op.I32Store);
        c.Get(fr); c.Get(ov); c.Mem(Op.I32Store, 4);
        c.Get(fr); c.Get(os); c.Mem(Op.I32Store, 8);
        DictArrays(c, d, 0, () => { c.Get(cap); c.I32(1); c.Emit(Op.I32Shl); });
        c.Get(0); c.I32(0); c.Mem(Op.I32Store, 8);
        var done = c.Block();
        var loop = c.Loop();
        c.Get(i); c.Get(cap); c.Emit(Op.I32GeU); c.BrIf(done);
        c.Get(os); c.Get(i); c.Emit(Op.I32Add); c.Mem(Op.I32Load8U, 8); c.I32(1); c.Emit(Op.I32Eq);
        var full = c.If();
        c.Get(0);
        c.Get(ok); c.Get(i); c.I32(k.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.Mem(k.Load, 8);
        c.Get(ov); c.Get(i); c.I32(v.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add);
        if (v.IsStruct) { c.I32(8); c.Emit(Op.I32Add); } else c.Mem(v.Load, 8);
        c.Call(DictSet(d));
        c.End(full);
        c.Get(i); c.I32(1); c.Emit(Op.I32Add); c.Set(i);
        c.Br(loop);
        c.End(loop);
        c.End(done);
        c.Get(fr); c.I32(16); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
    });

    /// <summary>`(dict, key) -> value address`, KeyNotFoundException when absent.</summary>
    internal WasmFunc DictGetAddr(WT d) => Helper("Dictionary<" + DKey(d) + ">.at", new FuncSig([ValType.I32, d.KeyT!.Val], [ValType.I32]), ["d", "key"], c =>
    {
        int slot = c.Local(ValType.I32, "slot");
        c.Get(0); c.Get(1); c.Call(DictFind(d)); c.Tee(slot);
        c.I32(0); c.Emit(Op.I32LtS);
        var miss = c.If(); c.I32(ErrKey); c.Call(ThrowCodeFunc); c.Emit(Op.Unreachable); c.End(miss);
        c.Get(0); c.Mem(Op.I32Load, 20); c.Get(slot); c.I32(d.Elem!.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.I32(8); c.Emit(Op.I32Add);
    });

    /// <summary>`(dict, key) -> removed?`: the slot becomes deleted, its key and value are cleared.</summary>
    internal WasmFunc DictRemove(WT d) => Helper("Dictionary<" + DKey(d) + ">.remove", new FuncSig([ValType.I32, d.KeyT!.Val], [ValType.I32]), ["d", "key"], c =>
    {
        var k = d.KeyT!;
        var v = d.Elem!;
        int slot = c.Local(ValType.I32, "slot");
        c.Get(0); c.Get(1); c.Call(DictFind(d)); c.Tee(slot);
        c.I32(0); c.Emit(Op.I32LtS);
        var miss = c.If(); c.I32(0); c.Emit(Op.Return); c.End(miss);
        c.Get(0); c.Mem(Op.I32Load, 24); c.Get(slot); c.Emit(Op.I32Add); c.I32(2); c.Mem(Op.I32Store8, 8);
        c.Get(0); c.Mem(Op.I32Load, 16); c.Get(slot); c.I32(k.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.I32(8); c.Emit(Op.I32Add); c.I32(0); c.I32(k.Size); c.Prefixed(Op.MemoryFill); c.B(0);
        c.Get(0); c.Mem(Op.I32Load, 20); c.Get(slot); c.I32(v.Size); c.Emit(Op.I32Mul); c.Emit(Op.I32Add); c.I32(8); c.Emit(Op.I32Add); c.I32(0); c.I32(v.Size); c.Prefixed(Op.MemoryFill); c.B(0);
        c.Get(0); c.Get(0); c.Mem(Op.I32Load, 8); c.I32(1); c.Emit(Op.I32Sub); c.Mem(Op.I32Store, 8);
        c.I32(1);
    });
}
