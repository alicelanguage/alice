using System.Globalization;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Wasm;

// Expressions: leaves, operators (C# promotion and checked arithmetic, ADR 0013), conversions, conditionals, type
// tests. Calls, members, indexing and construction are in WasmBody.Calls.cs.
public sealed partial class WasmBody
{
    /// <summary>Emits <paramref name="e"/> converted to <paramref name="want"/> (implicit conversions); returns the pushed type.</summary>
    internal WT Emit(MExpr e, WT? want)
    {
        if (want == null || want.K == WK.Void) return Raw(e);
        if (e.Span != null) span = e.Span;
        if (Literal(e, out var lit, out var litT) && lit != null && want.IsNumeric && litT.IsNumeric)
        {
            if (litT.IsFloat == want.IsFloat && Fits(lit, want) || !litT.IsFloat && want.IsFloat) { Const(want, lit); return want; }
        }
        if (IsNullLit(e) && (want.IsRef || want.K == WK.Func)) { code.I32(0); return want; }
        if (IsDefaultLit(e)) { DefaultValue(want); return want; }
        if (e is MCond cd) { Cond(cd, want); return want; }
        var t = Raw(e);
        Convert(t, want, explicitCast: false);
        return want;
    }

    static bool IsNullLit(MExpr e) => e is AirNull or MCode { Text: "null" };
    static bool IsDefaultLit(MExpr e) => e is AirDefault { Type: null } or MCode { Text: "default" or "default!" };

    /// <summary>Pushes the zero value of <paramref name="t"/> (a zeroed frame temp for a struct).</summary>
    void DefaultValue(WT t)
    {
        switch (t.Val)
        {
            case ValType.I64: code.I64(0); return;
            case ValType.F32: code.F32(0); return;
            case ValType.F64: code.F64(0); return;
        }
        if (t.IsStruct)
        {
            var tmp = FrameTemp(t);
            ZeroFrame(tmp.Frame, t.Size);
            FrameAddr(tmp.Frame);
            return;
        }
        if (t.K == WK.Void) return;
        code.I32(0);
    }

    void ZeroFrame(int off, int size)
    {
        FrameAddr(off);
        code.I32(0);
        code.I32(size);
        code.Prefixed(Op.MemoryFill);
        code.B(0);
    }

    // ------------------------------------------------------------------ dispatch

    WT Raw(MExpr e)
    {
        if (e.Span != null) span = e.Span;
        Seen(e.GetType().Name);
        switch (e)
        {
            case AirConst ac: return ConstExpr(ac);
            case AirNull: code.I32(0); return WT.Null;
            case AirThis th: return This(th.Extension);
            case AirBase: return This(false, asBase: true);
            case AirLocalRef { Narrowed: true }: throw No("AirLocalRef:narrowed-nullable");
            case AirLocalRef lr: return Name(lr.Name, e);
            case AirParamRef pr: return Name(pr.Name, e);
            case AirFieldRef fr: return Name(fr.Name, e);
            case AirDefault d:
                {
                    var t = d.Type != null ? c.FromAir(d.Type, span) : throw No("AirDefault:untyped");
                    DefaultValue(t);
                    return t;
                }
            case AirArrayLength al: return Length(new AirLocalRef(al.Array));
            case AirTypeRef tr: return WT.TypeRefOf(tr.Text, c.ClassNamed(tr.Text));
            case AirMethodGroup mg: return MethodGroup(mg);
            case AirTypeOf: throw No("AirTypeOf (reflection; runtime = \"dotnet\")");
            case AirSizeOf so: { var t = c.FromAir(so.Of, span); code.I32(t.Size); return WT.I32; }
            case AirDiscard: throw No("AirDiscard:value");
            case AirYieldBreak: throw No("AirYieldBreak");
            case AirStackAlloc sa: return NewArrayOf(c.FromAir(sa.Elem, span), sa.Count);
            case TempRef tr: return LoadVar(tr.V);
            case MCode mc: return Code(mc.Text, e);
            case MBinary b: return Binary(b);
            case MUnary u: return Unary(u);
            case MCond cd: { var t = CondType(cd); Cond(cd, t); return t; }
            case MCast cs: return Cast(cs);
            case MIs i: return Is(i);
            case AirAs aa: return As(aa);
            case MCall call: return Call(call);
            case MMember m: return Member(m);
            case MIndex ix: return Index(ix);
            case MNew n: return New(n);
            case MNewArray na: return NewArray(na);
            case AirStackBuffer sb:
                {
                    // `stackalloc T[n]` as a Span: a heap array here (same semantics; the collector frees it, not the frame)
                    var elem = c.Parse(sb.ElemCs, span);
                    code.I32(sb.Length); code.I32(Math.Max(1, elem.Size)); code.I32(elem.ArrayKind);
                    code.Call(c.Rt.Func("array_new"));
                    return WT.ArrayOf(elem);
                }
            case MInterp ip: return Interp(ip);
            case MInvoke inv: return Invoke(inv);
            case MLambda lam: return Lambda(lam);
            case AirSeq sq:
                {
                    foreach (var s in sq.Pre) Stmt(s);
                    return Raw(sq.Value);
                }
            case AirRent r: return Rent(r);
            case AirRelease r:
                {
                    Emit(r.Target, null);
                    code.Call(c.Rt.Func("rt_free"));
                    return WT.Void;
                }
            case AirLength l: return Length(l.Target);
            case AirOffset o: return Binary(new MBinary { Op = AirBinOp.Add, L = o.Base, R = o.Offset, Checked = false });
            case AirTripCount tc:
                {
                    var d = new MBinary { Op = AirBinOp.Sub, L = tc.End, R = tc.Start };
                    return Raw(tc.Inclusive ? new MBinary { Op = AirBinOp.Add, L = d, R = AirConst.Int(1) } : d);
                }
            case AirRangeLength rl: return MathMax0(rl.Lo, rl.Hi, !rl.Exclusive);
            case AirPresizeCount pc: return MathMax0(pc.From, pc.To, pc.Inclusive);
            case AirVectorFits: code.I32(0); return WT.Bool;   // no wide vectors in the core profile: the scalar loop runs
            case AirVectorOp { Op: AirVecOp.IsAccelerated }: code.I32(0); return WT.Bool;
            case AirVectorOp or AirVectorLoad or AirVectorStore or AirVectorReduce: throw No("Vector (SIMD128 not yet)");
            case MTuple: throw No("MTuple");
            case MArg a: throw No("MArg", a.Prefix);
            case AirParExpr p: throw No(p.GetType().Name);
            default: throw No(e.GetType().Name);
        }
    }

