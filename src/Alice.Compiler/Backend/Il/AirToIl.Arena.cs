using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Alice.Compiler.Mir;
using Alice.Runtime;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// IL for the affine arena nodes (Mir/MExpr.Arena.cs; ADR 0040 section 2). Terms are `uint64`; packing and the
/// `case` key are bit operations; the lane slab is read once per method into a local (lazily, at its first use);
/// slot access goes through the lane cursor: `(uint)(i - CurBase) &lt; Cur.Length` selects
/// `Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Cur), i - CurBase)` and the freed-bit word
/// `CurFreed[(i - CurBase) >> 6]` (no bounds checks, no Locate), else the slab's `Ref(i)` and its RawFreed/RawCheck/RawLive
/// calls; the freed bit is tested, set and cleared inline (a set bit on Take/Borrow calls the slab, which throws); the
/// stale check `i &lt; Len` is dropped under <see cref="SlotProof.Live"/>. Take reads the slot and
/// pushes the free list (load Free, store it into the slot's first uint, store i into Free; a slot below RegionBase
/// goes to its own level's list through FreeOuter, as in Arena.Take) when the term's lane is
/// this thread's; a foreign or stale term takes the out-of-line `Arena.RawForeign`. Alloc inlines the free-list pop
/// and the bump (a bumped slot below Dirty clears its bit); `Grow` stays a call.
/// </summary>
internal sealed partial class IlBody
{
    readonly Dictionary<(IlBuilder, Type), IlLocal> arenaSlabs = new();

    static readonly MethodInfo DataRef = typeof(MemoryMarshal).GetMethods()
        .Single(m => m.Name == nameof(MemoryMarshal.GetArrayDataReference) && m.IsGenericMethodDefinition && m.GetParameters()[0].ParameterType.IsArray);
    static readonly MethodInfo AddRef = typeof(Unsafe).GetMethods()
        .Single(m => m.Name == nameof(Unsafe.Add) && m.IsGenericMethodDefinition && m.GetParameters() is [{ ParameterType.IsByRef: true }, { } p1] && p1.ParameterType == typeof(nuint));
    static readonly MethodInfo AsRef = typeof(Unsafe).GetMethods()
        .Single(m => m.Name == nameof(Unsafe.As) && m.GetGenericArguments().Length == 2 && m.GetParameters()[0].ParameterType.IsByRef);

    static Type SlotType(AirSlab l) =>
        l.Words == 1 ? typeof(ulong) : Type.GetType("System.ValueTuple`" + l.Words)!.MakeGenericType(Enumerable.Repeat(typeof(ulong), l.Words).ToArray());

    static Type SlabType(Type slot) => typeof(ArenaSlab<>).MakeGenericType(slot);
    static Type ArenaType(Type slot) => typeof(Arena<>).MakeGenericType(slot);

    IlField SlabField(Type slot, string name) => c.Module.Import(SlabType(slot).GetField(name)!);
    IlMethod SlabMethod(Type slot, string name) => c.Module.Import(SlabType(slot).GetMethod(name)!);
    IlMethod ArenaMethod(Type slot, string name) => c.Module.Import(ArenaType(slot).GetMethod(name)!);

