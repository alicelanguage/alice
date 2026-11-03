using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Alice.Runtime;

/// <summary>Numeric conversions of boxed values (C# implicit/explicit numeric conversions, checked: ADR 0013).</summary>
internal static class Numbers
{
    public static bool IsNumeric(TypeCode c) => c is >= TypeCode.Char and <= TypeCode.Decimal;

    // C# implicit numeric conversions, nearest first (the rank breaks ties between overloads: Int32 -> Int64 beats -> Float64)
    static readonly TypeCode[][] Implicit = BuildImplicit();

    static TypeCode[][] BuildImplicit()
    {
        var t = new TypeCode[19][];
        for (int i = 0; i < t.Length; i++) t[i] = [];
        t[(int)TypeCode.SByte] = [TypeCode.Int16, TypeCode.Int32, TypeCode.Int64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.Byte] = [TypeCode.Int16, TypeCode.UInt16, TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.Int16] = [TypeCode.Int32, TypeCode.Int64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.UInt16] = [TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.Int32] = [TypeCode.Int64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.UInt32] = [TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.Int64] = [TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.UInt64] = [TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.Char] = [TypeCode.UInt16, TypeCode.Int32, TypeCode.UInt32, TypeCode.Int64, TypeCode.UInt64, TypeCode.Single, TypeCode.Double, TypeCode.Decimal];
        t[(int)TypeCode.Single] = [TypeCode.Double];
        return t;
    }

    /// <summary>The rank of the implicit numeric conversion from <paramref name="from"/> to <paramref name="to"/>, -1 when there is none.</summary>
    public static int WideningRank(Type from, Type to)
    {
        if (from.IsEnum || to.IsEnum) return -1;
        var f = Type.GetTypeCode(from);
        var t = Type.GetTypeCode(to);
        if (!IsNumeric(f) || !IsNumeric(t)) return -1;
        return Array.IndexOf(Implicit[(int)f], t);
    }

    /// <summary>A boxed number as <paramref name="to"/> when the conversion exists (implicit only unless <paramref name="explicitOk"/>).</summary>
    public static bool TryConvert(object v, Type to, bool explicitOk, out object? r)
    {
        r = null;
        if (v.GetType().IsEnum || to.IsEnum) return false;
        var f = Type.GetTypeCode(v.GetType());
        var t = Type.GetTypeCode(to);
        if (!IsNumeric(f) || !IsNumeric(t)) return false;
        if (f == t) { r = v; return true; }
        if (!explicitOk && Array.IndexOf(Implicit[(int)f], t) < 0) return false;
        r = f switch
        {
            TypeCode.Single or TypeCode.Double => FromDouble(t, System.Convert.ToDouble(v, CultureInfo.InvariantCulture)),
            TypeCode.Decimal => FromDecimal(t, (decimal)v),
            TypeCode.UInt64 => FromULong(t, (ulong)v),
            TypeCode.Char => FromLong(t, (char)v),
            _ => FromLong(t, System.Convert.ToInt64(v, CultureInfo.InvariantCulture)),
        };
        return true;
    }

    /// <summary>A value into a parameter or field of type <paramref name="to"/>: unchanged unless it is a number that widens.</summary>
    public static object? Coerce(object? v, Type to)
    {
        if (v == null) return null;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        if (target.IsInstanceOfType(v)) return v;
        return TryConvert(v, target, false, out var r) ? r : v;
    }

    static object FromLong(TypeCode t, long l) => checked(t switch
    {
        TypeCode.SByte => (object)(sbyte)l,
        TypeCode.Byte => (byte)l,
        TypeCode.Int16 => (short)l,
        TypeCode.UInt16 => (ushort)l,
        TypeCode.Int32 => (int)l,
        TypeCode.UInt32 => (uint)l,
        TypeCode.Int64 => l,
        TypeCode.UInt64 => (ulong)l,
        TypeCode.Char => (char)l,
        TypeCode.Single => (float)l,
        TypeCode.Double => (double)l,
        _ => (decimal)l,
    });

    static object FromULong(TypeCode t, ulong l) => checked(t switch
    {
        TypeCode.SByte => (object)(sbyte)l,
        TypeCode.Byte => (byte)l,
        TypeCode.Int16 => (short)l,
        TypeCode.UInt16 => (ushort)l,
        TypeCode.Int32 => (int)l,
        TypeCode.UInt32 => (uint)l,
        TypeCode.Int64 => (long)l,
        TypeCode.UInt64 => l,
        TypeCode.Char => (char)l,
        TypeCode.Single => (float)l,
        TypeCode.Double => (double)l,
        _ => (decimal)l,
    });

