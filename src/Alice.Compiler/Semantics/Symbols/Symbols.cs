using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>An Alice-declared type (class, struct, module, interface, enum, lib, or the implicit Program).</summary>
public sealed class TypeSym
{
    public string Name = "";
    public string Kind = "";          // class struct module interface enum lib program
    public string Namespace = "";
    public TypeDecl? Decl;
    public List<string> TypeParams = new();
    public List<AType> Bases = new();
    public TypeSym? BaseClass;
    public bool HasSubclass;
    public bool IsStack;
    public bool IsAbstract;
    public AType Type = AType.Unknown;
    public readonly Dictionary<string, List<MethodSym>> Methods = new();
    public readonly Dictionary<string, FieldSym> Fields = new();

    public bool IsStatic => Kind is "module" or "program" or "lib";

    public void AddMethod(MethodSym m)
    {
        if (!Methods.TryGetValue(m.Name, out var list)) Methods[m.Name] = list = new List<MethodSym>();
        list.Add(m);
    }

    /// <summary>Looks up through the Alice base-class chain.</summary>
    public MethodSym? FindMethod(string name, int argc)
    {
        for (var s = this; s != null; s = s.BaseClass)
            if (s.Methods.TryGetValue(name, out var list))
            {
                foreach (var m in list) if (m.Accepts(argc)) return m;
                return list[0];
            }
        return null;
    }

    public FieldSym? FindField(string name)
    {
        for (var s = this; s != null; s = s.BaseClass)
            if (s.Fields.TryGetValue(name, out var f)) return f;
        return null;
    }
}

public sealed class ParamSym
{
    public string Name = "";
    public string CsName = "";
    public AType Type = AType.Unknown;
    public bool HasDefault;
    public bool Splat;
    public bool Block;
    public bool Auto;
}

public sealed class MethodSym
{
    public string Name = "";          // Alice name, e.g. ok?
    public string CsName = "";        // ok_q
    public TypeSym Owner = null!;
    public MethodDecl? Decl;
    public List<ParamSym> Params = new();
    public List<string> TypeParams = new();
    public AType Ret = AType.Void;
    public bool RetDeclared;
    public bool IsStatic;
    public bool IsCtor;
    public bool UsesYield;

    public int Required => Params.Count(p => !p.HasDefault && !p.Splat && !p.Block);

    public bool Accepts(int argc)
    {
        int max = Params.Count(p => !p.Block);
        return argc >= Required && (argc <= max || Params.Any(p => p.Splat));
    }
}

public sealed class FieldSym
{
    public string Name = "";
    public string CsName = "";
    public AType Type = AType.Unknown;
    public string Kind = "";          // field getter property setter const enum-member
    public bool IsStatic;
    public TypeSym Owner = null!;
}

public sealed class LocalSym
{
    public string Name = "";
    public string CsName = "";
    public AType Type = AType.Unknown;
    public bool IsVal;
    public int LambdaDepth;           // nesting of lambdas at declaration
    public int SpawnDepth;            // nesting of spawn bodies at declaration
}
