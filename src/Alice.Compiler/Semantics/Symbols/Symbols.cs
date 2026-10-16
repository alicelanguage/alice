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
    public AType? ExtTarget;          // monkey patch of a .NET type (lowered to extension methods)
    public TypeSym? Parent;           // sum-type variant -> its enum
    public List<TypeSym> Variants = new();
    public List<string> FieldOrder = new();   // record / variant positional fields
    public TypeSym? SoaStore;         // @[SoA] struct -> its columnar store type
    public TypeSym? SoaOf;            // store -> element struct
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
    public bool Inferred;             // untyped: specialized per call site
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
    public bool IsAsync;
    public bool HasInferredGenerics;  // untyped params: monomorphized per call-site types
    public bool NeedsGeneric;         // some call site had unknown argument types
    public readonly Dictionary<string, Instance> Instances = new();

    public int Required => Params.Count(p => !p.HasDefault && !p.Splat && !p.Block);

    public bool Accepts(int argc)
    {
        int max = Params.Count(p => !p.Block);
        return argc >= Required && (argc <= max || Params.Any(p => p.Splat));
    }
}

/// <summary>A Crystal-style specialization of a method with untyped parameters.</summary>
public sealed class Instance
{
    public List<AType> Args = new();
    public AType Ret = AType.Unknown;
    public bool Seen;                 // reached in the latest binding pass
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
