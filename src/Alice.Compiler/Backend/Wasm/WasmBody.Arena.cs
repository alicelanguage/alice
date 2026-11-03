using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Wasm;

// The affine arena nodes (Mir/MExpr.Arena.cs, ADR 0037) in linear memory. A term is an i64 word, as in the IL
// backend: bit 63 packed, bits 56-62 the variant tag, bits 32-55 the lane (always 0: wasm runs one lane), bits 0-31
// the payload or the slot index. Slots live in one slab per width (rt.wat, arena section): Alloc pops the free list
// or bumps, Take/Borrow copy the slot into a frame temp (a slot struct of N u64 words; one word is the i64 itself)
// and Take pushes it on the free list, Drop frees (recursively with a mask). Regions save and restore every cursor.
public sealed partial class WasmBody
{
    WT Arena(AirArenaExpr e)
    {
        switch (e)
        {
            case AirTermPack p:
                code.I64(unchecked((long)AirSlab.PackedHead(p.Tag)));
                if (p.Payload != null)
                {
                    Payload(p.Payload, p.Kind);
                    code.Emit(Op.I64ExtendI32U);
                    code.Emit(Op.I64Or);
                }
                return WT.U64;
            case AirTermTag t:
                Emit(t.Term, WT.U64);
                code.I64(56); code.Emit(Op.I64ShrU); code.Emit(Op.I32WrapI64);
                return WT.I32;
            case AirTermPayload p:
                Emit(p.Term, WT.U64);
                code.Emit(Op.I32WrapI64);
                return Decode(p.Kind, payload: true);
            case AirSlotWord w:
                if (w.Layout.Words == 1) Emit(w.Slot, WT.U64);
                else
                {
                    Emit(w.Slot, SlotT(w.Layout.Words));
                    code.Mem(Op.I64Load, (uint)(8 * w.Word));
                }
                return Decode(w.Kind, payload: false);
            case AirArenaAlloc a: return Alloc(a);
            case AirArenaTake t: return Read(t.Layout, t.Term, t.Frees);
            case AirArenaBorrow b: return Read(b.Layout, b.Term, frees: false);
            case AirArenaDrop d:
                code.I32(d.Layout.Words);
                Emit(d.Term, WT.U64);
                code.I32(unchecked((int)(d.All != 0 ? d.Top : 0)));
                code.I32(unchecked((int)d.All));
                code.Call(c.Rt.Func("arena_drop"));
                return WT.Void;
            case AirArenaRegion:
                code.Call(c.Rt.Func("arena_region_enter"));
                return WT.I32;
            case AirArenaRegionEnd r:
                Emit(r.Region, WT.I32);
                code.Call(c.Rt.Func("arena_region_end"));
                return WT.Void;
            case AirLane:
                code.I32(0);
                return WT.I32;
        }
        throw No(e.GetType().Name);
    }

    /// <summary>The slot value of an N-word slab: a struct of N u64 words (one word: the u64 itself).</summary>
    WT SlotT(int words) => words == 1 ? WT.U64 : c.SlotClass(words).Type;

    static WT KindT(WordKind k) => k switch
    {
        WordKind.Term or WordKind.U64 => WT.U64,
        WordKind.I64 => WT.I64,
        WordKind.U32 => WT.U32,
        WordKind.I32 => WT.I32,
        WordKind.U16 => WT.U16,
        WordKind.I16 => WT.I16,
        WordKind.U8 => WT.U8,
        WordKind.I8 => WT.I8,
        WordKind.Bool => WT.Bool,
        WordKind.Char => WT.Char,
        WordKind.F32 => WT.F32,
        _ => WT.F64,
    };

    /// <summary>A narrow field value as its 32-bit payload bits (i32 on the stack).</summary>
    void Payload(MExpr v, WordKind k)
    {
        Emit(v, KindT(k));
        switch (k)
        {
            case WordKind.I16: code.I32(0xFFFF); code.Emit(Op.I32And); break;
            case WordKind.I8: code.I32(0xFF); code.Emit(Op.I32And); break;
            case WordKind.F32: code.Emit(Op.I32ReinterpretF32); break;
        }
    }

    /// <summary>A field value as its 64-bit slot word (i64 on the stack).</summary>
    void Word(MExpr v, WordKind k)
    {
        switch (k)
        {
            case WordKind.Term or WordKind.U64 or WordKind.I64: Emit(v, KindT(k)); return;
            case WordKind.F64: Emit(v, WT.F64); code.Emit(Op.I64ReinterpretF64); return;
        }
        Payload(v, k);
        code.Emit(Op.I64ExtendI32U);
    }

    /// <summary>A slot word (i64) or, with <paramref name="payload"/>, payload bits (i32) -> the field type.</summary>
    WT Decode(WordKind k, bool payload)
    {
        if (k is WordKind.Term or WordKind.U64 or WordKind.I64) return KindT(k);
        if (k == WordKind.F64) { code.Emit(Op.F64ReinterpretI64); return WT.F64; }
        if (!payload) code.Emit(Op.I32WrapI64);
        switch (k)
        {
            case WordKind.U16 or WordKind.Char: code.I32(0xFFFF); code.Emit(Op.I32And); break;
            case WordKind.I16: code.Emit(Op.I32Extend16S); break;
            case WordKind.U8: code.I32(0xFF); code.Emit(Op.I32And); break;
            case WordKind.I8: code.Emit(Op.I32Extend8S); break;
            case WordKind.Bool: code.I32(0); code.Emit(Op.I32Ne); break;
            case WordKind.F32: code.Emit(Op.F32ReinterpretI32); break;
        }
        return KindT(k);
    }

