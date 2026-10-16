using System.Reflection;
using System.Runtime.CompilerServices;

namespace Alice.Compiler.Semantics;

/// <summary>Static typing of Alice.Runtime.Prelude: free functions and Ruby-style extension methods.</summary>
public static class Builtins
{
    public static readonly HashSet<string> FreeFunctions = new() { "puts", "print", "p", "gets", "rand", "exit", "format", "read_file", "write_file", "file_exists?", "read_lines", "sleep", "now", "env", "to_json", "parse_json" };

    /// <summary>Prelude functions (non-extension) callable with no arguments: `be_nil`, `raise_error`; name -> return type.</summary>
    public static readonly Dictionary<string, Type> PreludeZeroArg = typeof(Alice.Runtime.Prelude)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(m => !m.IsDefined(typeof(ExtensionAttribute)) && !m.IsGenericMethodDefinition && m.GetParameters().All(p => p.IsOptional) && m.ReturnType != typeof(void))
        .GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First().ReturnType);

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
        "format" or "read_file" or "to_json" => AType.String,
        "env" => AType.String.AsNullable(),
        "file_exists?" => AType.Bool,
        "read_lines" => AType.Array(AType.String),
        "now" => ClrTypes.FromClr(typeof(DateTime)),
        "parse_json" => ClrTypes.FromClr(typeof(System.Text.Json.Nodes.JsonNode)),
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
        if (t.Sym?.EnumerableElem is AType enumElem) return enumElem;
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
        if (IsDict(recv))
        {
            switch (name)
            {
                case "each_pair":
                case "map":
                case "select":
                case "reject":
                case "max_by":
                case "min_by":
                case "sort_by":
                case "group_by":
                case "sum":
                case "count":
                case "any_q":
                case "all_q":
                    return new() { recv.Args[0], recv.Args[1] };
                case "transform_values": return new() { recv.Args[1] };
                case "transform_keys": return new() { recv.Args[0] };
            }
        }
        switch (name)
        {
            case "each_slice":
            case "each_cons":
                return e.IsKnown ? new() { AType.Array(e) } : new() { AType.Unknown };
            case "chunk_while":
            case "sort":
                return new() { e, e };
            case "inject":
                return args.Count > 0 ? new() { args[0], e } : new() { e, e };
            case "each_with_object":
                return args.Count > 0 ? new() { e, args[0] } : new() { e, AType.Unknown };
            case "step" when recv.Cs is "int" or "double":
                return new() { recv };
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

    static bool IsDict(AType t) =>
        t.Clr is { IsGenericType: true } c && c.GetGenericTypeDefinition() == typeof(Dictionary<,>) && t.Args.Count == 2;

    /// <summary>Hash (Dictionary) methods whose result differs from the Enumerable version; null falls through.</summary>
    static AType? DictReturn(string name, AType d, AType br)
    {
        AType k = d.Args[0], v = d.Args[1];
        AType kv = Generic(typeof(KeyValuePair<,>), k, v);
        return name switch
        {
            "select" or "reject" or "merge" => d,
            "map" => br.IsKnown && !br.IsVoid ? AType.Array(br) : AType.Unknown,
            "fetch" => v,
            "dig" => v.AsNullable(),
            "invert" => Generic(typeof(Dictionary<,>), v, k),
            "to_a" => AType.Array(Generic(typeof(ValueTuple<,>), k, v)),
            "max_by" or "min_by" => kv,
            "sort_by" => AType.Array(kv),
            "transform_values" => br.IsKnown && !br.IsVoid ? Generic(typeof(Dictionary<,>), k, br) : AType.Unknown,
            "transform_keys" => br.IsKnown && !br.IsVoid ? Generic(typeof(Dictionary<,>), br, v) : AType.Unknown,
            "group_by" => br.IsKnown && !br.IsVoid ? Generic(typeof(Dictionary<,>), br, AType.Array(kv)) : AType.Unknown,
            "sum" => br.IsKnown && !br.IsVoid ? br : AType.Unknown,
            "each_pair" => AType.Void,
            _ => null,
        };
    }

    public static AType? ExtReturn(string name, AType recv, List<AType> args, AType? blockRet)
    {
        if (!ExtNames.Contains(name)) return null;
        AType e = ElemOf(recv);
        AType br = blockRet ?? AType.Unknown;
        if (IsDict(recv) && DictReturn(name, recv, br) is AType dr) return dr;
        AType arr = e.IsKnown ? AType.Array(e) : AType.Unknown;
        AType arr2 = e.IsKnown ? AType.Array(AType.Array(e)) : AType.Unknown;
        switch (name)
        {
            case "each_slice" when blockRet == null:
            case "each_cons" when blockRet == null:
            case "chunk_while":
            case "partition":
            case "product":
            case "combination":
            case "permutation":
                return arr2;
            case "each_slice":
            case "each_cons":
            case "reverse_each":
            case "cycle":
            case "each_line":
            case "each_pair":
            case "step" when recv.Cs is "int" or "double":
                return AType.Void;
            case "first" when args.Count == 1:
            case "last" when args.Count == 1:
            case "compact":
            case "minmax":
            case "shuffle":
            case "rotate":
            case "take_while":
            case "drop_while":
            case "step":
                return arr;
            case "sample":
                return e;
            case "flatten":
                return ElemOf(e) is { IsKnown: true } fe ? AType.Array(fe) : AType.Unknown;
            case "flat_map":
                return br.IsKnown && ElemOf(br) is { IsKnown: true } fb ? AType.Array(fb) : AType.Unknown;
            case "group_by":
                return e.IsKnown && br.IsKnown && !br.IsVoid ? Generic(typeof(Dictionary<,>), br, AType.Array(e)) : AType.Unknown;
            case "tally":
                return e.IsKnown ? Generic(typeof(Dictionary<,>), e, AType.Int) : AType.Unknown;
            case "inject":
                return args.Count > 0 ? args[0] : e;
            case "each_with_object":
                return args.Count > 0 ? args[0] : AType.Unknown;
            case "find_index":
            case "rindex":
            case "hex":
            case "oct":
                return AType.Int;
            case "index" when recv.IsString:
            case "count" when recv.IsString:
                return AType.Int;
            case "gcd":
            case "lcm":
            case "clamp":
                return recv;
            case "digits":
            case "bytes":
                return AType.Array(AType.Int);
            case "divmod":
                return recv.IsKnown ? AType.Array(recv) : AType.Unknown;
            case "scan":
                return AType.Array(AType.String);
            case "succ" when recv.IsString:
            case "center":
            case "chomp":
            case "chop":
            case "squeeze":
            case "sub":
            case "tr":
            case "delete":
            case "repeat":
            case "to_sym":
            case "swapcase":
            case "title":
            case "to_s":
                return AType.String;
            case "round" when args.Count == 1:
            case "floor" when args.Count == 1:
            case "ceil" when args.Count == 1:
            case "cbrt":
            case "log":
            case "log2":
            case "log10":
            case "truncate":
                return AType.Double;
            case "zip" when e.IsKnown && args.Count == 1 && ElemOf(args[0]) is { IsKnown: true } ze:
                return AType.Array(Generic(typeof(ValueTuple<,>), e, ze));
            case "to_h" when e.Args.Count == 2:
                return Generic(typeof(Dictionary<,>), e.Args[0], e.Args[1]);
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
