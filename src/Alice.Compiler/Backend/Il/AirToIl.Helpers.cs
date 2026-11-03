using System.Reflection;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// The per-body pass order of the IL backend and the members the lowering adds next to a method:
/// <list type="number">
/// <item><see cref="Optimizer.Pipeline.Run"/> (the C# backend's passes; <see cref="Optimizer.Pipeline.Backend"/> is
/// <c>Il</c> for the whole compilation, <see cref="Air.Passes.ClosureConvert.Current"/> stays null there, so the IL
/// passes run here, in this order:)</item>
/// <item>DynLower (dynamic call sites, AirToIl.Dyn.cs), <see cref="IlLower"/> (Air/Passes/IlLowering, then
/// <see cref="IlPipeline.AfterLowering"/>), <see cref="ConvertClosures"/> (with the method as context),
/// SmLower (async/iterator state machines, AirToIl.StateMachines.cs), then emission.</item>
/// </list>
/// Typed helpers (<c>Lowerer.TypedExtra</c>: `__fork_*`, `__par_*`, `__parfor_*`, `__shared_*`, SoA, callback and
/// wordfreq helpers) are declared on the method's type before its body is emitted and emitted after it; a helper only
/// printed as C# text stays unsupported. A method with forks gets its `m__serial` clone (Emitter.SerialClone).
/// `ALICE_IL_PAR=0`: lower forks and parallel loops serially (<c>Lowerer.Sequential</c>), as before D1.5.
/// </summary>
public sealed partial class AirToIl
{
    static readonly bool ParallelOn = Environment.GetEnvironmentVariable("ALICE_IL_PAR") != "0";

    // ---- extension points for the other IL partials (no-ops until a partial implements them) ----
    // IlBody has its own (below): RawArena (affine-lower, AirArenaExpr), RawCm
    // (cm-lower, AirCmExpr), RawDataflow (dataflow-lower, AirDataflow) for expression nodes the switch does not know;
    // IndexLoadExt / IndexStoreExt (il-opt: proven-in-bounds
    // SZArray access, set the result to skip the checked path). Module-level passes: IlPipeline.AfterLowering (MIR,
    // per body after IlLowering) and IlPipeline.BeforeEncode (per emitted method body).

    /// <summary>The IL passes of one body, in order (see the type summary).</summary>
    void Passes(ref List<MStmt> body, AMethod am, IReadOnlyList<(string Name, Type Type)> ps)
    {
        DynLower(body);
        IlLower(body);
        ConvertClosures(body, am.Owner, am.Name, !am.IsStatic, ps);
        body = SmLower(am, body);
    }

    /// <summary>Takes the members the lowering added for the body just lowered: declares typed helpers on
    /// <paramref name="owner"/> (methods returned for <see cref="EmitHelpers"/>, static fields initialized in its .cctor).</summary>
    List<(AMethod Am, AirHelperDecl H)> TakeHelpers(AliceT owner, Span? sp)
    {
        var r = new List<(AMethod, AirHelperDecl)>();
        if (lowerer.Extra.Count == 0) return r;
        var texts = lowerer.Extra.ToList();
        var typed = texts.Select(t => lowerer.TypedExtra.GetValueOrDefault(t)).ToList();
        lowerer.Extra.Clear();
        foreach (var t in texts) lowerer.TypedExtra.Remove(t);
        if (typed.Any(h => h == null)) throw new IlUnsupportedException("Lowerer.Extra (C# helper text)", sp);
        foreach (var h in typed)
        {
            if (h!.Field is { } f)
            {
                owner.Fields[f.Name] = HelperField(owner, f, h.FieldInit, sp);
                continue;
            }
            var m = h.Method ?? throw new IlUnsupportedException("Helper:empty", sp);
            if (h.Attributes.Any(a => a.Type.ToCs().Contains("UnmanagedCallersOnly")))
                throw new IlUnsupportedException("Helper:UnmanagedCallersOnly", sp, m.Name);
            bool st = m.Flags.HasFlag(AirMethodFlags.Static);
            var ret = Types.Parse(m.Ret.ToCs(), sp);
            var ps = m.Params.Select(p => Types.Parse(p.Type.ToCs(), sp)).ToArray();
            var def = owner.Def.DefineMethod(m.Name, MethodAttributes.Private | MethodAttributes.HideBySig | (st ? MethodAttributes.Static : 0),
                Types.Il(ret), [.. ps.Select(Types.Il)]);
            for (int i = 0; i < m.Params.Count; i++) def.DefineParameter(i + 1, m.Params[i].Name);
            foreach (var a in h.Attributes)
            {
                string text = string.Join(" ", a.Args.Select(Printer.E));
                if (text.Contains("AggressiveInlining")) def.ImplAttributes |= MethodImplAttributes.AggressiveInlining;
                if (text.Contains("AggressiveOptimization")) def.ImplAttributes |= MethodImplAttributes.AggressiveOptimization;
            }
            var am = new AMethod { Name = m.Name, Owner = owner, Ret = ret, Params = ps, ParamNames = [.. m.Params.Select(p => p.Name)], IsStatic = st, Def = def };
            owner.Methods.Add(am);
            r.Add((am, h));
        }
        return r;
    }