    Type Arena(AirArenaExpr e)
    {
        switch (e)
        {
            case AirTermPack p:
                il.EmitLoadConst(unchecked((long)AirSlab.PackedHead(p.Tag)));
                if (p.Payload != null)
                {
                    Field(p.Payload, ClrOf(p.Kind));
                    switch (p.Kind)
                    {
                        case WordKind.I16: il.EmitConvert(PrimitiveTypeCode.UInt16); break;
                        case WordKind.I8: il.EmitConvert(PrimitiveTypeCode.Byte); break;
                        case WordKind.F32: il.EmitCall(c.Module.Import(typeof(BitConverter).GetMethod(nameof(BitConverter.SingleToUInt32Bits))!)); break;
                    }
                    il.EmitConvert(PrimitiveTypeCode.UInt64);   // zero-extends the 32-bit payload
                    il.EmitBinary(IlBinOp.Or);
                }
                return typeof(ulong);
            case AirTermTag t:
                Emit(t.Term, typeof(ulong));
                il.EmitLoadConst(56);
                il.EmitBinary(IlBinOp.Shr, unsigned: true);
                il.EmitConvert(PrimitiveTypeCode.Int32);
                return typeof(int);
            case AirTermPayload p:
                Emit(p.Term, typeof(ulong));
                il.EmitConvert(PrimitiveTypeCode.UInt32);
                return Decode(p.Kind, payload: true);
            case AirSlotWord w:
                {
                    var slot = SlotType(w.Layout);
                    if (w.Layout.Words == 1) Emit(w.Slot, typeof(ulong));
                    else
                    {
                        if (w.Slot is not AirLocalRef lr || FindLocal(lr.Name) is not { } l) throw No("AirSlotWord:slot", w.Slot.GetType().Name);
                        il.EmitLoadLocalAddress(l.L);
                        il.EmitLoadField(c.Module.Import(slot.GetField("Item" + (w.Word + 1))!));
                    }
                    return Decode(w.Kind, payload: false);
                }
            case AirArenaAlloc a: return Alloc(a);
            case AirArenaTake t: return Read(t.Layout, t.Term, t.Frees);
            case AirArenaBorrow b: return Read(b.Layout, b.Term, frees: false);
            case AirArenaDrop d: Drop(d); return typeof(void);
            case AirArenaRegion:
                il.EmitCall(c.Module.Import(typeof(ArenaRegion).GetMethod(nameof(ArenaRegion.Enter))!));
                return typeof(ArenaRegion);
            case AirArenaRegionEnd r:
                {
                    if (r.Region is not AirLocalRef lr || FindLocal(lr.Name) is not { } l) throw No("AirArenaRegionEnd:region");
                    il.EmitLoadLocalAddress(l.L);
                    il.EmitCall(c.Module.Import(typeof(ArenaRegion).GetMethod(nameof(ArenaRegion.Dispose))!));
                    return typeof(void);
                }
            case AirLane:
                il.EmitCall(c.Module.Import(typeof(ArenaLane).GetProperty(nameof(ArenaLane.Current))!.GetMethod!));
                return typeof(int);
        }
        throw No(e.GetType().Name);
    }

    static Type ClrOf(WordKind k) => k switch
    {
        WordKind.Term or WordKind.U64 => typeof(ulong),
        WordKind.I64 => typeof(long),
        WordKind.U32 => typeof(uint),
        WordKind.I32 => typeof(int),
        WordKind.U16 => typeof(ushort),
        WordKind.I16 => typeof(short),
        WordKind.U8 => typeof(byte),
        WordKind.I8 => typeof(sbyte),
        WordKind.Bool => typeof(bool),
        WordKind.Char => typeof(char),
        WordKind.F32 => typeof(float),
        _ => typeof(double),
    };

    /// <summary>A uint64 slot word (or, with <paramref name="payload"/>, a uint32 payload) on the stack -> the field type.</summary>
    Type Decode(WordKind k, bool payload)
    {
        switch (k)
        {
            case WordKind.Term or WordKind.U64 or WordKind.I64: break;
            case WordKind.U32: if (!payload) il.EmitConvert(PrimitiveTypeCode.UInt32); break;
            case WordKind.I32: il.EmitConvert(PrimitiveTypeCode.Int32); break;
            case WordKind.U16 or WordKind.Char: il.EmitConvert(PrimitiveTypeCode.UInt16); break;
            case WordKind.I16: il.EmitConvert(PrimitiveTypeCode.Int16); break;
            case WordKind.U8: il.EmitConvert(PrimitiveTypeCode.Byte); break;
            case WordKind.I8: il.EmitConvert(PrimitiveTypeCode.SByte); break;
            case WordKind.Bool:
                if (!payload) il.EmitConvert(PrimitiveTypeCode.UInt32);
                il.EmitLoadConst(0);
                il.Emit(ILOpCode.Cgt_un);
                break;
            case WordKind.F32:
                if (!payload) il.EmitConvert(PrimitiveTypeCode.UInt32);
                il.EmitCall(c.Module.Import(typeof(BitConverter).GetMethod(nameof(BitConverter.UInt32BitsToSingle))!));
                break;
            case WordKind.F64:
                il.EmitCall(c.Module.Import(typeof(BitConverter).GetMethod(nameof(BitConverter.UInt64BitsToDouble))!));
                break;
        }
        return ClrOf(k);
    }

