namespace Alice.Compiler.Mir;

/// <summary>Binary operators of <see cref="MBinary"/>.</summary>
public enum AirBinOp
{
    Add, Sub, Mul, Div, Mod,
    Shl, Shr, UShr,
    BitAnd, BitOr, BitXor,
    And, Or,
    Eq, Ne, Lt, Le, Gt, Ge,
    Coalesce,
}

/// <summary>Unary operators of <see cref="MUnary"/>. Inc/Dec are prefix or postfix (<see cref="MUnary.Postfix"/>);
/// NotNull is the postfix null-forgiving `x!`.</summary>
public enum AirUnOp { Neg, Not, BitNot, AddressOf, Inc, Dec, NotNull, Await, FromEnd }

/// <summary>C# spellings of the AIR operators, and the reverse for operators that arrive as text (Alice source).</summary>
public static class AirOps
{
    public static string ToCs(this AirBinOp op) => op switch
    {
        AirBinOp.Add => "+",
        AirBinOp.Sub => "-",
        AirBinOp.Mul => "*",
        AirBinOp.Div => "/",
        AirBinOp.Mod => "%",
        AirBinOp.Shl => "<<",
        AirBinOp.Shr => ">>",
        AirBinOp.UShr => ">>>",
        AirBinOp.BitAnd => "&",
        AirBinOp.BitOr => "|",
        AirBinOp.BitXor => "^",
        AirBinOp.And => "&&",
        AirBinOp.Or => "||",
        AirBinOp.Eq => "==",
        AirBinOp.Ne => "!=",
        AirBinOp.Lt => "<",
        AirBinOp.Le => "<=",
        AirBinOp.Gt => ">",
        AirBinOp.Ge => ">=",
        AirBinOp.Coalesce => "??",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>The prefix (or, for Inc/Dec/NotNull, postfix) text; `await` carries its trailing space.</summary>
    public static string ToCs(this AirUnOp op) => op switch
    {
        AirUnOp.Neg => "-",
        AirUnOp.Not or AirUnOp.NotNull => "!",
        AirUnOp.BitNot => "~",
        AirUnOp.AddressOf => "&",
        AirUnOp.FromEnd => "^",
        AirUnOp.Inc => "++",
        AirUnOp.Dec => "--",
        AirUnOp.Await => "await ",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    public static AirBinOp ParseBin(string op) => op switch
    {
        "+" => AirBinOp.Add,
        "-" => AirBinOp.Sub,
        "*" => AirBinOp.Mul,
        "/" => AirBinOp.Div,
        "%" => AirBinOp.Mod,
        "<<" => AirBinOp.Shl,
        ">>" => AirBinOp.Shr,
        ">>>" => AirBinOp.UShr,
        "&" => AirBinOp.BitAnd,
        "|" => AirBinOp.BitOr,
        "^" => AirBinOp.BitXor,
        "&&" => AirBinOp.And,
        "||" => AirBinOp.Or,
        "==" => AirBinOp.Eq,
        "!=" => AirBinOp.Ne,
        "<" => AirBinOp.Lt,
        "<=" => AirBinOp.Le,
        ">" => AirBinOp.Gt,
        ">=" => AirBinOp.Ge,
        "??" => AirBinOp.Coalesce,
        _ => throw new InvalidOperationException($"no AIR binary operator `{op}`"),
    };

    /// <summary>Prefix operators as Alice spells them (`-x`, `!x`, `~x`, `&amp;x`).</summary>
    public static AirUnOp ParseUn(string op) => op switch
    {
        "-" => AirUnOp.Neg,
        "!" => AirUnOp.Not,
        "~" => AirUnOp.BitNot,
        "&" => AirUnOp.AddressOf,
        "++" => AirUnOp.Inc,
        "--" => AirUnOp.Dec,
        _ => throw new InvalidOperationException($"no AIR unary operator `{op}`"),
    };
}
