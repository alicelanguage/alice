namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the nodes in Mir/MExpr.Iterators.cs.</summary>
public sealed partial class MirDump
{
    string? CoreIterators(MExpr e) => e switch
    {
        AirLocalRef l => $"(local {l.Name})",
        AirConst c => $"(const {Lit(c.Value)})",
        AirTypeRef t => $"(type {t.TypeCs})",
        AirDefault d => $"(default{(d.Suppress ? "!" : "")}{(d.TypeCs != null ? " " + d.TypeCs : "")})",
        AirRangeLength r => $"(range-length{(r.Exclusive ? " excl" : "")} {E(r.Lo)} {E(r.Hi)})",
        AirRefOrAddDefault a => $"(ref-or-add-default {E(a.Dict)} {E(a.Key)})",
        AirRefOrNullRef n => $"(ref-or-null-ref {E(n.Dict)} {E(n.Key)})",
        AirAlternateLookup al => $"(alternate-lookup {al.KeyTypeCs} {E(al.Dict)})",
        AirSpanSplit sp => $"(span-split {E(sp.Str)} {E(sp.Sep)})",
        AirStackBuffer sb => $"(stack-buffer {sb.ElemCs} {sb.Length})",
        AirParGate g => $"(par-gate {E(g.Size)} {g.Min})",
        _ => null,
    };

    static string Lit(object? v) => v switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
        char ch => "'" + ch + "'",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) + (v is long ? "L" : ""),
        _ => v.ToString() ?? "",
    };
}
