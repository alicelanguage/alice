using Alice.Compiler.Mir;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// String interpolation without Roslyn, with the invariant culture the C# backend prints (`string.Create(Invariant,
/// $"...")`). At most 4 parts that are all strings (literal chunks and string holes) become `string.Concat` (one
/// string hole alone: `hole ?? ""`); anything else fills a `DefaultInterpolatedStringHandler` on the stack:
/// `AppendLiteral` per non-empty chunk, `AppendFormatted(hole)` per hole (the generic overload instantiated at the
/// hole's static type, so value types are never boxed; the string overload for strings), then `ToStringAndClear()`.
/// Holes of unknown type stay and are reported. Chunks keep their C# literal spelling (MCode string literals).
/// </summary>
public sealed class InterpLower(IlLowerContext? ctx = null) : IlPass(ctx)
{
    public override string Name => "il-interp";

    const int MaxConcatParts = 4;

    static readonly AirType Handler = new AirType.ClrRef("global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler",
        typeof(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler), []);
    static readonly AirType Culture = new AirType.ClrRef("global::System.Globalization.CultureInfo", typeof(System.Globalization.CultureInfo), []);

    protected override MExpr LowerExpr(MExpr e)
    {
        if (e is not MInterp ip) return e;
        if (ip.Holes.Any(h => !IlTypes.Known(h.Type)))
        {
            Unsupported(null, "interpolated string with a hole of unknown type");
            return e;
        }
        // parts in order: chunk0 hole0 chunk1 hole1 ...
        var parts = new List<(string? Chunk, MExpr? Hole)>();
        for (int k = 0; k < Math.Max(ip.Chunks.Count, ip.Holes.Count); k++)
        {
            if (k < ip.Chunks.Count && ip.Chunks[k].Length > 0) parts.Add((ip.Chunks[k], null));
            if (k < ip.Holes.Count) parts.Add((null, ip.Holes[k]));
        }
        if (parts.Count <= MaxConcatParts && parts.All(p => p.Hole == null || p.Hole.Type is AirType.Prim { Kind: PrimKind.String }))
            return Concat(parts);
        string h = Ctx.Temp("h");
        int literal = ip.Chunks.Sum(c => c.Length);
        var invariant = Member(new AirTypeRef(Culture), "InvariantCulture", Culture);
        var pre = new List<MStmt>
        {
            Decl(h, Handler, new MNew { TypeCs = Handler.ToCs(), Args = [Const(literal), Const(ip.Holes.Count), invariant], Type = Handler }),
        };
        foreach (var (chunk, hole) in parts)
            pre.Add(Do(chunk != null
                ? Call(Local(h, Handler), "AppendLiteral", IlTypes.Void, Lit(chunk))
                : Call(Local(h, Handler), "AppendFormatted", IlTypes.Void, hole!)));
        return new AirSeq { Pre = pre, Value = Call(Local(h, Handler), "ToStringAndClear", IlTypes.Str), Type = IlTypes.Str };
    }

    static MExpr Lit(string chunk) => new MCode("\"" + chunk + "\"") { Type = IlTypes.Str };

    static MExpr Concat(List<(string? Chunk, MExpr? Hole)> parts)
    {
        var xs = parts.Select(p => p.Hole ?? Lit(p.Chunk!)).ToList();
        if (xs.Count == 0) return Const("");
        if (xs.Count == 1)
            return parts[0].Hole == null ? xs[0] : new MBinary { Op = AirBinOp.Coalesce, L = xs[0], R = Const(""), Type = IlTypes.Str };
        return Call(new AirTypeRef(IlTypes.Str), "Concat", IlTypes.Str, [.. xs]);
    }
}