    /// <summary>`max(0, hi - lo [+ 1])` (presize counts and range lengths).</summary>
    WT MathMax0(MExpr lo, MExpr hi, bool inclusive)
    {
        MExpr d = new MBinary { Op = AirBinOp.Sub, L = hi, R = lo, Checked = false };
        if (inclusive) d = new MBinary { Op = AirBinOp.Add, L = d, R = AirConst.Int(1), Checked = false };
        Emit(d, WT.I32);
        int t = Temp(ValType.I32);
        code.Tee(t);
        code.I32(0);
        code.Get(t);
        code.I32(0);
        code.Emit(Op.I32GtS);
        code.Emit(Op.Select);
        return WT.I32;
    }

    // ------------------------------------------------------------------ leaves

    WT This(bool extension, bool asBase = false)
    {
        if (thisLocal < 0 && lambda?.Find("this") is { } lt) { LoadVar(lt); return asBase && lt.T.Cls!.Base != null ? lt.T.Cls!.Base.Type : lt.T; }
        if (thisLocal < 0) throw No("AirThis:static");
        if (env != null && captured.Contains("this") && envSlots.ContainsKey("this") && thisEnv != null) { LoadVar(thisEnv); return asBase && owner.Base != null ? owner.Base.Type : owner.Type; }
        if (owner.IsStruct) code.Get(thisLocal);
        else if (thisSlot >= 0) { code.Get(Fp()); code.Mem(Op.I32Load, (uint)thisSlot); }
        else code.Get(thisLocal);
        if (asBase && owner.Base != null) return owner.Base.Type;
        return owner.Type;
    }

    /// <summary>A name: a local, a parameter, `this`'s field, a static field of the owner or Program, ARGV.</summary>
    WT Name(string name, MExpr? e)
    {
        string bare = AirToWasm.Bare(name);
        if (Find(bare) is { } v) return LoadVar(v);
        if (bare == "this") return This(false);
        if (bare == "ARGV")
        {
            c.usesArgv = true;
            code.I32(c.ArgvSlot);
            code.Mem(Op.I32Load);
            return WT.ArrayOf(WT.Str);
        }
        if (FieldOf(owner, bare) is { } f) return LoadField(f, null);
        if (owner != c.ProgramC && FieldOf(c.ProgramC, bare) is { } pf) return LoadField(pf, null);
        if (c.ClassNamed(bare) is { } cls) return WT.TypeRefOf(bare, cls);
        if (bare is "Prelude" or "Math" or "MathF" || bare.StartsWith("global::")) return WT.TypeRefOf(bare);
        // `xs.Length` left as text by OwnedBounds/LoopTiling (a loop bound hoisted to the array length)
        if (bare.EndsWith(".Length", StringComparison.Ordinal) && bare.IndexOf('.') == bare.Length - ".Length".Length)
            return Length(new AirLocalRef(bare[..^".Length".Length]));
        throw No("Name:unresolved", bare);
    }

    static WField? FieldOf(WClass k, string name) => k.FindField(name) ?? k.FindField("@" + name);

    WT LoadVar(Var v)
    {
        if (v.EnvHolder != null)
        {
            EnvAddr(v);
            if (v.T.IsStruct) { code.I32(v.EnvOff); code.Emit(Op.I32Add); return v.T; }
            code.Mem(v.T.Load, (uint)v.EnvOff);
            return v.T;
        }
        if (v.Local >= 0) { code.Get(v.Local); return v.T; }
        if (v.AddrLocal >= 0) { code.Get(v.AddrLocal); return v.T; }
        if (v.T.IsStruct) { FrameAddr(v.Frame); return v.T; }
        code.Get(Fp());
        code.Mem(v.T.Load, (uint)v.Frame);
        return v.T;
    }

    /// <summary>Stores into a variable; <paramref name="value"/> pushes the value (a struct: its address, copied).</summary>
    void StoreVar(Var v, Func<WT> value)
    {
        if (v.T.IsStruct)
        {
            VarAddr(v);
            var t = value();
            if (!t.IsStruct) throw No("Assign:struct-from", t.Name);
            code.I32(v.T.Size);
            code.Call(c.Rt.Func("memcpy"));
            return;
        }
        if (v.EnvHolder != null)
        {
            EnvAddr(v);
            Convert(value(), v.T, false);
            code.Mem(v.T.Store, (uint)v.EnvOff);
            return;
        }
        if (v.Local >= 0)
        {
            Convert(value(), v.T, false);
            code.Set(v.Local);
            return;
        }
        code.Get(Fp());
        Convert(value(), v.T, false);
        code.Mem(v.T.Store, (uint)v.Frame);
    }

    void VarAddr(Var v)
    {
        if (v.EnvHolder != null) { EnvAddr(v); code.I32(v.EnvOff); code.Emit(Op.I32Add); return; }
        if (v.AddrLocal >= 0) { code.Get(v.AddrLocal); return; }
        if (v.Frame < 0) throw No("Var:address", v.Name);
        FrameAddr(v.Frame);
    }

    /// <summary>A numeric or char literal leaf: `3`, `2654435761U`, `1.5`, `0.5f`, `'a'`, `-1`.</summary>
    bool Literal(MExpr e, out object? v, out WT t)
    {
        v = null;
        t = WT.Void;
        string? text = e switch
        {
            AirConst ac when ac.Kind is AirConstKind.Int or AirConstKind.Float or AirConstKind.Char => ac.Text,
            AirConst => null,
            MCode mc when e is not (AirLocalRef or AirParamRef or AirFieldRef or AirThis or AirBase) && Find(mc.Text) == null => mc.Text,
            _ => null,
        };
        bool neg = false;
        if (text == null && e is MUnary { Op: AirUnOp.Neg, E: MCode inner } && inner is not (AirLocalRef or AirParamRef or AirFieldRef) && Find(inner.Text) == null)
        {
            text = inner.Text;
            neg = true;
        }
        if (text == null || !Il.IlTypes.TryLiteral((neg ? "-" : "") + text, out var ct, out v) || v == null || v is string or bool) { v = null; return false; }
        t = ClrPrim(ct);
        return t.K != WK.Void;
    }

    static WT ClrPrim(Type t) =>
        t == typeof(int) ? WT.I32 : t == typeof(uint) ? WT.U32 : t == typeof(long) ? WT.I64 : t == typeof(ulong) ? WT.U64 :
        t == typeof(float) ? WT.F32 : t == typeof(double) ? WT.F64 : t == typeof(char) ? WT.Char : t == typeof(bool) ? WT.Bool :
        t == typeof(byte) ? WT.U8 : t == typeof(sbyte) ? WT.I8 : t == typeof(short) ? WT.I16 : t == typeof(ushort) ? WT.U16 : WT.Void;

