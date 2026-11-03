using System.Reflection;
using System.Reflection.Metadata;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Runtime;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// D1.9 consumer: operations on `Any` values. Air/Passes/DynSites (run on every body right after the MIR pipeline,
/// one instance per module so site numbers are unique) rewrites them into <see cref="AirDynCall"/>; each one is
/// `ldsflda &lt;&gt;DynSites::siteN` plus a call of <see cref="Dyn"/>. The holder type `&lt;&gt;DynSites` (static
/// `DynSite` fields, a `.cctor` that builds each site) comes from <see cref="DynSites.Declaration"/> and is emitted
/// after every body. `dynamic` is an object everywhere else (locals, parameters, returns).
/// </summary>
public sealed partial class AirToIl
{
    DynSites? dyn;
    AliceT? dynHolder;
    readonly Dictionary<int, IlFieldDef> dynFields = new();

    /// <summary>Whether the module has dynamic sites (AL0232 under `native = true` / wasm: <see cref="DynCheck"/>).</summary>
    internal IReadOnlyList<DynSiteDecl> DynSiteDecls => (IReadOnlyList<DynSiteDecl>?)dyn?.Sites ?? [];

    /// <summary>H4a: the DynSites pass over a body (after Optimizer.Pipeline, before IlLowering), then `dynamic`
    /// spelled `object` for the emitter.</summary>
    internal void DynLower(List<MStmt> body)
    {
        dyn ??= new DynSites(File);
        dyn.Run(body);
        if (dyn.Unsupported.Count > 0)
        {
            var u = dyn.Unsupported[0];
            dyn.Unsupported.Clear();
            throw new IlUnsupportedException("DynSites:unsupported", null, u);
        }
        DynErase(body);
    }

    /// <summary>`dynamic` as `object` in the declarations the emitter reads as text (locals, casts, defaults).</summary>
    static void DynErase(List<MStmt> body)
    {
        foreach (var node in SmTree.All(body, lambdas: true))
        {
            switch (node)
            {
                case MDecl { TypeCs: "dynamic" } d: d.TypeCs = "object"; break;
                case MCast { TypeCs: "dynamic" } c: c.TypeCs = "object"; break;
            }
            SmTree.MapExprs(node, e => e is AirDefault { Type: AirType.Prim { Kind: PrimKind.Dynamic } } ? new AirDefault(new AirType.Prim(PrimKind.Object)) : e);
        }
    }

    /// <summary>The static field of site <paramref name="id"/> (the holder type is created on first use).</summary>
    internal IlFieldDef DynField(int id)
    {
        if (dynFields.TryGetValue(id, out var f)) return f;
        if (dynHolder == null)
        {
            var sym = new TypeSym { Name = DynSites.HolderName, Kind = "module", Type = new AType("module", DynSites.HolderName) };
            dynHolder = new AliceT { Sym = sym, IsStatic = true };
            dynHolder.Def = Module.DefineType("", DynSites.HolderName, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
            dynHolder.Proxy = Types.MakeProxy(dynHolder, null, null);
            Types.ByCs[DynSites.HolderName] = dynHolder;
        }
        var site = Types.Il(typeof(DynSite));
        f = dynHolder.Def.DefineField("site" + id, site, FieldAttributes.Assembly | FieldAttributes.Static);
        dynHolder.Fields["site" + id] = new AField { Name = "site" + id, Type = typeof(DynSite), IsStatic = true, Owner = dynHolder, Field = f };
        return dynFields[id] = f;
    }

    /// <summary>H4b: the holder's `.cctor` (after every body: <see cref="DynSites.Declaration"/> lists all sites).</summary>
    internal void EmitDynSites()
    {
        var decl = dyn?.Declaration();
        if (decl == null) return;
        decl.Sym = dynHolder?.Sym ?? decl.Sym;
        foreach (var s in dyn!.Sites) DynField(s.Id);
        var cctor = decl.Methods.Single(m => m.Name == ".cctor");
        var md = dynHolder!.Def.DefineConstructor(MethodAttributes.Private | MethodAttributes.HideBySig, isStatic: true);
        Guard(DynSites.HolderName + "::.cctor", () =>
        {
            var b = new IlBody(this, dynHolder, null, Body(md), typeof(void), true, []);
            b.Body(cctor.Body!);
        });
    }

    /// <summary>AL0232 for the module's sites when the build is NativeAOT or wasm (empty otherwise).</summary>
    public List<Diagnostic> DynCheck(bool native, bool wasm) => DynSites.Check(DynSiteDecls, native, wasm, File);
}

internal sealed partial class IlBody
{
    static readonly MethodInfo[] DynMethods = typeof(Dyn).GetMethods(BindingFlags.Public | BindingFlags.Static);

    /// <summary>H1: expressions the core switch does not know.</summary>
    internal Type? RawExt(MExpr e) => e switch
    {
        AirDynCall d => DynCall(d),
        _ => null,
    };

    /// <summary>`Dyn.Kind(ref site, target, args...)`: operands boxed; more operands than the fixed overloads take go in
    /// an object[] wrapped as a ReadOnlySpan.</summary>
    Type DynCall(AirDynCall d)
    {
        var field = c.DynField(d.SiteId);
        string name = d.Kind switch
        {
            DynKind.Call => "Call",
            DynKind.GetMember => "GetMember",
            DynKind.SetMember => "SetMember",
            DynKind.GetIndex => "Index",
            DynKind.SetIndex => "SetIndex",
            DynKind.BinaryOp => "BinaryOp",
            DynKind.UnaryOp => "UnaryOp",
            DynKind.Convert or DynKind.Cast => "Convert",
            DynKind.Invoke => "Invoke",
            _ => throw No("AirDynCall:kind", d.Kind.ToString()),
        };
        il.EmitLoadFieldAddress(field);
        if (name == "Convert")
        {
            var to = c.AirClr(d.To ?? throw No("AirDynCall:convert-type"), span);
            var conv = DynMethods.Single(m => m.Name == "Convert").MakeGenericMethod(to);
            Emit(d.Target!, typeof(object));
            il.EmitCall(T.Method(conv));
            return to;
        }
        Emit(d.Target ?? throw No("AirDynCall:target"), typeof(object));
        // fixed-arity overloads: (ref site, target, object a0, ...); otherwise (ref site, target, ReadOnlySpan<object>)
        int n = d.Args.Count;
        var exact = DynMethods.FirstOrDefault(m => m.Name == name && m.GetParameters() is var ps && ps.Length == n + 2
            && ps.Skip(2).All(p => p.ParameterType == typeof(object)));
        if (exact != null)
        {
            foreach (var a in d.Args) Emit(a, typeof(object));
            il.EmitCall(T.Method(exact));
            return exact.ReturnType;
        }
        var spanM = DynMethods.FirstOrDefault(m => m.Name == name && m.GetParameters() is var ps && ps.Length == 3 && ps[2].ParameterType == typeof(ReadOnlySpan<object>))
            ?? throw No("AirDynCall:arity", name + "/" + n);
        il.EmitLoadConst(n);
        il.EmitNewArray(T.Il(typeof(object)));
        for (int i = 0; i < n; i++)
        {
            il.EmitDup();
            il.EmitLoadConst(i);
            Emit(d.Args[i], typeof(object));
            il.EmitStoreElement(T.Il(typeof(object)));
        }
        il.EmitNewObj(T.Method(typeof(ReadOnlySpan<object>).GetConstructor([typeof(object[])])!));
        il.EmitCall(T.Method(spanM));
        return spanM.ReturnType;
    }
}
