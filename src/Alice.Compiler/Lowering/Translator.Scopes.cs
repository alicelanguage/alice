using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
    void PushScope() => scopes.Add(new Dictionary<string, string>());
    void PopScope() => scopes.RemoveAt(scopes.Count - 1);

    string Declare(string name)
    {
        string cs = Mangle(name);
        if (scopes.Count > 1)
        {
            tmp++;
            cs = cs.TrimStart('@') + "_" + tmp;
        }
        scopes[scopes.Count - 1][name] = cs;
        return cs;
    }

    bool IsLocal(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].ContainsKey(name)) return true;
        return false;
    }

    string Local(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].ContainsKey(name)) return scopes[i][name];
        return name;
    }
}