    static object FromDouble(TypeCode t, double d) => checked(t switch
    {
        TypeCode.SByte => (object)(sbyte)d,
        TypeCode.Byte => (byte)d,
        TypeCode.Int16 => (short)d,
        TypeCode.UInt16 => (ushort)d,
        TypeCode.Int32 => (int)d,
        TypeCode.UInt32 => (uint)d,
        TypeCode.Int64 => (long)d,
        TypeCode.UInt64 => (ulong)d,
        TypeCode.Char => (char)d,
        TypeCode.Single => (float)d,
        TypeCode.Double => d,
        _ => (decimal)d,
    });

    static object FromDecimal(TypeCode t, decimal m) => t switch
    {
        TypeCode.SByte => (object)(sbyte)m,
        TypeCode.Byte => (byte)m,
        TypeCode.Int16 => (short)m,
        TypeCode.UInt16 => (ushort)m,
        TypeCode.Int32 => (int)m,
        TypeCode.UInt32 => (uint)m,
        TypeCode.Int64 => (long)m,
        TypeCode.UInt64 => (ulong)m,
        TypeCode.Char => (char)m,
        TypeCode.Single => (float)m,
        TypeCode.Double => (double)m,
        _ => m,
    };
}

/// <summary>Operators on numbers, Bool and String without reflection (Alice semantics: checked arithmetic, floored
/// `/` and `%` on Int32/Int64 and floored `%` on floats, as statically typed code), and the
/// `op_*` method names of user-defined operators.</summary>
internal static class Operators
{
    public static string Spelling(DynOp op) => op switch
    {
        DynOp.Add or DynOp.Plus => "+",
        DynOp.Sub or DynOp.Neg => "-",
        DynOp.Mul => "*",
        DynOp.Div => "/",
        DynOp.Mod => "%",
        DynOp.Shl => "<<",
        DynOp.Shr => ">>",
        DynOp.UShr => ">>>",
        DynOp.BitAnd => "&",
        DynOp.BitOr => "|",
        DynOp.BitXor => "^",
        DynOp.Eq => "==",
        DynOp.Ne => "!=",
        DynOp.Lt => "<",
        DynOp.Le => "<=",
        DynOp.Gt => ">",
        DynOp.Ge => ">=",
        DynOp.Not => "!",
        DynOp.BitNot => "~",
        _ => op.ToString(),
    };

    /// <summary>The checked operator first where one exists (ADR 0013: arithmetic is checked).</summary>
    public static string[] MethodNames(DynOp op) => op switch
    {
        DynOp.Add => ["op_CheckedAddition", "op_Addition"],
        DynOp.Sub => ["op_CheckedSubtraction", "op_Subtraction"],
        DynOp.Mul => ["op_CheckedMultiply", "op_Multiply"],
        DynOp.Div => ["op_CheckedDivision", "op_Division"],
        DynOp.Mod => ["op_Modulus"],
        DynOp.Shl => ["op_LeftShift"],
        DynOp.Shr => ["op_RightShift"],
        DynOp.UShr => ["op_UnsignedRightShift"],
        DynOp.BitAnd => ["op_BitwiseAnd"],
        DynOp.BitOr => ["op_BitwiseOr"],
        DynOp.BitXor => ["op_ExclusiveOr"],
        DynOp.Eq => ["op_Equality"],
        DynOp.Ne => ["op_Inequality"],
        DynOp.Lt => ["op_LessThan"],
        DynOp.Le => ["op_LessThanOrEqual"],
        DynOp.Gt => ["op_GreaterThan"],
        DynOp.Ge => ["op_GreaterThanOrEqual"],
        DynOp.Neg => ["op_CheckedUnaryNegation", "op_UnaryNegation"],
        DynOp.Plus => ["op_UnaryPlus"],
        DynOp.Not => ["op_LogicalNot"],
        DynOp.BitNot => ["op_OnesComplement"],
        _ => [],
    };