    static bool Fits(object v, WT t)
    {
        if (v is double or float) return t.IsFloat;
        decimal d;
        try { d = v is char ch ? ch : System.Convert.ToDecimal(v, CultureInfo.InvariantCulture); } catch { return false; }
        return t.K switch
        {
            WK.I8 => d >= sbyte.MinValue && d <= sbyte.MaxValue,
            WK.U8 => d >= 0 && d <= byte.MaxValue,
            WK.I16 => d >= short.MinValue && d <= short.MaxValue,
            WK.U16 or WK.Char => d >= 0 && d <= ushort.MaxValue,
            WK.I32 => d >= int.MinValue && d <= int.MaxValue,
            WK.U32 => d >= 0 && d <= uint.MaxValue,
            WK.I64 => d >= long.MinValue && d <= long.MaxValue,
            WK.U64 => d >= 0 && d <= ulong.MaxValue,
            WK.F32 or WK.F64 => true,
            WK.Enum => Fits(v, t.Cls!.EnumUnderlying!),
            _ => false,
        };
    }

    void Const(WT t, object v)
    {
        switch (t.Val)
        {
            case ValType.F32: code.F32(v is float f ? f : v is double d ? (float)d : v is char c0 ? c0 : System.Convert.ToSingle(v, CultureInfo.InvariantCulture)); return;
            case ValType.F64: code.F64(v is double d2 ? d2 : v is float f2 ? f2 : v is char c1 ? c1 : System.Convert.ToDouble(v, CultureInfo.InvariantCulture)); return;
            case ValType.I64: code.I64(v is ulong ul ? unchecked((long)ul) : v is char c2 ? c2 : System.Convert.ToInt64(v, CultureInfo.InvariantCulture)); return;
        }
        long x = v switch
        {
            ulong ul2 => unchecked((long)ul2),
            char ch => ch,
            bool b => b ? 1 : 0,
            _ => System.Convert.ToInt64(v, CultureInfo.InvariantCulture),
        };
        code.I32(unchecked((int)x));
    }

    WT ConstExpr(AirConst ac)
    {
        switch (ac.Kind)
        {
            case AirConstKind.Bool: code.I32(ac.Literal == "true" ? 1 : 0); return WT.Bool;
            case AirConstKind.String:
                code.I32(c.Literal(ac.Value as string ?? Unescape(ac.Literal)));
                return WT.Str;
        }
        if (ac.Value != null && ac.Value is not string)
        {
            var t = ClrPrim(ac.Value.GetType());
            Const(t, ac.Value);
            return t;
        }
        if (Literal(ac, out var v, out var lt) && v != null) { Const(lt, v); return lt; }
        // a narrow integer constant the lowering spells as a cast: `((byte)105)`
        if (System.Text.RegularExpressions.Regex.Match(ac.Text, @"^\(*\((sbyte|byte|short|ushort|int|uint|long|ulong)\)(-?[0-9]+)\)*$") is { Success: true } cm)
        {
            var ct = cm.Groups[1].Value switch
            {
                "sbyte" => WT.I8, "byte" => WT.U8, "short" => WT.I16, "ushort" => WT.U16, "int" => WT.I32, "uint" => WT.U32, "long" => WT.I64, _ => WT.U64,
            };
            Const(ct, ct.K == WK.U64 ? ulong.Parse(cm.Groups[2].Value, CultureInfo.InvariantCulture) : long.Parse(cm.Groups[2].Value, CultureInfo.InvariantCulture));
            return ct;
        }
        throw No("AirConst", ac.Text);
    }

