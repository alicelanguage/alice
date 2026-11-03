using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>A JSON call the compiler maps without reflection (ADR 0036, C2.5): the lowerer emits
/// <c>__AliceJson</c> readers/writers for <see cref="Type"/> and calls them through <c>JsonMap</c>.</summary>
public sealed class JsonMapCall
{
    public bool Read;            // JSON.parse(T, s) / T.from_json(s); else to_json / generate
    public bool Indented;        // JSON.pretty_generate
    public AType Type = AType.Unknown;
    public Expr Arg = null!;     // the text (read) or the value (write)
}

public sealed partial class Binder
{
    /// <summary>Binds a call, then recognises the typed JSON forms (`JSON.parse(Point, s)`, `JSON.parse<Array(Point)>(s)`,
    /// `Point.from_json(s)`, `to_json(p)`, `p.to_json`, `JSON.generate(p)`, `JSON.pretty_generate(p)`) when the
    /// type involves an Alice record, class or enum. Other `to_json` calls keep the Prelude (Ruby-shape) path.</summary>
    AType BindCall(CallExpr c)
    {
        var t = BindCallUnmapped(c);
        return JsonForm(c) is { } j ? MapJson(c, j) : t;
    }

    JsonMapCall? JsonForm(CallExpr c)
    {
        bool onJson = c.Recv is ConstExpr { Res: "type" } rc && rc.Type.Clr?.FullName == "Alice.Runtime.JSON"
                      || c.Recv?.Type.Clr?.FullName == "Alice.Runtime.JSON";
        var a = c.Args.Select(x => x.Value).ToList();
        if (a.Any(x => x == null) || c.Block != null || c.Args.Any(x => x.Name != null)) return null;
        if (onJson && c.Name == "parse")
        {
            if (c.TypeArgs.Count == 1 && a.Count == 1) return new JsonMapCall { Read = true, Type = ResolveType(c.TypeArgs[0]), Arg = a[0]! };
            if (a.Count == 2 && a[0] is ConstExpr { Res: "type" } ce) return new JsonMapCall { Read = true, Type = ResolveType(ce.Ref), Arg = a[1]! };
            return null;
        }
        if (onJson && c.Name is "generate" or "pretty_generate" && a.Count == 1)
            return new JsonMapCall { Indented = c.Name == "pretty_generate", Type = a[0]!.Type, Arg = a[0]! };
        if (c.Name == "from_json" && a.Count == 1 && c.Recv is ConstExpr { Res: "type" } tc && tc.Type.Sym is { } ts && ts.FindMethod("from_json", 1) == null)
            return new JsonMapCall { Read = true, Type = ResolveType(tc.Ref), Arg = a[0]! };
        if (c.Name == "to_json" && c.Recv == null && a.Count == 1) return new JsonMapCall { Type = a[0]!.Type, Arg = a[0]! };
        if (c.Name == "to_json" && a.Count == 0 && c.Recv is { } r && r is not ConstExpr { Res: "type" } && r.Type.Sym is { } rs && rs.FindMethod("to_json", 0) == null)
            return new JsonMapCall { Type = r.Type, Arg = r };
        return null;
    }

    AType MapJson(CallExpr c, JsonMapCall j)
    {
        if (!JsonInvolvesAlice(j.Type, new HashSet<string>()))
        {
            if (j.Read) throw Err(c, $"JSON.parse({j.Type.Cs}, ...): typed parsing needs an Alice record, class or enum (use JSON.parse(text) for JSON::Any)");
            return c.Type;   // plain values: the Prelude writer (Ruby shape)
        }
        if (JsonUnsupported(j.Type, new HashSet<string>()) is string why) throw Err(c, $"cannot map {j.Type.Cs} to JSON: {why}");
        c.Res = "json";
        c.Sym = j;
        return j.Read ? j.Type : AType.String;
    }

    public static bool IsAliceJsonType(AType t) => t.Sym is { } s && t.Clr == null && s.ExtTarget == null
        && s.Kind is "class" or "struct" or "record" or "record struct" or "enum" or "adt" or "variant";

    static bool JsonInvolvesAlice(AType t, HashSet<string> seen)
    {
        if (!seen.Add(t.Cs)) return false;
        if (IsAliceJsonType(t)) return true;
        if (t.Elem is { } e && JsonInvolvesAlice(e, seen)) return true;
        return t.Args.Any(x => JsonInvolvesAlice(x, seen));
    }

    /// <summary>Why <paramref name="t"/> cannot be mapped (null: it can). Unsupported shapes are compile errors.</summary>
    static string? JsonUnsupported(AType t, HashSet<string> seen)
    {
        var u = t.Nullable ? JsonUnderlying(t) : t;
        if (!seen.Add(u.Cs)) return null;   // recursive types are fine
        if (u.Kind == "prim") return u.Cs is "bool" or "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" ? null : $"{u.Cs} has no JSON mapping";
        if (u.Cs == "string") return null;
        if (u.Kind == "array" && u.Elem is { } e) return JsonUnsupported(e, seen);
        if (u.Clr is { IsGenericType: true } g || u.OpenClr != null)
        {
            var def = u.OpenClr ?? u.Clr!.GetGenericTypeDefinition();
            if (def == typeof(List<>) && u.Args.Count == 1) return JsonUnsupported(u.Args[0], seen);
            if (def == typeof(Dictionary<,>) && u.Args.Count == 2)
                return u.Args[0].Cs != "string" ? "Hash keys must be String" : JsonUnsupported(u.Args[1], seen);
        }
        if (u.Sym is { } s && IsAliceJsonType(u))
        {
            if (s.TypeParams.Count > 0) return $"generic type {s.Name} (map a concrete wrapper instead)";
            if (s.Kind == "enum") return null;   // by name
            if (s.Kind == "adt")
            {
                foreach (var v in s.Variants)
                {
                    if (v.FieldOrder.Contains("type")) return $"variant {v.Name} has a field named `type` (the tag)";
                    foreach (var f in v.FieldOrder)
                        if (v.FindField(f) is { } fs && JsonUnsupported(fs.Type, seen) is string w) return $"{v.Name}.{f}: {w}";
                }
                return null;
            }
            foreach (var f in JsonFields(s))
                if (JsonUnsupported(f.Type, seen) is string w) return $"{s.Name}.{f.Name}: {w}";
            return null;
        }
        return $"{u.Cs} has no JSON mapping";
    }

    public static AType JsonUnderlying(AType t) => t.Nullable && t.Cs.EndsWith('?') ? new AType(t.Kind, t.Cs[..^1], t.Clr) { Sym = t.Sym, Args = t.Args, Elem = t.Elem, OpenClr = t.OpenClr } : t;

    /// <summary>The fields a record/class maps, in declaration order: positional fields of a record, else its public
    /// instance properties.</summary>
    public static List<FieldSym> JsonFields(TypeSym s)
    {
        if (s.FieldOrder.Count > 0) return s.FieldOrder.Select(n => s.FindField(n)).OfType<FieldSym>().ToList();
        return s.Fields.Values.Where(f => !f.IsStatic && f.Kind is "property" or "getter").ToList();
    }
}
