using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Sharing of constant immutable objects (hash-consing). Interaction nets duplicate and erase by sharing nodes;
/// the CPU-sized version is: `T.new(lit, ...)` of an immutable class whose identity the program never observes is
/// one `static readonly` instance. In a binary tree builder the leaves (`Node.new(nil, nil)`) are half of all
/// nodes, so half the allocations and half the objects the GC traces disappear.
/// <list type="bullet">
/// <item>T is a sealed plain class (no base, no subclass, no generics) whose constructor only stores its arguments
/// in fields, and no method or setter writes a field afterwards.</item>
/// <item>No code observes identity: no `equal?`/`object_id`/`same?`, no `==`/`!=` on T without a user `==`, no T
/// passed to .NET, `dynamic` or a generic container that may hash or lock it.</item>
/// </list>
/// </summary>
public sealed partial class Lowerer
{
    Dictionary<TypeSym, bool>? shareable;
    readonly Dictionary<(TypeSym Owner, string Key), string> sharedFields = new();
    int sharedN;

    static readonly HashSet<string> IdentityOps = ["equal?", "object_id", "same?", "__id__", "hash", "freeze", "frozen?", "instance_variable_set", "instance_variable_get", "send", "public_send", "__send__", "dup", "clone", "itself"];

    MExpr? TryShared(NewExpr ne)
    {
        if (ne.Block != null || ne.Type.Sym is not { Kind: "class" } t || !ne.Args.All(a => a.Mod == "" && a.Name == null && a.Value is LitExpr { Kind: not "regex" }))
            return null;
        if (!Shareable(t)) return null;
        string key = string.Join(",", ne.Args.Select(a => ((LitExpr)a.Value!).Kind + ":" + ((LitExpr)a.Value!).Text));
        if (!sharedFields.TryGetValue((Type, t.Name + "(" + key + ")"), out var field))
        {
            field = $"__shared_{t.Name}_{++sharedN}";
            sharedFields[(Type, t.Name + "(" + key + ")")] = field;
            var init = new MNew { TypeCs = ne.Type.Cs, Args = Args(ne.Args) };
            var hp = new Backend.Printer("", 0);
            hp.Root(new MExprStmt { E = init });
            Extra.Add($"internal static readonly {ne.Type.Cs} {field} = {hp.ToString().Trim().TrimEnd(';')};\n");
        }
        return new MCode(field);
    }

    bool Shareable(TypeSym t)
    {
        shareable ??= FindShareable();
        return shareable.TryGetValue(t, out var ok) && ok;
    }

    Dictionary<TypeSym, bool> FindShareable()
    {
        var result = new Dictionary<TypeSym, bool>();
        var classes = binder.Types.Values.Distinct().Where(Immutable).ToList();
        foreach (var c in classes) result[c] = true;
        if (classes.Count == 0) return result;
        foreach (var e in AllBodies().SelectMany(Walk))
        {
            switch (e)
            {
                case CallExpr c when IdentityOps.Contains(c.Name) && c.Recv?.Type.Sym is TypeSym r:
                    result[r] = false;
                    break;
                case BinaryExpr { Op: "==" or "!=" or "===" } b:
                    foreach (var side in new[] { b.L, b.R })
                        if (side.Type.Sym is TypeSym s && !s.Methods.ContainsKey("==")) result[s] = false;
                    break;
                case CallExpr { Res: "clr" or "dynamic" or "dyncall" or "unknown-member" } c:
                    foreach (var a in c.Args.Select(x => x.Value).Append(c.Recv).OfType<Syntax.Expr>())
                        if (a.Type.Sym is TypeSym s) result[s] = false;
                    break;
            }
            // T inside a generic container or held as Object/dynamic: identity may be hashed or compared there
            foreach (var s in Mentioned(e.Type)) result[s] = false;
            if (e is not LitExpr && e.Type.Kind != "null" && (e.Type.Kind is "unknown" or "dynamic" || e.Type.Cs is "object" or "object?"))   // `nil` holds no object
                foreach (var c in classes) result[c] = false;
        }
        return result;
    }

    /// <summary>Alice classes nested in a generic type other than an array.</summary>
    static IEnumerable<TypeSym> Mentioned(AType t)
    {
        if (t.Args == null || t.Kind == "array") yield break;
        foreach (var a in t.Args)
        {
            if (a.Sym is TypeSym s) yield return s;
            foreach (var n in Mentioned(a)) yield return n;
        }
    }

    static bool Immutable(TypeSym t)
    {
        if (t is not { Kind: "class", Decl: { } d, HasSubclass: false, IsAbstract: false, IsStack: false, BaseClass: null, Parent: null, SoaOf: null, SoaStore: null }) return false;
        if (t.TypeParams.Count > 0 || t.Bases.Count > 0 || d.Mixins.Count > 0) return false;
        if (t.Fields.Values.Any(f => !f.IsStatic && f.Kind is not ("getter" or "field"))) return false;
        if (!t.Methods.TryGetValue("initialize", out var ctors) || ctors.Count != 1 || ctors[0].Decl is not { } ctor) return false;
        // the constructor only stores its arguments: `@x` parameters, or `@x = param`
        var ps = ctor.Params.Select(p => p.Name).ToHashSet();
        if (ctor.Body.Any(s => s is not Assign { Op: "=", Target: IvarExpr, Value: NameExpr n } || !ps.Contains(n.Name))) return false;
        foreach (var m in t.Methods.Values.SelectMany(x => x).Where(m => !m.IsCtor && m.Decl != null))
            if (WalkStmts(m.Decl!.Body).Any(s => s is Assign { Target: IvarExpr } or Assign { Target: CallExpr { Recv: SelfExpr } })) return false;
        return true;
    }

    IEnumerable<List<Stmt>> AllBodies()
    {
        foreach (var f in binder.Files)
        {
            var main = f.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList();
            if (main.Count > 0) yield return main;
            foreach (var b in ItemBodies(f.Items)) yield return b;
        }
    }

    static IEnumerable<List<Stmt>> ItemBodies(List<Item> items)
    {
        foreach (var it in items)
        {
            if (it is MethodDecl m) yield return m.Body;
            else if (it is TypeDecl td) foreach (var b in ItemBodies(td.Members)) yield return b;
        }
    }
}
