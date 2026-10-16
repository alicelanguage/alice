using System.Text;

namespace Alice.Compiler;

/// <summary>Cross-file facts used to resolve deferred markers after every file is parsed.</summary>
public sealed class Globals
{
    public readonly HashSet<string> ZeroArg = new();
    public readonly HashSet<string> Props = new();
    public readonly Dictionary<string, string> BaseOf = new();
    public readonly Dictionary<string, HashSet<string>> Methods = new();

    const string PreludeNames = "puts print p gets size length count first last empty_q any_q sum max min sort reverse uniq to_a to_list to_i to_f to_i64 to_f32 chars upcase downcase capitalize strip lines abs even_q odd_q zero_q ord chr keys values join succ pred floor ceil round sqrt inspect rand exit pop receive close stop wait";

    public Globals()
    {
        foreach (var n in PreludeNames.Split(' ')) ZeroArg.Add(n);
    }

    public void AddMethod(string cls, string name)
    {
        if (!Methods.ContainsKey(cls)) Methods[cls] = new HashSet<string>();
        Methods[cls].Add(name);
    }

    bool AncestorHas(string cls, string name)
    {
        string b = BaseOf.ContainsKey(cls) ? BaseOf[cls] : "";
        int guard = 0;
        while (b != "" && guard < 64)
        {
            if (Methods.ContainsKey(b) && Methods[b].Contains(name)) return true;
            b = BaseOf.ContainsKey(b) ? BaseOf[b] : "";
            guard++;
        }
        return false;
    }

    bool HasSubclass(string cls)
    {
        foreach (var kv in BaseOf) if (kv.Value == cls) return true;
        return false;
    }

    /// <summary>Replaces \u0001M:name\u0001 (member-or-call) and \u0001V:cls:name\u0001 (virtual/override) markers.</summary>
    public string Resolve(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int s = text.IndexOf('\u0001', i);
            if (s < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, s - i);
            int e = text.IndexOf('\u0001', s + 1);
            string body = text.Substring(s + 1, e - s - 1);
            if (body.StartsWith("M:"))
            {
                string n = body.Substring(2);
                string bare = n.StartsWith("@") ? n.Substring(1) : n;
                sb.Append(n);
                if (ZeroArg.Contains(bare) && !Props.Contains(bare)) sb.Append("()");
            }
            else
            {
                int c = body.IndexOf(':', 2);
                string cls = body.Substring(2, c - 2);
                string n = body.Substring(c + 1);
                if (AncestorHas(cls, n)) sb.Append("override ");
                else if (HasSubclass(cls)) sb.Append("virtual ");
            }
            i = e + 1;
        }
        return sb.ToString();
    }
}
