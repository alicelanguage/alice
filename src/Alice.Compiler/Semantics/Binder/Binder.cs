using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Binds the AST into HIR: resolves names against Alice symbols and .NET reflection, assigns types,
/// infers method return types to a fixpoint, hoists Ruby function-scoped locals, and enforces safety rules.
/// </summary>
public sealed partial class Binder
{
    public readonly List<FileNode> Files;
    public readonly ClrTypes Clr;
    public readonly Dictionary<string, TypeSym> Types = new();
    public readonly TypeSym Program = new() { Name = "Program", Kind = "program" };
    readonly HashSet<string> usings = new();

    // per-body state
    TypeSym curType = null!;
    MethodSym? curMethod;
    List<Dictionary<string, LocalSym>> scopes = new();
    Dictionary<string, LocalSym> closed = new();
    HashSet<string> hoist = new();
    List<LocalSym> hoisted = new();
    readonly List<LambdaExpr> lambdas = new();
    int spawnDepth;
    int tmp;
    List<AType> returns = new();
    List<string> typeParams = new();
    bool changed;
    string file = "";

    public Binder(List<FileNode> files, ClrTypes clr)
    {
        Files = files;
        Clr = clr;
        Program.Type = new AType("class", "Program") { Sym = Program };
    }

    public void Bind()
    {
        Collect();
        Declare();
        for (int pass = 0; pass < 8; pass++)
        {
            changed = false;
            BindAll();
            if (!changed && pass >= 1) break;
        }
        BindAll();
    }

    CompileException Err(Node n, string msg) => new($"{file}:{n.Line}: {msg}");

    public static string Mangle(string n)
    {
        if (n.EndsWith("?")) n = n[..^1] + "_q";
        else if (n.EndsWith("!")) n = n[..^1] + "_b";
        if (CsKeywords.Contains(" " + n + " ")) return "@" + n;
        return n;
    }

    const string CsKeywords = " abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while ";

    // ---- scopes
    void PushScope() => scopes.Add(new Dictionary<string, LocalSym>());

    void PopScope()
    {
        foreach (var kv in scopes[^1]) closed[kv.Key] = kv.Value;
        scopes.RemoveAt(scopes.Count - 1);
    }

    LocalSym? FindLocal(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(name, out var l)) return l;
        return null;
    }

    LocalSym Declare(Node at, string name, AType type, bool isVal = false)
    {
        if (type.Kind == "null") throw Err(at, $"cannot infer type of '{name}' from nil; declare it: {name} : Type? = nil");
        if (hoist.Contains(name) && scopes.Count > 1 && lambdas.Count == 0)
        {
            var h = hoisted.FirstOrDefault(x => x.Name == name);
            if (h == null)
            {
                h = new LocalSym { Name = name, CsName = Mangle(name), Type = type, IsVal = isVal, SpawnDepth = spawnDepth };
                hoisted.Add(h);
            }
            else if (!h.Type.IsKnown) h.Type = type;
            scopes[0][name] = h;
            return h;
        }
        string cs = Mangle(name);
        if (scopes.Count > 1) cs = cs.TrimStart('@') + "_" + ++tmp;
        var l = new LocalSym { Name = name, CsName = cs, Type = type, IsVal = isVal, LambdaDepth = lambdas.Count, SpawnDepth = spawnDepth };
        scopes[^1][name] = l;
        return l;
    }

    /// <summary>Records a read of a local; marks enclosing lambdas that capture it.</summary>
    void UseLocal(LocalSym l)
    {
        for (int i = l.LambdaDepth; i < lambdas.Count; i++) lambdas[i].Captures = true;
    }

    void UseThis()
    {
        foreach (var lam in lambdas) lam.Captures = true;
    }

    void BeginBody(TypeSym type, MethodSym? m, HashSet<string> hoistSet)
    {
        curType = type;
        curMethod = m;
        scopes = new List<Dictionary<string, LocalSym>>();
        closed = new Dictionary<string, LocalSym>();
        hoist = hoistSet;
        hoisted = new List<LocalSym>();
        returns = new List<AType>();
        spawnDepth = 0;
        tmp = 0;
        PushScope();
    }
}
