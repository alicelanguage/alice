using System.Text;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Typed JSON mapping (ADR 0036, C2.5). A <see cref="JsonMapCall"/> lowers to <c>JsonMap.Parse&lt;T&gt;(s, R)</c> or
/// <c>JsonMap.ToJson&lt;T&gt;(v, W)</c>; R and W are static methods of <c>__AliceJson</c>, emitted once per type and program
/// (<see cref="FlushJsonMappings"/>, after each file): on-demand <c>JsonIterator</c> readers that compare field names
/// as <c>"name"u8</c> and skip unknown fields, <c>JsonWriter</c> writers. No reflection. Shapes: records and variants by
/// their positional fields, classes by public properties (built through the `initialize` whose parameters are all
/// properties, else a parameterless one), sum types as <c>{"type":"Variant", ...fields}</c>, plain enums by member
/// name, nil-able values as null, Array / List / Hash(String, V). A missing non-nil-able field raises
/// <c>JsonParseException</c>; nil-able fields default to nil.
/// </summary>
public sealed partial class Lowerer
{
    const string JM = "global::Alice.Core.Json.JsonMap";
    const string JIt = "ref global::Alice.Core.Json.JsonIterator r";
    const string JWr = "ref global::Alice.Core.Json.JsonWriter w";

    readonly Dictionary<string, string> jsonIds = new();
    readonly List<AType> jsonPending = new();
    int jsonN;

    MExpr JsonCall(CallExpr c)
    {
        var j = (JsonMapCall)c.Sym!;
        var arg = Expr(j.Arg);
        string id = JsonId(j.Type);
        var map = AirTypeRef.Named(JM);
        if (j.Read) return At(new MCall { Target = map, Name = $"Parse<{j.Type.Cs}>", Args = { arg, JsonMember("R" + id) } }, c);
        var call = new MCall { Target = map, Name = $"ToJson<{j.Type.Cs}>", Args = { arg, JsonMember("W" + id) } };
        if (j.Indented) call.Args.Add(AirConst.Bool(true));
        return At(call, c);
    }

    /// <summary>A generated reader/writer as a method group (converted to JsonRead/JsonWrite by C#).</summary>
    static MMember JsonMember(string name) => new() { Target = AirTypeRef.Named("global::__AliceJson"), Name = name };

    string JsonId(AType t)
    {
        if (jsonIds.TryGetValue(t.Cs, out var id)) return id;
        var sb = new StringBuilder().Append(jsonN++).Append('_');
        foreach (char ch in t.Cs.Replace("global::", "")) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        id = sb.ToString();
        jsonIds[t.Cs] = id;
        jsonPending.Add(t);
        return id;
    }

    /// <summary>The <c>__AliceJson</c> members requested since the last flush (empty when none).</summary>
    public string FlushJsonMappings()
    {
        if (jsonPending.Count == 0) return "";
        var sb = new StringBuilder("internal static partial class __AliceJson\n{\n");
        while (jsonPending.Count > 0)
        {
            var t = jsonPending[0];
            jsonPending.RemoveAt(0);
            JsonEmit(sb, t, jsonIds[t.Cs]);
        }
        return sb.Append("}\n").ToString();
    }

    static bool JsonIsValue(AType t) => t.Kind is "prim" or "struct" or "enum" || t.Sym?.Kind is "struct" or "record struct" or "enum";

    static Type? JsonGeneric(AType t) => t.OpenClr ?? (t.Clr is { IsGenericType: true } g ? g.GetGenericTypeDefinition() : null);