    /// <summary>C# escapes of a string literal body.</summary>
    internal static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch != '\\' || i + 1 >= s.Length) { sb.Append(ch); continue; }
            char n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'e': sb.Append('\u001b'); break;
                case 'u' when i + 4 < s.Length: sb.Append((char)int.Parse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                case 'U' when i + 8 < s.Length: sb.Append(char.ConvertFromUtf32(int.Parse(s.Substring(i + 1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture))); i += 8; break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>MCode text: a literal, `null`/`default`/`true`/`false`, a string literal, or a name.</summary>
    WT Code(string text, MExpr e)
    {
        switch (text)
        {
            case "null": code.I32(0); return WT.Null;
            case "true": code.I32(1); return WT.Bool;
            case "false": code.I32(0); return WT.Bool;
            case "default" or "default!": throw No("Default:untyped");
        }
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') { code.I32(c.Literal(Unescape(text[1..^1]))); return WT.Str; }
        if (text.StartsWith("default(") && text.EndsWith(')'))
        {
            var t = c.Parse(text[8..^1], span);
            DefaultValue(t);
            return t;
        }
        if (Literal(e, out var v, out var lt) && v != null) { Const(lt, v); return lt; }
        // C# expression text the lowering still produces (`c.Length`, `((byte)105)`): parsed as the IL backend does
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^@?[A-Za-z_][A-Za-z0-9_]*$") && Find(text) == null
            && Il.CsText.Parse(text, IsTypeText) is { } parsed && !(parsed is MCode pm && pm.Text == text))
            return Raw(parsed);
        return Name(text, e);
    }

    bool IsTypeText(string t)
    {
        if (Find(t) != null) return false;
        try { return c.Parse(t, span).K != WK.TypeRef; }
        catch (WasmUnsupportedException) { return false; }
    }

    // ------------------------------------------------------------------ conversions

    /// <summary>Converts the value on the stack. Implicit conversions are the C# ones; <paramref name="explicitCast"/>
    /// adds narrowing (checked in a checked context: OverflowException, ADR 0013) and downcasts.</summary>
    internal void Convert(WT from, WT to, bool explicitCast, bool? chk = null)
    {
        if (from.Same(to) || to.K == WK.Void && from.K == WK.Void) return;
        if (to.K == WK.Void) { if (from.K != WK.Void) code.Emit(Op.Drop); return; }
        if (from.K == WK.Null && (to.IsRef || to.K == WK.Func)) return;
        if (from.IsRef && to.IsRef)
        {
            if (from.K == WK.Class && to.K == WK.Class && !from.Cls!.IsSubclassOf(to.Cls!))
            {
                if (!explicitCast && !to.Cls!.IsSubclassOf(from.Cls!)) throw No("Convert:class", from.Name + " -> " + to.Name);
                CastCheck(to.Cls!);
                return;
            }
            if (from.K != to.K && from.K != WK.Null && !(from.K == WK.Builder || to.K == WK.Builder)) throw No("Convert:ref", from.Name + " -> " + to.Name);
            return;
        }
        if (from.K == WK.Func && to.K == WK.Func) return;
        if (from.K == WK.Bool && to.K == WK.Bool) return;
        if (from.IsStruct && to.IsStruct && from.Cls == to.Cls) return;
        if ((from.IsNumeric || from.K == WK.Char) && (to.IsNumeric || to.K == WK.Char))
        {
            Numeric(from, to, chk ?? (explicitCast && Checked));
            return;
        }
        throw No("Convert", from.Name + " -> " + to.Name);
    }

    static WT Under(WT t) => t.K == WK.Enum ? t.Cls!.EnumUnderlying! : t;

    void Numeric(WT from, WT to, bool chk)
    {
        var f = Under(from);
        var t = Under(to);
        if (f.Same(t)) return;
        if (f.IsFloat && t.IsFloat)
        {
            code.Emit(t.K == WK.F64 ? Op.F64PromoteF32 : Op.F32DemoteF64);
            return;
        }
        if (f.IsInt && t.IsFloat)
        {
            bool u = f.IsUnsigned;
            if (f.Is64) code.Emit(t.K == WK.F64 ? (u ? Op.F64ConvertI64U : Op.F64ConvertI64S) : (u ? Op.F32ConvertI64U : Op.F32ConvertI64S));
            else code.Emit(t.K == WK.F64 ? (u ? Op.F64ConvertI32U : Op.F64ConvertI32S) : (u ? Op.F32ConvertI32U : Op.F32ConvertI32S));
            return;
        }
        if (f.IsFloat && t.IsInt)
        {
            if (chk) { FloatToIntChecked(f, t); return; }
            bool u = t.IsUnsigned;
            bool d = f.K == WK.F64;
            if (t.Is64) code.Prefixed(d ? (u ? Op.I64TruncSatF64U : Op.I64TruncSatF64S) : (u ? Op.I64TruncSatF32U : Op.I64TruncSatF32S));
            else if (t.K == WK.U32) code.Prefixed(d ? Op.I32TruncSatF64U : Op.I32TruncSatF32U);
            else code.Prefixed(d ? Op.I32TruncSatF64S : Op.I32TruncSatF32S);
            Normalize(t);
            return;
        }
        // integer to integer
        if (!f.Is64 && t.Is64)
        {
            if (chk && !f.IsUnsigned && t.IsUnsigned) RangeCheck32(0, int.MaxValue, signed: true);
            code.Emit(f.IsUnsigned ? Op.I64ExtendI32U : Op.I64ExtendI32S);
            return;
        }
        if (f.Is64 && t.Is64)
        {
            if (chk && f.IsUnsigned != t.IsUnsigned)
            {
                // either direction: the top bit must be clear
                int tmp = Temp(ValType.I64);
                code.Tee(tmp);
                code.I64(0);
                code.Emit(Op.I64LtS);
                ThrowIf(AirToWasm.ErrOverflow);
                code.Get(tmp);
            }
            return;
        }
        if (f.Is64 && !t.Is64)
        {
            if (chk)
            {
                int tmp = Temp(ValType.I64);
                code.Tee(tmp);
                var (lo, hi) = Range(t);
                if (f.IsUnsigned) { code.I64(hi); code.Emit(Op.I64GtU); }
                else
                {
                    code.I64(lo); code.Emit(Op.I64LtS);
                    code.Get(tmp); code.I64(hi); code.Emit(Op.I64GtS);
                    code.Emit(Op.I32Or);
                }
                ThrowIf(AirToWasm.ErrOverflow);
                code.Get(tmp);
            }
            code.Emit(Op.I32WrapI64);
            Normalize(t);
            return;
        }
        // 32-bit representations
        if (chk)
        {
            var (lo, hi) = Range(t);
            var (flo, fhi) = Range(f);
            if (lo > flo || hi < fhi) RangeCheck32(lo, hi, signed: !f.IsUnsigned);
        }
        Normalize(t);
    }

    static (long Lo, long Hi) Range(WT t) => t.K switch
    {
        WK.I8 => (sbyte.MinValue, sbyte.MaxValue),
        WK.U8 or WK.Bool => (0, byte.MaxValue),
        WK.I16 => (short.MinValue, short.MaxValue),
        WK.U16 or WK.Char => (0, ushort.MaxValue),
        WK.I32 => (int.MinValue, int.MaxValue),
        WK.U32 => (0, uint.MaxValue),
        WK.I64 => (long.MinValue, long.MaxValue),
        _ => (0, long.MaxValue),
    };

    /// <summary>Overflow unless lo &lt;= v &lt;= hi for the 32-bit value on the stack (interpreted signed or not).</summary>
    void RangeCheck32(long lo, long hi, bool signed)
    {
        int tmp = Temp(ValType.I32);
        code.Tee(tmp);
        if (signed)
        {
            code.I32(unchecked((int)Math.Max(lo, int.MinValue)));
            code.Emit(Op.I32LtS);
            code.Get(tmp);
            code.I32(unchecked((int)Math.Min(hi, int.MaxValue)));
            code.Emit(Op.I32GtS);
            code.Emit(Op.I32Or);
        }
        else
        {
            code.I32(unchecked((int)(uint)Math.Min(hi, uint.MaxValue)));
            code.Emit(Op.I32GtU);
        }
        ThrowIf(AirToWasm.ErrOverflow);
        code.Get(tmp);
    }

    /// <summary>Small integer types keep their value normalized in an i32 (sign- or zero-extended).</summary>
    void Normalize(WT t)
    {
        switch (t.K)
        {
            case WK.I8: code.Emit(Op.I32Extend8S); break;
            case WK.U8: code.I32(0xFF); code.Emit(Op.I32And); break;
            case WK.I16: code.Emit(Op.I32Extend16S); break;
            case WK.U16 or WK.Char: code.I32(0xFFFF); code.Emit(Op.I32And); break;
        }
    }

    /// <summary>Checked float to integer (C# `checked((int)x)`, `x.to_u32`): NaN or out of range is OverflowException;
    /// the value truncates toward zero.</summary>
    void FloatToIntChecked(WT f, WT t)
    {
        bool d = f.K == WK.F64;
        var vt = d ? ValType.F64 : ValType.F32;
        int tmp = Temp(vt);
        code.Tee(tmp);
        // in range: lo - 1 < x < hi + 1 (NaN fails both)
        var (lo, hi) = Range(t);
        double dlo = t.IsUnsigned ? -1.0 : lo - 1.0, dhi = t.K == WK.U64 ? 18446744073709551616.0 : hi + 1.0;
        if (t.K == WK.I64) { dlo = -9223372036854777856.0; dhi = 9223372036854775808.0; }
        if (!d) code.Emit(Op.F64PromoteF32);
        code.F64(dlo); code.Emit(Op.F64Gt);
        code.Get(tmp);
        if (!d) code.Emit(Op.F64PromoteF32);
        code.F64(dhi); code.Emit(Op.F64Lt);
        code.Emit(Op.I32And);
        code.Emit(Op.I32Eqz);
        ThrowIf(AirToWasm.ErrOverflow);
        code.Get(tmp);
        bool u = t.IsUnsigned;
        if (t.Is64) code.Prefixed(d ? (u ? Op.I64TruncSatF64U : Op.I64TruncSatF64S) : (u ? Op.I64TruncSatF32U : Op.I64TruncSatF32S));
        else if (t.K == WK.U32) code.Prefixed(d ? Op.I32TruncSatF64U : Op.I32TruncSatF32U);
        else code.Prefixed(d ? Op.I32TruncSatF64S : Op.I32TruncSatF32S);
        Normalize(t);
    }

    /// <summary>Raises runtime error <paramref name="err"/> when the i32 on the stack is non-zero.</summary>
    void ThrowIf(int err)
    {
        var l = code.If();
        code.I32(err);
        code.Call(c.ThrowCodeFunc);
        code.Emit(Op.Unreachable);
        code.End(l);
    }

    /// <summary>InvalidCastException unless the object on the stack (null passes) is a <paramref name="target"/>.</summary>
    void CastCheck(WClass target)
    {
        int tmp = Temp(ValType.I32);
        code.Tee(tmp);
        TypeTest(target, nullIs: true);
        code.Emit(Op.I32Eqz);
        ThrowIf(AirToWasm.ErrCast);
        code.Get(tmp);
    }

    /// <summary>Replaces the object on the stack with whether it is a <paramref name="target"/> (id range test;
    /// interfaces test each implementer).</summary>
    void TypeTest(WClass target, bool nullIs = false)
    {
        int o = Temp(ValType.I32);
        code.Set(o);
        code.Get(o);
        var iff = code.If(ValType.I32);
        code.Get(o);
        code.Mem(Op.I32Load);
        code.I32(0xFFFFFF);
        code.Emit(Op.I32And);
        if (target.IsInterface)
        {
            int id = Temp(ValType.I32);
            code.Set(id);
            var impls = c.classes.Where(k => !k.IsInterface && !k.IsStruct && k.IsSubclassOf(target)).ToList();
            code.I32(0);
            foreach (var k in impls)
            {
                code.Get(id); code.I32(k.Id); code.Emit(Op.I32Eq);
                code.Emit(Op.I32Or);
            }
        }
        else
        {
            code.I32(target.Id);
            code.Emit(Op.I32Sub);
            code.I32(target.IdEnd - target.Id);
            code.Emit(Op.I32LeU);
        }
        code.Else();
        code.I32(nullIs ? 1 : 0);
        code.End(iff);
    }

    WT Cast(MCast cs)
    {
        var to = c.Parse(cs.TypeCs, span);
        if (Literal(cs.E, out var lit, out _) && lit != null && to.IsNumeric && Fits(lit, to)) { Const(to, lit); return to; }
        var from = Raw(cs.E);
        Convert(from, to, explicitCast: true);
        return to;
    }

    WT Is(MIs i)
    {
        string tcs = i.TypeCs.Trim();
        if (tcs == "null" || tcs == "not null")
        {
            var t0 = Raw(i.E);
            if (!t0.IsRef) throw No("MIs:null-of-value", t0.Name);
            code.Emit(Op.I32Eqz);
            if (tcs == "not null") code.Emit(Op.I32Eqz);
            return WT.Bool;
        }
        // `x is var name` / `x is T name`: binds name
        string? bind = null;
        int sp = tcs.LastIndexOf(' ');
        if (sp > 0 && !tcs.EndsWith('>') && !tcs[(sp + 1)..].Contains('.') && !tcs[..sp].EndsWith(','))
        {
            bind = tcs[(sp + 1)..];
            tcs = tcs[..sp];
        }
        var et = Ty(i.E);
        if (tcs == "var")
        {
            var v = Declare(bind!, et);
            StoreVar(v, () => Raw(i.E));
            code.I32(1);
            return WT.Bool;
        }
        var target = c.Parse(tcs, span);
        if (target.K != WK.Class)
        {
            // a value against its own type: always true (pattern bindings of typed members)
            if (!target.Same(et)) throw No("MIs:type", et.Name + " is " + target.Name);
            if (bind != null) { var v = Declare(bind, target); StoreVar(v, () => Raw(i.E)); }
            else { Emit(i.E, null); code.Emit(Op.Drop); }
            code.I32(1);
            return WT.Bool;
        }
        Emit(i.E, null);
        if (bind == null)
        {
            if (et.K == WK.Class && et.Cls!.IsSubclassOf(target.Cls!)) { code.I32(0); code.Emit(Op.I32Ne); return WT.Bool; }
            TypeTest(target.Cls!);
            return WT.Bool;
        }
        int o = Temp(ValType.I32);
        code.Tee(o);
        TypeTest(target.Cls!);
        var bv = Declare(bind, target);
        var iff = code.If(ValType.I32);
        StoreVar(bv, () => { code.Get(o); return target; });
        code.I32(1);
        code.Else();
        code.I32(0);
        code.End(iff);
        return WT.Bool;
    }

    WT As(AirAs a)
    {
        var target = c.Parse(a.TypeCs, span);
        if (target.K != WK.Class) throw No("AirAs:type", target.Name);
        Emit(a.E, null);
        int o = Temp(ValType.I32);
        code.Tee(o);
        TypeTest(target.Cls!);
        var iff = code.If(ValType.I32);
        code.Get(o);
        code.Else();
        code.I32(0);
        code.End(iff);
        return target;
    }

    // ------------------------------------------------------------------ conditionals

    WT CondType(MCond cd)
    {
        var a = Ty(cd.A);
        var b = Ty(cd.B);
        if (a.Same(b)) return a;
        if (a.K == WK.Null) return b;
        if (b.K == WK.Null) return a;
        if (a.IsNumeric && b.IsNumeric) return Promote(a, b, cd.A, cd.B);
        if (a.K == WK.Class && b.K == WK.Class)
        {
            if (a.Cls!.IsSubclassOf(b.Cls!)) return b;
            if (b.Cls!.IsSubclassOf(a.Cls!)) return a;
            for (var k = a.Cls!.Base; k != null; k = k.Base) if (b.Cls!.IsSubclassOf(k)) return k.Type;
        }
        if (cd.Type != null) return c.FromAir(cd.Type, span);
        throw No("MCond:types", a.Name + " / " + b.Name);
    }

    void Cond(MCond cd, WT t)
    {
        CondBool(cd.C);
        var iff = code.If(t.K == WK.Void ? null : t.Val);
        Emit(cd.A, t);
        code.Else();
        Emit(cd.B, t);
        code.End(iff);
    }

    /// <summary>A condition as an i32 0/1.</summary>
    void CondBool(MExpr e)
    {
        var t = Raw(e);
        if (t.K != WK.Bool) throw No("Condition:type", t.Name);
    }

    // ------------------------------------------------------------------ operators

    /// <summary>C# binary numeric promotion; an integer literal adapts to the other operand when it fits.</summary>
    WT Promote(WT l, WT r, MExpr le, MExpr re)
    {
        l = Under(l);
        r = Under(r);
        bool litL = Literal(le, out var vl, out _) && vl is not (double or float);
        bool litR = Literal(re, out var vr, out _) && vr is not (double or float);
        if (l.K == WK.F64 || r.K == WK.F64) return WT.F64;
        if (l.K == WK.F32 || r.K == WK.F32) return WT.F32;
        var wl = l.Widened;
        var wr = r.Widened;
        if (litL && vl != null && !litR && Fits(vl, wr)) return wr.K is WK.I32 or WK.U32 or WK.I64 or WK.U64 ? wr : WT.I32;
        if (litR && vr != null && !litL && Fits(vr, wl)) return wl.K is WK.I32 or WK.U32 or WK.I64 or WK.U64 ? wl : WT.I32;
        if (wl.K == WK.U64 || wr.K == WK.U64) return WT.U64;
        if (wl.K == WK.I64 || wr.K == WK.I64) return WT.I64;
        if (wl.K == WK.U32 && wr.K == WK.U32) return WT.U32;
        if (wl.K == WK.U32 || wr.K == WK.U32)
        {
            var other = wl.K == WK.U32 ? r : l;
            return other.IsUnsigned ? WT.U32 : WT.I64;
        }
        return WT.I32;
    }

    WT Binary(MBinary b)
    {
        switch (b.Op)
        {
            case AirBinOp.And or AirBinOp.Or:
                {
                    CondBool(b.L);
                    var iff = code.If(ValType.I32);
                    if (b.Op == AirBinOp.And) { CondBool(b.R); code.Else(); code.I32(0); }
                    else { code.I32(1); code.Else(); CondBool(b.R); }
                    code.End(iff);
                    return WT.Bool;
                }
            case AirBinOp.Coalesce:
                {
                    var lt = Ty(b.L);
                    if (!lt.IsRef) throw No("Coalesce:value", lt.Name);
                    var rt = Ty(b.R);
                    var t = lt.K == WK.Null ? rt : lt;
                    Emit(b.L, t);
                    int tmp = Temp(ValType.I32);
                    code.Tee(tmp);
                    var iff = code.If(ValType.I32);
                    code.Get(tmp);
                    code.Else();
                    Emit(b.R, t);
                    code.End(iff);
                    return t;
                }
        }
        var l = Ty(b.L);
        var r = Ty(b.R);
        bool chk = b.Checked && Checked;
        // strings
        if (b.Op == AirBinOp.Add && (l.K == WK.Str || r.K == WK.Str))
        {
            StrOperands(b.L, l, b.R, r);
            code.Call(c.Rt.Func("str_concat"));
            return WT.Str;
        }
        if (b.Op is AirBinOp.Eq or AirBinOp.Ne)
        {
            if (l.K is WK.Str or WK.Null && r.K is WK.Str or WK.Null && (l.K == WK.Str || r.K == WK.Str))
            {
                RefOperands(b.L, l, b.R, r);
                code.Call(c.Rt.Func("str_eq"));
                if (b.Op == AirBinOp.Ne) code.Emit(Op.I32Eqz);
                return WT.Bool;
            }
            if (l.IsRef || r.IsRef || l.K == WK.Func || r.K == WK.Func)
            {
                if ((l.K == WK.Class && l.Cls!.IsRecord || r.K == WK.Class && r.Cls!.IsRecord) && l.K != WK.Null && r.K != WK.Null)
                {
                    RefOperands(b.L, l, b.R, r);
                    code.Call(c.RecordEquals(l.K == WK.Class ? l.Cls! : r.Cls!));
                }
                else
                {
                    Emit(b.L, null);
                    Emit(b.R, null);
                    code.Emit(Op.I32Eq);
                }
                if (b.Op == AirBinOp.Ne) code.Emit(Op.I32Eqz);
                return WT.Bool;
            }
            if (l.K == WK.Bool && r.K == WK.Bool)
            {
                Emit(b.L, WT.Bool); Emit(b.R, WT.Bool);
                code.Emit(b.Op == AirBinOp.Eq ? Op.I32Eq : Op.I32Ne);
                return WT.Bool;
            }
            if (l.IsStruct && r.IsStruct && l.Cls == r.Cls)
            {
                Emit(b.L, l);
                Emit(b.R, r);
                code.Call(c.StructEquals(l.Cls!));
                if (b.Op == AirBinOp.Ne) code.Emit(Op.I32Eqz);
                return WT.Bool;
            }
            if (l.K == WK.Enum && r.K == WK.Enum)
            {
                Emit(b.L, l); Emit(b.R, l);
                code.Emit(l.Is64 ? (b.Op == AirBinOp.Eq ? Op.I64Eq : Op.I64Ne) : (b.Op == AirBinOp.Eq ? Op.I32Eq : Op.I32Ne));
                return WT.Bool;
            }
        }
        if (l.K == WK.Bool && r.K == WK.Bool && b.Op is AirBinOp.BitAnd or AirBinOp.BitOr or AirBinOp.BitXor)
        {
            Emit(b.L, WT.Bool); Emit(b.R, WT.Bool);
            code.Emit(b.Op == AirBinOp.BitAnd ? Op.I32And : b.Op == AirBinOp.BitOr ? Op.I32Or : Op.I32Xor);
            return WT.Bool;
        }
        if (!(l.IsNumeric || l.K == WK.Char) || !(r.IsNumeric || r.K == WK.Char)) return OperatorMethod(b, l, r);
        if (b.Op is AirBinOp.Shl or AirBinOp.Shr or AirBinOp.UShr)
        {
            var lt = Under(l).Widened;
            Emit(b.L, lt);
            Emit(b.R, WT.I32);
            if (lt.Is64) code.Emit(Op.I64ExtendI32U);
            bool u = b.Op == AirBinOp.UShr || lt.IsUnsigned;
            code.Emit(lt.Is64 ? (b.Op == AirBinOp.Shl ? Op.I64Shl : u ? Op.I64ShrU : Op.I64ShrS) : (b.Op == AirBinOp.Shl ? Op.I32Shl : u ? Op.I32ShrU : Op.I32ShrS));
            return lt;
        }
        var pt = Promote(l, r, b.L, b.R);
        bool enumOps = l.K == WK.Enum && r.K == WK.Enum && b.Op is AirBinOp.BitAnd or AirBinOp.BitOr or AirBinOp.BitXor;
        switch (b.Op)
        {
            case AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge:
                Emit(b.L, pt);
                Emit(b.R, pt);
                code.Emit(CompareOp(b.Op, pt));
                return WT.Bool;
            case AirBinOp.Div or AirBinOp.Mod when pt.IsInt:
                DivRem(b, pt);
                return pt;
            case AirBinOp.Mod:
                Emit(b.L, pt);
                Emit(b.R, pt);
                code.Call(c.Rt.Func(pt.K == WK.F64 ? "fmod" : "fmodf"));
                return pt;
        }
        Emit(b.L, pt);
        Emit(b.R, pt);
        if (chk && pt.IsInt && b.Op is AirBinOp.Add or AirBinOp.Sub or AirBinOp.Mul) { CheckedArith(b.Op, pt); return pt; }
        code.Emit(ArithOp(b.Op, pt));
        return enumOps ? l : pt;
    }

    /// <summary>Two reference operands; the left one is rooted in a frame temp while a right operand that can
    /// allocate runs.</summary>
    void RefOperands(MExpr le, WT lt, MExpr re, WT rt)
    {
        if (!MayAlloc(re)) { Emit(le, lt.K == WK.Null ? null : lt); Emit(re, rt.K == WK.Null ? null : rt); return; }
        var tmp = FrameTemp(WT.Str);
        code.Get(Fp());
        Emit(le, null);
        code.Mem(Op.I32Store, (uint)tmp.Frame);
        Emit(re, null);
        int r = Temp(ValType.I32);
        code.Set(r);
        code.Get(Fp());
        code.Mem(Op.I32Load, (uint)tmp.Frame);
        code.Get(r);
    }

    /// <summary>Both operands of a string `+`, each converted to a string (C# formatting), the left rooted.</summary>
    void StrOperands(MExpr le, WT lt, MExpr re, WT rt)
    {
        var tmp = FrameTemp(WT.Str);
        code.Get(Fp());
        Emit(le, lt);
        ToStr(lt, csharp: true);
        code.Mem(Op.I32Store, (uint)tmp.Frame);
        Emit(re, rt);
        ToStr(rt, csharp: true);
        int r = Temp(ValType.I32);
        code.Set(r);
        code.Get(Fp());
        code.Mem(Op.I32Load, (uint)tmp.Frame);
        code.Get(r);
    }

    /// <summary>Whether evaluating <paramref name="e"/> may allocate (and so collect): calls, construction, strings.</summary>
    internal static bool MayAlloc(MExpr e) => e switch
    {
        AirConst or AirNull or AirLocalRef or AirParamRef or AirThis or AirFieldRef => false,
        MCall { Name: "unchecked" or "checked", Target: null, Args.Count: 1 } w => MayAlloc(w.Args[0]),
        MCode => false,
        MBinary b => b.Op == AirBinOp.Add && (b.L.Type is AirType.Prim { Kind: PrimKind.String } || b.R.Type is AirType.Prim { Kind: PrimKind.String } || b.Type is AirType.Prim { Kind: PrimKind.String } || b.Type == null) || MayAlloc(b.L) || MayAlloc(b.R),
        MUnary u => MayAlloc(u.E),
        MMember m => MayAlloc(m.Target),
        MIndex ix => MayAlloc(ix.Target) || ix.Args.Any(MayAlloc),
        MCast cs => MayAlloc(cs.E),
        MCond cd => MayAlloc(cd.C) || MayAlloc(cd.A) || MayAlloc(cd.B),
        _ => true,
    };

    static byte CompareOp(AirBinOp op, WT t)
    {
        bool u = t.IsUnsigned;
        return t.Val switch
        {
            ValType.I64 => op switch { AirBinOp.Eq => Op.I64Eq, AirBinOp.Ne => Op.I64Ne, AirBinOp.Lt => u ? Op.I64LtU : Op.I64LtS, AirBinOp.Le => u ? Op.I64LeU : Op.I64LeS, AirBinOp.Gt => u ? Op.I64GtU : Op.I64GtS, _ => u ? Op.I64GeU : Op.I64GeS },
            ValType.F32 => op switch { AirBinOp.Eq => Op.F32Eq, AirBinOp.Ne => Op.F32Ne, AirBinOp.Lt => Op.F32Lt, AirBinOp.Le => Op.F32Le, AirBinOp.Gt => Op.F32Gt, _ => Op.F32Ge },
            ValType.F64 => op switch { AirBinOp.Eq => Op.F64Eq, AirBinOp.Ne => Op.F64Ne, AirBinOp.Lt => Op.F64Lt, AirBinOp.Le => Op.F64Le, AirBinOp.Gt => Op.F64Gt, _ => Op.F64Ge },
            _ => op switch { AirBinOp.Eq => Op.I32Eq, AirBinOp.Ne => Op.I32Ne, AirBinOp.Lt => u ? Op.I32LtU : Op.I32LtS, AirBinOp.Le => u ? Op.I32LeU : Op.I32LeS, AirBinOp.Gt => u ? Op.I32GtU : Op.I32GtS, _ => u ? Op.I32GeU : Op.I32GeS },
        };
    }

    byte ArithOp(AirBinOp op, WT t) => t.Val switch
    {
        ValType.I64 => op switch { AirBinOp.Add => Op.I64Add, AirBinOp.Sub => Op.I64Sub, AirBinOp.Mul => Op.I64Mul, AirBinOp.BitAnd => Op.I64And, AirBinOp.BitOr => Op.I64Or, AirBinOp.BitXor => Op.I64Xor, _ => throw No("Binary:" + op, t.Name) },
        ValType.F32 => op switch { AirBinOp.Add => Op.F32Add, AirBinOp.Sub => Op.F32Sub, AirBinOp.Mul => Op.F32Mul, AirBinOp.Div => Op.F32Div, _ => throw No("Binary:" + op, t.Name) },
        ValType.F64 => op switch { AirBinOp.Add => Op.F64Add, AirBinOp.Sub => Op.F64Sub, AirBinOp.Mul => Op.F64Mul, AirBinOp.Div => Op.F64Div, _ => throw No("Binary:" + op, t.Name) },
        _ => op switch { AirBinOp.Add => Op.I32Add, AirBinOp.Sub => Op.I32Sub, AirBinOp.Mul => Op.I32Mul, AirBinOp.BitAnd => Op.I32And, AirBinOp.BitOr => Op.I32Or, AirBinOp.BitXor => Op.I32Xor, _ => throw No("Binary:" + op, t.Name) },
    };

    /// <summary>Checked + - * (OverflowException): the two operands are on the stack.</summary>
    void CheckedArith(AirBinOp op, WT t)
    {
        bool w = t.Is64;
        var vt = w ? ValType.I64 : ValType.I32;
        int b = Temp(vt), a = Temp(vt), r = Temp(vt);
        code.Set(b);
        code.Set(a);
        if (op == AirBinOp.Mul)
        {
            if (w) { code.Get(a); code.Get(b); code.Call(c.Rt.Func(t.IsUnsigned ? "mul_ovf_u64" : "mul_ovf_i64")); return; }
            int p = Temp(ValType.I64);
            code.Get(a); code.Emit(t.IsUnsigned ? Op.I64ExtendI32U : Op.I64ExtendI32S);
            code.Get(b); code.Emit(t.IsUnsigned ? Op.I64ExtendI32U : Op.I64ExtendI32S);
            code.Emit(Op.I64Mul);
            code.Tee(p);
            if (t.IsUnsigned) { code.I64(32); code.Emit(Op.I64ShrU); code.I64(0); code.Emit(Op.I64Ne); }
            else { code.Get(p); code.Emit(Op.I32WrapI64); code.Emit(Op.I64ExtendI32S); code.Emit(Op.I64Ne); }
            ThrowIf(AirToWasm.ErrOverflow);
            code.Get(p);
            code.Emit(Op.I32WrapI64);
            return;
        }
        code.Get(a); code.Get(b);
        code.Emit(op == AirBinOp.Add ? (w ? Op.I64Add : Op.I32Add) : (w ? Op.I64Sub : Op.I32Sub));
        code.Set(r);
        if (t.IsUnsigned)
        {
            // add: r < a; sub: a < b
            if (op == AirBinOp.Add) { code.Get(r); code.Get(a); } else { code.Get(a); code.Get(b); }
            code.Emit(w ? Op.I64LtU : Op.I32LtU);
        }
        else
        {
            // add: ((a ^ r) & (b ^ r)) < 0; sub: ((a ^ b) & (a ^ r)) < 0
            if (op == AirBinOp.Add) { code.Get(a); code.Get(r); code.Emit(w ? Op.I64Xor : Op.I32Xor); code.Get(b); code.Get(r); code.Emit(w ? Op.I64Xor : Op.I32Xor); }
            else { code.Get(a); code.Get(b); code.Emit(w ? Op.I64Xor : Op.I32Xor); code.Get(a); code.Get(r); code.Emit(w ? Op.I64Xor : Op.I32Xor); }
            code.Emit(w ? Op.I64And : Op.I32And);
            if (w) { code.I64(0); code.Emit(Op.I64LtS); } else { code.I32(0); code.Emit(Op.I32LtS); }
        }
        ThrowIf(AirToWasm.ErrOverflow);
        code.Get(r);
    }

    /// <summary>Integer / and %: DivideByZeroException on a zero divisor, OverflowException on MinValue / -1 (also
    /// for %, as .NET on x64). A constant divisor other than 0 and -1 needs no checks.</summary>
    void DivRem(MBinary b, WT t)
    {
        bool w = t.Is64, u = t.IsUnsigned, div = b.Op == AirBinOp.Div;
        var vt = w ? ValType.I64 : ValType.I32;
        bool constDivisor = Literal(b.R, out var lv, out _) && lv != null && (lv is char ? (decimal)(char)lv : System.Convert.ToDecimal(lv, CultureInfo.InvariantCulture)) is var dv && dv != 0 && dv != -1;
        Emit(b.L, t);
        Emit(b.R, t);
        if (!constDivisor)
        {
            int y = Temp(vt), x = Temp(vt);
            code.Set(y);
            code.Set(x);
            code.Get(y);
            code.Emit(w ? Op.I64Eqz : Op.I32Eqz);
            ThrowIf(AirToWasm.ErrDivZero);
            if (!u)
            {
                code.Get(y);
                if (w) { code.I64(-1); code.Emit(Op.I64Eq); code.Get(x); code.I64(long.MinValue); code.Emit(Op.I64Eq); }
                else { code.I32(-1); code.Emit(Op.I32Eq); code.Get(x); code.I32(int.MinValue); code.Emit(Op.I32Eq); }
                code.Emit(Op.I32And);
                ThrowIf(AirToWasm.ErrOverflow);
            }
            code.Get(x);
            code.Get(y);
        }
        code.Emit(w ? (div ? (u ? Op.I64DivU : Op.I64DivS) : (u ? Op.I64RemU : Op.I64RemS)) : (div ? (u ? Op.I32DivU : Op.I32DivS) : (u ? Op.I32RemU : Op.I32RemS)));
    }

    WT Unary(MUnary u)
    {
        switch (u.Op)
        {
            case AirUnOp.Not:
                CondBool(u.E);
                code.Emit(Op.I32Eqz);
                return WT.Bool;
            case AirUnOp.NotNull:
                return Raw(u.E);
            case AirUnOp.Neg:
                {
                    if (Literal(u, out var v, out var lt) && v != null) { Const(lt, v); return lt; }
                    var t = Under(Ty(u.E));
                    if (!t.IsNumeric) throw No("Neg", t.Name);
                    var pt = t.IsFloat ? t : t.K == WK.U32 ? WT.I64 : t.Widened;
                    if (pt.IsFloat) { Emit(u.E, pt); code.Emit(pt.K == WK.F64 ? Op.F64Neg : Op.F32Neg); return pt; }
                    if (pt.Is64) code.I64(0); else code.I32(0);
                    Emit(u.E, pt);
                    if (u.Checked && Checked) CheckedArith(AirBinOp.Sub, pt);
                    else code.Emit(pt.Is64 ? Op.I64Sub : Op.I32Sub);
                    return pt;
                }
            case AirUnOp.BitNot:
                {
                    var t = Under(Ty(u.E)).Widened;
                    Emit(u.E, t);
                    if (t.Is64) { code.I64(-1); code.Emit(Op.I64Xor); } else { code.I32(-1); code.Emit(Op.I32Xor); }
                    return t;
                }
            case AirUnOp.Inc or AirUnOp.Dec:
                return IncDec(u);
            case AirUnOp.Await: throw No("Await (async; runtime = \"dotnet\")");
            default: throw No("Unary:" + u.Op);
        }
    }

    /// <summary>`++x`, `x++`, `--x`, `x--` on a local, field or element: the value before (postfix) or after.</summary>
    WT IncDec(MUnary u)
    {
        var t = Ty(u.E);
        if (!t.IsNumeric) throw No("IncDec", t.Name);
        int before = Temp(t.Val), after = Temp(t.Val);
        var op = u.Op == AirUnOp.Inc ? AirBinOp.Add : AirBinOp.Sub;
        Store(u.E, () =>
        {
            Emit(u.E, t);
            code.Tee(before);
            var wt = t.IsFloat || t.Is64 ? t : t.Widened;
            if (wt.Is64) code.I64(1); else if (wt.K == WK.F64) code.F64(1); else if (wt.K == WK.F32) code.F32(1); else code.I32(1);
            if (u.Checked && Checked && wt.IsInt) CheckedArith(op, wt);
            else code.Emit(ArithOp(op, wt));
            if (!wt.Same(t)) Convert(wt, t, explicitCast: true, chk: u.Checked && Checked);
            code.Tee(after);
            return t;
        });
        code.Get(u.Postfix ? before : after);
        return t;
    }
}
