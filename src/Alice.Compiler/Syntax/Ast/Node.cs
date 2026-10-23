namespace Alice.Compiler.Syntax;

public abstract class Node
{
    public int Line;
    public int Col;
}

/// <summary>Alice-spelled type reference; resolved to an AType by the binder.</summary>
public sealed class TypeRef : Node
{
    public string Name = "";
    public List<TypeRef> Args = new();
    public bool Nullable;
    public bool Pointer;
    public string Special = "";   // "" | "tuple" | "proc"
    public List<string>? TupleNames;   // `{min: Int32, max: Int32}`
}
