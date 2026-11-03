using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// il-opt hooks of <see cref="IlBody"/> (AirToIl.Helpers.cs extension points): SZArray reads and writes whose index
/// <see cref="Air.Passes.IlOpt.InBounds"/> proved inside the array are emitted through
/// <see cref="Opt.UncheckedIndex"/> (`GetArrayDataReference` + `Unsafe.Add`), with no bounds check. Unproven
/// accesses fall through to the checked `ldelem` / `stelem` path.
/// </summary>
internal sealed partial class IlBody
{
    partial void IndexLoadExt(MIndex ix, Type array, ref Type? result)
    {
        if (!Opt.UncheckedIndex.Proven(ix)) return;
        var et = array.GetElementType()!;
        var iet = T.Il(et);
        Raw(ix.Target);
        Opt.UncheckedIndex.Head(il, c.Module, iet);
        Emit(ix.Args[0], typeof(int));
        Opt.UncheckedIndex.Tail(il, c.Module, iet);
        il.EmitLoadObj(iet);
        result = et;
    }

    partial void IndexStoreExt(MIndex ix, Type array, string op, MExpr value, bool nodeChecked, ref bool done)
    {
        if (!Opt.UncheckedIndex.Proven(ix)) return;
        var et = array.GetElementType()!;
        var iet = T.Il(et);
        Raw(ix.Target);
        Opt.UncheckedIndex.Head(il, c.Module, iet);
        Emit(ix.Args[0], typeof(int));
        Opt.UncheckedIndex.Tail(il, c.Module, iet);
        if (op != "=") { il.EmitDup(); il.EmitLoadObj(iet); BinTail(et, op, value, nodeChecked && Checked); }
        else Emit(value, et);
        il.EmitStoreObj(iet);
        done = true;
    }
}