    /// <summary>A field value in its field type: an explicit conversion, as the C# form's casts.</summary>
    void Field(MExpr v, Type want)
    {
        if (Literal(v, out var lit) && lit != null && IlTypes.IsNumeric(want) && IlTypes.LiteralFits(lit, want)) { Emit(v, want); return; }
        var t = Raw(v);
        if (t != want) Convert(t, want, false, implicitOnly: false);
    }

    /// <summary>A field value -> its uint64 slot word.</summary>
    void EncodeWord(MExpr v, WordKind k)
    {
        Field(v, ClrOf(k));
        switch (k)
        {
            case WordKind.Term or WordKind.U64 or WordKind.I64: break;
            case WordKind.I16: il.EmitConvert(PrimitiveTypeCode.UInt16); il.EmitConvert(PrimitiveTypeCode.UInt64); break;
            case WordKind.I8: il.EmitConvert(PrimitiveTypeCode.Byte); il.EmitConvert(PrimitiveTypeCode.UInt64); break;
            case WordKind.F32:
                il.EmitCall(c.Module.Import(typeof(BitConverter).GetMethod(nameof(BitConverter.SingleToUInt32Bits))!));
                il.EmitConvert(PrimitiveTypeCode.UInt64);
                break;
            case WordKind.F64: il.EmitCall(c.Module.Import(typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToUInt64Bits))!)); break;
            default: il.EmitConvert(PrimitiveTypeCode.UInt64); break;   // U32 I32 U16 U8 Bool Char: zero-extend
        }
    }

    /// <summary>The method's slab local of one slot type, loaded from the thread-static slab on first use.</summary>
    IlLocal Slab(Type slot)
    {
        if (!arenaSlabs.TryGetValue((il, slot), out var l))
            arenaSlabs[(il, slot)] = l = il.DeclareLocal(T.Il(SlabType(slot)));
        var ready = il.DefineLabel();
        il.EmitLoadLocal(l);
        il.EmitBranch(ready, IlCond.True);
        il.EmitCall(c.Module.Import(ArenaType(slot).GetProperty(nameof(Arena<ulong>.RawSlab))!.GetMethod!));
        il.EmitStoreLocal(l);
        il.MarkLabel(ready);
        return l;
    }

    /// <summary>
    /// Slot <paramref name="i"/> on the lane cursor's chunk (`(uint)(i - CurBase) &lt; Cur.Length`): stores `ref TSlot`
    /// in <paramref name="r"/>, `ref ulong` of the slot's freed-bit word (`CurFreed[off >> 6]`, in bounds because a
    /// chunk's bitmap has one bit per slot) in <paramref name="w"/> and the bit in <paramref name="bit"/>, without
    /// bounds checks or `Locate`, and falls through; a slot of another chunk branches to <paramref name="slow"/>.
    /// </summary>
    void LocateFast(Type slot, IlLocal sl, IlLocal i, IlLocal r, IlLocal w, IlLocal bit, IlLabel slow)
    {
        var off = Temp(typeof(uint));
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "CurBase"));
        il.EmitBinary(IlBinOp.Sub);
        il.EmitStoreLocal(off);
        il.EmitLoadLocal(off);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Cur"));
        il.Emit(ILOpCode.Ldlen);
        il.EmitConvert(PrimitiveTypeCode.UInt32);
        il.EmitBranch(slow, IlCond.GeUn);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Cur"));
        il.EmitCall(c.Module.Import(DataRef.MakeGenericMethod(slot)));
        il.EmitLoadLocal(off);
        il.EmitConvert(PrimitiveTypeCode.UIntPtr);
        il.EmitCall(c.Module.Import(AddRef.MakeGenericMethod(slot)));
        il.EmitStoreLocal(r);
        BitWord(slot, sl, off, w, bit);
    }

    /// <summary>`w = ref CurFreed[off >> 6]` (no bounds check) and `bit = 1UL &lt;&lt; (off &amp; 63)`.</summary>
    void BitWord(Type slot, IlLocal sl, IlLocal off, IlLocal w, IlLocal bit)
    {
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "CurFreed"));
        il.EmitCall(c.Module.Import(DataRef.MakeGenericMethod(typeof(ulong))));
        il.EmitLoadLocal(off);
        il.EmitLoadConst(6);
        il.EmitBinary(IlBinOp.Shr, unsigned: true);
        il.EmitConvert(PrimitiveTypeCode.UIntPtr);
        il.EmitCall(c.Module.Import(AddRef.MakeGenericMethod(typeof(ulong))));
        il.EmitStoreLocal(w);
        il.EmitLoadConst(1L);
        il.EmitLoadLocal(off);
        il.EmitLoadConst(63);
        il.EmitBinary(IlBinOp.And);
        il.EmitBinary(IlBinOp.Shl);
        il.EmitStoreLocal(bit);
    }

    /// <summary>`*w |= bit` (set) or `*w &amp;= ~bit` (clear).</summary>
    void SetBit(IlLocal w, IlLocal bit, bool set)
    {
        il.EmitLoadLocal(w);
        il.EmitLoadLocal(w);
        il.EmitLoadObj(T.Il(typeof(ulong)));
        il.EmitLoadLocal(bit);
        if (!set) il.Emit(ILOpCode.Not);
        il.EmitBinary(set ? IlBinOp.Or : IlBinOp.And);
        il.EmitStoreObj(T.Il(typeof(ulong)));
    }

    /// <summary>The freed-bit check of a Take/Drop (<paramref name="frees"/>) or Borrow, inline: a set bit calls the
    /// slab's <c>RawFreed</c>/<c>RawCheck</c>, which throws (every build, INVARIANTS #9); a Take then sets the bit.</summary>
    void CheckBit(Type slot, IlLocal sl, IlLocal i, IlLocal tt, IlLocal w, IlLocal bit, bool frees)
    {
        var ok = il.DefineLabel();
        il.EmitLoadLocal(w);
        il.EmitLoadObj(T.Il(typeof(ulong)));
        il.EmitLoadLocal(bit);
        il.EmitBinary(IlBinOp.And);
        il.EmitBranch(ok, IlCond.False);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(tt);
        il.EmitCall(SlabMethod(slot, frees ? "RawFreed" : "RawCheck"));   // throws
        il.MarkLabel(ok);
        if (frees) SetBit(w, bit, set: true);
    }

    /// <summary>Slot <paramref name="i"/> with its freed bit checked (and set for a Take): the cursor chunk inline,
    /// any other chunk through the slab's <c>Ref</c> and <c>RawFreed</c>/<c>RawCheck</c>. Leaves `ref TSlot` in r.</summary>
    void Access(Type slot, IlLocal sl, IlLocal i, IlLocal tt, IlLocal r, bool frees)
    {
        var w = il.DeclareLocal(T.Il(typeof(ulong).MakeByRefType()));
        var bit = Temp(typeof(ulong));
        var slow = il.DefineLabel();
        var done = il.DefineLabel();
        LocateFast(slot, sl, i, r, w, bit, slow);
        CheckBit(slot, sl, i, tt, w, bit, frees);
        il.EmitBranch(done);
        il.MarkLabel(slow);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitCall(SlabMethod(slot, "Ref"));
        il.EmitStoreLocal(r);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(tt);
        il.EmitCall(SlabMethod(slot, frees ? "RawFreed" : "RawCheck"));
        il.MarkLabel(done);
    }

    /// <summary>The slot's first uint (the free-list link) as `ref uint`, from the `ref TSlot` on the stack.</summary>
    void FirstUint(Type slot) => il.EmitCall(c.Module.Import(AsRef.MakeGenericMethod(slot, typeof(uint))));

    /// <summary>Take (frees) or Borrow: the slot value on the stack.</summary>
    Type Read(AirSlab layout, MExpr term, bool frees)
    {
        var slot = SlotType(layout);
        var tt = Temp(typeof(ulong));
        Emit(term, typeof(ulong));
        il.EmitStoreLocal(tt);
        var sl = Slab(slot);
        var i = Temp(typeof(uint));
        var r = il.DeclareLocal(T.Il(slot.MakeByRefType()));
        var res = Temp(slot);
        var foreign = il.DefineLabel();
        var end = il.DefineLabel();
        // the lane test: a packed or foreign-lane term never matches (the packed bit is in the mask)
        il.EmitLoadLocal(tt);
        il.EmitLoadConst(unchecked((long)Arena<ulong>.RawLaneMask));
        il.EmitBinary(IlBinOp.And);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "LaneBits"));
        il.EmitBranch(foreign, IlCond.NotEq);
        il.EmitLoadLocal(tt);
        il.EmitConvert(PrimitiveTypeCode.UInt32);
        il.EmitStoreLocal(i);
        if (layout.Proof != SlotProof.Live)
        {
            il.EmitLoadLocal(i);
            il.EmitLoadLocal(sl);
            il.EmitLoadField(SlabField(slot, "Len"));
            il.EmitBranch(foreign, IlCond.GeUn);   // stale: the slow path throws
        }
        Access(slot, sl, i, tt, r, frees);
        il.EmitLoadLocal(r);
        il.EmitLoadObj(T.Il(slot));
        il.EmitStoreLocal(res);
        if (frees) Free(slot, sl, i, r);
        il.EmitBranch(end);
        il.MarkLabel(foreign);
        il.EmitLoadLocal(tt);
        il.EmitCall(ArenaMethod(slot, "RawForeign"));
        il.EmitStoreLocal(res);
        il.MarkLabel(end);
        il.EmitLoadLocal(res);
        return slot;
    }

    void Drop(AirArenaDrop d)
    {
        var slot = SlotType(d.Layout);
        if (d.All != 0)
        {
            // recursive: the runtime walks the subtree (lane-local frees only)
            Emit(d.Term, typeof(ulong));
            il.EmitLoadConst(unchecked((int)d.Top));
            il.EmitLoadConst(unchecked((int)d.All));
            il.EmitCall(c.Module.Import(ArenaType(slot).GetMethod("DropDeep")!));
            return;
        }
        var tt = Temp(typeof(ulong));
        Emit(d.Term, typeof(ulong));
        il.EmitStoreLocal(tt);
        var sl = Slab(slot);
        var i = Temp(typeof(uint));
        var r = il.DeclareLocal(T.Il(slot.MakeByRefType()));
        var end = il.DefineLabel();
        il.EmitLoadLocal(tt);
        il.EmitLoadConst(unchecked((long)Arena<ulong>.RawLaneMask));
        il.EmitBinary(IlBinOp.And);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "LaneBits"));
        il.EmitBranch(end, IlCond.NotEq);   // packed or foreign: nothing to free
        il.EmitLoadLocal(tt);
        il.EmitConvert(PrimitiveTypeCode.UInt32);
        il.EmitStoreLocal(i);
        if (d.Layout.Proof != SlotProof.Live)
        {
            var ok = il.DefineLabel();
            il.EmitLoadLocal(i);
            il.EmitLoadLocal(sl);
            il.EmitLoadField(SlabField(slot, "Len"));
            il.EmitBranch(ok, IlCond.LtUn);
            il.EmitLoadLocal(tt);
            il.EmitCall(ArenaMethod(slot, "Drop"));   // stale: the runtime throws
            il.EmitBranch(end);
            il.MarkLabel(ok);
        }
        Access(slot, sl, i, tt, r, frees: true);
        Free(slot, sl, i, r);
        il.MarkLabel(end);
    }

    /// <summary>Frees slot <paramref name="i"/> (its freed bit already set) as <c>Arena.Take</c> does: a slot of the
    /// current level (i &gt;= RegionBase) is pushed onto the lane's free list (the slot's first uint links the old head,
    /// the head becomes i); a slot allocated before the innermost open region goes to its own level's list through
    /// <c>FreeOuter</c>, so the region's reset does not discard it.</summary>
    void Free(Type slot, IlLocal sl, IlLocal i, IlLocal r)
    {
        var outer = il.DefineLabel();
        var done = il.DefineLabel();
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "RegionBase"));
        il.EmitBranch(outer, IlCond.LtUn);
        il.EmitLoadLocal(r);
        FirstUint(slot);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Free"));
        il.EmitStoreObj(T.Il(typeof(uint)));
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitStoreField(SlabField(slot, "Free"));
        il.EmitBranch(done);
        il.MarkLabel(outer);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitCall(SlabMethod(slot, "FreeOuter"));
        il.MarkLabel(done);
    }

    Type Alloc(AirArenaAlloc a)
    {
        var slot = SlotType(a.Layout);
        // the field values first: they may allocate (nested constructors, calls)
        var v = Temp(slot);
        for (int k = 0; k < a.Layout.Words; k++)
            if (a.FieldAt(k) is int f and >= 0) EncodeWord(a.Fields[f], a.Kinds[f]);
            else il.EmitLoadConst((a.Layout.TermWords & (1u << k)) != 0 ? unchecked((long)AirSlab.PackedNil) : 0L);
        if (a.Layout.Words > 1) il.EmitNewObj(c.Module.Import(slot.GetConstructor(Enumerable.Repeat(typeof(ulong), a.Layout.Words).ToArray())!));
        il.EmitStoreLocal(v);
        var sl = Slab(slot);
        var i = Temp(typeof(uint));
        var off = Temp(typeof(uint));
        var r = il.DeclareLocal(T.Il(slot.MakeByRefType()));
        var w = il.DeclareLocal(T.Il(typeof(ulong).MakeByRefType()));
        var bit = Temp(typeof(ulong));
        var bump = il.DefineLabel();
        var room = il.DefineLabel();
        var popSlow = il.DefineLabel();
        var store = il.DefineLabel();
        // pop the free list: the head's first uint is the next head; its freed bit is cleared
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Free"));
        il.EmitStoreLocal(i);
        il.EmitLoadLocal(i);
        il.EmitLoadConst(-1);
        il.EmitBranch(bump, IlCond.Eq);
        LocateFast(slot, sl, i, r, w, bit, popSlow);
        PopHead(slot, sl, r);
        SetBit(w, bit, set: false);
        il.EmitBranch(store);
        il.MarkLabel(popSlow);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitCall(SlabMethod(slot, "Ref"));
        il.EmitStoreLocal(r);
        PopHead(slot, sl, r);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitCall(SlabMethod(slot, "RawLive"));
        il.EmitBranch(store);
        // or bump the cursor (Grow moves Cur into the next chunk, out of line); a slot past Dirty was never freed,
        // so its bit is already clear (Arena.Alloc does the same)
        il.MarkLabel(bump);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Len"));
        il.EmitStoreLocal(i);
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "CurEnd"));
        il.EmitBranch(room, IlCond.LtUn);
        il.EmitLoadLocal(sl);
        il.EmitCall(SlabMethod(slot, "Grow"));
        il.MarkLabel(room);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitLoadConst(1);
        il.EmitBinary(IlBinOp.Add);
        il.EmitStoreField(SlabField(slot, "Len"));
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "CurBase"));
        il.EmitBinary(IlBinOp.Sub);
        il.EmitStoreLocal(off);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Cur"));
        il.EmitCall(c.Module.Import(DataRef.MakeGenericMethod(slot)));
        il.EmitLoadLocal(off);
        il.EmitConvert(PrimitiveTypeCode.UIntPtr);
        il.EmitCall(c.Module.Import(AddRef.MakeGenericMethod(slot)));
        il.EmitStoreLocal(r);
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Dirty"));
        il.EmitBranch(store, IlCond.GeUn);
        BitWord(slot, sl, off, w, bit);   // a slot reused after a region reset
        SetBit(w, bit, set: false);
        il.MarkLabel(store);
        il.EmitLoadLocal(r);
        il.EmitLoadLocal(v);
        il.EmitStoreObj(T.Il(slot));
        // the term: tag << 56 | lane bits | slot index
        il.EmitLoadConst(unchecked((long)((ulong)(uint)a.Tag << 56)));
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "LaneBits"));
        il.EmitBinary(IlBinOp.Or);
        il.EmitLoadLocal(i);
        il.EmitConvert(PrimitiveTypeCode.UInt64);
        il.EmitBinary(IlBinOp.Or);
        return typeof(ulong);
    }

    /// <summary>`Free = *(uint*)r`: the popped slot's link becomes the free-list head.</summary>
    void PopHead(Type slot, IlLocal sl, IlLocal r)
    {
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(r);
        FirstUint(slot);
        il.EmitLoadObj(T.Il(typeof(uint)));
        il.EmitStoreField(SlabField(slot, "Free"));
    }
}
