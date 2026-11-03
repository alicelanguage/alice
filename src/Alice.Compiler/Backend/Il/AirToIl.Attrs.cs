using System.Reflection;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// User attributes (`@[Obsolete("old")]`, `@[NonSerialized]`) on methods and fields as custom-attribute blobs (D1.10):
/// the attribute type is found by name (with or without the `Attribute` suffix), the arguments are literals (strings,
/// numbers, booleans), `Name = value` pairs become named arguments. The compiler's own attributes (Inline, Hot, Native,
/// ...) are not emitted as blobs.
/// </summary>
public sealed partial class AirToIl
{
    static bool IsCompilerAttr(string a) =>
        a is "Inline" or "NoInline" or "Hot" or "StrictMath" or "Native" or "Open" or "Stack" or "SoA" or "Fast" || a.StartsWith("Native(") || a.StartsWith("__enumerable:");

    /// <summary>Appends the blobs of <paramref name="attrs"/> to <paramref name="target"/>; null when an attribute cannot be resolved.</summary>
    bool TryApplyAttributes(List<IlCustomAttribute> target, IEnumerable<string> attrs, out string bad)
    {
        bad = "";
        foreach (var a in attrs)
        {
            if (IsCompilerAttr(a)) continue;
            if (BuildAttribute(a) is not { } ca) { bad = a; return false; }
            target.Add(ca);
        }
        return true;
    }

    IlCustomAttribute? BuildAttribute(string text)
    {
        text = text.Trim();
        int lp = text.IndexOf('(');
        string name = lp < 0 ? text : text[..lp];
        if (name.Contains(':') || name.Length == 0) return null;
        var type = Types.TryParse(name + "Attribute") ?? Types.TryParse(name);
        if (type == null || !typeof(Attribute).IsAssignableFrom(type) || Types.Alice(type) != null) return null;
        var positional = new List<(Type T, object? V)>();
        var named = new List<(string Name, Type T, object? V)>();
        if (lp >= 0)
        {
            if (!text.EndsWith(')')) return null;
            foreach (var arg in IlTypes.SplitTop(text[(lp + 1)..^1]))
            {
                string left = "", right = arg;
                int eq = arg.IndexOf('=');
                if (eq > 0 && eq + 1 < arg.Length && arg[eq + 1] != '=' && arg[..eq].Trim().All(char.IsLetterOrDigit)) { left = arg[..eq].Trim(); right = arg[(eq + 1)..].Trim(); }
                if (!IlTypes.TryLiteral(right.Trim(), out var lt, out var lv)) return null;
                if (left.Length == 0) positional.Add((lt, lv));
                else
                {
                    Type? mt = (type.GetProperty(left, BindingFlags.Public | BindingFlags.Instance)?.PropertyType) ?? type.GetField(left, BindingFlags.Public | BindingFlags.Instance)?.FieldType;
                    if (mt == null) return null;
                    named.Add((left, mt, lv));
                }
            }
        }
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = ctor.GetParameters();
            if (ps.Length != positional.Count) continue;
            var values = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                var pt = ps[i].ParameterType;
                var (lt, lv) = positional[i];
                if (pt == lt) values[i] = lv;
                else if (pt == typeof(object)) values[i] = lv;
                else if (lv != null && IlTypes.IsNumeric(pt) && IlTypes.IsNumeric(lt)) { try { values[i] = System.Convert.ChangeType(lv, pt, System.Globalization.CultureInfo.InvariantCulture); } catch (OverflowException) { ok = false; } }
                else ok = false;
            }
            if (!ok) continue;
            var nargs = new List<IlNamedArgument>();
            foreach (var (n, nt, nv) in named)
            {
                object? v = nv;
                if (nv != null && nv.GetType() != nt && IlTypes.IsNumeric(nt)) v = System.Convert.ChangeType(nv, nt, System.Globalization.CultureInfo.InvariantCulture);
                nargs.Add(new IlNamedArgument(n, Types.Il(nt), v, IsField: type.GetProperty(n) == null));
            }
            return new IlCustomAttribute(Module.Import(ctor), values, nargs.Count > 0 ? nargs : null);
        }
        return null;
    }
}
