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
/// `Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Cur), i - CurBase)` (no bounds check, no Locate), else the
/// slab's `Ref(i)`; the stale check `i &lt; Len` is dropped under <see cref="SlotProof.Live"/>. Take reads the slot and
/// pushes the free list (load Free, store it into the slot's first uint, store i into Free) when the term's lane is
/// this thread's; a foreign or stale term takes the out-of-line `Arena.RawForeign`. Alloc inlines the free-list pop
/// and the bump; `Grow` stays a call. The debug-build liveness checks are calls that are empty in Release.
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

    /// <summary>`ref TSlot` of slot <paramref name="i"/> (uint) of the slab: the lane-cursor fast path, else `Ref(i)`.</summary>
    void SlotRef(Type slot, IlLocal sl, IlLocal i)
    {
        var off = Temp(typeof(uint));
        var slow = il.DefineLabel();
        var done = il.DefineLabel();
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
        il.EmitBranch(done);
        il.MarkLabel(slow);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitCall(SlabMethod(slot, "Ref"));
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
        SlotRef(slot, sl, i);
        il.EmitStoreLocal(r);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(tt);
        il.EmitCall(SlabMethod(slot, frees ? "RawFreed" : "RawCheck"));
        il.EmitLoadLocal(r);
        il.EmitLoadObj(T.Il(slot));
        il.EmitStoreLocal(res);
        if (frees)
        {
            // push: the slot's first uint links the old head, the head becomes i
            il.EmitLoadLocal(r);
            FirstUint(slot);
            il.EmitLoadLocal(sl);
            il.EmitLoadField(SlabField(slot, "Free"));
            il.EmitStoreObj(T.Il(typeof(uint)));
            il.EmitLoadLocal(sl);
            il.EmitLoadLocal(i);
            il.EmitStoreField(SlabField(slot, "Free"));
        }
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
        SlotRef(slot, sl, i);
        il.EmitStoreLocal(r);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(tt);
        il.EmitCall(SlabMethod(slot, "RawFreed"));
        il.EmitLoadLocal(r);
        FirstUint(slot);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Free"));
        il.EmitStoreObj(T.Il(typeof(uint)));
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitStoreField(SlabField(slot, "Free"));
        il.MarkLabel(end);
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
        var r = il.DeclareLocal(T.Il(slot.MakeByRefType()));
        var bump = il.DefineLabel();
        var room = il.DefineLabel();
        var store = il.DefineLabel();
        // pop the free list
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Free"));
        il.EmitStoreLocal(i);
        il.EmitLoadLocal(i);
        il.EmitLoadConst(-1);
        il.EmitBranch(bump, IlCond.Eq);
        SlotRef(slot, sl, i);
        il.EmitStoreLocal(r);
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(r);
        FirstUint(slot);
        il.EmitLoadObj(T.Il(typeof(uint)));
        il.EmitStoreField(SlabField(slot, "Free"));
        il.EmitBranch(store);
        // or bump the cursor (Grow moves Cur into the next chunk, out of line)
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
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "Cur"));
        il.EmitCall(c.Module.Import(DataRef.MakeGenericMethod(slot)));
        il.EmitLoadLocal(i);
        il.EmitLoadLocal(sl);
        il.EmitLoadField(SlabField(slot, "CurBase"));
        il.EmitBinary(IlBinOp.Sub);
        il.EmitConvert(PrimitiveTypeCode.UIntPtr);
        il.EmitCall(c.Module.Import(AddRef.MakeGenericMethod(slot)));
        il.EmitStoreLocal(r);
        il.MarkLabel(store);
        il.EmitLoadLocal(r);
        il.EmitLoadLocal(v);
        il.EmitStoreObj(T.Il(slot));
        il.EmitLoadLocal(sl);
        il.EmitLoadLocal(i);
        il.EmitCall(SlabMethod(slot, "RawLive"));
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
}
