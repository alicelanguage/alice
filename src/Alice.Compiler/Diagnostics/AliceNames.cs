using System.Text;

namespace Alice.Compiler.Diagnostics;

/// <summary>Spells C# names the Alice way in messages: int -> Int32, List&lt;T&gt; -> List(T), T[] -> Array(T), Func -> A -> R.</summary>
public static class AliceNames
{
    static readonly Dictionary<string, string> Prims = new()
    {
        ["int"] = "Int32",
        ["long"] = "Int64",
        ["short"] = "Int16",
        ["sbyte"] = "Int8",
        ["byte"] = "UInt8",
        ["uint"] = "UInt32",
        ["ulong"] = "UInt64",
        ["ushort"] = "UInt16",
        ["double"] = "Float64",
        ["float"] = "Float32",
        ["bool"] = "Bool",
        ["string"] = "String",
        ["char"] = "Char",
        ["object"] = "Object",
        ["void"] = "Nil",
        ["decimal"] = "Decimal",
        ["dynamic"] = "Object",
    };

    public static string Type(string cs)
    {
        int p = 0;
        try { return Parse(cs.Trim(), ref p); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) { return cs.Replace("global::", ""); }
    }

    static string Parse(string s, ref int p)
    {
        Skip(s, ref p);
        string t;
        if (p < s.Length && s[p] == '(')
        {
            p++;
            var items = new List<string>();
            while (true)
            {
                items.Add(Parse(s, ref p));
                Skip(s, ref p);
                if (p < s.Length && s[p] == ' ') p++;
                while (p < s.Length && char.IsLetterOrDigit(s[p])) p++;   // tuple element name
                Skip(s, ref p);
                if (s[p] == ',') { p++; continue; }
                p++;   // ')'
                break;
            }
            t = "{" + string.Join(", ", items) + "}";
        }
        else
        {
            int start = p;
            while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] is '_' or '.' or ':' or '@')) p++;
            string name = s[start..p].Replace("global::", "");
            if (name.LastIndexOf('.') is int dot and >= 0) name = name[(dot + 1)..];
            name = name.TrimStart('@');
            if (p < s.Length && s[p] == '<')
            {
                p++;
                var args = new List<string>();
                while (true)
                {
                    args.Add(Parse(s, ref p));
                    Skip(s, ref p);
                    if (s[p] == ',') { p++; continue; }
                    p++;   // '>'
                    break;
                }
                t = name switch
                {
                    "Func" => (args.Count == 1 ? "->" : string.Join(", ", args.Take(args.Count - 1)) + " ->") + " " + args[^1],
                    "Action" => string.Join(", ", args) + " -> Nil",
                    "Nullable" => args[0] + "?",
                    "ValueTuple" or "Tuple" => "{" + string.Join(", ", args) + "}",
                    _ => name + "(" + string.Join(", ", args) + ")",
                };
            }
            else t = Prims.TryGetValue(name, out var prim) ? prim : name;
        }
        while (p < s.Length)
        {
            if (s[p] == '?') { t += "?"; p++; }
            else if (p + 1 < s.Length && s[p] == '[' && s[p + 1] == ']') { t = $"Array({t})"; p += 2; }
            else if (s[p] == '*') { t = $"Pointer({t})"; p++; }
            else break;
        }
        return t;
    }

    static void Skip(string s, ref int p) { while (p < s.Length && s[p] == ' ') p++; }

    /// <summary>Generated member name back to Alice: respond_to_q -> respond_to?, save_bang -> save!.</summary>
    public static string Member(string cs)
    {
        cs = cs.TrimStart('@');
        if (cs.EndsWith("_q")) return cs[..^2] + "?";
        if (cs.EndsWith("_bang")) return cs[..^5] + "!";
        if (cs.StartsWith("__managed_")) return cs["__managed_".Length..];
        return cs;
    }

    /// <summary>Every 'quoted' C# type/member in a message, re-spelled.</summary>
    public static string Quoted(string message)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < message.Length)
        {
            int a = message.IndexOf('\'', i);
            if (a < 0) { sb.Append(message[i..]); break; }
            int b = message.IndexOf('\'', a + 1);
            if (b < 0) { sb.Append(message[i..]); break; }
            sb.Append(message[i..a]).Append('\'').Append(Type(message[(a + 1)..b])).Append('\'');
            i = b + 1;
        }
        return sb.ToString();
    }
}
