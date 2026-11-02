using Alice.Compiler.Mir;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// Lifted operators on Nullable&lt;T&gt; without Roslyn, with C#'s semantics. Both operands are evaluated once, left
/// to right (into temps unless they are locals or literals), then:
/// - arithmetic, bitwise, shifts, unary - ~ !: `has ? new T?(l.GetValueOrDefault() op r.GetValueOrDefault()) : default(T?)`;
/// - &lt; &lt;= &gt; &gt;=: `(lv op rv) &amp; has` (false when either is null);
/// - == / !=: values and HasValue flags compared (`null == null`); against a `nil` literal just `!x.HasValue`;
/// - `x ?? y`: `x.GetValueOrDefault(y)` for a simple non-nullable y, otherwise `x.HasValue ? x.GetValueOrDefault() : y`.
/// `has` is `l.HasValue &amp; r.HasValue` (only the nullable operands). Three-valued `bool?` &amp; | stay and are reported.
/// </summary>
public sealed class NullableLift(IlLowerContext? ctx = null) : IlPass(ctx)
{
    public override string Name => "il-nullable-lift";

    protected override MExpr LowerExpr(MExpr e) => e switch
    {
        MBinary b when IlTypes.IsNullableValue(b.L.Type) || IlTypes.IsNullableValue(b.R.Type) => Binary(b),
        MUnary { Op: AirUnOp.Neg or AirUnOp.BitNot or AirUnOp.Not } u when IlTypes.IsNullableValue(u.E.Type) => Unary(u),
        _ => e,
    };

    static bool IsNull(MExpr e) => e is AirConst { Value: null } || e is MCode { Text: "null" };

    /// <summary>An operand read: the operand itself when it is simple, else a temp assigned in evaluation order.</summary>
    sealed class Operands(IlLowerContext ctx)
    {
        public readonly List<MStmt> Pre = new();

        public Func<MExpr> Bind(MExpr e)
        {
            if (IsSimple(e)) return () => Copy(e);
            string t = ctx.Temp("n");
            var ty = e.Type!;
            Pre.Add(Decl(t, ty, e));
            return () => Local(t, ty);
        }

        public MExpr Wrap(MExpr value) => Pre.Count == 0 ? value : new AirSeq { Pre = Pre, Value = value, Type = value.Type };
    }

    static MExpr Copy(MExpr e) => e switch
    {
        AirLocalRef l => new AirLocalRef(l.Name, l.Type),
        AirConst k => new AirConst(k.Value, k.Type),
        MCode c => new MCode(c.Text) { Type = c.Type },
        _ => e,
    };

    static AirType Under(AirType t) => t with { Nullable = false };

    static MExpr HasValue(MExpr e) => Member(e, "HasValue", IlTypes.Bool);

    static MExpr Value(MExpr e) => Call(e, "GetValueOrDefault", Under(e.Type!));

    static MExpr And(MExpr a, MExpr b) => new MBinary { Op = AirBinOp.BitAnd, L = a, R = b, Type = IlTypes.Bool };

    static MExpr Lifted(MExpr has, MExpr value, AirType under) =>
        new MCond
        {
            C = has,
            A = new MNew { TypeCs = $"global::System.Nullable<{under.ToCs()}>", Args = [value], Type = under with { Nullable = true } },
            B = new AirDefault { TypeCs = under.ToCs() + "?", Type = under with { Nullable = true } },
            Type = under with { Nullable = true },
        };

    MExpr Binary(MBinary b)
    {
        bool nl = IlTypes.IsNullableValue(b.L.Type), nr = IlTypes.IsNullableValue(b.R.Type);
        if (b.Op is AirBinOp.Eq or AirBinOp.Ne && (IsNull(b.L) || IsNull(b.R)))
        {
            var x = IsNull(b.L) ? b.R : b.L;
            if (!IlTypes.IsNullableValue(x.Type)) return b;
            var has = HasValue(x);
            return b.Op == AirBinOp.Ne ? has : new MUnary { Op = AirUnOp.Not, E = has, Type = IlTypes.Bool };
        }
        if (b.Op == AirBinOp.Coalesce) return nl ? Coalesce(b) : b;
        if (b.Op is AirBinOp.And or AirBinOp.Or || b.Op is AirBinOp.BitAnd or AirBinOp.BitOr or AirBinOp.BitXor && IsBool(b.L.Type) && IsBool(b.R.Type))
        {
            Unsupported(null, $"three-valued `bool?` operator {b.Op.ToCs()}");
            return b;
        }
        if (!IlTypes.Known(b.L.Type) || !IlTypes.Known(b.R.Type))
        {
            Unsupported(null, $"lifted {b.Op.ToCs()} with an operand of unknown type");
            return b;
        }
        var ops = new Operands(Ctx);
        var l = ops.Bind(b.L);
        var r = ops.Bind(b.R);
        MExpr Lv() => nl ? Value(l()) : l();
        MExpr Rv() => nr ? Value(r()) : r();
        MExpr Has() => nl && nr ? And(HasValue(l()), HasValue(r())) : HasValue(nl ? l() : r());
        MExpr Op(AirBinOp op, AirType? t) => new MBinary { Op = op, Checked = b.Checked, L = Lv(), R = Rv(), Type = t };
        switch (b.Op)
        {
            case AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge:
                return ops.Wrap(And(Op(b.Op, IlTypes.Bool), Has()));
            case AirBinOp.Eq:
                return ops.Wrap(nl && nr
                    ? And(Op(AirBinOp.Eq, IlTypes.Bool), new MBinary { Op = AirBinOp.Eq, L = HasValue(l()), R = HasValue(r()), Type = IlTypes.Bool })
                    : And(Op(AirBinOp.Eq, IlTypes.Bool), Has()));
            case AirBinOp.Ne:
                {
                    var flags = nl && nr
                        ? new MBinary { Op = AirBinOp.Ne, L = HasValue(l()), R = HasValue(r()), Type = IlTypes.Bool }
                        : (MExpr)new MUnary { Op = AirUnOp.Not, E = Has(), Type = IlTypes.Bool };
                    return ops.Wrap(new MBinary { Op = AirBinOp.BitOr, L = Op(AirBinOp.Ne, IlTypes.Bool), R = flags, Type = IlTypes.Bool });
                }
            default:
                {
                    var under = b.Type is { } bt && IlTypes.Known(bt) ? Under(bt) : Under(nl ? b.L.Type! : b.R.Type!);
                    return ops.Wrap(Lifted(Has(), Op(b.Op, under), under));
                }
        }
    }

    MExpr Coalesce(MBinary b)
    {
        var ops = new Operands(Ctx);
        var x = ops.Bind(b.L);
        if (!IlTypes.IsNullableValue(b.R.Type))
        {
            if (IsSimple(b.R)) return ops.Wrap(Call(x(), "GetValueOrDefault", Under(b.L.Type!), b.R));
            return ops.Wrap(new MCond { C = HasValue(x()), A = Value(x()), B = b.R, Type = Under(b.L.Type!) });
        }
        return ops.Wrap(new MCond { C = HasValue(x()), A = x(), B = b.R, Type = b.L.Type });
    }

    MExpr Unary(MUnary u)
    {
        var ops = new Operands(Ctx);
        var x = ops.Bind(u.E);
        var under = Under(u.E.Type!);
        return ops.Wrap(Lifted(HasValue(x()), new MUnary { Op = u.Op, Checked = u.Checked, E = Value(x()), Type = under }, under));
    }

    static bool IsBool(AirType? t) => t is AirType.Prim { Kind: PrimKind.Bool };
}
