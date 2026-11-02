namespace Alice.Compiler.Mir;

/// <summary>
/// MIR statements: structured, explicitly-typed, closure-free where possible. Every Alice construct that
/// allocates implicitly (blocks, iterator chains, if-expressions) has been lowered away before this level.
/// </summary>
public abstract class MStmt
{
    int line;

    /// <summary>Alice source line (0: synthesized by the lowerer); the line of <see cref="Span"/> when one is set.</summary>
    public int Line
    {
        get => Span?.Line ?? line;
        set { line = value; if (Span is { } s) Span = s with { Line = value }; }
    }

    public Span? Span;
}

public sealed class MDecl : MStmt
{
    public string Name = "";
    public string? TypeCs;          // null => var
    public MExpr? Init;
    public string? RentLength;      // Init is Own<T>.Rent(RentLength) of an owned array; RentLength names a local
    public bool Using;              // `using var x = ...`: disposed at the end of the enclosing block
}

public sealed class MDeconstruct : MStmt
{
    public List<string> Names = new();
    public bool Declare;
    public MExpr Value = null!;
}

public sealed class MAssign : MStmt
{
    public MExpr Target = null!;
    public string Op = "=";
    public MExpr Value = null!;
}

public sealed class MExprStmt : MStmt { public MExpr E = null!; }

public sealed class MIf : MStmt
{
    public MExpr Cond = null!;
    public List<MStmt> Then = new();
    public List<MStmt>? Else;
}

public sealed class MWhile : MStmt
{
    public MExpr Cond = null!;
    public List<MStmt> Body = new();
}

/// <summary>Counted loop: for (var v = start; v cmp end; v step).</summary>
public sealed class MFor : MStmt
{
    public string Var = "";
    public string? VarType;          // null => var (inferred from Start)
    public MExpr Start = null!;
    public string Cmp = "<";
    public MExpr End = null!;
    public AirForStep Step = new();
    public List<MStmt> Body = new();
    public long? MinStart;           // a proven lower bound of Start when it is not a literal (parallel chunk loops)
}

public sealed class MForeach : MStmt
{
    public bool Await;
    public List<string> Vars = new();
    public MExpr Coll = null!;
    public List<MStmt> Body = new();
}

public sealed class MReturn : MStmt { public MExpr? Value; }
public sealed class MBreak : MStmt { }
public sealed class MContinue : MStmt { }
public sealed class MThrow : MStmt { public MExpr Value = null!; }

public sealed class MCatch
{
    public string TypeCs = "global::System.Exception";
    public string? Name;
    public List<MStmt> Body = new();
}

public sealed class MTry : MStmt
{
    public List<MStmt> Body = new();
    public List<MCatch> Catches = new();
    public List<MStmt>? Finally;
}

public sealed class MBlock : MStmt
{
    public List<MStmt> Body = new();
    public bool Unchecked;   // C# `unchecked { }`
    public bool Unsafe;      // C# `unsafe { }`
    public MExpr? Lock;      // C# `lock (x) { }`
}

public sealed class MUsing : MStmt
{
    public bool Await;
    public string Name = "";
    public MExpr Value = null!;
    public List<MStmt> Body = new();
}

/// <summary>
/// The step of a counted <see cref="MFor"/>, assignment-shaped: `i++`, `i--`, `i += k`, `i -= k`, `i >>= k`, or
/// `i = v` (`i = unchecked(i + 1)` once InductionVariables proved the increment cannot overflow). <see cref="Op"/> is
/// the operator token, as in <see cref="MAssign.Op"/>; <see cref="Value"/> is null for ++ and --.
/// </summary>
public sealed class AirForStep
{
    public string Op = "++";
    public MExpr? Value;

    public static AirForStep Inc() => new();
    public static AirForStep Dec() => new() { Op = "--" };
    public static AirForStep By(string op, MExpr value) => new() { Op = op, Value = value };

    /// <summary>`v = unchecked(v + k)`.</summary>
    public static AirForStep UncheckedAdd(string v, MExpr k, AirType? type = null) => new()
    {
        Op = "=",
        Value = new MCall { Name = "unchecked", Args = { new MBinary { Op = AirBinOp.Add, Checked = false, L = new AirLocalRef(v, type), R = k, Type = type } }, Type = type },
    };

    /// <summary>`v++`, or `v = unchecked(v + 1)`.</summary>
    public bool IsIncrement(string v) => Op == "++" || Op == "=" && Value is MCall { Name: "unchecked", Args: [MBinary { Op: AirBinOp.Add, L: MCode l, R: MCode { Text: "1" } }] } && l.Text == v;

    /// <summary>The `(v + k)` or `(v - k)` inside `v = unchecked(...)`, when the step is that shape.</summary>
    public MBinary? UncheckedStep(string v) =>
        Op == "=" && Value is MCall { Name: "unchecked", Args: [MBinary { Op: AirBinOp.Add or AirBinOp.Sub, L: MCode l } b] } && l.Text == v ? b : null;
}
