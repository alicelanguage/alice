namespace Alice.Compiler.Mir;

/// <summary>
/// MIR statements: structured, explicitly-typed, closure-free where possible. Every Alice construct that
/// allocates implicitly (blocks, iterator chains, if-expressions) has been lowered away before this level.
/// </summary>
public abstract class MStmt
{
    public int Line;
}

public sealed class MDecl : MStmt
{
    public string Name = "";
    public string? TypeCs;          // null => var
    public MExpr? Init;
    public string? RentLength;      // Init is Own<T>.Rent(RentLength) of an owned array; RentLength names a local
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
    public string Step = "++";
    public List<MStmt> Body = new();
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
