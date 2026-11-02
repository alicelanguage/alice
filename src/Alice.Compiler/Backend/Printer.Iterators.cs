using System.Globalization;
using System.Text;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>C# forms of the nodes in Mir/MExpr.Iterators.cs.</summary>
public sealed partial class Printer
{
    const string CollectionsMarshal = "global::System.Runtime.InteropServices.CollectionsMarshal";

    static bool AtomicIterators(MExpr e) => e switch
    {
        AirConst c => Const(c.Value) is not ['-', ..],
        AirLocalRef or AirTypeRef or AirDefault or AirRangeLength or AirRefOrAddDefault or AirRefOrNullRef or AirAlternateLookup or AirSpanSplit => true,
        _ => false,
    };

    static string? EIterators(MExpr e) => e switch
    {
        AirLocalRef l => l.Name,
        AirConst c => Const(c.Value),
        AirTypeRef t => t.TypeCs,
        AirDefault d => d.TypeCs != null ? $"default({d.TypeCs})" : d.Suppress ? "default!" : "default",
        AirRangeLength r => $"global::System.Math.Max(0, ({E(r.Hi)}) - ({E(r.Lo)}){(r.Exclusive ? "" : " + 1")})",
        AirRefOrAddDefault a => $"{CollectionsMarshal}.GetValueRefOrAddDefault({E(a.Dict)}, {E(a.Key)}, out _)",
        AirRefOrNullRef n => $"{CollectionsMarshal}.GetValueRefOrNullRef({E(n.Dict)}, {E(n.Key)})",
        AirAlternateLookup al => $"{P(al.Dict)}.GetAlternateLookup<{al.KeyTypeCs}>()",
        AirSpanSplit sp => $"{P(sp.Str)}.AsSpan().Split({E(sp.Sep)})",
        AirStackBuffer sb => $"stackalloc {sb.ElemCs}[{sb.Length.ToString(CultureInfo.InvariantCulture)}]",
        _ => null,
    };

    /// <summary>A literal as C# source text.</summary>
    static string Const(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture) + "L",
        char ch => "'" + Escape(ch.ToString(), '\'') + "'",
        string s => "\"" + Escape(s, '"') + "\"",
        _ => throw new InvalidOperationException("AirConst " + v.GetType().Name),
    };

    static string Escape(string s, char quote)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when ch == quote => "\\" + quote,
                _ => ch.ToString(),
            });
        return sb.ToString();
    }
}
