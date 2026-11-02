namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the nodes in Mir/MExpr.Iterators.cs.</summary>
public sealed partial class MirDump
{
    string? CoreIterators(MExpr e) => e switch
    {
        AirRangeLength r => $"(range-length{(r.Exclusive ? " excl" : "")} {E(r.Lo)} {E(r.Hi)})",
        AirRefOrAddDefault a => $"(ref-or-add-default {E(a.Dict)} {E(a.Key)})",
        AirRefOrNullRef n => $"(ref-or-null-ref {E(n.Dict)} {E(n.Key)})",
        AirAlternateLookup al => $"(alternate-lookup {al.KeyTypeCs} {E(al.Dict)})",
        AirSpanSplit sp => $"(span-split {E(sp.Str)} {E(sp.Sep)})",
        AirStackBuffer sb => $"(stack-buffer {sb.ElemCs} {sb.Length})",
        _ => null,
    };
}
