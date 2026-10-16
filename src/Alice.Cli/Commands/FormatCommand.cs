using Alice.Compiler.Syntax;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>`alice fmt [--check] [paths]`: formats .al files in place (or reports unformatted ones for CI).</summary>
public static class FormatCommand
{
    public static int Run(List<string> a)
    {
        bool check = a.Remove("--check");
        var roots = a.Count > 0 ? a : ["."];
        var files = roots.SelectMany(r => File.Exists(r) ? [r] : Directory.GetFiles(r, "*.al", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f).ToList();
        int changed = 0;
        foreach (var f in files)
        {
            var src = File.ReadAllText(f);
            var fmt = Formatter.Format(src);
            if (fmt == src) continue;
            changed++;
            if (check) UiH.Status(T("fmt.would"), f, "yellow");
            else
            {
                File.WriteAllText(f, fmt);
                UiH.Status(T("fmt.formatted"), f);
            }
        }
        UiH.Status(T("finished"), T("fmt.summary", files.Count, changed));
        return check && changed > 0 ? 1 : 0;
    }
}
