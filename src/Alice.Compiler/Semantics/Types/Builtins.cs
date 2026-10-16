using System.Reflection;
using System.Runtime.CompilerServices;

namespace Alice.Compiler.Semantics;

/// <summary>Static typing of Alice.Runtime.Prelude: free functions and Ruby-style extension methods.</summary>
public static class Builtins
{
    public static readonly HashSet<string> FreeFunctions = new() { "puts", "print", "p", "gets", "rand", "exit" };

    /// <summary>Extension method names exposed by the Prelude (discovered by reflection, so the runtime is the source of truth).</summary>
    public static readonly HashSet<string> ExtNames = typeof(Alice.Runtime.Prelude)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(m => m.IsDefined(typeof(ExtensionAttribute)))
        .Select(m => m.Name)
        .ToHashSet();

    /// <summary>Iteration methods whose literal blocks the lowerer may inline into loops.</summary>
    public static readonly HashSet<string> Iterators = new() { "each", "each_with_index", "map", "select", "reject", "sum", "count", "any_q", "all_q", "find", "reduce", "times", "upto", "downto", "each_char", "max_by", "min_by" };

    static readonly HashSet<string> ClrExtNames = new[] { typeof(Enumerable), typeof(MemoryExtensions), typeof(System.Collections.Generic.CollectionExtensions) }
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Where(m => m.IsDefined(typeof(ExtensionAttribute)))
        .Select(m => m.Name)
        .ToHashSet();

    public static bool IsClrExtension(string name) => ClrExtNames.Contains(name);

    /// <summary>Result type of common LINQ terminals; Unknown lets C# infer the rest.</summary>
    public static AType ClrExtReturn(string name, AType recv)
    {
        AType e = ElemOf(recv);
        return name switch
        {
            "Count" or "LongCount" => name == "Count" ? AType.Int : AType.Long,
            "Any" or "All" or "Contains" or "SequenceEqual" => AType.Bool,
            "Sum" or "Max" or "Min" or "First" or "Last" or "Single" or "ElementAt" => e,
            "Average" => e.Cs is "float" or "decimal" ? e : AType.Double,
            "ToArray" => e.IsKnown ? AType.Array(e) : AType.Unknown,
            "ToList" => e.IsKnown ? Generic(typeof(List<>), e) : AType.Unknown,
            _ => AType.Unknown,
        };
    }

    public static AType FreeReturn(string name, List<AType> args) => name switch
    {
        "p" => args.Count > 0 ? args[0] : AType.Void,
        "gets" => AType.String.AsNullable(),
        "rand" => args.Count == 0 ? AType.Double : AType.Int,
        _ => AType.Void,
    };

    public static AType Generic(Type open, params AType[] args)
    {
        var closed = ClrTypes.Close(open, args.ToList());
        return closed == null ? AType.Unknown : ClrTypes.FromClr(closed);
    }

    public static AType ElemOf(AType t)
    {
        if (t.Kind == "array" && t.Elem != null) return t.Elem;
        if (t.Sym?.SoaOf is TypeSym soaElem) return soaElem.Type;
        if (t.IsString) return AType.Char;
        if (t.Clr != null)
        {
            if (t.Clr.IsGenericType && t.Clr.GetGenericTypeDefinition() == typeof(Alice.Runtime.Channel<>)) return t.Args[0];
            var e = ClrTypes.ElementOf(t.Clr);
            if (e != null) return ClrTypes.FromClr(e);
        }
        return AType.Unknown;
    }

    /// <summary>Block parameter types for a Prelude iterator call; empty when unknown.</summary>
    public static List<AType> BlockParams(string name, AType recv, List<AType> args)
    {
        AType e = ElemOf(recv);
        switch (name)
        {
            case "times":
            case "upto":
            case "downto":
            case "par_each":
                return new() { AType.Int };
            case "each_with_index":
            case "map_with_index":
                return new() { e, AType.Int };
            case "reduce":
                return args.Count > 0 ? new() { args[0], e } : new() { e, e };
            case "each" when recv.Clr is { IsGenericType: true } c && c.GetGenericTypeDefinition() == typeof(Dictionary<,>):
                return new() { recv.Args[0], recv.Args[1] };
        }
        return new() { e };
    }

    public static AType? ExtReturn(string name, AType recv, List<AType> args, AType? blockRet)
    {
        if (!ExtNames.Contains(name)) return null;
        AType e = ElemOf(recv);
        AType br = blockRet ?? AType.Unknown;
        switch (name)
        {
            case "each":
            case "each_with_index":
            case "times":
            case "upto":
            case "downto":
            case "each_char":
            case "push":
            case "par_each":
                return AType.Void;
            case "par_sum":
                return br.IsKnown && !br.IsVoid ? br : AType.Unknown;
            case "map":
            case "map_with_index":
            case "par_map":
                return br.IsKnown && !br.IsVoid ? AType.Array(br) : AType.Unknown;
            case "reverse":
                return recv.IsString ? AType.String : e.IsKnown ? AType.Array(e) : AType.Unknown;
            case "select":
            case "reject":
            case "sort":
            case "sort_by":
            case "uniq":
            case "to_a":
            case "take":
            case "drop":
                return e.IsKnown ? AType.Array(e) : AType.Unknown;
            case "sum" when blockRet != null:
                return br;
            case "first":
            case "last":
            case "max":
            case "min":
            case "sum":
            case "find":
            case "pop":
            case "max_by":
            case "min_by":
                return e;
            case "reduce":
                return args.Count > 0 ? args[0] : e;
            case "size":
            case "length":
            case "count":
            case "index":
            case "ord":
                return AType.Int;
            case "to_i":
                return AType.Int;
            case "to_f":
            case "floor":
            case "ceil":
            case "round":
            case "sqrt":
                return AType.Double;
            case "to_f32":
                return AType.Float;
            case "to_i64":
                return AType.Long;
            case "to_list":
                return e.IsKnown ? Generic(typeof(List<>), e) : AType.Unknown;
            case "chars":
                return AType.Array(AType.Char);
            case "lines":
            case "split":
                return AType.Array(AType.String);
            case "upcase":
            case "downcase":
            case "capitalize":
            case "strip":
            case "gsub":
            case "ljust":
            case "rjust":
            case "join":
            case "inspect":
                return AType.String;
            case "keys":
                return recv.Args.Count == 2 ? AType.Array(recv.Args[0]) : AType.Unknown;
            case "values":
                return recv.Args.Count == 2 ? AType.Array(recv.Args[1]) : AType.Unknown;
            case "abs":
            case "succ":
            case "pred":
                return recv;
            case "chr":
                return AType.Char;
            case "wait":
                return recv.Args.Count == 1 ? recv.Args[0] : AType.Void;
        }
        return name.EndsWith("_q") ? AType.Bool : AType.Unknown;
    }
}
