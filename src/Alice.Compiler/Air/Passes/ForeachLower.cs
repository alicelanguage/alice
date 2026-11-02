using Alice.Compiler.Mir;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// `foreach` without Roslyn: arrays, spans and strings become counted loops over a local copy of the collection;
/// a type whose GetEnumerator returns a struct (List, Dictionary, HashSet...) keeps the enumerator in a local of that
/// struct type (MoveNext/Current/Dispose called on it directly, never boxed); any other enumerable (IEnumerable&lt;T&gt;,
/// classes) runs in try/finally with a null-checked Dispose. A loop that binds several names binds one element and
/// deconstructs it (TupleLower finishes that). `await foreach` and collections of unknown type stay and are reported.
/// </summary>
public sealed class ForeachLower(IlLowerContext? ctx = null) : IlPass(ctx)
{
    public override string Name => "il-foreach";

    protected override List<MStmt>? Lower(MStmt s)
    {
        if (s is not MForeach fe) return null;
        if (fe.Await)
        {
            Unsupported(s, "`await foreach` (IAsyncEnumerable) is not lowered for the IL backend yet");
            return null;
        }
        var t = fe.Coll.Type;
        if (!IlTypes.Known(t))
        {
            Unsupported(s, "foreach over a collection of unknown type");
            return null;
        }
        // a nil array or string throws NullReferenceException at `.Length`, as foreach does
        if (t is AirType.Array { Rank: 1 } arr) return Counted(fe, t, arr.Elem, "Length");
        if (t is AirType.Prim { Kind: PrimKind.String }) return Counted(fe, t, new AirType.Prim(PrimKind.Char), "Length");
        if (t is AirType.ClrRef { Name: "global::System.Span" or "global::System.ReadOnlySpan", Args.Count: 1, Nullable: false } sp)
            return Counted(fe, t, sp.Args[0], "Length");
        if (IlTypes.Clr(t) is { } clr && Enumerator(clr, t as AirType.ClrRef) is var (enumType, enumClr, elem))
            return Enumerated(fe, enumType, enumClr, elem);
        Unsupported(s, $"foreach over `{t!.ToCs()}`: no GetEnumerator pattern the IL lowering understands");
        return null;
    }

    /// <summary>`decl c = coll; for (int i = 0; i &lt; c.Length; i++) { decl x = c[i]; body }`.</summary>
    List<MStmt> Counted(MForeach fe, AirType collType, AirType elem, string length)
    {
        string c = Ctx.Temp("c"), i = Ctx.Temp("i");
        var body = Bind(fe, new MIndex { Target = Local(c, collType), Args = { Local(i, IlTypes.Int) }, Type = elem }, elem);
        return
        [
            Decl(c, collType, fe.Coll),
            new MFor { Var = i, VarType = "int", Start = new AirConst(0), Cmp = "<", End = Member(Local(c, collType), length, IlTypes.Int), Step = "++", Body = body },
        ];
    }

    /// <summary>
    /// `decl e = coll.GetEnumerator(); try { while (e.MoveNext()) { decl x = e.Current; body } } finally { dispose }`;
    /// without IDisposable a struct enumerator needs no try at all.
    /// </summary>
    List<MStmt> Enumerated(MForeach fe, AirType enumType, Type enumClr, AirType elem)
    {
        string e = Ctx.Temp("e");
        MExpr E() => Local(e, enumType);
        var loop = new MWhile { Cond = Call(E(), "MoveNext", IlTypes.Bool), Body = Bind(fe, Member(E(), "Current", elem), elem) };
        var decl = Decl(e, enumType, Call(fe.Coll, "GetEnumerator", enumType));
        bool disposable = typeof(IDisposable).IsAssignableFrom(enumClr);
        if (enumClr.IsValueType)
            return disposable ? [decl, new MTry { Body = [loop], Finally = [Do(Call(E(), "Dispose", IlTypes.Void))] }] : [decl, loop];
        List<MStmt> fin;
        if (disposable)
            fin = [new MIf { Cond = new MBinary { Op = AirBinOp.Ne, L = E(), R = new AirConst(null), Type = IlTypes.Bool }, Then = [Do(Call(E(), "Dispose", IlTypes.Void))] }];
        else if (enumClr.IsSealed)
            return [decl, loop];
        else
        {
            // a non-sealed enumerator class may still implement IDisposable in a subclass
            string d = Ctx.Temp("d");
            var idisp = new AirType.ClrRef("global::System.IDisposable", typeof(IDisposable), []);
            fin =
            [
                Decl(d, idisp, new AirAs { TypeCs = idisp.ToCs(), E = E(), Type = idisp }),
                new MIf { Cond = new MBinary { Op = AirBinOp.Ne, L = Local(d, idisp), R = new AirConst(null), Type = IlTypes.Bool }, Then = [Do(Call(Local(d, idisp), "Dispose", IlTypes.Void))] },
            ];
        }
        return [decl, new MTry { Body = [loop], Finally = fin }];
    }

    /// <summary>The loop body with the iteration variable(s) bound to <paramref name="element"/> first.</summary>
    List<MStmt> Bind(MForeach fe, MExpr element, AirType elem)
    {
        var body = new List<MStmt>();
        if (fe.Vars.Count == 1) body.Add(Decl(fe.Vars[0], elem, element));
        else
        {
            string x = Ctx.Temp("x");
            body.Add(Decl(x, elem, element));
            body.Add(new MDeconstruct { Names = [.. fe.Vars], Declare = true, Value = Local(x, elem) });
        }
        body.AddRange(fe.Body);
        return body;
    }

    /// <summary>The enumerator type GetEnumerator returns and its element type, or null without the pattern.</summary>
    static (AirType Type, Type Clr, AirType Elem)? Enumerator(Type coll, AirType.ClrRef? owner)
    {
        var get = IlTypes.Method(coll, "GetEnumerator", 0);
        if (get == null) return null;
        var et = get.ReturnType;
        if (IlTypes.Property(et, "Current") is not { } cur || IlTypes.Method(et, "MoveNext", 0) == null) return null;
        if (IlTypes.FromClr(et, owner) is not { } enumType || IlTypes.FromClr(cur, owner) is not { } elem) return null;
        return (enumType, et, elem);
    }
}
