using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Null safety of fields (ADR 0004, security audit F17): a declared field, accessor or ctor-parameter field of a
/// non-nilable reference type must hold a value once the object is built (`property`/`setter` accessors are
/// publicly assignable and stay exempt: frameworks and serializers fill them). It has an initializer, or every
/// `initialize` of its class definitely assigns it (on every path, possibly through a self method it calls).
/// Otherwise it would be nil at runtime with no diagnostic (the generated C# disables CS8618): AL0221.
/// Inferred ivars (`@x = v` in some method) are Ruby-style: nil until first assigned, as in Ruby.
/// </summary>
public sealed partial class Binder
{
    public const string UnassignedFieldCode = "AL0221";

    void CheckFieldInitialization()
    {
        var decls = new Dictionary<TypeSym, List<(TypeDecl Decl, string File)>>();
        void Collect(IEnumerable<Item> items, string path)
        {
            foreach (var it in items)
                switch (it)
                {
                    case TypeDecl { Sym: TypeSym s } td:
                        if (!decls.TryGetValue(s, out var l)) decls[s] = l = new();
                        l.Add((td, path));
                        Collect(td.Members, path);
                        break;
                    case NamespaceItem n:
                        Collect(n.Items, path);
                        break;
                }
        }
        foreach (var f in Files) Collect(f.Items, f.Path);

        foreach (var (s, parts) in decls)
        {
            if (s.Kind is not ("class" or "record")) continue;   // structs are zero-initialized values; modules have no instances
            var ctors = s.Methods.TryGetValue("initialize", out var ms) ? ms.Where(m => m.Owner == s && m.Decl != null).ToList() : [];
            foreach (var (td, path) in parts)
                foreach (var it in td.Members)
                {
                    (string name, Expr? init, Node at) = it switch
                    {
                        FieldDecl fd => (fd.Name, fd.Init, (Node)fd),
                        // `property`/`setter`: publicly assignable, so frameworks fill them (EF's DbSet, serializers)
                        AccessorDecl { Kind: "getter" } a => (a.Name, a.Init, a),
                        _ => ("", null, it),
                    };
                    if (name == "" || init != null || s.FieldOrder.Contains(name)) continue;
                    if (!s.Fields.TryGetValue(name, out var fs) || fs.IsStatic || !NeedsValue(fs.Type, td)) continue;
                    if (ctors.Count > 0 && ctors.All(c => CtorAssigns(c, name))) continue;
                    throw Unassigned(path, at, name, fs.Type, ctors.Count == 0);
                }
            // `def initialize(@name : String)` declares the field for that constructor only: its overloads must assign it too
            foreach (var f in s.Fields.Values.Where(f => f.Kind == "auto" && !f.IsStatic && NeedsValue(f.Type)))
                if (ctors.FirstOrDefault(c => !CtorAssigns(c, f.Name)) is { } miss)
                    throw Unassigned(parts.FirstOrDefault(p => p.Decl.Members.Contains(miss.Decl!)).File ?? parts[0].File, miss.Decl!, f.Name, f.Type, false);
        }
    }

    CompileException Unassigned(string path, Node at, string name, AType t, bool noCtor)
    {
        // Alice spelling of the type: `DbSet(Blog)`, not `global::Microsoft.EntityFrameworkCore.DbSet<Blog>`
        string ty = System.Text.RegularExpressions.Regex.Replace(t.Cs, @"global::(\w+\.)*", "").Replace('<', '(').Replace('>', ')');
        return new([new Diagnostic("error", UnassignedFieldCode, path, at.Line, Math.Max(1, at.Col),
            $"field `@{name}` ({ty}) is never assigned{(noCtor ? " (the class has no `initialize`)" : " by every `initialize`")} and has no initializer, so it would be nil; assign it in every `initialize`, give it a default (`@{name} : {ty} = ...`) or make it nilable (`{ty}?`)")]);
    }

    /// <summary>Also a type parameter field (`@v : T`): nil for every reference type argument unless `T` is
    /// constrained to values (`struct`, `unmanaged`); C# CS8618 says the same and the generated code disables it.</summary>
    static bool NeedsValue(AType t, TypeDecl td) =>
        NeedsValue(t) || t.Kind == "param" && !t.Nullable && !t.Cs.EndsWith('?')
            && !td.Constraints.Any(c => c.Param == t.Cs && c.Keywords.Any(k => k is "struct" or "unmanaged"));

    /// <summary>A reference type that may not hold nil (numbers, structs, enums, type parameters and `T?` may).</summary>
    static bool NeedsValue(AType t)
    {
        if (t.Nullable || t.Cs.EndsWith('?')) return false;
        if (t.Kind is not ("class" or "array" or "iface" or "func" or "generic")) return false;
        if (t.Clr is { IsValueType: true }) return false;
        return t.Sym is not { Kind: "struct" or "record struct" or "enum" };
    }

    bool CtorAssigns(MethodSym ctor, string field) =>
        ctor.Params.Any(p => p.Auto && p.Name == field) || Assigns(ctor.Decl!.Body, field, [ctor]);

    /// <summary>Definite assignment of `@field` by a statement list: on every path that completes it, before any `return`.</summary>
    bool Assigns(List<Stmt> body, string field, HashSet<MethodSym> seen)
    {
        foreach (var s in body)
        {
            if (StmtAssigns(s, field, seen)) return true;
            if (MayReturn(s)) return false;
        }
        return false;
    }

    bool StmtAssigns(Stmt s, string field, HashSet<MethodSym> seen) => s switch
    {
        Assign a => a.Target switch
        {
            IvarExpr iv => iv.Name == field,
            NameExpr { Res: "field" } nf => nf.Name == field,
            CallExpr { Res: "field", Recv: null or SelfExpr } cf => cf.Name == field,
            _ => false,
        },
        RaiseStmt => true,   // the path ends: no object is built
        IfStmt i => i.Else != null && i.Bodies.All(b => Assigns(b, field, seen)) && Assigns(i.Else, field, seen),
        CaseStmt c => c.Else != null && c.Whens.All(w => Assigns(w.Body, field, seen)) && Assigns(c.Else, field, seen),
        BeginStmt b => b.Ensure != null && Assigns(b.Ensure, field, seen)
            || Assigns(b.Body, field, seen) && b.Rescues.All(r => Assigns(r.Body, field, seen)),
        // a self method (or another `initialize`) called by the constructor
        ExprStmt { Expr: CallExpr { Recv: null or SelfExpr, Sym: MethodSym m } } when m.Decl != null && !m.IsStatic && seen.Add(m) => Assigns(m.Decl.Body, field, seen),
        ExprStmt { Expr: NameExpr { Res: "call", Sym: MethodSym m } } when m.Decl != null && !m.IsStatic && seen.Add(m) => Assigns(m.Decl.Body, field, seen),
        _ => false,
    };

    static bool MayReturn(Stmt s)
    {
        bool r = false;
        AstWalk.Each(s, n => r |= n is ReturnStmt);
        return r;
    }
}
