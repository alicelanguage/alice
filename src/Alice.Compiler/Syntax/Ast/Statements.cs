using Alice.Compiler.Semantics;

namespace Alice.Compiler.Syntax;

public abstract class Stmt : Node { }

public sealed class ExprStmt : Stmt { public Expr Expr = null!; }

/// <summary>`val x = v`, `x : T = v`. Plain `x = v` is an Assign; the binder decides if it declares.</summary>
public sealed class LocalDecl : Stmt
{
    public string Name = "";
    public TypeRef? Type;
    public Expr? Value;
    public bool IsVal;
    public LocalSym? Local;
}

public sealed class Assign : Stmt
{
    public Expr Target = null!;
    public string Op = "=";
    public Expr Value = null!;
    public LocalSym? Declares;   // set when this assignment introduces the local
}

public sealed class MultiAssign : Stmt
{
    public List<string> Names = new();
    public List<Expr> Values = new();
    public List<LocalSym> Locals = new();
    public bool Declares;
}

public sealed class IfStmt : Stmt
{
    public List<Expr> Conds = new();
    public List<List<Stmt>> Bodies = new();
    public List<Stmt>? Else;
}

public sealed class WhileStmt : Stmt
{
    public Expr Cond = null!;
    public List<Stmt> Body = new();
}

public sealed class ForStmt : Stmt
{
    public List<string> Names = new();
    public Expr Iter = null!;
    public List<Stmt> Body = new();
    public List<LocalSym> Locals = new();
}

public sealed class When : Node
{
    public List<Expr> Patterns = new();
    public List<Stmt> Body = new();
    public Expr? Guard;          // `in pattern if cond`
}

public sealed class CaseStmt : Stmt
{
    public Expr? Subject;
    public List<When> Whens = new();
    public List<Stmt>? Else;
    public bool Exhaustive;      // covers every variant of a sum type
}

/// <summary>Bound form of `when Shape::Circle(r, 0.0)`: type test, field bindings, field equality checks.</summary>
public sealed class Destructure
{
    public AType Type = AType.Unknown;
    public List<(string Field, Semantics.LocalSym? Bind, Expr? Eq)> Parts = new();
}

public sealed class Rescue : Node
{
    public string? Name;
    public TypeRef? Type;
    public List<Stmt> Body = new();
    public LocalSym? Local;
    public AType Resolved = AType.Unknown;
}

public sealed class BeginStmt : Stmt
{
    public List<Stmt> Body = new();
    public List<Rescue> Rescues = new();
    public List<Stmt>? Ensure;
}

public sealed class ReturnStmt : Stmt { public Expr? Value; }
public sealed class BreakStmt : Stmt { }
public sealed class RetryStmt : Stmt { }
public sealed class NextStmt : Stmt { public Expr? Value; }
public sealed class RaiseStmt : Stmt { public Expr Value = null!; }
