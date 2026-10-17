using Alice.Compiler;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Cli.Lsp;

public static class Analyzer
{
    public static void Parse(Document doc)
    {
        try { doc.Ast = Parser.ParseFile(doc.Text, doc.Path); }
        catch (CompileException e) { Recover(doc, e); }
    }

    static void Recover(Document doc, CompileException error)
    {
        var lines = doc.Text.Replace("\r\n", "\n").Split('\n').ToList();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (error.Message.Contains("expected 'end'")) lines.Add("end");
            else
            {
                int line = Math.Clamp(error.Diagnostics.FirstOrDefault()?.Line - 1 ?? lines.Count - 1, 0, lines.Count - 1);
                while (line > 0 && string.IsNullOrWhiteSpace(lines[line])) line--;
                lines[line] = "";
            }
            try { doc.Ast = Parser.ParseFile(string.Join('\n', lines), doc.Path); return; }
            catch (CompileException next) { error = next; }
        }
    }

    public static List<Diagnostic> Quick(Document doc, Project project, List<Document>? sources = null)
    {
        doc.AnalyzedVersion = doc.Version;
        doc.AnalyzedProject = project;
        doc.Related = (sources ?? []).Where(s => s != doc).ToList();
        var asts = new List<FileNode>();
        var diagnostics = new List<Diagnostic>();
        try
        {
            foreach (var source in sources ?? [doc])
            {
                try { source.Ast = Parser.ParseFile(source.Text, source.Path); }
                catch (CompileException e) { diagnostics.AddRange(e.Diagnostics); Recover(source, e); }
                if (source.Ast != null) asts.Add(source.Ast);
            }
            try { new Binder(asts, project.Clr).Bind(); }
            catch (CompileException e) { if (diagnostics.Count == 0) diagnostics.AddRange(e.Diagnostics); }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { diagnostics.Add(new Diagnostic("error", "AL0001", doc.Path, 1, 1, "internal: " + e.Message)); }
        return Here(diagnostics, doc);
    }

    public static List<Diagnostic> Deep(Document doc, Project project, List<Document>? sources = null)
    {
        try
        {
            var cs = AliceCompiler.TranspileAll((sources ?? [doc]).Select(d => new SourceFile(d.Path, d.Text)).ToList(), project.Refs, project.Clr);
            return Here(AliceCompiler.Check(cs, project.Refs), doc);
        }
        catch (CompileException e) { return Here(e.Diagnostics, doc); }
    }

    static List<Diagnostic> Here(IReadOnlyList<Diagnostic> diagnostics, Document doc) =>
        diagnostics.Where(d => d.File.Length == 0 || Path.GetFullPath(d.File) == Path.GetFullPath(doc.Path) || d.Line == 0)
            .Select(d => d with { Line = Math.Max(1, d.Line), Col = Math.Max(1, d.Col) }).ToList();
}
