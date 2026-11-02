using System.Globalization;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>C# forms of the nodes in Mir/MExpr.Iterators.cs.</summary>
public sealed partial class Printer
{
    const string CollectionsMarshal = "global::System.Runtime.InteropServices.CollectionsMarshal";

    static bool AtomicIterators(MExpr e) => e switch
    {
        AirRangeLength or AirRefOrAddDefault or AirRefOrNullRef or AirAlternateLookup or AirSpanSplit => true,
        _ => false,
    };

    static string? EIterators(MExpr e) => e switch
    {
        AirRangeLength r => $"global::System.Math.Max(0, ({E(r.Hi)}) - ({E(r.Lo)}){(r.Exclusive ? "" : " + 1")})",
        AirRefOrAddDefault a => $"{CollectionsMarshal}.GetValueRefOrAddDefault({E(a.Dict)}, {E(a.Key)}, out _)",
        AirRefOrNullRef n => $"{CollectionsMarshal}.GetValueRefOrNullRef({E(n.Dict)}, {E(n.Key)})",
        AirAlternateLookup al => $"{P(al.Dict)}.GetAlternateLookup<{al.KeyTypeCs}>()",
        AirSpanSplit sp => $"{P(sp.Str)}.AsSpan().Split({E(sp.Sep)})",
        AirStackBuffer sb => $"stackalloc {sb.ElemCs}[{sb.Length.ToString(CultureInfo.InvariantCulture)}]",
        _ => null,
    };
}
