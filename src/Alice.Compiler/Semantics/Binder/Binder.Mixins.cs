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
                if (mref.Name is "Comparable" or "Enumerable" && !Types.ContainsKey(mref.Name))
                {
                    CoreMixin(d, mref.Name);
                    continue;
                }
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

    /// <summary>
    /// Ruby's core mixins, written in Alice and re-parsed into the class:
    /// Comparable (needs `<=>`) -> < > <= >= == between? clamp + IComparable(T);
    /// Enumerable (needs `each(&block : T ->)`) -> IEnumerable(T), so every iterator/LINQ method applies.
    /// </summary>
    void CoreMixin(TypeDecl d, string name)
    {
        string self = d.Name + (d.TypeParams.Count > 0 ? "(" + string.Join(", ", d.TypeParams) + ")" : "");
        var own = d.Members.OfType<MethodDecl>().Select(m => m.Name).ToHashSet();
        string src;
        if (name == "Comparable")
        {
            if (!own.Contains("<=>")) throw Err(d, $"include Comparable needs `def <=>(other : {self}) : Int32` in {d.Name}");
            src = $$"""
                def <(o : {{self}}) : Bool = (self <=> o) < 0
                def >(o : {{self}}) : Bool = (self <=> o) > 0
                def <=(o : {{self}}) : Bool = (self <=> o) <= 0
                def >=(o : {{self}}) : Bool = (self <=> o) >= 0
                def ==(o : {{self}}) : Bool = (self <=> o) == 0
                def between?(lo : {{self}}, hi : {{self}}) : Bool = (self <=> lo) >= 0 && (self <=> hi) <= 0
                def clamp(lo : {{self}}, hi : {{self}}) : {{self}} = (self <=> lo) < 0 ? lo : ((self <=> hi) > 0 ? hi : self)
                def CompareTo(o : {{self}}) : Int32 = self <=> o
                """;
            d.Bases.Add(new TypeRef { Name = "IComparable", Args = { new TypeRef { Name = self.Contains('(') ? d.Name : self } } });
        }
        else
        {
            var each = d.Members.OfType<MethodDecl>().FirstOrDefault(m => m.Name == "each" && m.Params.Any(p => p.Block))
                ?? throw Err(d, $"include Enumerable needs `def each(&block : T ->)` in {d.Name}");
            var elemRef = each.Params.First(p => p.Block).Type?.Args.FirstOrDefault()
                ?? throw Err(each, "each's block parameter needs a type: &block : T ->");
            string et = TypeRefText(elemRef);
            src = $$"""
                def GetEnumerator : System.Collections.Generic.IEnumerator({{et}})
                  items = List({{et}}).new
                  each { |x| items.Add(x) }
                  items.GetEnumerator
                end
                """;
            d.Bases.Add(new TypeRef { Name = "System.Collections.Generic.IEnumerable", Args = { elemRef } });
            d.Attrs.Add("__enumerable:" + et);
        }
        foreach (var text in SplitDefs(src))
        {
            var m = Parser.ParseMethod(text, file, d.Line, d.Kind);
            if (!own.Contains(m.Name)) d.Members.Add(m);
        }
    }

    static string TypeRefText(TypeRef r) => r.Args.Count == 0 ? r.Name : r.Name + "(" + string.Join(", ", r.Args.Select(TypeRefText)) + ")";

    static IEnumerable<string> SplitDefs(string src)
    {
        var lines = src.Replace("\r\n", "\n").Split('\n');
        var cur = new List<string>();
        foreach (var l in lines)
        {
            if (l.StartsWith("def ") && cur.Count > 0) { yield return string.Join("\n", cur); cur.Clear(); }
            if (l.Trim().Length > 0) cur.Add(l);
        }
        if (cur.Count > 0) yield return string.Join("\n", cur);
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