    void JsonEmit(StringBuilder sb, AType t, string id)
    {
        string T = t.Cs;
        void Pair(string read, string write)
        {
            sb.Append($"internal static {T} R{id}({JIt}) => {read};\n");
            sb.Append($"internal static void W{id}({JWr}, {T} v) {{ {write} }}\n");
        }
        if (t.Nullable)
        {
            var u = Binder.JsonUnderlying(t);
            string uid = JsonId(u);
            Pair($"r.TryReadNull() ? default({T}) : R{uid}(ref r)",
                JsonIsValue(u) ? $"if (v is {{ }} x) W{uid}(ref w, x); else w.WriteNull();" : $"if (v is null) w.WriteNull(); else W{uid}(ref w, v);");
            return;
        }
        switch (T)
        {
            case "bool": Pair("r.ReadBoolean()", "w.WriteBoolean(v);"); return;
            case "int": Pair($"{JM}.ReadInt32(ref r)", "w.WriteNumber((long)v);"); return;
            case "long": Pair("r.ReadInt64()", "w.WriteNumber(v);"); return;
            case "uint": Pair($"{JM}.ReadUInt32(ref r)", "w.WriteNumber((ulong)v);"); return;
            case "ulong": Pair("r.ReadUInt64()", "w.WriteNumber(v);"); return;
            case "sbyte" or "byte" or "short" or "ushort": Pair($"checked(({T})r.ReadInt64())", "w.WriteNumber((long)v);"); return;
            case "double": Pair("r.ReadDouble()", "w.WriteNumber(v);"); return;
            case "float": Pair("(float)r.ReadDouble()", "w.WriteNumber((double)v);"); return;
            case "string": Pair("r.ReadString()", "if (v is null) w.WriteNull(); else w.WriteString(v.AsSpan());"); return;
        }
        // collections: the loops are written out (direct calls the JIT can inline, no delegate per element)
        if (t.Kind == "array" && t.Elem is { } e)
        {
            string eid = JsonId(e);
            int b = e.Cs.IndexOf('[');
            string alloc = b < 0 ? $"new {e.Cs}[4]" : $"new {e.Cs[..b]}[4]{e.Cs[b..]}";
            sb.Append($"internal static {T} R{id}({JIt})\n{{\n    r.ReadStartArray();\n    if (!r.NextElement()) return [];\n")
              .Append($"    var a = {alloc}; int n = 0;\n")
              .Append($"    do {{ if (n == a.Length) global::System.Array.Resize(ref a, n * 2); a[n++] = R{eid}(ref r); }} while (r.NextElement());\n")
              .Append("    if (n != a.Length) global::System.Array.Resize(ref a, n);\n    return a;\n}\n");
            sb.Append($"internal static void W{id}({JWr}, {T} v)\n{{\n    if (v is null) {{ w.WriteNull(); return; }}\n")
              .Append($"    w.WriteStartArray();\n    for (int i = 0; i < v.Length; i++) W{eid}(ref w, v[i]);\n    w.WriteEndArray();\n}}\n");
            return;
        }
        if (JsonGeneric(t) is { } def && def == typeof(List<>))
        {
            string eid = JsonId(t.Args[0]);
            sb.Append($"internal static {T} R{id}({JIt})\n{{\n    var l = new {T}();\n    r.ReadStartArray();\n")
              .Append($"    while (r.NextElement()) l.Add(R{eid}(ref r));\n    return l;\n}}\n");
            sb.Append($"internal static void W{id}({JWr}, {T} v)\n{{\n    if (v is null) {{ w.WriteNull(); return; }}\n")
              .Append($"    w.WriteStartArray();\n    for (int i = 0; i < v.Count; i++) W{eid}(ref w, v[i]);\n    w.WriteEndArray();\n}}\n");
            return;
        }
        if (JsonGeneric(t) is { } hdef && hdef == typeof(Dictionary<,>))
        {
            string eid = JsonId(t.Args[1]);
            sb.Append($"internal static {T} R{id}({JIt})\n{{\n    var d = new {T}();\n    r.ReadStartObject();\n")
              .Append($"    while (r.NextProperty(out var k)) {{ string key = {JM}.Key(k); d[key] = R{eid}(ref r); }}\n    return d;\n}}\n");
            sb.Append($"internal static void W{id}({JWr}, {T} v)\n{{\n    if (v is null) {{ w.WriteNull(); return; }}\n")
              .Append($"    w.WriteStartObject();\n    foreach (var kv in v) {{ w.WritePropertyName(kv.Key.AsSpan()); W{eid}(ref w, kv.Value); }}\n    w.WriteEndObject();\n}}\n");
            return;
        }
        var s = t.Sym ?? throw new CompileException($"internal: no JSON mapping for {T}");
        if (s.Kind == "enum")
        {
            Pair($"{JM}.ReadEnum<{T}>(ref r)", $"{JM}.WriteEnum(ref w, v);");
            return;
        }
        if (s.Kind == "adt")
        {
            sb.Append($"internal static {T} R{id}({JIt})\n{{\n    var tag = {JM}.PeekTag(ref r, \"type\"u8);\n");
            foreach (var v in s.Variants) sb.Append($"    if (tag.SequenceEqual(\"{v.Name}\"u8)) return R{JsonId(v.Type)}(ref r);\n");
            sb.Append($"    throw new global::Alice.Core.Json.JsonParseException(\"no variant of {s.Name} in \\\"type\\\"\", r.Offset);\n}}\n");
            sb.Append($"internal static void W{id}({JWr}, {T} v)\n{{\n    switch (v)\n    {{\n");
            foreach (var v in s.Variants) sb.Append($"        case {v.Type.Cs} x: W{JsonId(v.Type)}(ref w, x); break;\n");
            sb.Append("        default: w.WriteNull(); break;\n    }\n}\n");
            return;
        }
        JsonObject(sb, t, s, id);
    }

