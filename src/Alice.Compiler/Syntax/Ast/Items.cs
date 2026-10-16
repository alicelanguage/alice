using Alice.Compiler.Semantics;

namespace Alice.Compiler.Syntax;

public abstract class Item : Node
{
    public List<string> Attrs = new();
    public object? Sym;          // TypeSym / MethodSym / FieldSym after binding
}

public sealed class FileNode : Node
{
    public string Path = "";
    public List<Item> Items = new();
    public HashSet<string> Hoisted = new();
    public List<LocalSym> HoistedLocals = new();
}

public sealed class UsingItem : Item { public string Name = ""; }

public sealed class NamespaceItem : Item
{
    public string Name = "";
    public List<Item> Items = new();
}

public sealed class TypeDecl : Item
{
    public string Kind = "";     // class struct module interface
    public string Name = "";
    public bool IsAbstract;
    public List<string> TypeParams = new();
    public List<TypeRef> Bases = new();
    public List<Item> Members = new();
    public List<Constraint> Constraints = new();
    public List<Param> RecordParams = new();    // record Point(x : Int32, y : Int32)
}

/// <summary>`where T : IComparable(T), struct, new` (Rust/C# style generic constraint).</summary>
public sealed class Constraint : Node
{
    public string Param = "";
    public List<TypeRef> Types = new();
    public List<string> Keywords = new();      // struct class unmanaged notnull new
}

public sealed class EnumDecl : Item
{
    public string Name = "";
    public TypeRef? Underlying;
    public List<string> Names = new();
    public List<Expr?> Values = new();
    public List<List<Param>?> Fields = new();   // sum type: Circle(r : Float64); null for unit variants
    public bool IsAdt => Fields.Any(f => f != null);
}

public sealed class LibDecl : Item
{
    public string Name = "";
    public string LibName = "c";
    public List<FunDecl> Funs = new();
}

public sealed class FunDecl : Node
{
    public string Name = "";
    public string Entry = "";
    public List<Param> Params = new();
    public TypeRef? Ret;
}

public sealed class Param : Node
{
    public string Name = "";
    public TypeRef? Type;
    public Expr? Default;
    public bool Auto;     // @x : T  (assigns the field)
    public bool Splat;    // *xs : T
    public bool Block;    // &blk : A -> R
}

public sealed class MethodDecl : Item
{
    public string Name = "";
    public string Vis = "public";
    public bool IsStatic;
    public bool IsAbstract;
    public List<string> TypeParams = new();
    public List<Param> Params = new();
    public TypeRef? Ret;
    public List<Stmt> Body = new();
    public List<Arg>? SuperInit;
    public List<Constraint> Constraints = new();
    public bool HasBody = true;
    public HashSet<string> Hoisted = new();      // Ruby function-scoped locals first assigned in a nested block
    public List<LocalSym> HoistedLocals = new();
}

public sealed class AccessorDecl : Item
{
    public string Kind = "";     // getter property setter
    public string Name = "";
    public TypeRef? Type;
    public Expr? Init;
}

public sealed class FieldDecl : Item
{
    public string Name = "";
    public TypeRef Type = new();
    public Expr? Init;
}

public sealed class ConstDecl : Item
{
    public string Name = "";
    public TypeRef? Type;
    public Expr Value = null!;
}

public sealed class IncludeDecl : Item { public TypeRef Type = new(); }

public sealed class StmtItem : Item { public Stmt Stmt = null!; }