    /// <summary>C# binary numeric promotion; Empty when C# rejects the pair (Decimal with a float, UInt64 with a signed type).</summary>
    static TypeCode Promote(TypeCode a, TypeCode b)
    {
        static bool Signed(TypeCode c) => c is TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64;
        if (a == TypeCode.Decimal || b == TypeCode.Decimal)
            return a is TypeCode.Double or TypeCode.Single || b is TypeCode.Double or TypeCode.Single ? TypeCode.Empty : TypeCode.Decimal;
        if (a == TypeCode.Double || b == TypeCode.Double) return TypeCode.Double;
        if (a == TypeCode.Single || b == TypeCode.Single) return TypeCode.Single;
        if (a == TypeCode.UInt64 || b == TypeCode.UInt64) return Signed(a) || Signed(b) ? TypeCode.Empty : TypeCode.UInt64;
        if (a == TypeCode.Int64 || b == TypeCode.Int64) return TypeCode.Int64;
        if (a == TypeCode.UInt32 || b == TypeCode.UInt32) return Signed(a) || Signed(b) ? TypeCode.Int64 : TypeCode.UInt32;
        return TypeCode.Int32;
    }

    /// <summary>C# `dynamic` on two values of one enum type: `&amp; | ^` give the enum, `== != &lt; &lt;= &gt; &gt;=` compare the
    /// underlying values; anything else falls through to operator lookup (and raises there).</summary>
    static bool EnumOp(DynOp op, Type t, object l, object r, out object? result)
    {
        result = null;
        var code = Type.GetTypeCode(Enum.GetUnderlyingType(t));
        bool signed = code is TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64;
        ulong a = EnumBits(l, code), b = EnumBits(r, code);
        int cmp = signed ? ((long)a).CompareTo((long)b) : a.CompareTo(b);
        result = op switch
        {
            DynOp.BitAnd => Enum.ToObject(t, a & b),
            DynOp.BitOr => Enum.ToObject(t, a | b),
            DynOp.BitXor => Enum.ToObject(t, a ^ b),
            DynOp.Eq => a == b,
            DynOp.Ne => a != b,
            DynOp.Lt => cmp < 0,
            DynOp.Le => cmp <= 0,
            DynOp.Gt => cmp > 0,
            DynOp.Ge => cmp >= 0,
            _ => null,
        };
        return result != null;
    }

    /// <summary>The underlying value of a boxed enum, sign-extended to 64 bits (a boxed enum unboxes as its underlying type).</summary>
    static ulong EnumBits(object e, TypeCode code) => code switch
    {
        TypeCode.SByte => (ulong)(sbyte)e,
        TypeCode.Byte => (byte)e,
        TypeCode.Int16 => (ulong)(short)e,
        TypeCode.UInt16 => (ushort)e,
        TypeCode.Int32 => (ulong)(int)e,
        TypeCode.UInt32 => (uint)e,
        TypeCode.Int64 => (ulong)(long)e,
        TypeCode.UInt64 => (ulong)e,
        TypeCode.Char => (char)e,
        _ => (ulong)(byte)((bool)e ? 1 : 0),
    };

    static TypeCode Unary(TypeCode c) => c is TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal ? c : TypeCode.Int32;

    static T As<T>(object v) where T : INumberBase<T> => v switch
    {
        char c => T.CreateChecked((int)c),
        _ => (T)(Numbers.TryConvert(v, typeof(T), false, out var r) ? r! : v),
    };