    void JsonObject(StringBuilder sb, AType t, TypeSym s, string id)
    {
        string T = t.Cs;
        var fields = Binder.JsonFields(s);
        var fids = fields.Select(f => JsonId(f.Type)).ToList();
        bool isRef = !JsonIsValue(t);

        // writer
        sb.Append($"internal static void W{id}({JWr}, {T} v)\n{{\n");
        if (isRef) sb.Append("    if (v is null) { w.WriteNull(); return; }\n");
        sb.Append("    w.WriteStartObject();\n");
        if (s.Kind == "variant") sb.Append($"    {JM}.WriteTag(ref w, \"type\"u8, \"{s.Name}\"u8);\n");
        for (int i = 0; i < fields.Count; i++)
            sb.Append($"    w.WritePropertyName(\"{fields[i].Name}\"u8); W{fids[i]}(ref w, v.{fields[i].CsName});\n");
        sb.Append("    w.WriteEndObject();\n}\n");

        // construction: positional fields, else the matching `initialize`, else a parameterless constructor
        var used = new bool[fields.Count];
        string ctor;
        if (s.Kind == "variant" && fields.Count == 0) ctor = $"{T}.Value";
        else if (s.FieldOrder.Count > 0)
        {
            Array.Fill(used, true);
            ctor = $"new {T}({string.Join(", ", Enumerable.Range(0, fields.Count).Select(i => "f" + i))})";
        }
        else
        {
            var names = fields.Select(f => f.Name).ToList();
            var inits = s.Methods.TryGetValue("initialize", out var ms) ? ms : new List<MethodSym>();
            var pick = inits.Where(m => m.Params.All(p => !p.Splat && !p.Block && names.Contains(p.Name))).OrderByDescending(m => m.Params.Count).FirstOrDefault();
            if (pick == null && inits.Count > 0)
                throw new CompileException($"{s.Name}.from_json: no `initialize` whose parameters are all properties of {s.Name}");
            var args = new List<string>();
            foreach (var p in pick?.Params ?? new List<ParamSym>())
            {
                int k = names.IndexOf(p.Name);
                used[k] = true;
                args.Add("f" + k);
            }
            var rest = new List<string>();
            for (int i = 0; i < fields.Count; i++)
                if (!used[i] && fields[i].Kind == "property")
                {
                    used[i] = true;
                    rest.Add($"{fields[i].CsName} = f{i}");
                }
            ctor = $"new {T}({string.Join(", ", args)})" + (rest.Count > 0 ? " { " + string.Join(", ", rest) + " }" : "");
        }

        // reader
        sb.Append($"internal static {T} R{id}({JIt})\n{{\n    long at = r.Offset;\n");
        for (int i = 0; i < fields.Count; i++)
            if (used[i]) sb.Append($"    {fields[i].Type.Cs} f{i} = default!; bool h{i} = false;\n");
        sb.Append("    r.ReadStartObject();\n    while (r.NextProperty(out var n))\n    {\n        switch (n.Length)\n        {\n");
        for (int i = 0; i < fields.Count; i++)
        {
            if (!used[i]) continue;
            string key = fields[i].Name;
            sb.Append($"            case {Encoding.UTF8.GetByteCount(key)} when n.SequenceEqual(\"{key}\"u8): f{i} = R{fids[i]}(ref r); h{i} = true; break;\n");
        }
        sb.Append("            default: r.Skip(); break;\n        }\n    }\n");
        for (int i = 0; i < fields.Count; i++)
            if (used[i] && !fields[i].Type.Nullable)
                sb.Append($"    if (!h{i}) throw {JM}.MissingField(\"{fields[i].Name}\", \"{s.Name}\", at);\n");
        sb.Append($"    return {ctor};\n}}\n");
    }
}
