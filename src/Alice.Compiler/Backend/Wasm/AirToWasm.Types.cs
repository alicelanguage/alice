using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Backend.Wasm;

// Binder types, AIR types and C# type text (MDecl.TypeCs, MNew.TypeCs, MCast...) to wasm-level types.
public sealed partial class AirToWasm
{
    internal WT FromAType(AType t, Span? sp) => FromAir(t.ToAir(), sp);

    internal WT FromAir(AirType t, Span? sp)
    {
        switch (t)
        {
            case AirType.Prim p:
                return p.Kind switch
                {
                    PrimKind.Void => WT.Void,
                    PrimKind.Bool => Nn(WT.Bool, t, sp),
                    PrimKind.Char => Nn(WT.Char, t, sp),
                    PrimKind.SByte => Nn(WT.I8, t, sp),
                    PrimKind.Byte => Nn(WT.U8, t, sp),
                    PrimKind.Short => Nn(WT.I16, t, sp),
                    PrimKind.UShort => Nn(WT.U16, t, sp),
                    PrimKind.Int => Nn(WT.I32, t, sp),
                    PrimKind.UInt => Nn(WT.U32, t, sp),
                    PrimKind.Long => Nn(WT.I64, t, sp),
                    PrimKind.ULong => Nn(WT.U64, t, sp),
                    PrimKind.Float => Nn(WT.F32, t, sp),
                    PrimKind.Double => Nn(WT.F64, t, sp),
                    PrimKind.String => WT.Str,
                    PrimKind.Null => WT.Null,
                    PrimKind.Object => RuntimeObject(sp),
                    _ => throw new WasmUnsupportedException("Type:" + p.Kind + " (runtime = \"dotnet\")", sp),
                };
            case AirType.Array a:
                if (a.Rank != 1) throw new WasmUnsupportedException("Type:multi-dimensional-array", sp, t.ToCs());
                return WT.ArrayOf(FromAir(a.Elem, sp));
            case AirType.AliceType at:
                if (at.Args.Count > 0) throw new WasmUnsupportedException("Type:generic", sp, t.ToCs());
                if (bySym.TryGetValue(at.Sym, out var c)) return Nn(c.Type, t, sp);
                throw new WasmUnsupportedException("Type:alice", sp, t.ToCs());
            case AirType.ClrRef cr:
                return Clr(cr.Name, cr.Args.Select(x => FromAir(x, sp)).ToList(), t, sp);
            default:
                throw new WasmUnsupportedException("Type:" + t.GetType().Name, sp, t.ToCs());
        }
    }

    /// <summary>`T?` of a value type is not modeled (no Nullable&lt;T&gt; layout yet).</summary>
    static WT Nn(WT w, AirType t, Span? sp) => t.Nullable && !w.IsRef ? throw new WasmUnsupportedException("Type:nullable-value", sp, t.ToCs()) : w;

    WT RuntimeObject(Span? sp) => throw new WasmUnsupportedException("Type:object (boxing)", sp);

    WT Clr(string name, List<WT> args, AirType? t, Span? sp)
    {
        if (name.StartsWith("global::")) name = name[8..];
        switch (name)
        {
            case "System.Func" when args.Count >= 1: return WT.FuncOf(args.Take(args.Count - 1).ToArray(), args[^1]);
            case "System.Action": return WT.FuncOf(args.ToArray(), WT.Void);
            case "System.Collections.Generic.List" when args.Count == 1: return WT.ListOf(args[0]);
            case "System.Collections.Generic.Dictionary" when args.Count == 2: return DictKey(args[0], sp) is var k ? WT.DictOf(k, args[1]) : null!;
            case "System.Span" or "System.ReadOnlySpan" when args.Count == 1: return WT.ArrayOf(args[0]);
            case "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler" or "System.Text.StringBuilder": return WT.Builder;
            case "System.Diagnostics.Stopwatch": return WT.Stopwatch;
            case "System.TimeSpan": return WT.TimeSpan;
            case "System.String": return WT.Str;
            case "System.Int32": return WT.I32;
            case "System.Int64": return WT.I64;
            case "System.UInt32": return WT.U32;
            case "System.UInt64": return WT.U64;
            case "System.Double": return WT.F64;
            case "System.Single": return WT.F32;
            case "System.Boolean": return WT.Bool;
            case "System.Char": return WT.Char;
            case "System.Byte": return WT.U8;
        }
        if (args.Count == 0 && byCs.TryGetValue(name, out var c)) return c.Type;
        throw new WasmUnsupportedException("Type:.NET " + name + " (runtime = \"dotnet\")", sp, t?.ToCs() ?? name);
    }

    /// <summary>Dictionary keys hash by value: strings, numbers, chars, booleans, enums.</summary>
    static WT DictKey(WT k, Span? sp) => k.K is WK.Str || k.IsInt || k.IsFloat || k.K is WK.Bool or WK.Char ? k : throw new WasmUnsupportedException("Type:Dictionary-key", sp, k.Name);

    static readonly Dictionary<string, WT> Keywords = new(StringComparer.Ordinal)
    {
        ["void"] = WT.Void, ["bool"] = WT.Bool, ["char"] = WT.Char, ["sbyte"] = WT.I8, ["byte"] = WT.U8, ["short"] = WT.I16,
        ["ushort"] = WT.U16, ["int"] = WT.I32, ["uint"] = WT.U32, ["long"] = WT.I64, ["ulong"] = WT.U64, ["float"] = WT.F32,
        ["double"] = WT.F64, ["string"] = WT.Str,
    };

    /// <summary>C# type text as the lowering spells it: `int`, `uint[]`, `Shape.Circle`, `global::System.Exception`,
    /// `global::System.Collections.Generic.List&lt;int&gt;`.</summary>
    internal WT Parse(string cs, Span? sp)
    {
        cs = cs.Trim();
        if (cs.EndsWith('?'))
        {
            var inner = Parse(cs[..^1], sp);
            return inner.IsRef ? inner : throw new WasmUnsupportedException("Type:nullable-value", sp, cs);
        }
        if (cs.EndsWith("[]")) return WT.ArrayOf(Parse(cs[..^2], sp));
        if (Keywords.TryGetValue(cs, out var k)) return k;
        if (cs == "object") return RuntimeObject(sp);
        if (byCs.TryGetValue(cs, out var c)) return c.Type;
        string bare = cs.StartsWith("global::") ? cs[8..] : cs;
        if (byCs.TryGetValue(bare, out c)) return c.Type;
        if (bare.EndsWith(">.Enumerator") && Parse(bare[..^".Enumerator".Length], sp) is { K: WK.List } listT) return ListEnumerator(listT.Elem!).Type;
        int lt = bare.IndexOf('<');
        if (lt > 0 && bare.EndsWith('>'))
        {
            var args = SplitTop(bare[(lt + 1)..^1]).Select(a => Parse(a, sp)).ToList();
            return Clr(bare[..lt], args, null, sp);
        }
        if (bare.StartsWith('(')) throw new WasmUnsupportedException("Type:tuple", sp, cs);
        return Clr(bare, [], null, sp);
    }

    static List<string> SplitTop(string s)
    {
        var r = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            else if (ch == ',' && depth == 0) { r.Add(s[start..i].Trim()); start = i + 1; }
        }
        r.Add(s[start..].Trim());
        return r;
    }

    /// <summary>The class a type-reference text names (static calls, enum members), or null.</summary>
    internal WClass? ClassNamed(string cs)
    {
        if (byCs.TryGetValue(cs, out var c)) return c;
        if (cs.StartsWith("global::") && byCs.TryGetValue(cs[8..], out c)) return c;
        return null;
    }
}