    /// <summary>A `__shared_*` / launcher field: a static field of a nested holder class whose .cctor runs the
    /// initializer (lazily, as a C# static readonly field), so the owner's synthesized type initializer is untouched.</summary>
    AField HelperField(AliceT owner, AirFieldDecl f, MExpr? init, Span? sp)
    {
        var ft = Types.Parse(f.Type.ToCs(), sp);
        var holder = owner.Def.DefineNestedType("<>h__" + f.Name, TypeAttributes.NestedPrivate | TypeAttributes.Abstract | TypeAttributes.Sealed
            | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, Module.CoreType("Object"));
        var fd = holder.DefineField(f.Name, Types.Il(ft), FieldAttributes.Assembly | FieldAttributes.Static | (f.ReadOnly && init != null ? FieldAttributes.InitOnly : 0));
        if (init != null)
        {
            var ht = new AliceT { Def = holder, Sym = owner.Sym, Host = owner, IsStatic = true };
            ht.Proxy = Types.MakeDisplayProxy(ht);
            var cctor = holder.DefineConstructor(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, isStatic: true);
            var b = new IlBody(this, ht, null, Body(cctor), typeof(void), true, []);
            b.Emit(init, ft);
            cctor.Il.EmitStoreField(fd);
            cctor.Il.Emit(System.Reflection.Metadata.ILOpCode.Ret);
        }
        return new AField { Name = f.Name, Type = ft, IsStatic = true, Owner = owner, Field = fd, ReadOnly = f.ReadOnly };
    }

    /// <summary>Emits the helper methods <see cref="TakeHelpers"/> declared (their bodies went through the MIR pipeline
    /// when the lowering built them; the IL passes run here).</summary>
    void EmitHelpers(List<(AMethod Am, AirHelperDecl H)> helpers)
    {
        foreach (var (am, h) in helpers)
        {
            var m = h.Method!;
            var body = m.Body ?? (h.ExprBody is { } e ? (am.Ret == typeof(void) ? [new MExprStmt { E = e }] : [new MReturn { Value = e }]) : []);
            var ps = am.ParamNames.Select((n, i) => (n, am.Params[i])).ToList();
            Passes(ref body, am, ps);
            new IlBody(this, am.Owner, am, Body(am.Def), am.Ret, am.IsStatic, ps).Body(body);
            FlushClosures();
        }
    }

    /// <summary>`m__serial` (Backend/Emitter.cs SerialClone): the method lowered again without forks, self-calls
    /// staying in the clone. Declared now (the forked body calls it), emitted after the method.</summary>
    (AMethod Am, List<MStmt> Body, List<(AMethod, AirHelperDecl)> Helpers)? SerialClone(AMethod am, MethodDecl d, bool returns)
    {
        if (am.Inst != null || am.Sym!.HasInferredGenerics || am.IsCtor) throw new IlUnsupportedException("Lowerer.Forked:specialized", Sp(d), am.Name);
        lowerer.SerialClone = true;
        lowerer.Forked = false;
        List<MStmt> body;
        try { body = lowerer.LowerBody(d.Body, returns ? Tail.Return : Tail.None, d.HoistedLocals, d.Line); }
        finally { lowerer.SerialClone = false; }
        Optimizer.Pipeline.Run(body);
        string name = am.Sym.CsName.TrimStart('@') + Lowerer.SerialSuffix;
        var def = am.Owner.Def.DefineMethod(name, MethodAttributes.Private | MethodAttributes.HideBySig | (am.IsStatic ? MethodAttributes.Static : 0),
            Types.Il(am.Ret), [.. am.Params.Select(Types.Il)]);
        for (int i = 0; i < am.ParamNames.Length; i++) def.DefineParameter(i + 1, am.ParamNames[i]);
        var sam = new AMethod { Name = name, Owner = am.Owner, Ret = am.Ret, Params = am.Params, ParamNames = am.ParamNames, IsStatic = am.IsStatic, Def = def, Sym = am.Sym, Decl = d };
        am.Owner.Methods.Add(sam);
        var helpers = TakeHelpers(am.Owner, Sp(d));
        CheckLowerer(Sp(d));
        return (sam, body, helpers);
    }
}

/// <summary>Dispatch hooks of the emitter for nodes owned by other IL partials (see AirToIl's extension points).</summary>
internal sealed partial class IlBody
{
    partial void RawArena(MExpr e, ref Type? result);
    partial void RawCm(MExpr e, ref Type? result);
    partial void RawDataflow(MExpr e, ref Type? result);
    partial void IndexLoadExt(MIndex ix, Type array, ref Type? result);
    partial void IndexStoreExt(MIndex ix, Type array, string op, MExpr value, bool nodeChecked, ref bool done);
}
