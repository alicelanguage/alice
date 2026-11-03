using System.Reflection;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Binder = Alice.Compiler.Semantics.Binder;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// D1.10 consumer: `lib` blocks through <see cref="AirToIlFfi"/> (ADR 0018). Each `lib` is a static class with one
/// P/Invoke (+ marshalling wrapper) per `fun`; Alice calls bind to the wrapper like any static method of an Alice
/// type. Struct parameters (`out ts : Timespec`) resolve to the module's own TypeDefs.
/// </summary>
public sealed partial class AirToIl
{
    AirToIlFfi? ffi;

    internal AirToIlFfi Ffi => ffi ??= new AirToIlFfi(Module, cs => Types.TryParse(cs) is { } t ? Types.Il(t) : null);

    /// <summary>H5: every `lib` of the program (after the TypeDefs exist: wrappers may name user structs).</summary>
    void DeclareLibs(IEnumerable<(Item Item, string Ns, Item? Outer)> all)
    {
        foreach (var (it, ns, _) in all)
        {
            if (it is not LibDecl l) continue;
            var sym = (TypeSym)l.Sym!;
            var def = Ffi.EmitLib(l, ns);
            var a = new AliceT { Sym = sym, IsStatic = true, Def = def };
            a.Proxy = Types.MakeProxy(a, null, null);
            Types.BySym[sym] = a;
            Types.ByCs[sym.Type.Cs] = a;
            foreach (var f in l.Funs)
            {
                var m = sym.Methods[f.Name][0];
                var ps = m.Params.Select(p =>
                {
                    var t = Types.FromAType(p.Type, Sp(f));
                    return p.Mod is "out" or "ref" ? t.MakeByRefType() : t;
                }).ToArray();
                a.Methods.Add(new AMethod
                {
                    Name = Bare(m.CsName), Owner = a, IsStatic = true, Ret = m.Ret.IsVoid ? typeof(void) : Types.FromAType(m.Ret, Sp(f)),
                    Params = ps, ParamNames = [.. m.Params.Select(p => Bare(p.CsName))], Def = Ffi.Funs[m], Sym = m,
                });
            }
        }
    }

    /// <summary>
    /// A bare MIR body as `public static string Program.Run()` (tests of the IL lowering passes: their bodies append to a
    /// `StringBuilder __o`, which Run returns). <paramref name="refs"/>: assemblies the body's C# type names live in.
    /// The body is emitted as given (no binder, lowering or MIR pipeline runs over it).
    /// </summary>
    public static IlCompileResult CompileBody(List<MStmt> body, string name, IEnumerable<string>? refs = null)
    {
        var c = new AirToIl();
        return c.RunBody(body, name, refs);
    }

    IlCompileResult RunBody(List<MStmt> body, string name, IEnumerable<string>? refs)
    {
        var ast = Parser.ParseFile("", "body.al");
        binder = new Binder([ast], new ClrTypes(refs));
        binder.Bind();
        lowerer = new Lowerer(binder) { Sequential = true };
        Module = new IlModule(name);
        Types = new IlTypes(binder, Module);
        hot = new HotPolicy(binder);
        try
        {
            DeclareTypes([ast]);
            if (SynthOn) SynthDeclareMembers([ast]); else DeclareMembers([ast]);
            var md = ProgramT.Def.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, IlType.String);
            Guard("Run", () =>
            {
                var sb = new AirType.ClrRef("global::System.Text.StringBuilder", typeof(System.Text.StringBuilder), []);
                List<MStmt> full =
                [
                    new MDecl { Name = "__o", TypeCs = sb.ToCs(), Init = new MNew { TypeCs = sb.ToCs(), Type = sb } },
                    .. body,
                    new MReturn { Value = new MCall { Target = new AirLocalRef("__o", sb), Name = "ToString", Type = new AirType.Prim(PrimKind.String) } },
                ];
                var self = new AMethod { Name = "Run", Owner = ProgramT, IsStatic = true, Def = md, Ret = typeof(string) };
                new IlBody(this, ProgramT, self, Body(md), typeof(string), true, []).Body(full);
            });
            EmitDynSites();
        }
        catch (IlUnsupportedException e) { Fail(e); }
        if (errors.Count > 0) return new IlCompileResult(null, null, Stats, errors);
        var (pe, pdb) = Module.Emit();
        return new IlCompileResult(pe, pdb, Stats, errors) { Module = Module };
    }
}
