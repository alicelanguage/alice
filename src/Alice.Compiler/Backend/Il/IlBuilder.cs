using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>A branch target inside one method body (<see cref="IlBuilder.DefineLabel"/>).</summary>
public readonly record struct IlLabel(int Id);

/// <summary>A local variable slot. Named locals appear in the PDB scope that was open when they were declared.</summary>
public sealed class IlLocal
{
    internal IlLocal(int index, IlType type, string? name, bool pinned, int scope)
    {
        Index = index; Type = type; Name = name; Pinned = pinned; Scope = scope;
    }

    public int Index { get; }
    public IlType Type { get; }
    public string? Name { get; }
    public bool Pinned { get; }
    internal int Scope { get; }
    public override string ToString() => "V_" + Index + (Name != null ? " " + Name : "") + ": " + Type;
}

/// <summary>The condition of <see cref="IlBuilder.EmitBranch"/>. `Un` compares unsigned / unordered.</summary>
public enum IlCond { Always, True, False, Eq, NotEq, Lt, Le, Gt, Ge, LtUn, LeUn, GtUn, GeUn }

/// <summary>Two-operand arithmetic of <see cref="IlBuilder.EmitBinary"/>.</summary>
public enum IlBinOp { Add, Sub, Mul, Div, Rem, And, Or, Xor, Shl, Shr }

/// <summary>Peephole passes run over the instruction list before encoding (<see cref="IlBuilder.Peephole"/>).</summary>
[Flags]
public enum IlPeephole
{
    None = 0,
    /// <summary>`stloc t; ldloc t` of an unnamed temp read once, `ldloc x; stloc x`, side-effect-free push + `pop`.</summary>
    Temps = 1,
    /// <summary>Branch-to-branch chaining, branches to the next instruction, `brcond L1; br L2; L1:` inversion.</summary>
    Branches = 2,
    /// <summary>`ldc` + `conv` and `ldc ldc op` folding (checked forms only when they cannot throw).</summary>
    Constants = 4,
    All = Temps | Branches | Constants,
}

/// <summary>What the metadata writer supplies while a body is encoded: tokens for the symbolic operands.</summary>
internal interface IIlTokens : IIlTypeResolver
{
    EntityHandle Type(IlType type);
    EntityHandle Method(IlMethod method);
    EntityHandle Field(IlField field);
    UserStringHandle String(string value);
    StandaloneSignatureHandle CallSite(MethodSig signature);
    StandaloneSignatureHandle Locals(IReadOnlyList<IlLocal> locals);
}