    /// <summary>Byte offset (from the runtime area global) of the slab state of width <paramref name="n"/> (rt.wat).</summary>
    static uint SlabState(int n) => (uint)(136720 + 32 * n);

    /// <summary>Pushes `data + i * 8n` (slot i is at that address + 8, after the array header).</summary>
    void SlotBase(int n, int i)
    {
        code.GGet(c.Rt.Global("rt")); code.Mem(Op.I32Load, SlabState(n));
        code.Get(i); code.I32(8 * n); code.Emit(Op.I32Mul); code.Emit(Op.I32Add);
    }

    /// <summary>Every word first (a field may allocate, and the slab may move), then a slot (the free-list pop or
    /// the bump, inline; growth is the rt.wat call), then the stores.</summary>
    WT Alloc(AirArenaAlloc a)
    {
        int n = a.Layout.Words;
        uint st = SlabState(n);
        var rt = c.Rt.Global("rt");
        var words = new int[n];
        for (int k = 0; k < n; k++)
        {
            words[k] = Temp(ValType.I64);
            int f = a.FieldAt(k);
            if (f >= 0) Word(a.Fields[f], a.Kinds[f]);
            else code.I64((a.Layout.TermWords & (1u << k)) != 0 ? unchecked((long)AirSlab.PackedNil) : 0);
            code.Set(words[k]);
        }
        int i = Temp(ValType.I32), p = Temp(ValType.I32), head = Temp(ValType.I32);
        code.GGet(rt); code.Mem(Op.I32Load, st + 12); code.Tee(head);
        var pop = code.If();
        {
            code.Get(head); code.I32(1); code.Emit(Op.I32Sub); code.Set(i);
            SlotBase(n, i); code.Set(p);
            code.GGet(rt); code.Get(p); code.Mem(Op.I32Load, 8); code.Mem(Op.I32Store, st + 12);
        }
        code.Else();
        {
            code.GGet(rt); code.Mem(Op.I32Load, st + 4); code.Set(i);
            code.Get(i); code.GGet(rt); code.Mem(Op.I32Load, st + 8); code.Emit(Op.I32GeU);
            var grow = code.If();
            code.GGet(rt); code.I32((int)st); code.Emit(Op.I32Add); code.I32(n); code.Call(c.Rt.Func("arena_grow"));
            code.End(grow);
            code.GGet(rt); code.Get(i); code.I32(1); code.Emit(Op.I32Add); code.Mem(Op.I32Store, st + 4);
            SlotBase(n, i); code.Set(p);
        }
        code.End(pop);
        for (int k = 0; k < n; k++) { code.Get(p); code.Get(words[k]); code.Mem(Op.I64Store, (uint)(8 + 8 * k)); }
        // the term: tag << 56 | lane 0 | slot index
        code.I64(unchecked((long)((ulong)(uint)a.Tag << 56)));
        code.Get(i); code.Emit(Op.I64ExtendI32U);
        code.Emit(Op.I64Or);
        return WT.U64;
    }

    /// <summary>Take / Borrow inline: the stale check `i &lt; Len` unless the slab is <see cref="SlotProof.Live"/> (as
    /// the IL backend), the words copied out (one word: the i64 itself, else a frame temp slot struct), then for Take
    /// the free-list push (the next head into the first word).</summary>
    WT Read(AirSlab layout, MExpr term, bool frees)
    {
        int n = layout.Words;
        uint st = SlabState(n);
        var rt = c.Rt.Global("rt");
        int i = Temp(ValType.I32), p = Temp(ValType.I32);
        Emit(term, WT.U64); code.Emit(Op.I32WrapI64); code.Set(i);
        if (layout.Proof != SlotProof.Live)
        {
            code.Get(i); code.GGet(rt); code.Mem(Op.I32Load, st + 4); code.Emit(Op.I32GeU);
            var stale = code.If();
            code.I32(AirToWasm.ErrStaleTerm); code.Call(c.ThrowCodeFunc); code.Emit(Op.Unreachable);
            code.End(stale);
        }
        SlotBase(n, i); code.Set(p);
        WT result;
        int v = -1;
        Var? tmp = null;
        if (n == 1)
        {
            v = Temp(ValType.I64);
            code.Get(p); code.Mem(Op.I64Load, 8); code.Set(v);
            result = WT.U64;
        }
        else
        {
            result = SlotT(n);
            tmp = FrameTemp(result);
            for (int k = 0; k < n; k++) { FrameAddr(tmp.Frame); code.Get(p); code.Mem(Op.I64Load, (uint)(8 + 8 * k)); code.Mem(Op.I64Store, (uint)(8 * k)); }
        }
        if (frees)
        {
            code.Get(p); code.GGet(rt); code.Mem(Op.I32Load, st + 12); code.Emit(Op.I64ExtendI32U); code.Mem(Op.I64Store, 8);
            code.GGet(rt); code.Get(i); code.I32(1); code.Emit(Op.I32Add); code.Mem(Op.I32Store, st + 12);
        }
        if (n == 1) code.Get(v); else FrameAddr(tmp!.Frame);
        return result;
    }
}