    /// <summary>Evaluates <paramref name="op"/> when both operands are numbers, Bools or Strings; false otherwise.</summary>
    public static bool Primitive(DynOp op, object? l, object? r, out object? result)
    {
        result = null;
        if (l == null || r == null)
        {
            if (op == DynOp.Add && (l is string || r is string)) { result = string.Concat(l, r); return true; }
            return false;
        }
        var lt = l.GetType();
        var rt = r.GetType();
        if (lt.IsEnum || rt.IsEnum) return lt == rt && EnumOp(op, lt, l, r, out result);
        var lc = Type.GetTypeCode(lt);
        var rc = Type.GetTypeCode(rt);
        if (lc == TypeCode.String || rc == TypeCode.String)
        {
            if (op == DynOp.Add) { result = string.Concat(l, r); return true; }
            if (lc == rc && op is DynOp.Eq or DynOp.Ne) { result = string.Equals((string)l, (string)r) == (op == DynOp.Eq); return true; }
            return false;
        }
        if (lc == TypeCode.Boolean && rc == TypeCode.Boolean)
        {
            bool a = (bool)l, b = (bool)r;
            switch (op)
            {
                case DynOp.BitAnd: result = a & b; return true;
                case DynOp.BitOr: result = a | b; return true;
                case DynOp.BitXor: result = a ^ b; return true;
                case DynOp.Eq: result = a == b; return true;
                case DynOp.Ne: result = a != b; return true;
                default: return false;
            }
        }
        if (!Numbers.IsNumeric(lc) || !Numbers.IsNumeric(rc)) return false;
        if (op is DynOp.Shl or DynOp.Shr or DynOp.UShr)
        {
            if (rc is not (TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Char or TypeCode.Int32)) return false;
            int n = As<int>(r);
            return Unary(lc) switch
            {
                TypeCode.Int32 => Shift(op, As<int>(l), n, out result),
                TypeCode.UInt32 => Shift(op, (uint)l, n, out result),
                TypeCode.Int64 => Shift(op, (long)l, n, out result),
                TypeCode.UInt64 => Shift(op, (ulong)l, n, out result),
                _ => false,
            };
        }
        return Promote(lc, rc) switch
        {
            TypeCode.Int32 => Int(op, As<int>(l), As<int>(r), out result),
            TypeCode.UInt32 => Int(op, As<uint>(l), As<uint>(r), out result),
            TypeCode.Int64 => Int(op, As<long>(l), As<long>(r), out result),
            TypeCode.UInt64 => Int(op, As<ulong>(l), As<ulong>(r), out result),
            TypeCode.Single => Arith(op, As<float>(l), As<float>(r), out result),
            TypeCode.Double => Arith(op, As<double>(l), As<double>(r), out result),
            TypeCode.Decimal => Arith(op, As<decimal>(l), As<decimal>(r), out result),
            _ => false,
        };
    }

    static bool Arith<T>(DynOp op, T x, T y, out object? r) where T : INumber<T>
    {
        r = op switch
        {
            DynOp.Add => checked(x + y),
            DynOp.Sub => checked(x - y),
            DynOp.Mul => checked(x * y),
            DynOp.Div => FlooredDiv<T>() ? FloorDiv(x, y) : checked(x / y),
            DynOp.Mod => FlooredMod<T>() ? FloorMod(x, y) : x % y,
            DynOp.Eq => x == y,
            DynOp.Ne => x != y,
            DynOp.Lt => x < y,
            DynOp.Le => x <= y,
            DynOp.Gt => x > y,
            DynOp.Ge => x >= y,
            _ => null,
        };
        return r != null;
    }

    // the types the lowering gives floored helpers (Lowerer.FlooredHelper): floor_div/floor_mod, floor_fmod
    static bool FlooredDiv<T>() => typeof(T) == typeof(int) || typeof(T) == typeof(long);

    static bool FlooredMod<T>() => FlooredDiv<T>() || typeof(T) == typeof(double) || typeof(T) == typeof(float);

    /// <summary>-7 / 2 == -4 (Prelude.floor_div).</summary>
    static T FloorDiv<T>(T x, T y) where T : INumber<T>
    {
        T q = checked(x / y);
        if (x % y != T.Zero && T.IsNegative(x) != T.IsNegative(y)) q -= T.One;
        return q;
    }

    /// <summary>-7 % 3 == 2, the sign of the divisor (Prelude.floor_mod / floor_fmod).</summary>
    static T FloorMod<T>(T x, T y) where T : INumber<T>
    {
        T r = x % y;
        if (r != T.Zero && T.IsNegative(r) != T.IsNegative(y)) r += y;
        return r;
    }

    static bool Int<T>(DynOp op, T x, T y, out object? r) where T : IBinaryInteger<T>
    {
        switch (op)
        {
            case DynOp.BitAnd: r = x & y; return true;
            case DynOp.BitOr: r = x | y; return true;
            case DynOp.BitXor: r = x ^ y; return true;
            default: return Arith(op, x, y, out r);
        }
    }

    static bool Shift<T>(DynOp op, T x, int n, out object? r) where T : IBinaryInteger<T>
    {
        r = op switch
        {
            DynOp.Shl => x << n,
            DynOp.Shr => x >> n,
            _ => x >>> n,
        };
        return true;
    }

