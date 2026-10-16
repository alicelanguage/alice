using System.Globalization;
using System.Text;

namespace Alice.Cli.Projects;

/// <summary>
/// The TOML subset alice.toml needs: [tables], bare/"quoted" keys, strings ("..." and '...'), integers,
/// floats, booleans, arrays and inline tables. Errors name the line.
/// </summary>
public static class TomlLite
{
    public static Dictionary<string, Dictionary<string, object>> Parse(string text)
    {
        var root = new Dictionary<string, Dictionary<string, object>> { [""] = new() };
        var table = root[""];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            int lineNo = n + 1;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new FormatException($"alice.toml line {lineNo}: unterminated table header");
                string name = line[1..^1].Trim();
                if (!root.TryGetValue(name, out table!)) root[name] = table = new Dictionary<string, object>();
                continue;
            }
            int i = 0;
            string key = Key(line, ref i, lineNo);
            Skip(line, ref i);
            if (i >= line.Length || line[i] != '=') throw new FormatException($"alice.toml line {lineNo}: expected '=' after key");
            i++;
            Skip(line, ref i);
            if (i >= line.Length) throw new FormatException($"alice.toml line {lineNo}: missing value for '{key}'");
            table[key] = Value(line, ref i, lineNo);
        }
        return root;
    }

    static string StripComment(string line)
    {
        bool inStr = false;
        char q = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr) { if (c == '\\' && q == '"') i++; else if (c == q) inStr = false; }
            else if (c is '"' or '\'') { inStr = true; q = c; }
            else if (c == '#') return line[..i];
        }
        return line;
    }

    static void Skip(string s, ref int i) { while (i < s.Length && s[i] is ' ' or '\t') i++; }

    static string Key(string s, ref int i, int line)
    {
        Skip(s, ref i);
        if (i < s.Length && s[i] is '"' or '\'') return (string)Str(s, ref i, line);
        int st = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-' or '.')) i++;
        if (i == st) throw new FormatException($"alice.toml line {line}: expected a key");
        return s[st..i];
    }

    static object Value(string s, ref int i, int line)
    {
        Skip(s, ref i);
        char c = s[i];
        if (c is '"' or '\'') return Str(s, ref i, line);
        if (c == '{')
        {
            i++;
            var t = new Dictionary<string, object>();
            while (true)
            {
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return t; }
                string k = Key(s, ref i, line);
                Skip(s, ref i);
                if (i >= s.Length || s[i] != '=') throw new FormatException($"alice.toml line {line}: expected '=' in inline table");
                i++;
                t[k] = Value(s, ref i, line);
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
                else if (i >= s.Length) throw new FormatException($"alice.toml line {line}: unterminated inline table");
            }
        }
        if (c == '[')
        {
            i++;
            var list = new List<object>();
            while (true)
            {
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                list.Add(Value(s, ref i, line));
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
                else if (i >= s.Length) throw new FormatException($"alice.toml line {line}: unterminated array");
            }
        }
        int st = i;
        while (i < s.Length && s[i] is not (',' or '}' or ']' or ' ' or '\t')) i++;
        string tok = s[st..i];
        if (tok == "true") return true;
        if (tok == "false") return false;
        if (long.TryParse(tok.Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(tok.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new FormatException($"alice.toml line {line}: invalid value '{tok}' (strings need quotes)");
    }

    static object Str(string s, ref int i, int line)
    {
        char q = s[i++];
        var sb = new StringBuilder();
        while (i < s.Length && s[i] != q)
        {
            if (q == '"' && s[i] == '\\' && i + 1 < s.Length)
            {
                char e = s[++i];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
            }
            else sb.Append(s[i]);
            i++;
        }
        if (i >= s.Length) throw new FormatException($"alice.toml line {line}: unterminated string");
        i++;
        return sb.ToString();
    }
}