/// <summary>A sequence point after encoding (hidden when <see cref="Document"/> is null).</summary>
internal readonly record struct IlSequencePoint(int Offset, IlDocument? Document, int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>A lexical scope after encoding: IL range and the named locals declared in it.</summary>
internal sealed record IlScopeInfo(int Start, int Length, IReadOnlyList<IlLocal> Locals, int Order);

/// <summary>The encoded body and the facts the PDB needs about it.</summary>
internal sealed record IlEncodedBody(int Offset, StandaloneSignatureHandle LocalSignature, int CodeSize, int MaxStack,
    IReadOnlyList<IlSequencePoint> SequencePoints, IReadOnlyList<IlScopeInfo> Scopes);

/// <summary>
/// A method body under construction: a stack-oriented instruction list with symbolic operands (types, methods,
/// fields, labels, locals) that becomes ECMA-335 IL when the module is written. At that point it runs the
/// <see cref="Peephole"/> passes, computes max-stack by abstract interpretation of stack depths, picks short or
/// long branch forms by iterated relaxation, and encodes through <see cref="InstructionEncoder"/> and
/// <see cref="ControlFlowBuilder"/> (labels and exception regions). Short forms of `ldloc`/`ldarg`/`ldc.i4` are
/// chosen by the encoder; `ldc.i8`/`ldc.r8` shrink to `ldc.i4; conv.i8`/`ldc.r4` when the value is exact.
///
/// Typed helpers (<see cref="EmitLoadLocal"/>, <see cref="EmitCall"/>, <see cref="EmitBranch"/>,
/// <see cref="EmitBinary"/>, <see cref="EmitConvert"/>...) are the surface the AIR emitter uses; <see cref="Emit(ILOpCode)"/>
/// and its overloads accept any opcode with the matching operand kind.
/// </summary>
public sealed class IlBuilder
{
    enum IK : byte { Op, Label, Seq, ScopeOpen, ScopeClose }

    enum Operand : byte { None, Local, Arg, I4, I8, R4, R8, Branch, Switch, Type, Method, Field, Token, String, Sig, Byte }

    struct Instr
    {
        public IK Kind;
        public ILOpCode Op;
        public long I;
        public double R;
        public object? Obj;
        public override readonly string ToString() => Kind == IK.Op ? Op + " " + (Obj ?? (object)I) : Kind + " " + I;
    }

    sealed class Scope(int parent) { public readonly int Parent = parent; public readonly List<IlLocal> Locals = []; }

    enum RegionKind : byte { Catch, Filter, Finally, Fault }

    sealed record Region(RegionKind Kind, IlLabel TryStart, IlLabel TryEnd, IlLabel HandlerStart, IlLabel HandlerEnd, IlType? CatchType, IlLabel FilterStart);

    enum Phase : byte { Try, Catch, Filter, FilterHandler, Finally, Fault }

    sealed class TryBlock
    {
        public IlLabel Start, End, TryEnd, HandlerStart, FilterStart;
        public Phase Phase;
        public IlType? CatchType;
    }

    readonly List<Instr> code = [];
    readonly List<IlLocal> locals = [];
    readonly List<Scope> scopes = [new Scope(-1)];
    readonly List<int> labelPos = [];
    readonly List<Region> regions = [];
    readonly Stack<TryBlock> tries = new();
    int scope;
    IlEncodedBody? encoded;
    bool optimized;

    /// <summary>A builder for a body whose method returns a value (<paramref name="returnsValue"/>) or not.</summary>
    public IlBuilder(bool returnsValue, IlModule? module = null)
    {
        ReturnsValue = returnsValue;
        Module = module;
    }

    public bool ReturnsValue { get; }

    /// <summary>The module documents come from for <see cref="MarkSequencePoint(Span)"/>.</summary>
    public IlModule? Module { get; }

    /// <summary>Zero-initialize locals (`.locals init`), required by verifiable code. On by default.</summary>
    public bool InitLocals { get; set; } = true;

    /// <summary>Peephole passes to run before encoding; none by default (the AIR emitter turns them on).</summary>
    public IlPeephole Peephole { get; set; }

    public IReadOnlyList<IlLocal> Locals => locals;

    /// <summary>Instructions emitted so far (pseudo entries such as labels excluded).</summary>
    public int InstructionCount => code.Count(c => c.Kind == IK.Op);

    /// <summary>Max stack of the encoded body (valid after the module was written).</summary>
    public int MaxStack => encoded?.MaxStack ?? throw new InvalidOperationException("the body has not been encoded yet");

    /// <summary>IL code size in bytes of the encoded body (valid after the module was written).</summary>
    public int CodeSize => encoded?.CodeSize ?? throw new InvalidOperationException("the body has not been encoded yet");

    // ---------------------------------------------------------------- labels, locals, scopes

    public IlLabel DefineLabel()
    {
        labelPos.Add(-1);
        return new IlLabel(labelPos.Count - 1);
    }

    public void MarkLabel(IlLabel label)
    {
        if ((uint)label.Id >= (uint)labelPos.Count) throw new ArgumentException("label from another builder");
        if (labelPos[label.Id] >= 0) throw new InvalidOperationException("label " + label.Id + " is already marked");
        labelPos[label.Id] = code.Count;
        code.Add(new Instr { Kind = IK.Label, I = label.Id });
    }

    /// <summary>A new local slot; <paramref name="name"/> makes it visible to debuggers in the current scope.</summary>
    public IlLocal DeclareLocal(IlType type, string? name = null, bool pinned = false)
    {
        var l = new IlLocal(locals.Count, type, name, pinned, scope);
        locals.Add(l);
        scopes[scope].Locals.Add(l);
        return l;
    }

    /// <summary>Opens a lexical scope (a PDB LocalScope row); locals declared until <see cref="EndScope"/> belong to it.</summary>
    public void BeginScope()
    {
        scopes.Add(new Scope(scope));
        scope = scopes.Count - 1;
        code.Add(new Instr { Kind = IK.ScopeOpen, I = scope });
    }

    public void EndScope()
    {
        if (scope == 0) throw new InvalidOperationException("EndScope without BeginScope");
        code.Add(new Instr { Kind = IK.ScopeClose, I = scope });
        scope = scopes[scope].Parent;
    }

    // ---------------------------------------------------------------- sequence points

    /// <summary>A sequence point for the next instruction at <paramref name="span"/> (document looked up by file).</summary>
    public void MarkSequencePoint(Span span)
    {
        var module = Module ?? throw new InvalidOperationException("sequence points by Span need the builder's module");
        int len = Math.Max(span.Len, 1);
        MarkSequencePoint(module.Document(span.File), span.Line, span.Col, span.Line, span.Col + len);
    }

    /// <summary>A sequence point for the next instruction: 1-based lines and columns, end column exclusive.</summary>
    public void MarkSequencePoint(IlDocument document, int startLine, int startColumn, int endLine, int endColumn)
    {
        if (startLine < 1 || startLine >= 0x20000000 || endLine < startLine || startColumn < 1 || endColumn >= 0x10000
            || (endLine == startLine && endColumn <= startColumn))
            throw new ArgumentOutOfRangeException(nameof(startLine), $"bad sequence point {startLine}:{startColumn}-{endLine}:{endColumn}");
        code.Add(new Instr { Kind = IK.Seq, Obj = new IlSequencePoint(0, document, startLine, startColumn, endLine, endColumn) });
    }

    /// <summary>A hidden sequence point (compiler-generated code the debugger steps over).</summary>
    public void MarkHiddenSequencePoint() => code.Add(new Instr { Kind = IK.Seq, Obj = new IlSequencePoint(0, null, 0, 0, 0, 0) });

    // ---------------------------------------------------------------- raw emission

    public void Emit(ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3: Add(ILOpCode.Ldarg, op - ILOpCode.Ldarg_0); return;
            case ILOpCode.Ldloc_0 or ILOpCode.Ldloc_1 or ILOpCode.Ldloc_2 or ILOpCode.Ldloc_3: Add(ILOpCode.Ldloc, op - ILOpCode.Ldloc_0); return;
            case ILOpCode.Stloc_0 or ILOpCode.Stloc_1 or ILOpCode.Stloc_2 or ILOpCode.Stloc_3: Add(ILOpCode.Stloc, op - ILOpCode.Stloc_0); return;
            case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8: Add(ILOpCode.Ldc_i4, op - ILOpCode.Ldc_i4_0); return;
        }
        Expect(op, Operand.None);
        Add(op, 0);
    }

    /// <summary>An opcode with an integer operand: locals/arguments by index, `ldc.i4`/`ldc.i8`, `unaligned.`.</summary>
    public void Emit(ILOpCode op, long value)
    {
        op = Normalize(op);
        var k = OperandOf(op);
        if (k is not (Operand.Local or Operand.Arg or Operand.I4 or Operand.I8 or Operand.Byte))
            throw new ArgumentException(op + " does not take an integer operand");
        if (k == Operand.I4 && value is < int.MinValue or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        if (k is Operand.Local && (ulong)value >= (ulong)locals.Count) throw new ArgumentOutOfRangeException(nameof(value), "no local " + value);
        if (k is Operand.Byte or Operand.Arg && value is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        Add(op, value);
    }

    public void Emit(ILOpCode op, double value)
    {
        var k = OperandOf(op);
        if (k is not (Operand.R4 or Operand.R8)) throw new ArgumentException(op + " does not take a float operand");
        code.Add(new Instr { Kind = IK.Op, Op = op, R = k == Operand.R4 ? (float)value : value });
    }

    public void Emit(ILOpCode op, IlLabel target)
    {
        op = Normalize(op);
        Expect(op, Operand.Branch);
        CheckLabel(target);
        Add(op, target.Id);
    }

    public void Emit(ILOpCode op, IlType type) { Expect(op, op == ILOpCode.Ldtoken ? Operand.Token : Operand.Type); AddObj(op, type); }

    public void Emit(ILOpCode op, IlMethod method) { Expect(op, op == ILOpCode.Ldtoken ? Operand.Token : Operand.Method); AddObj(op, method); }

    public void Emit(ILOpCode op, IlField field) { Expect(op, op == ILOpCode.Ldtoken ? Operand.Token : Operand.Field); AddObj(op, field); }

    public void Emit(ILOpCode op, string value) { Expect(op, Operand.String); AddObj(op, value); }

    public void Emit(ILOpCode op, MethodSig callSite) { Expect(op, Operand.Sig); AddObj(op, callSite); }

    // ---------------------------------------------------------------- typed helpers (AIR surface)

    public void EmitLoadLocal(IlLocal l) => Add(ILOpCode.Ldloc, Own(l).Index);
    public void EmitStoreLocal(IlLocal l) => Add(ILOpCode.Stloc, Own(l).Index);
    public void EmitLoadLocalAddress(IlLocal l) => Add(ILOpCode.Ldloca, Own(l).Index);
    public void EmitLoadArg(int index) => Emit(ILOpCode.Ldarg, index);
    public void EmitStoreArg(int index) => Emit(ILOpCode.Starg, index);
    public void EmitLoadArgAddress(int index) => Emit(ILOpCode.Ldarga, index);

    public void EmitLoadConst(int v) => Add(ILOpCode.Ldc_i4, v);
    public void EmitLoadConst(long v) => Add(ILOpCode.Ldc_i8, v);
    public void EmitLoadConst(bool v) => Add(ILOpCode.Ldc_i4, v ? 1 : 0);
    public void EmitLoadConst(float v) => code.Add(new Instr { Kind = IK.Op, Op = ILOpCode.Ldc_r4, R = v });
    public void EmitLoadConst(double v) => code.Add(new Instr { Kind = IK.Op, Op = ILOpCode.Ldc_r8, R = v });
    public void EmitLoadConst(string v) => AddObj(ILOpCode.Ldstr, v);
    public void EmitLoadNull() => Add(ILOpCode.Ldnull, 0);

    /// <summary>`call`, or `callvirt` when <paramref name="virtualCall"/> (instance methods of reference types).</summary>
    public void EmitCall(IlMethod method, bool virtualCall = false) => AddObj(virtualCall ? ILOpCode.Callvirt : ILOpCode.Call, method);
    public void EmitNewObj(IlMethod constructor) => AddObj(ILOpCode.Newobj, constructor);
    public void EmitCallIndirect(MethodSig signature) => AddObj(ILOpCode.Calli, signature);
    public void EmitLoadFunction(IlMethod method) => AddObj(ILOpCode.Ldftn, method);

    /// <summary>`br`, `brtrue`, `brfalse` or a compare-and-branch; the short form is chosen at encoding.</summary>
    public void EmitBranch(IlLabel target, IlCond cond = IlCond.Always) => Emit(cond switch
    {
        IlCond.Always => ILOpCode.Br, IlCond.True => ILOpCode.Brtrue, IlCond.False => ILOpCode.Brfalse,
        IlCond.Eq => ILOpCode.Beq, IlCond.NotEq => ILOpCode.Bne_un, IlCond.Lt => ILOpCode.Blt, IlCond.Le => ILOpCode.Ble,
        IlCond.Gt => ILOpCode.Bgt, IlCond.Ge => ILOpCode.Bge, IlCond.LtUn => ILOpCode.Blt_un, IlCond.LeUn => ILOpCode.Ble_un,
        IlCond.GtUn => ILOpCode.Bgt_un, _ => ILOpCode.Bge_un,
    }, target);

    /// <summary>`leave` (exit a protected region or handler).</summary>
    public void EmitLeave(IlLabel target) => Emit(ILOpCode.Leave, target);

    public void EmitSwitch(IReadOnlyList<IlLabel> targets)
    {
        foreach (var t in targets) CheckLabel(t);
        AddObj(ILOpCode.Switch, targets.ToArray());
    }

    /// <summary>Arithmetic with overflow checks when <paramref name="isChecked"/> (add/sub/mul only) and the unsigned
    /// form when <paramref name="unsigned"/> (div/rem/shr, checked add/sub/mul).</summary>
    public void EmitBinary(IlBinOp op, bool isChecked = false, bool unsigned = false) => Emit(op switch
    {
        IlBinOp.Add => isChecked ? (unsigned ? ILOpCode.Add_ovf_un : ILOpCode.Add_ovf) : ILOpCode.Add,
        IlBinOp.Sub => isChecked ? (unsigned ? ILOpCode.Sub_ovf_un : ILOpCode.Sub_ovf) : ILOpCode.Sub,
        IlBinOp.Mul => isChecked ? (unsigned ? ILOpCode.Mul_ovf_un : ILOpCode.Mul_ovf) : ILOpCode.Mul,
        IlBinOp.Div => unsigned ? ILOpCode.Div_un : ILOpCode.Div,
        IlBinOp.Rem => unsigned ? ILOpCode.Rem_un : ILOpCode.Rem,
        IlBinOp.And => ILOpCode.And, IlBinOp.Or => ILOpCode.Or, IlBinOp.Xor => ILOpCode.Xor, IlBinOp.Shl => ILOpCode.Shl,
        _ => unsigned ? ILOpCode.Shr_un : ILOpCode.Shr,
    });

    /// <summary>`conv.*` to <paramref name="to"/>; checked conversions throw on overflow, <paramref name="fromUnsigned"/>
    /// treats the source as unsigned (`conv.ovf.*.un`, `conv.r.un`).</summary>
    public void EmitConvert(PrimitiveTypeCode to, bool isChecked = false, bool fromUnsigned = false)
    {
        if (!isChecked)
        {
            if (fromUnsigned && to is PrimitiveTypeCode.Single or PrimitiveTypeCode.Double)
            {
                Emit(ILOpCode.Conv_r_un);
                if (to == PrimitiveTypeCode.Single) Emit(ILOpCode.Conv_r4);
                return;
            }
            Emit(to switch
            {
                PrimitiveTypeCode.SByte => ILOpCode.Conv_i1, PrimitiveTypeCode.Byte or PrimitiveTypeCode.Boolean => ILOpCode.Conv_u1,
                PrimitiveTypeCode.Int16 => ILOpCode.Conv_i2, PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char => ILOpCode.Conv_u2,
                PrimitiveTypeCode.Int32 => ILOpCode.Conv_i4, PrimitiveTypeCode.UInt32 => ILOpCode.Conv_u4,
                PrimitiveTypeCode.Int64 => ILOpCode.Conv_i8, PrimitiveTypeCode.UInt64 => ILOpCode.Conv_u8,
                PrimitiveTypeCode.Single => ILOpCode.Conv_r4, PrimitiveTypeCode.Double => ILOpCode.Conv_r8,
                PrimitiveTypeCode.IntPtr => ILOpCode.Conv_i, PrimitiveTypeCode.UIntPtr => ILOpCode.Conv_u,
                _ => throw new ArgumentException("no conversion to " + to),
            });
            return;
        }
        Emit((to, fromUnsigned) switch
        {
            (PrimitiveTypeCode.SByte, false) => ILOpCode.Conv_ovf_i1, (PrimitiveTypeCode.SByte, true) => ILOpCode.Conv_ovf_i1_un,
            (PrimitiveTypeCode.Byte, false) => ILOpCode.Conv_ovf_u1, (PrimitiveTypeCode.Byte, true) => ILOpCode.Conv_ovf_u1_un,
            (PrimitiveTypeCode.Int16, false) => ILOpCode.Conv_ovf_i2, (PrimitiveTypeCode.Int16, true) => ILOpCode.Conv_ovf_i2_un,
            (PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char, false) => ILOpCode.Conv_ovf_u2,
            (PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char, true) => ILOpCode.Conv_ovf_u2_un,
            (PrimitiveTypeCode.Int32, false) => ILOpCode.Conv_ovf_i4, (PrimitiveTypeCode.Int32, true) => ILOpCode.Conv_ovf_i4_un,
            (PrimitiveTypeCode.UInt32, false) => ILOpCode.Conv_ovf_u4, (PrimitiveTypeCode.UInt32, true) => ILOpCode.Conv_ovf_u4_un,
            (PrimitiveTypeCode.Int64, false) => ILOpCode.Conv_ovf_i8, (PrimitiveTypeCode.Int64, true) => ILOpCode.Conv_ovf_i8_un,
            (PrimitiveTypeCode.UInt64, false) => ILOpCode.Conv_ovf_u8, (PrimitiveTypeCode.UInt64, true) => ILOpCode.Conv_ovf_u8_un,
            (PrimitiveTypeCode.IntPtr, false) => ILOpCode.Conv_ovf_i, (PrimitiveTypeCode.IntPtr, true) => ILOpCode.Conv_ovf_i_un,
            (PrimitiveTypeCode.UIntPtr, false) => ILOpCode.Conv_ovf_u, (PrimitiveTypeCode.UIntPtr, true) => ILOpCode.Conv_ovf_u_un,
            _ => throw new ArgumentException("no checked conversion to " + to),
        });
    }

    /// <summary>`ldfld`/`ldsfld` by the field's staticness.</summary>
    public void EmitLoadField(IlField f) => AddObj(f.IsStatic ? ILOpCode.Ldsfld : ILOpCode.Ldfld, f);
    public void EmitStoreField(IlField f) => AddObj(f.IsStatic ? ILOpCode.Stsfld : ILOpCode.Stfld, f);
    public void EmitLoadFieldAddress(IlField f) => AddObj(f.IsStatic ? ILOpCode.Ldsflda : ILOpCode.Ldflda, f);

    public void EmitNewArray(IlType element) => AddObj(ILOpCode.Newarr, element);

    /// <summary>`ldelem.*` for primitives and references, `ldelem T` otherwise.</summary>
    public void EmitLoadElement(IlType element)
    {
        var op = element switch
        {
            IlType.PrimType p => p.Code switch
            {
                PrimitiveTypeCode.SByte => ILOpCode.Ldelem_i1, PrimitiveTypeCode.Byte or PrimitiveTypeCode.Boolean => ILOpCode.Ldelem_u1,
                PrimitiveTypeCode.Int16 => ILOpCode.Ldelem_i2, PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char => ILOpCode.Ldelem_u2,
                PrimitiveTypeCode.Int32 => ILOpCode.Ldelem_i4, PrimitiveTypeCode.UInt32 => ILOpCode.Ldelem_u4,
                PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => ILOpCode.Ldelem_i8,
                PrimitiveTypeCode.Single => ILOpCode.Ldelem_r4, PrimitiveTypeCode.Double => ILOpCode.Ldelem_r8,
                PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => ILOpCode.Ldelem_i,
                PrimitiveTypeCode.String or PrimitiveTypeCode.Object => ILOpCode.Ldelem_ref,
                _ => ILOpCode.Ldelem,
            },
            IlType.SzArrayType or IlType.ArrayType => ILOpCode.Ldelem_ref,
            IlType.NamedType { IsValueType: false } or IlType.GenericInstType { IsValueType: false } => ILOpCode.Ldelem_ref,
            _ => ILOpCode.Ldelem,
        };
        if (op == ILOpCode.Ldelem) AddObj(op, element); else Emit(op);
    }

    /// <summary>`stelem.*` for primitives and references, `stelem T` otherwise.</summary>
    public void EmitStoreElement(IlType element)
    {
        var op = element switch
        {
            IlType.PrimType p => p.Code switch
            {
                PrimitiveTypeCode.SByte or PrimitiveTypeCode.Byte or PrimitiveTypeCode.Boolean => ILOpCode.Stelem_i1,
                PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 or PrimitiveTypeCode.Char => ILOpCode.Stelem_i2,
                PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 => ILOpCode.Stelem_i4,
                PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => ILOpCode.Stelem_i8,
                PrimitiveTypeCode.Single => ILOpCode.Stelem_r4, PrimitiveTypeCode.Double => ILOpCode.Stelem_r8,
                PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => ILOpCode.Stelem_i,
                PrimitiveTypeCode.String or PrimitiveTypeCode.Object => ILOpCode.Stelem_ref,
                _ => ILOpCode.Stelem,
            },
            IlType.SzArrayType or IlType.ArrayType => ILOpCode.Stelem_ref,
            IlType.NamedType { IsValueType: false } or IlType.GenericInstType { IsValueType: false } => ILOpCode.Stelem_ref,
            _ => ILOpCode.Stelem,
        };
        if (op == ILOpCode.Stelem) AddObj(op, element); else Emit(op);
    }

    public void EmitBox(IlType t) => AddObj(ILOpCode.Box, t);
    public void EmitUnboxAny(IlType t) => AddObj(ILOpCode.Unbox_any, t);
    public void EmitCastClass(IlType t) => AddObj(ILOpCode.Castclass, t);
    public void EmitIsInst(IlType t) => AddObj(ILOpCode.Isinst, t);
    public void EmitInitObj(IlType t) => AddObj(ILOpCode.Initobj, t);
    public void EmitDup() => Emit(ILOpCode.Dup);
    public void EmitPop() => Emit(ILOpCode.Pop);
    public void EmitReturn() => Emit(ILOpCode.Ret);
    public void EmitThrow() => Emit(ILOpCode.Throw);
    public void EmitRethrow() => Emit(ILOpCode.Rethrow);

    // prefixes: they apply to the next instruction; the peephole passes never split a prefix from it.
    public void EmitTailPrefix() => Emit(ILOpCode.Tail);
    public void EmitConstrainedPrefix(IlType t) => AddObj(ILOpCode.Constrained, t);
    public void EmitReadonlyPrefix() => Emit(ILOpCode.Readonly);
    public void EmitVolatilePrefix() => Emit(ILOpCode.Volatile);
    public void EmitUnalignedPrefix(byte alignment)
    {
        if (alignment is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(alignment));
        Add(ILOpCode.Unaligned, alignment);
    }

    // ---------------------------------------------------------------- exception regions

    /// <summary>Opens a protected region; returns the label after the whole construct (the target of `leave`).</summary>
    public IlLabel BeginTry()
    {
        var b = new TryBlock { Start = DefineLabel(), End = DefineLabel(), Phase = Phase.Try };
        MarkLabel(b.Start);
        tries.Push(b);
        return b.End;
    }

    /// <summary>Ends the try body or the previous handler (with `leave` to the end) and starts a typed catch handler.</summary>
    public void BeginCatch(IlType exceptionType)
    {
        var b = NextHandler();
        b.Phase = Phase.Catch;
        b.CatchType = exceptionType;
    }

    /// <summary>Starts a filter block: the exception is on the stack, the filter leaves an int32 (0 = no, 1 = handle).</summary>
    public void BeginFilter()
    {
        var b = NextHandler();
        b.Phase = Phase.Filter;
        b.FilterStart = b.HandlerStart;
    }

    /// <summary>Ends the filter with `endfilter` and starts its handler (the exception is on the stack).</summary>
    public void BeginFilterHandler()
    {
        var b = Current();
        if (b.Phase != Phase.Filter) throw new InvalidOperationException("BeginFilterHandler outside a filter");
        Emit(ILOpCode.Endfilter);
        b.HandlerStart = DefineLabel();
        MarkLabel(b.HandlerStart);
        b.Phase = Phase.FilterHandler;
    }

    /// <summary>Starts a finally handler. After catch handlers the finally protects the try and its catches
    /// (the nested shape C# compiles try/catch/finally to).</summary>
    public void BeginFinally() => BeginTerminal(Phase.Finally);

    /// <summary>Starts a fault handler (runs only when the protected region exits by an exception).</summary>
    public void BeginFault() => BeginTerminal(Phase.Fault);

    /// <summary>Closes the last handler (`leave` from catch, `endfinally` from finally/fault) and marks the end label.</summary>
    public void EndTry()
    {
        var b = Current();
        switch (b.Phase)
        {
            case Phase.Try: throw new InvalidOperationException("a try block needs a handler");
            case Phase.Filter: throw new InvalidOperationException("a filter needs BeginFilterHandler");
            case Phase.Catch or Phase.FilterHandler: CloseHandler(b); break;
            default:
                if (!Terminated()) Emit(ILOpCode.Endfinally);
                var end = DefineLabel();
                MarkLabel(end);
                regions.Add(new Region(b.Phase == Phase.Finally ? RegionKind.Finally : RegionKind.Fault, b.Start, b.TryEnd, b.HandlerStart, end, null, default));
                break;
        }
        MarkLabel(b.End);
        tries.Pop();
    }

    TryBlock Current() => tries.Count > 0 ? tries.Peek() : throw new InvalidOperationException("no open try block");

    TryBlock NextHandler()
    {
        var b = Current();
        switch (b.Phase)
        {
            case Phase.Try:
                if (!Terminated()) EmitLeave(b.End);
                b.TryEnd = DefineLabel();
                MarkLabel(b.TryEnd);
                b.HandlerStart = b.TryEnd;
                return b;
            case Phase.Catch or Phase.FilterHandler:
                b.HandlerStart = CloseHandler(b);
                return b;
            default: throw new InvalidOperationException("no handler can follow a " + b.Phase.ToString().ToLowerInvariant() + " block");
        }
    }

    /// <summary>Closes a catch or filter handler and returns the label right after it.</summary>
    IlLabel CloseHandler(TryBlock b)
    {
        if (!Terminated()) EmitLeave(b.End);
        var after = DefineLabel();
        MarkLabel(after);
        regions.Add(b.Phase == Phase.Catch
            ? new Region(RegionKind.Catch, b.Start, b.TryEnd, b.HandlerStart, after, b.CatchType, default)
            : new Region(RegionKind.Filter, b.Start, b.TryEnd, b.HandlerStart, after, null, b.FilterStart));
        return after;
    }

    void BeginTerminal(Phase phase)
    {
        var b = Current();
        if (b.Phase is Phase.Catch or Phase.FilterHandler)
        {
            var after = CloseHandler(b);
            b.TryEnd = after;              // the finally protects try + catches
            b.HandlerStart = after;
        }
        else NextHandler();
        b.Phase = phase;
    }

    /// <summary>True when the last instruction cannot fall through (no label after it).</summary>
    bool Terminated()
    {
        for (int i = code.Count - 1; i >= 0; i--)
        {
            var c = code[i];
            if (c.Kind == IK.Label) return false;
            if (c.Kind != IK.Op) continue;
            return IsUnconditional(c.Op);
        }
        return false;
    }

    // ---------------------------------------------------------------- internals

    void Add(ILOpCode op, long v) => code.Add(new Instr { Kind = IK.Op, Op = op, I = v });

    void AddObj(ILOpCode op, object o) => code.Add(new Instr { Kind = IK.Op, Op = op, Obj = o });

    IlLocal Own(IlLocal l) => l.Index < locals.Count && ReferenceEquals(locals[l.Index], l) ? l : throw new ArgumentException("local from another builder");

    void CheckLabel(IlLabel l)
    {
        if ((uint)l.Id >= (uint)labelPos.Count) throw new ArgumentException("label from another builder");
    }

    static void Expect(ILOpCode op, Operand k)
    {
        if (OperandOf(op) != k) throw new ArgumentException($"{op} takes a {OperandOf(op)} operand, not {k}");
    }

    static ILOpCode Normalize(ILOpCode op) => op switch
    {
        ILOpCode.Ldloc_s => ILOpCode.Ldloc, ILOpCode.Stloc_s => ILOpCode.Stloc, ILOpCode.Ldloca_s => ILOpCode.Ldloca,
        ILOpCode.Ldarg_s => ILOpCode.Ldarg, ILOpCode.Starg_s => ILOpCode.Starg, ILOpCode.Ldarga_s => ILOpCode.Ldarga,
        ILOpCode.Ldc_i4_s => ILOpCode.Ldc_i4,
        _ when op.IsBranch() => op.GetLongBranch(),
        _ => op,
    };

    static Operand OperandOf(ILOpCode op) => op switch
    {
        ILOpCode.Ldloc or ILOpCode.Stloc or ILOpCode.Ldloca => Operand.Local,
        ILOpCode.Ldarg or ILOpCode.Starg or ILOpCode.Ldarga => Operand.Arg,
        ILOpCode.Ldc_i4 => Operand.I4, ILOpCode.Ldc_i8 => Operand.I8, ILOpCode.Ldc_r4 => Operand.R4, ILOpCode.Ldc_r8 => Operand.R8,
        ILOpCode.Switch => Operand.Switch,
        ILOpCode.Box or ILOpCode.Unbox or ILOpCode.Unbox_any or ILOpCode.Newarr or ILOpCode.Castclass or ILOpCode.Isinst
            or ILOpCode.Ldobj or ILOpCode.Stobj or ILOpCode.Cpobj or ILOpCode.Initobj or ILOpCode.Sizeof or ILOpCode.Ldelema
            or ILOpCode.Ldelem or ILOpCode.Stelem or ILOpCode.Mkrefany or ILOpCode.Refanyval or ILOpCode.Constrained => Operand.Type,
        ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Jmp => Operand.Method,
        ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld => Operand.Field,
        ILOpCode.Ldtoken => Operand.Token,
        ILOpCode.Ldstr => Operand.String,
        ILOpCode.Calli => Operand.Sig,
        ILOpCode.Unaligned => Operand.Byte,
        _ when op.IsBranch() => Operand.Branch,
        ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3 or ILOpCode.Ldloc_0 or ILOpCode.Ldloc_1
            or ILOpCode.Ldloc_2 or ILOpCode.Ldloc_3 or ILOpCode.Stloc_0 or ILOpCode.Stloc_1 or ILOpCode.Stloc_2 or ILOpCode.Stloc_3
            or (>= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8) => Operand.None,
        ILOpCode.Ldarg_s or ILOpCode.Starg_s or ILOpCode.Ldarga_s or ILOpCode.Ldloc_s or ILOpCode.Stloc_s or ILOpCode.Ldloca_s
            or ILOpCode.Ldc_i4_s => Operand.I4 + 100,   // never accepted: use the long form, the encoder shortens it
        _ => Operand.None,
    };

    static bool IsUnconditional(ILOpCode op) => op is ILOpCode.Br or ILOpCode.Leave or ILOpCode.Ret or ILOpCode.Throw
        or ILOpCode.Rethrow or ILOpCode.Endfinally or ILOpCode.Endfilter or ILOpCode.Jmp;

    static bool IsPrefix(ILOpCode op) => op is ILOpCode.Tail or ILOpCode.Constrained or ILOpCode.Readonly or ILOpCode.Volatile
        or ILOpCode.Unaligned;

    /// <summary>(pop, push) of one instruction; `ret` uses <see cref="ReturnsValue"/>.</summary>
    (int Pop, int Push) Effect(in Instr x)
    {
        switch (x.Op)
        {
            case ILOpCode.Call or ILOpCode.Callvirt:
                { var s = ((IlMethod)x.Obj!).Signature; return (s.ArgumentCount, s.ReturnsValue ? 1 : 0); }
            case ILOpCode.Newobj:
                return (((IlMethod)x.Obj!).Signature.Parameters.Length, 1);
            case ILOpCode.Calli:
                { var s = (MethodSig)x.Obj!; return (s.ArgumentCount + 1, s.ReturnsValue ? 1 : 0); }
            case ILOpCode.Ret: return (ReturnsValue ? 1 : 0, 0);
        }
        return StaticEffect(x.Op);
    }

    static (int, int) StaticEffect(ILOpCode op) => op switch
    {
        ILOpCode.Nop or ILOpCode.Break or ILOpCode.Tail or ILOpCode.Readonly or ILOpCode.Volatile or ILOpCode.Unaligned
            or ILOpCode.Constrained or ILOpCode.Jmp or ILOpCode.Br or ILOpCode.Leave or ILOpCode.Endfinally
            or ILOpCode.Rethrow => (0, 0),
        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Ldnull or ILOpCode.Ldc_i4
            or ILOpCode.Ldc_i8 or ILOpCode.Ldc_r4 or ILOpCode.Ldc_r8 or ILOpCode.Ldstr or ILOpCode.Ldsfld or ILOpCode.Ldsflda
            or ILOpCode.Ldtoken or ILOpCode.Arglist or ILOpCode.Ldftn or ILOpCode.Sizeof => (0, 1),
        ILOpCode.Starg or ILOpCode.Stloc or ILOpCode.Pop or ILOpCode.Brtrue or ILOpCode.Brfalse or ILOpCode.Switch
            or ILOpCode.Throw or ILOpCode.Stsfld or ILOpCode.Endfilter or ILOpCode.Initobj => (1, 0),
        ILOpCode.Dup => (1, 2),
        ILOpCode.Beq or ILOpCode.Bge or ILOpCode.Bgt or ILOpCode.Ble or ILOpCode.Blt or ILOpCode.Bne_un or ILOpCode.Bge_un
            or ILOpCode.Bgt_un or ILOpCode.Ble_un or ILOpCode.Blt_un or ILOpCode.Stfld or ILOpCode.Stobj or ILOpCode.Cpobj
            or (>= ILOpCode.Stind_ref and <= ILOpCode.Stind_r8) or ILOpCode.Stind_i => (2, 0),
        ILOpCode.Add or ILOpCode.Sub or ILOpCode.Mul or ILOpCode.Div or ILOpCode.Div_un or ILOpCode.Rem or ILOpCode.Rem_un
            or ILOpCode.And or ILOpCode.Or or ILOpCode.Xor or ILOpCode.Shl or ILOpCode.Shr or ILOpCode.Shr_un
            or ILOpCode.Add_ovf or ILOpCode.Add_ovf_un or ILOpCode.Sub_ovf or ILOpCode.Sub_ovf_un or ILOpCode.Mul_ovf
            or ILOpCode.Mul_ovf_un or ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Cgt_un or ILOpCode.Clt or ILOpCode.Clt_un
            or ILOpCode.Ldelema or ILOpCode.Ldelem or (>= ILOpCode.Ldelem_i1 and <= ILOpCode.Ldelem_ref) => (2, 1),
        ILOpCode.Stelem or (>= ILOpCode.Stelem_i and <= ILOpCode.Stelem_ref) or ILOpCode.Cpblk or ILOpCode.Initblk => (3, 0),
        _ => (1, 1),   // ldind, neg/not, conv*, ldobj, castclass/isinst, unbox*, box, ldfld(a), newarr, ldlen, ckfinite,
                       // mkrefany, refanyval, refanytype, ldvirtftn, localloc
    };

    // ---------------------------------------------------------------- encoding

    int Target(long labelId)
    {
        int p = labelPos[(int)labelId];
        return p >= 0 ? p : throw new InvalidOperationException("label " + labelId + " is used but never marked");
    }

    static bool FitsR4(double v) => !double.IsNaN(v) && (double)(float)v == v;

    static int SizeI4(long v) => v is >= -1 and <= 8 ? 1 : v is >= sbyte.MinValue and <= sbyte.MaxValue ? 2 : 5;

    static int Size(in Instr x, bool longBranch)
    {
        if (x.Kind != IK.Op) return 0;
        int opSize = (ushort)x.Op > 0xFF ? 2 : 1;
        long v = x.I;
        return OperandOf(x.Op) switch
        {
            Operand.Local => x.Op == ILOpCode.Ldloca ? (v < 256 ? 2 : 4) : (v < 4 ? 1 : v < 256 ? 2 : 4),
            Operand.Arg => x.Op == ILOpCode.Ldarg ? (v < 4 ? 1 : v < 256 ? 2 : 4) : (v < 256 ? 2 : 4),
            Operand.I4 => SizeI4(v),
            Operand.I8 => v is >= int.MinValue and <= int.MaxValue ? SizeI4(v) + 1 : 9,
            Operand.R4 => 5,
            Operand.R8 => FitsR4(x.R) ? 5 : 9,
            Operand.Branch => longBranch ? 5 : 2,
            Operand.Switch => 5 + 4 * ((IlLabel[])x.Obj!).Length,
            Operand.Byte => opSize + 1,
            Operand.None => opSize,
            _ => opSize + 4,
        };
    }

    internal IlEncodedBody Encode(MethodBodyStreamEncoder bodies, IIlTokens tokens)
    {
        if (tries.Count > 0) throw new InvalidOperationException("unclosed try block");
        if (scope != 0) throw new InvalidOperationException("unclosed scope");
        if (Peephole != IlPeephole.None && !optimized) { RunPeephole(); optimized = true; }
        int n = code.Count;
        RebuildLabels();
        int maxStack = ComputeMaxStack();

        // branch relaxation: start short, widen the ones out of range until nothing changes
        var longBr = new bool[n];
        var off = new int[n + 1];
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < n; i++) off[i + 1] = off[i] + Size(code[i], longBr[i]);
            for (int i = 0; i < n; i++)
            {
                if (code[i].Kind != IK.Op || longBr[i] || OperandOf(code[i].Op) != Operand.Branch) continue;
                int delta = off[Target(code[i].I)] - (off[i] + 2);
                if (delta is < sbyte.MinValue or > sbyte.MaxValue) { longBr[i] = true; changed = true; }
            }
        }

        var enc = new InstructionEncoder(new BlobBuilder(), new ControlFlowBuilder());
        var handles = new LabelHandle[labelPos.Count];
        for (int i = 0; i < handles.Length; i++) handles[i] = enc.DefineLabel();
        var seq = new List<IlSequencePoint>();
        var scopeStart = new int[scopes.Count];
        var scopeEnd = new int[scopes.Count];
        bool localloc = false;
        for (int i = 0; i < n; i++)
        {
            var x = code[i];
            if (enc.Offset != off[i]) throw new InvalidOperationException($"IlBuilder size table out of sync at {i} ({code[i - 1]})");
            switch (x.Kind)
            {
                case IK.Label: enc.MarkLabel(handles[(int)x.I]); continue;
                case IK.Seq: seq.Add(((IlSequencePoint)x.Obj!) with { Offset = off[i] }); continue;
                case IK.ScopeOpen: scopeStart[(int)x.I] = off[i]; continue;
                case IK.ScopeClose: scopeEnd[(int)x.I] = off[i]; continue;
            }
            localloc |= x.Op == ILOpCode.Localloc;
            switch (OperandOf(x.Op))
            {
                case Operand.None: enc.OpCode(x.Op); break;
                case Operand.Local:
                    if (x.Op == ILOpCode.Ldloc) enc.LoadLocal((int)x.I);
                    else if (x.Op == ILOpCode.Stloc) enc.StoreLocal((int)x.I);
                    else enc.LoadLocalAddress((int)x.I);
                    break;
                case Operand.Arg:
                    if (x.Op == ILOpCode.Ldarg) enc.LoadArgument((int)x.I);
                    else if (x.Op == ILOpCode.Starg) enc.StoreArgument((int)x.I);
                    else enc.LoadArgumentAddress((int)x.I);
                    break;
                case Operand.I4: enc.LoadConstantI4((int)x.I); break;
                case Operand.I8:
                    if (x.I is >= int.MinValue and <= int.MaxValue) { enc.LoadConstantI4((int)x.I); enc.OpCode(ILOpCode.Conv_i8); }
                    else enc.LoadConstantI8(x.I);
                    break;
                case Operand.R4: enc.LoadConstantR4((float)x.R); break;
                case Operand.R8:
                    if (FitsR4(x.R)) enc.LoadConstantR4((float)x.R); else enc.LoadConstantR8(x.R);
                    break;
                case Operand.Branch: Target(x.I); enc.Branch(longBr[i] ? x.Op : x.Op.GetShortBranch(), handles[(int)x.I]); break;
                case Operand.Switch:
                    var sw = enc.Switch(((IlLabel[])x.Obj!).Length);
                    foreach (var l in (IlLabel[])x.Obj!) { Target(l.Id); sw.Branch(handles[l.Id]); }
                    break;
                case Operand.Type: enc.OpCode(x.Op); enc.Token(tokens.Type((IlType)x.Obj!)); break;
                case Operand.Method: enc.OpCode(x.Op); enc.Token(tokens.Method((IlMethod)x.Obj!)); break;
                case Operand.Field: enc.OpCode(x.Op); enc.Token(tokens.Field((IlField)x.Obj!)); break;
                case Operand.Token:
                    enc.OpCode(x.Op);
                    enc.Token(x.Obj switch
                    {
                        IlType t => tokens.Type(t), IlMethod m => tokens.Method(m), IlField f => tokens.Field(f),
                        _ => throw new InvalidOperationException("bad ldtoken operand"),
                    });
                    break;
                case Operand.String: enc.LoadString(tokens.String((string)x.Obj!)); break;
                case Operand.Sig: enc.OpCode(x.Op); enc.Token(tokens.CallSite((MethodSig)x.Obj!)); break;
                case Operand.Byte: enc.OpCode(x.Op); enc.CodeBuilder.WriteByte((byte)x.I); break;
            }
        }
        if (enc.Offset != off[n]) throw new InvalidOperationException("IlBuilder size table out of sync at the end");

        foreach (var r in regions)
        {
            LabelHandle H(IlLabel l) => handles[l.Id];
            switch (r.Kind)
            {
                case RegionKind.Catch: enc.ControlFlowBuilder!.AddCatchRegion(H(r.TryStart), H(r.TryEnd), H(r.HandlerStart), H(r.HandlerEnd), tokens.Type(r.CatchType!)); break;
                case RegionKind.Filter: enc.ControlFlowBuilder!.AddFilterRegion(H(r.TryStart), H(r.TryEnd), H(r.HandlerStart), H(r.HandlerEnd), H(r.FilterStart)); break;
                case RegionKind.Finally: enc.ControlFlowBuilder!.AddFinallyRegion(H(r.TryStart), H(r.TryEnd), H(r.HandlerStart), H(r.HandlerEnd)); break;
                default: enc.ControlFlowBuilder!.AddFaultRegion(H(r.TryStart), H(r.TryEnd), H(r.HandlerStart), H(r.HandlerEnd)); break;
            }
        }

        var localSig = locals.Count > 0 ? tokens.Locals(locals) : default;
        var attrs = InitLocals ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None;
        int offset = bodies.AddMethodBody(enc, maxStack, localSig, attrs, localloc);

        scopeEnd[0] = off[n];
        var infos = new List<IlScopeInfo>();
        for (int s = 0; s < scopes.Count; s++)
            infos.Add(new IlScopeInfo(scopeStart[s], scopeEnd[s] - scopeStart[s], [.. scopes[s].Locals.Where(l => l.Name != null)], s));
        infos.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Length != b.Length ? b.Length.CompareTo(a.Length) : a.Order.CompareTo(b.Order));
        return encoded = new IlEncodedBody(offset, localSig, off[n], maxStack, Dedupe(seq), infos);
    }

    /// <summary>At most one sequence point per IL offset (the last one marked wins).</summary>
    static List<IlSequencePoint> Dedupe(List<IlSequencePoint> seq)
    {
        var r = new List<IlSequencePoint>(seq.Count);
        foreach (var s in seq)
        {
            if (r.Count > 0 && r[^1].Offset == s.Offset) r[^1] = s; else r.Add(s);
        }
        return r;
    }

    void RebuildLabels()
    {
        for (int i = 0; i < labelPos.Count; i++) labelPos[i] = -1;
        for (int i = 0; i < code.Count; i++) if (code[i].Kind == IK.Label) labelPos[(int)code[i].I] = i;
    }

    /// <summary>Stack depths by abstract interpretation; inconsistent depths at a join or underflow are errors.</summary>
    int ComputeMaxStack()
    {
        int n = code.Count;
        var depth = new int[n + 1];
        Array.Fill(depth, -1);
        var work = new Stack<(int At, int Depth)>();
        work.Push((0, 0));
        foreach (var r in regions)
        {
            work.Push((labelPos[r.TryStart.Id], 0));
            work.Push((labelPos[r.HandlerStart.Id], r.Kind is RegionKind.Catch or RegionKind.Filter ? 1 : 0));
            if (r.Kind == RegionKind.Filter) work.Push((labelPos[r.FilterStart.Id], 1));
        }
        int max = 0;
        while (work.Count > 0)
        {
            var (i, d) = work.Pop();
            for (; i <= n; i++)
            {
                if (depth[i] >= 0)
                {
                    if (depth[i] != d) throw new InvalidOperationException($"stack depth {d} meets {depth[i]} at instruction {i}");
                    break;
                }
                depth[i] = d;
                if (i == n) throw new InvalidOperationException("control falls off the end of the method body");
                var x = code[i];
                if (x.Kind != IK.Op) continue;
                var (pop, push) = Effect(x);
                if (d < pop) throw new InvalidOperationException($"stack underflow at {x.Op} (instruction {i})");
                d = d - pop + push;
                max = Math.Max(max, d);
                if (x.Op == ILOpCode.Switch) foreach (var l in (IlLabel[])x.Obj!) work.Push((Target(l.Id), d));
                else if (OperandOf(x.Op) == Operand.Branch) work.Push((Target(x.I), x.Op == ILOpCode.Leave ? 0 : d));
                if (IsUnconditional(x.Op)) break;
            }
        }
        return max;
    }

    // ---------------------------------------------------------------- peephole

    void RunPeephole()
    {
        RebuildLabels();
        for (int round = 0; round < 16; round++)
        {
            bool changed = false;
            if ((Peephole & IlPeephole.Constants) != 0) changed |= FoldConstants();
            if ((Peephole & IlPeephole.Temps) != 0) changed |= Temps();
            if ((Peephole & IlPeephole.Branches) != 0) changed |= Branches();
            if (!changed) break;
        }
    }

    /// <summary>The next instruction after <paramref name="i"/> if nothing (label, sequence point, scope) separates them.</summary>
    int NextOp(int i) => i + 1 < code.Count && code[i + 1].Kind == IK.Op ? i + 1 : -1;

    bool PrefixedAt(int i) => i > 0 && code[i - 1].Kind == IK.Op && IsPrefix(code[i - 1].Op);

    void Remove(params int[] at)
    {
        foreach (var i in at.OrderByDescending(x => x)) code.RemoveAt(i);
        RebuildLabels();
    }

    void Replace(int i, Instr x) => code[i] = x;

    static Instr I4(long v) => new() { Kind = IK.Op, Op = ILOpCode.Ldc_i4, I = v };
    static Instr I8(long v) => new() { Kind = IK.Op, Op = ILOpCode.Ldc_i8, I = v };
    static Instr R4(double v) => new() { Kind = IK.Op, Op = ILOpCode.Ldc_r4, R = (float)v };
    static Instr R8(double v) => new() { Kind = IK.Op, Op = ILOpCode.Ldc_r8, R = v };

    bool FoldConstants()
    {
        bool changed = false;
        for (int i = 0; i < code.Count; i++)
        {
            var a = code[i];
            if (a.Kind != IK.Op || a.Op is not (ILOpCode.Ldc_i4 or ILOpCode.Ldc_i8 or ILOpCode.Ldc_r4 or ILOpCode.Ldc_r8)) continue;
            int j = NextOp(i);
            if (j < 0) continue;
            if (FoldConvValue(a, code[j].Op) is { } folded)
            {
                Replace(i, folded);
                Remove(j);
                changed = true;
                i = Math.Max(-1, i - 2);
                continue;
            }
            int k = NextOp(j);
            if (k >= 0 && FoldBinary(a, code[j], code[k].Op) is { } bin)
            {
                Replace(i, bin);
                Remove(j, k);
                changed = true;
                i = Math.Max(-1, i - 2);
            }
        }
        return changed;
    }

    /// <summary>`ldc x; conv.*` as one constant, or null when the conversion cannot be folded (or could throw).</summary>
    static Instr? FoldConvValue(in Instr a, ILOpCode conv)
    {
        bool isInt = a.Op is ILOpCode.Ldc_i4 or ILOpCode.Ldc_i8;
        bool is32 = a.Op == ILOpCode.Ldc_i4;
        long v = a.I;
        double r = a.R;
        // an integer source of conv.r.un / conv.ovf.*.un is reinterpreted as unsigned at its width
        ulong u = is32 ? (uint)(int)v : (ulong)v;
        static bool InI4(long x) => x is >= int.MinValue and <= int.MaxValue;
        static bool FiniteIn(double x, double lo, double hi) => !double.IsNaN(x) && x >= lo && x < hi;
        switch (conv)
        {
            case ILOpCode.Conv_i1: return isInt ? I4((sbyte)v) : FiniteIn(r, -128, 128) ? I4((sbyte)r) : null;
            case ILOpCode.Conv_u1: return isInt ? I4((byte)v) : FiniteIn(r, 0, 256) ? I4((byte)r) : null;
            case ILOpCode.Conv_i2: return isInt ? I4((short)v) : FiniteIn(r, -32768, 32768) ? I4((short)r) : null;
            case ILOpCode.Conv_u2: return isInt ? I4((ushort)v) : FiniteIn(r, 0, 65536) ? I4((ushort)r) : null;
            case ILOpCode.Conv_i4: return isInt ? I4((int)v) : FiniteIn(r, int.MinValue, 2147483648.0) ? I4((int)r) : null;
            case ILOpCode.Conv_u4: return isInt ? I4((int)(uint)v) : FiniteIn(r, 0, 4294967296.0) ? I4((int)(uint)r) : null;
            case ILOpCode.Conv_i8: return isInt ? I8(is32 ? (int)v : v) : FiniteIn(r, long.MinValue, 9223372036854775808.0) ? I8((long)r) : null;
            case ILOpCode.Conv_u8: return isInt ? I8(is32 ? (long)(uint)(int)v : v) : FiniteIn(r, 0, 18446744073709551616.0) ? I8((long)(ulong)r) : null;
            case ILOpCode.Conv_r4: return isInt ? R4(is32 ? (float)(int)v : (float)v) : R4((float)r);
            case ILOpCode.Conv_r8: return isInt ? R8(is32 ? (int)v : (double)v) : R8(a.Op == ILOpCode.Ldc_r4 ? (float)r : r);
            case ILOpCode.Conv_r_un: return isInt ? R8(u) : null;
            case ILOpCode.Conv_ovf_i1: return isInt && v is >= sbyte.MinValue and <= sbyte.MaxValue ? I4(v) : null;
            case ILOpCode.Conv_ovf_u1: return isInt && v is >= 0 and <= byte.MaxValue ? I4(v) : null;
            case ILOpCode.Conv_ovf_i2: return isInt && v is >= short.MinValue and <= short.MaxValue ? I4(v) : null;
            case ILOpCode.Conv_ovf_u2: return isInt && v is >= 0 and <= ushort.MaxValue ? I4(v) : null;
            case ILOpCode.Conv_ovf_i4: return isInt && InI4(v) ? I4(v) : null;
            case ILOpCode.Conv_ovf_u4: return isInt && v is >= 0 and <= uint.MaxValue ? I4((int)(uint)v) : null;
            case ILOpCode.Conv_ovf_i8: return isInt ? I8(v) : null;
            case ILOpCode.Conv_ovf_u8: return isInt && v >= 0 ? I8(v) : null;
            case ILOpCode.Conv_ovf_u1_un: return isInt && u <= byte.MaxValue ? I4((long)u) : null;
            case ILOpCode.Conv_ovf_u2_un: return isInt && u <= ushort.MaxValue ? I4((long)u) : null;
            case ILOpCode.Conv_ovf_i4_un: return isInt && u <= int.MaxValue ? I4((long)u) : null;
            default: return null;
        }
    }

    static Instr? FoldBinary(in Instr a, in Instr b, ILOpCode op)
    {
        if (a.Op != b.Op) return null;
        if (a.Op == ILOpCode.Ldc_i4)
        {
            int x = (int)a.I, y = (int)b.I;
            long wide = op switch
            {
                ILOpCode.Add or ILOpCode.Add_ovf => (long)x + y, ILOpCode.Sub or ILOpCode.Sub_ovf => (long)x - y,
                ILOpCode.Mul or ILOpCode.Mul_ovf => (long)x * y, _ => 0,
            };
            return op switch
            {
                ILOpCode.Add => I4(unchecked(x + y)), ILOpCode.Sub => I4(unchecked(x - y)), ILOpCode.Mul => I4(unchecked(x * y)),
                ILOpCode.And => I4(x & y), ILOpCode.Or => I4(x | y), ILOpCode.Xor => I4(x ^ y),
                ILOpCode.Shl when y is >= 0 and < 32 => I4(x << y),
                ILOpCode.Shr when y is >= 0 and < 32 => I4(x >> y),
                ILOpCode.Shr_un when y is >= 0 and < 32 => I4((int)((uint)x >> y)),
                ILOpCode.Add_ovf or ILOpCode.Sub_ovf or ILOpCode.Mul_ovf when wide is >= int.MinValue and <= int.MaxValue => I4(wide),
                _ => null,
            };
        }
        if (a.Op == ILOpCode.Ldc_i8)
        {
            long x = a.I, y = b.I;
            return op switch
            {
                ILOpCode.Add => I8(unchecked(x + y)), ILOpCode.Sub => I8(unchecked(x - y)), ILOpCode.Mul => I8(unchecked(x * y)),
                ILOpCode.And => I8(x & y), ILOpCode.Or => I8(x | y), ILOpCode.Xor => I8(x ^ y),
                _ => null,
            };
        }
        return null;
    }

    bool Temps()
    {
        bool changed = false;
        var loads = new int[locals.Count];
        var addressed = new bool[locals.Count];
        foreach (var c in code)
        {
            if (c.Kind != IK.Op) continue;
            if (c.Op == ILOpCode.Ldloc) loads[c.I]++;
            else if (c.Op == ILOpCode.Ldloca) addressed[c.I] = true;
        }
        for (int i = 0; i < code.Count; i++)
        {
            var a = code[i];
            if (a.Kind != IK.Op || PrefixedAt(i)) continue;
            int j = NextOp(i);
            if (j < 0) continue;
            var b = code[j];
            // stloc t; ldloc t: the value just stays on the stack (t unnamed, read only here, never addressed)
            if (a.Op == ILOpCode.Stloc && b.Op == ILOpCode.Ldloc && a.I == b.I)
            {
                var l = locals[(int)a.I];
                if (l.Name == null && !l.Pinned && !addressed[a.I] && loads[a.I] == 1)
                {
                    loads[a.I] = 0;
                    Remove(i, j); changed = true; i = Math.Max(-1, i - 2); continue;
                }
            }
            // ldloc x; stloc x
            if (a.Op == ILOpCode.Ldloc && b.Op == ILOpCode.Stloc && a.I == b.I)
            {
                loads[a.I]--;
                Remove(i, j); changed = true; i = Math.Max(-1, i - 2); continue;
            }
            // side-effect-free push; pop
            if (b.Op == ILOpCode.Pop && a.Op is ILOpCode.Dup or ILOpCode.Ldloc or ILOpCode.Ldarg or ILOpCode.Ldc_i4 or ILOpCode.Ldc_i8
                    or ILOpCode.Ldc_r4 or ILOpCode.Ldc_r8 or ILOpCode.Ldnull or ILOpCode.Ldstr or ILOpCode.Ldloca or ILOpCode.Ldarga)
            {
                if (a.Op == ILOpCode.Ldloc) loads[a.I]--;
                Remove(i, j); changed = true; i = Math.Max(-1, i - 2);
            }
        }
        return changed;
    }

    /// <summary>The first instruction executed after label <paramref name="id"/>.</summary>
    int OpAt(long id)
    {
        for (int p = Target(id); p < code.Count; p++) if (code[p].Kind == IK.Op) return p;
        return -1;
    }

    /// <summary>True when only labels, sequence points or scope markers separate <paramref name="i"/> from label <paramref name="id"/>'s position.</summary>
    bool FallsInto(int i, long id)
    {
        int t = Target(id);
        if (t <= i) return false;
        for (int p = i + 1; p < t; p++) if (code[p].Kind == IK.Op) return false;
        return true;
    }

    bool Branches()
    {
        bool changed = false;
        for (int i = 0; i < code.Count; i++)
        {
            var a = code[i];
            if (a.Kind != IK.Op || OperandOf(a.Op) != Operand.Branch || a.Op == ILOpCode.Leave) continue;
            // chain: the target is an unconditional br
            for (int hop = 0; hop < 8; hop++)
            {
                int t = OpAt(a.I);
                if (t < 0 || code[t].Op != ILOpCode.Br || code[t].I == a.I || t == i) break;
                a.I = code[t].I;
                code[i] = a;
                changed = true;
            }
            // br to the next instruction
            if (a.Op == ILOpCode.Br && FallsInto(i, a.I))
            {
                Remove(i); changed = true; i--; continue;
            }
            // brcond L1; br L2; L1:  ->  brinv L2; L1:
            int j = NextOp(i);
            if (j >= 0 && code[j].Op == ILOpCode.Br && FallsInto(j, a.I) && Invert(a.Op) is { } inv)
            {
                code[i] = new Instr { Kind = IK.Op, Op = inv, I = code[j].I };
                Remove(j); changed = true;
            }
        }
        return changed;
    }

    static ILOpCode? Invert(ILOpCode op) => op switch
    {
        ILOpCode.Brtrue => ILOpCode.Brfalse, ILOpCode.Brfalse => ILOpCode.Brtrue,
        ILOpCode.Beq => ILOpCode.Bne_un, ILOpCode.Bne_un => ILOpCode.Beq,
        _ => null,
    };
}
