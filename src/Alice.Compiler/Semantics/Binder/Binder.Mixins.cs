using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Ruby mixins. `include M` copies M's instance methods (`def x`, not `def self.x`) into the class, re-parsed
/// from source so they bind in the class (calls like `name` resolve to the class's members). `extend M`
/// copies them as class methods. The class's own methods win. Non-module targets (interfaces) become bases.
/// </summary>
public sealed partial class Binder
{
    void ExpandMixins()
    {
        foreach (var d in AllTypeDecls(Files.SelectMany(f => f.Items)))
        {
            foreach (var (mref, extend) in d.Mixins)
            {
                if (Types.TryGetValue(mref.Name, out var mod) && mod.Kind == "module" && mod.Decl != null)
                {
                    var own = d.Members.OfType<MethodDecl>().Select(m => m.Name).ToHashSet();
                    foreach (var md in AllModuleDecls(mod.Name).SelectMany(x => x.Members.OfType<MethodDecl>()))
                    {
                        if (!md.SelfDef) md.IsTemplate = true;
                        if (md.SelfDef || own.Contains(md.Name) || md.SourceText.Length == 0) continue;
                        var copy = Parser.ParseMethod(md.SourceText, md.File, md.Line, extend ? "module" : d.Kind);
                        if (extend) copy.IsStatic = true;
                        d.Members.Add(copy);
                        own.Add(copy.Name);
                    }
                }
                else d.Bases.Add(mref);
            }
            d.Mixins.Clear();
            foreach (var al in d.Members.OfType<AliasDecl>().ToList())
            {
                var old = d.Members.OfType<MethodDecl>().FirstOrDefault(m => m.Name == al.OldName)
                    ?? throw new CompileException($"{file}:{al.Line}:{al.Col}: alias {al.NewName}: no method '{al.OldName}' in {d.Name}");
                var text = System.Text.RegularExpressions.Regex.Replace(old.SourceText, @"def\s+(self\.)?" + System.Text.RegularExpressions.Regex.Escape(al.OldName), m => "def " + m.Groups[1].Value + al.NewName, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                d.Members.Add(Parser.ParseMethod(text, old.File, old.Line, d.Kind));
            }
        }
    }

    IEnumerable<TypeDecl> AllModuleDecls(string name) => AllTypeDecls(Files.SelectMany(f => f.Items)).Where(t => t.Name == name && t.Kind == "module");

    static IEnumerable<TypeDecl> AllTypeDecls(IEnumerable<Item> items)
    {
        foreach (var it in items)
        {
            if (it is TypeDecl d)
            {
                yield return d;
                foreach (var n in AllTypeDecls(d.Members)) yield return n;
            }
            else if (it is NamespaceItem ns)
                foreach (var n in AllTypeDecls(ns.Items)) yield return n;
        }
    }
}
