using System.Globalization;
using System.Text;

namespace Alice.Core.Toml;

/// <summary>TOML text output: documents (<see cref="Toml.Write"/>) and inline values.</summary>
static class TomlWriter
{
    public static string Write(TomlTable table)
    {
        var sb = new StringBuilder();
        Body(sb, table, "");
        return sb.ToString();
    }

    static void Body(StringBuilder sb, TomlTable t, string path)
    {
        // plain key/values (including inline arrays and tables that are not sub-sections) first
        foreach (var (k, v) in t)
        {
            if (IsSection(v)) continue;
            sb.Append(Key(k)).Append(" = ");
            Value(sb, v);
            sb.Append('\n');
        }
        foreach (var (k, v) in t)
        {
            if (!IsSection(v)) continue;
            string sub = path.Length == 0 ? Key(k) : path + "." + Key(k);
            if (v.Obj is TomlTable st)
            {
                bool hasPlain = st.Values.Any(x => !IsSection(x));
                if (hasPlain || st.Count == 0)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append('[').Append(sub).Append("]\n");
                }
                Body(sb, st, sub);
            }
            else
            {
                foreach (var e in (TomlArray)v.Obj!)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("[[").Append(sub).Append("]]\n");
                    Body(sb, (TomlTable)e.Obj!, sub);
                }
            }
        }
    }

    static bool IsSection(TomlAny v) => v.Obj switch
    {
        TomlTable t => !t.Inline,
        TomlArray a => a.Count > 0 && a.All(x => x.Kind == TomlKind.Table && !((TomlTable)x.Obj!).Inline),
        _ => false,
    };

    public static string Inline(TomlAny v)
    {
        var sb = new StringBuilder();
        Value(sb, v);
        return sb.ToString();
    }

    static void Value(StringBuilder sb, TomlAny v)
    {
        switch (v.Kind)
        {
            case TomlKind.String: Str(sb, (string)v.Obj!); break;
            case TomlKind.Integer: sb.Append(v.Bits.ToString(CultureInfo.InvariantCulture)); break;
            case TomlKind.Boolean: sb.Append(v.Bits != 0 ? "true" : "false"); break;
            case TomlKind.Float:
                {
                    double d = v.GetDouble();
                    if (double.IsNaN(d)) sb.Append("nan");
                    else if (double.IsInfinity(d)) sb.Append(d > 0 ? "inf" : "-inf");
                    else
                    {
                        string r = d.ToString("R", CultureInfo.InvariantCulture);
                        sb.Append(r);
                        if (r.AsSpan().IndexOfAny('.', 'E', 'e') < 0) sb.Append(".0");
                    }
                    break;
                }
            case TomlKind.Array:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var e in (TomlArray)v.Obj!)
                    {
                        if (!first) sb.Append(", ");
                        Value(sb, e);
                        first = false;
                    }
                    sb.Append(']');
                    break;
                }
            case TomlKind.Table:
                {
                    var t = (TomlTable)v.Obj!;
                    if (t.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{ ");
                    bool first = true;
                    foreach (var (k, e) in t)
                    {
                        if (!first) sb.Append(", ");
                        sb.Append(Key(k)).Append(" = ");
                        Value(sb, e);
                        first = false;
                    }
                    sb.Append(" }");
                    break;
                }
            case TomlKind.None: throw new InvalidOperationException("cannot write a TOML value of kind None");
            default: sb.Append(TomlText.Rfc3339(v)); break;
        }
    }

    static string Key(string k)
    {
        if (k.Length > 0 && k.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')) return k;
        var sb = new StringBuilder();
        Str(sb, k);
        return sb.ToString();
    }

    static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var r in s.EnumerateRunes())
        {
            int c = r.Value;
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append("\\u").Append(c.ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(r.ToString());
                    break;
            }
        }
        sb.Append('"');
    }
}