    /// <summary>Unary operators on numbers and Bool; false otherwise.</summary>
    public static bool PrimitiveUnary(DynOp op, object? v, out object? result)
    {
        result = null;
        if (v == null || v.GetType().IsEnum) return false;
        var c = Type.GetTypeCode(v.GetType());
        if (c == TypeCode.Boolean) { if (op != DynOp.Not) return false; result = !(bool)v; return true; }
        if (!Numbers.IsNumeric(c)) return false;
        var u = Unary(c);
        if (op == DynOp.Neg && u == TypeCode.UInt32) u = TypeCode.Int64;   // C#: -uint is long
        result = (op, u) switch
        {
            (DynOp.Neg, TypeCode.Int32) => checked(-As<int>(v)),
            (DynOp.Neg, TypeCode.Int64) => checked(-As<long>(v)),
            (DynOp.Neg, TypeCode.Single) => -(float)v,
            (DynOp.Neg, TypeCode.Double) => -(double)v,
            (DynOp.Neg, TypeCode.Decimal) => -(decimal)v,
            (DynOp.Plus, TypeCode.Int32) => As<int>(v),
            (DynOp.Plus, _) => v,
            (DynOp.BitNot, TypeCode.Int32) => ~As<int>(v),
            (DynOp.BitNot, TypeCode.UInt32) => ~(uint)v,
            (DynOp.BitNot, TypeCode.Int64) => ~(long)v,
            (DynOp.BitNot, TypeCode.UInt64) => ~(ulong)v,
            _ => null,
        };
        return result != null;
    }
}

/// <summary>
/// Overload resolution by runtime argument types. Cost per argument: exact 0, nullable lift 1, base class 2, nil to a
/// reference 5, implicit numeric widening 10 + rank, interface 20, Object 30; optional parameters filled and a
/// `params` expansion cost extra. The cheapest applicable method wins; equal costs go to the more specific signature.
/// </summary>
internal static class Overloads
{
    public static DynMatch? Pick(List<MethodInfo> cands, ReadOnlySpan<object?> args, string name, Type recv)
    {
        DynMatch? best = null;
        int bestCost = int.MaxValue;
        MethodInfo? tie = null;
        foreach (var def in cands)
        {
            var m = def;
            if (m.IsGenericMethodDefinition)
            {
                if (Infer(m, args) is not { } closed) continue;
                m = closed;
            }
            if (Applicable(m, args) is not { } a) continue;
            if (a.Cost < bestCost) { best = a.DynMatch; bestCost = a.Cost; tie = null; }
            else if (a.Cost == bestCost && best is { } b)
            {
                // C#: a non-generic method beats a generic one with the same parameter types
                if (MoreSpecific(m, b.Method) || !MoreSpecific(b.Method, m) && b.Method.IsGenericMethod && !m.IsGenericMethod) { best = a.DynMatch; tie = null; }
                else if (!MoreSpecific(b.Method, m) && !(m.IsGenericMethod && !b.Method.IsGenericMethod)) tie = m;
            }
        }
        if (tie != null)
            throw new AmbiguousMatchException($"ambiguous call `{name}` on {Dyn.Alice(recv)} with ({Dyn.ArgList(args)}): {Dyn.Signature(best!.Value.Method)} and {Dyn.Signature(tie)} both match");
        return best;
    }

