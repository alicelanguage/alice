using Alice.Compiler.Semantics;

namespace Alice.Compiler.Syntax;

/// <summary>Expression node. After binding it carries its type: the AST + annotations is Alice's HIR.</summary>
public abstract class Expr : Node
{
    public AType Type = AType.Unknown;
    public string Res = "";      // binder resolution kind (local field call static ext clr dynamic ...)
    public object? Sym;          // LocalSym / FieldSym / MethodSym / TypeSym bound to this node
}

public sealed class Arg : Node
{
    public string? Name;     // named argument
    public string Mod = "";  // "" | out | ref
    public string? OutName;  // out x (declares x)
    public Expr? Value;
    public LocalSym? OutLocal;
}

public sealed class LitExpr : Expr
{
    public string Kind = "";   // int float char str bool nil
    public string Text = "";   // C#-ready text (str: escaped body without quotes)
}

public sealed class InterpExpr : Expr
{
    public List<string> Chunks = new();   // literal chunks, C#-escaped
    public List<Expr> Holes = new();      // Chunks[i] precedes Holes[i]; Chunks has Holes.Count + 1 items
}

public sealed class NameExpr : Expr { public string Name = ""; }
public sealed class IvarExpr : Expr { public string Name = ""; }
public sealed class SelfExpr : Expr { }
public sealed class SuperExpr : Expr { public List<Arg>? Args; }
public sealed class ConstExpr : Expr { public TypeRef Ref = new(); }

public sealed class CallExpr : Expr
{
    public Expr? Recv;
    public string Name = "";
    public List<Arg> Args = new();
    public LambdaExpr? Block;
    public bool Parens;
    public bool Safe;
    public List<TypeRef> TypeArgs = new();
}

public sealed class NewExpr : Expr
{
    public TypeRef TypeRef = new();
    public List<Arg> Args = new();
    public LambdaExpr? Block;
    public CallExpr? AsCall;     // `Add(5)`: not a type, so it is a (C#-style capitalized) method call
}

public sealed class IndexExpr : Expr
{
    public Expr Target = null!;
    public List<Arg> Args = new();
}

public sealed class BinaryExpr : Expr
{
    public string Op = "";
    public Expr L = null!;
    public Expr R = null!;
}

public sealed class UnaryExpr : Expr
{
    public string Op = "";
    public Expr E = null!;
}

public sealed class TernaryExpr : Expr
{
    public Expr C = null!;
    public Expr A = null!;
    public Expr B = null!;
}

public sealed class RangeExpr : Expr
{
    public Expr A = null!;
    public Expr B = null!;
    public bool Excl;
}

public sealed class ArrayLit : Expr
{
    public List<Expr> Items = new();
    public TypeRef? Of;
}

public sealed class HashLit : Expr
{
    public List<Expr> Keys = new();
    public List<Expr> Values = new();
    public TypeRef? K;
    public TypeRef? V;
}

public sealed class TupleLit : Expr { public List<Expr> Items = new(); }

public sealed class LambdaExpr : Expr
{
    public List<Param> Params = new();
    public List<Stmt> Body = new();
    public List<LocalSym> ParamSyms = new();
    public AType Result = AType.Void;
    public bool ReturnsValue;
    public bool Captures;
}

public sealed class TypeTestExpr : Expr
{
    public string Kind = "";   // is as typeof sizeof default nil? not_nil!
    public Expr? E;
    public TypeRef? Ref;
}

public sealed class InvokeExpr : Expr
{
    public Expr Target = null!;
    public List<Arg> Args = new();
}

/// <summary>`in [a, b]`, `in [first, *rest]`, `in []` (Ruby 3 array pattern).</summary>
public sealed class ArrayPatternExpr : Expr
{
    public List<string> Names = new();
    public string? Rest;
    public List<Semantics.LocalSym> Binds = new();
    public Semantics.LocalSym? RestSym;
}

/// <summary>`in x` binds the subject; `in _` matches anything.</summary>
public sealed class BindPatternExpr : Expr
{
    public string Name = "";
    public Semantics.LocalSym? Local;
}

/// <summary>`in {"k" => v, "lang" => "pt"}`: keys must exist; names bind values, other values must be equal.</summary>
public sealed class HashPatternExpr : Expr
{
    public List<Expr> Keys = new();
    public List<Expr> Values = new();
    public List<Semantics.LocalSym?> Binds = new();
}

public sealed class RegexExpr : Expr
{
    public string Pattern = "";   // C#-escaped string literal body
    public string Flags = "";
}

public sealed class AwaitExpr : Expr { public Expr E = null!; }

public sealed class SpawnExpr : Expr { public LambdaExpr Body = null!; }
public sealed class YieldExpr : Expr { public List<Arg> Args = new(); }

/// <summary>if/case/begin used as a value.</summary>
public sealed class StmtExpr : Expr { public Stmt Stmt = null!; }
