namespace Alice.Compiler;

/// <summary>A lowered statement. If-chains stay structured so tail position can push `return` into branches.</summary>
public sealed class Stmt
{
    public int Line;
    public string Code = "";
    public string Expr = "";
    public bool HasExpr;
    public string Kind = "stmt";
    public string Pre = "";
    public List<string>? Conds;
    public List<List<Stmt>>? Bodies;
    public List<Stmt>? Else;
}