    /// <summary>C# `dynamic` type inference from the runtime argument types: a type parameter used as a parameter
    /// type, an array element or a type argument of a generic parameter type (matched against the argument's class,
    /// base classes and interfaces) binds to what the argument has. Null when a type parameter stays unbound,
    /// two arguments disagree, or a constraint fails.</summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Dynamic dispatch closes generic methods at run time by design.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Dynamic dispatch reflects over runtime types by design.")]
    static MethodInfo? Infer(MethodInfo def, ReadOnlySpan<object?> args)
    {
        var tps = def.GetGenericArguments();
        var bound = new Type?[tps.Length];
        var ps = def.GetParameters();
        for (int i = 0; i < args.Length && i < ps.Length; i++)
            if (args[i] is { } a && !Unify(ps[i].ParameterType, a.GetType(), bound)) return null;
        foreach (var t in bound) if (t == null) return null;
        try { return def.MakeGenericMethod(bound!); }
        catch (ArgumentException) { return null; }   // a constraint does not hold
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Dynamic dispatch reflects over runtime types by design.")]
    static bool Unify(Type p, Type a, Type?[] bound)
    {
        if (p.IsGenericMethodParameter)
        {
            ref var b = ref bound[p.GenericParameterPosition];
            if (b == null || b == a) { b = a; return true; }
            if (b.IsAssignableFrom(a)) return true;
            if (a.IsAssignableFrom(b)) { b = a; return true; }
            return false;
        }
        if (!p.ContainsGenericParameters) return true;   // checked by Applicable
        if (p.IsArray) return a.IsArray && a.GetArrayRank() == p.GetArrayRank() && Unify(p.GetElementType()!, a.GetElementType()!, bound);
        if (!p.IsGenericType) return true;
        var gd = p.GetGenericTypeDefinition();
        for (var t = a; t != null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == gd) return UnifyArgs(p, t, bound);
        foreach (var i in a.GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == gd) return UnifyArgs(p, i, bound);
        return false;
    }

    static bool UnifyArgs(Type p, Type a, Type?[] bound)
    {
        var pa = p.GetGenericArguments();
        var aa = a.GetGenericArguments();
        for (int k = 0; k < pa.Length; k++) if (!Unify(pa[k], aa[k], bound)) return false;
        return true;
    }

    static (DynMatch DynMatch, int Cost)? Applicable(MethodInfo m, ReadOnlySpan<object?> args)
    {
        var ps = m.GetParameters();
        foreach (var p in ps) if (p.ParameterType.IsByRef || p.ParameterType.IsPointer) return null;
        if (args.Length == ps.Length && Fit(ps, args, ps.Length, null) is { } normal)
            return (new DynMatch(m, CallForm.Normal, normal.Conv), normal.Cost);
        if (args.Length < ps.Length && ps[args.Length].IsOptional && Fit(ps, args, args.Length, null) is { } opt)
        {
            for (int i = args.Length; i < ps.Length; i++) if (!ps[i].IsOptional) return null;
            return (new DynMatch(m, CallForm.Optional, opt.Conv), opt.Cost + 100);
        }
        if (ps.Length > 0 && args.Length >= ps.Length - 1 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false)
            && Fit(ps, args, ps.Length - 1, ps[^1].ParameterType.GetElementType()) is { } exp)
            return (new DynMatch(m, CallForm.Expanded, exp.Conv), exp.Cost + 200);
        return null;
    }

    /// <summary>Fits the first <paramref name="fixedCount"/> arguments to their parameters and the rest to <paramref name="rest"/>.</summary>
    static (Type?[]? Conv, int Cost)? Fit(ParameterInfo[] ps, ReadOnlySpan<object?> args, int fixedCount, Type? rest)
    {
        Type?[]? conv = null;
        int cost = 0;
        for (int i = 0; i < args.Length; i++)
        {
            Type p = i < fixedCount ? ps[i].ParameterType : rest ?? (i < ps.Length ? ps[i].ParameterType : null)!;
            if (p == null) return null;
            int c = Cost(args[i], p, out var to);
            if (c < 0) return null;
            cost += c;
            if (to != null) (conv ??= new Type?[args.Length])[i] = to;
        }
        return (conv, cost);
    }

    static int Cost(object? a, Type p, out Type? conv)
    {
        conv = null;
        var u = Nullable.GetUnderlyingType(p);
        if (a == null) return !p.IsValueType || u != null ? (p == typeof(object) ? 30 : 5) : -1;
        var at = a.GetType();
        if (at == p) return 0;
        if (u == at) return 1;
        if (p == typeof(object)) return 30;
        if (p.IsAssignableFrom(at)) return p.IsInterface ? 20 : 2;
        int rank = Numbers.WideningRank(at, u ?? p);
        if (rank >= 0) { conv = u ?? p; return 10 + rank; }
        return -1;
    }

    /// <summary>Every parameter of <paramref name="a"/> converts to the one of <paramref name="b"/>, and they differ.</summary>
    static bool MoreSpecific(MethodInfo a, MethodInfo b)
    {
        var pa = a.GetParameters();
        var pb = b.GetParameters();
        if (pa.Length != pb.Length) return pa.Length > pb.Length;   // fewer defaults/expansion
        bool differ = false;
        for (int i = 0; i < pa.Length; i++)
        {
            Type x = pa[i].ParameterType, y = pb[i].ParameterType;
            if (x == y) continue;
            differ = true;
            if (!y.IsAssignableFrom(x) && Numbers.WideningRank(x, Nullable.GetUnderlyingType(y) ?? y) < 0) return false;
        }
        return differ;
    }
}
