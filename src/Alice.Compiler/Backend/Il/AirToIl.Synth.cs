using System.Reflection;
using System.Reflection.Metadata;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Binder = Alice.Compiler.Semantics.Binder;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// D1.6 consumer: the declarations Roslyn synthesizes for the C# backend, taken from <see cref="Synthesize.Run"/>'s
/// <see cref="AirModuleDecl"/> instead of being mirrored from Backend/Emitter.cs. Records and record structs get
/// the Roslyn members (value equality, GetHashCode, PrintMembers/ToString, Deconstruct, `&lt;Clone&gt;$`, `__With`,
/// IEquatable), sum types their base and variant members (Tag, Is&lt;Variant&gt;, unit `Value`), classes their
/// auto-properties, default constructors, derived operators, the IEnumerable bridge and type initializers.
/// User members (plain fields, constants, methods, user constructors) are still declared by the driver
/// (<see cref="DeclareMethod"/>, <see cref="Field"/>); <see cref="AirTypeDecl.FieldInits"/> run at the start of every
/// user constructor (<see cref="SynthCtorPrologue"/>). `ALICE_IL_SYNTH=0` keeps the mirrored declarations (A/B).
/// </summary>
public sealed partial class AirToIl
{
    /// <summary>Whether declarations come from Synthesize (default) or from the Emitter mirror (`ALICE_IL_SYNTH=0`).</summary>
    internal static readonly bool SynthOn = Environment.GetEnvironmentVariable("ALICE_IL_SYNTH") != "0";

    AirModuleDecl? synth;
    readonly List<(AliceT Owner, AirMethodDecl Decl, AMethod M)> synthBodies = [];

    /// <summary>S2: the member declarations (replaces DeclareMembers when <see cref="SynthOn"/>).</summary>
    void SynthDeclareMembers(List<FileNode> asts)
    {
        synth = Synthesize.Run(binder);
        foreach (var (it, a) in decls)
        {
            File = FileOf(asts, it);
            if (it is TypeDecl td) SynthUserMembers(td, a);
        }
        foreach (var ast in asts)
        {
            File = ast.Path;
            foreach (var it in ast.Items.Concat(ast.Items.OfType<NamespaceItem>().SelectMany(n => n.Items)))
                switch (it)
                {
                    case MethodDecl m: DeclareMethod(m, ProgramT); break;
                    case ConstDecl k: SynthConst(k, ProgramT); break;
                }
        }
        foreach (var a in decls.Select(d => d.T).Distinct())
        {
            if (a == ProgramT || a.IsEnum || a.IsInterface || a.IsStatic || a.IsAdt) continue;
            foreach (var f in a.Sym.Fields.Values)
                if (f.Kind is "inferred" or "inferred-emitted" or "auto" or "auto-emitted" && !a.Fields.ContainsKey(Bare(f.CsName)))
                    Field(a, f, FieldAttributes.Private);
        }
        foreach (var t in synth.Types)
        {
            if (genericBySym.ContainsKey(t.Sym)) continue;   // declared per instance (DeclareInstance)
            var a = t.Sym == binder.Program ? ProgramT : Types.BySym.GetValueOrDefault(t.Sym)
                ?? throw new IlUnsupportedException("Synth:type", null, t.Name);
            SynthDeclare(a, t);
        }
        if (synth.ModuleInitializer != null) throw new IlUnsupportedException("Decl:class-body (module initializer)", null);
        declaring = 0;
        DrainInstances();
    }

    /// <summary>The user members of a type declaration: plain fields (initializers run from FieldInits / `.cctor`),
    /// constant fields, methods and user constructors. Accessors and record parameters are synthesized.</summary>
    void SynthUserMembers(TypeDecl d, AliceT a)
    {
        var s = a.Sym;
        if (d.Members.Any(m => m is MethodDecl { Name: "__class_body" })) throw new IlUnsupportedException("Decl:class-body", Sp(d), s.Name);
        foreach (var it in d.Members)
        {
            switch (it)
            {
                case AccessorDecl { Kind: "event" } ev: throw new IlUnsupportedException("Decl:event", Sp(ev), ((FieldSym)ev.Sym!).Name);
                case FieldDecl fd:
                    {
                        if (fd.Attrs.Count > 0) throw new IlUnsupportedException("Decl:field-attribute", Sp(fd), string.Join(",", fd.Attrs));
                        Field(a, (FieldSym)fd.Sym!, FieldAttributes.Private);
                        break;
                    }
                case ConstDecl k: SynthConst(k, a); break;
                case MethodDecl { IsTemplate: true }: break;
                case MethodDecl m: DeclareMethod(m, a); break;
            }
        }
    }

    /// <summary>A constant: a static readonly field; its value is assigned by the synthesized `.cctor`.</summary>
    void SynthConst(ConstDecl k, AliceT a)
    {
        var f = (FieldSym)k.Sym!;
        var t = Types.FromAType(f.Type);
        string n = Bare(f.CsName);
        var fd = a.Def.DefineField(n, Types.Il(t), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        a.Fields[n] = new AField { Name = n, Type = t, IsStatic = true, Owner = a, Field = fd, ReadOnly = true };
    }

    /// <summary>Declares the synthesized members of <paramref name="d"/> on <paramref name="a"/> (and its nested variants).</summary>
    void SynthDeclare(AliceT a, AirTypeDecl d)
    {
        if (a.IsVariant && d.Base != null) a.Def.BaseType = SynthIl(d.Base);
        var have = a.Def.Interfaces.ToList();
        foreach (var i in d.Interfaces)
        {
            var it = SynthIl(i);
            if (!have.Any(h => h.ToString() == it.ToString())) a.Def.Interfaces.Add(it);
        }
        foreach (var f in d.Fields)
        {
            if (a.Fields.ContainsKey(f.Name)) continue;
            var ft = AirClr(f.Type, null);
            var attrs = Vis(f.Vis) switch
            {
                MethodAttributes.Public => FieldAttributes.Public,
                MethodAttributes.Family => FieldAttributes.Family,
                MethodAttributes.Assembly => FieldAttributes.Assembly,
                _ => FieldAttributes.Private,
            } | (f.Static ? FieldAttributes.Static : 0) | (f.ReadOnly ? FieldAttributes.InitOnly : 0);
            var fd = a.Def.DefineField(f.Name, Types.Il(ft), attrs);
            a.Fields[f.Name] = new AField { Name = f.Name, Type = ft, IsStatic = f.Static || a.IsStatic, Owner = a, Field = fd, ReadOnly = f.ReadOnly };
        }
        var made = new Dictionary<AirMethodDecl, AMethod>();
        foreach (var md in d.Methods)
        {
            if (md.User != null) continue;   // `def to_s` is declared as ToString by DeclareMethod
            var am = SynthMethod(a, md);
            made[md] = am;
            if (md.Body != null) synthBodies.Add((a, md, am));
        }
        foreach (var p in d.Properties)
        {
            var get = p.Get != null && made.TryGetValue(p.Get, out var g) ? g.Def : null;
            var set = p.Set != null && made.TryGetValue(p.Set, out var st) ? st.Def : null;
            var pt = AirClr(p.Type, null);
            a.Def.DefineProperty(p.Name, Types.Il(pt), get, set);
            var backing = a.Fields.GetValueOrDefault(Synthesize.BackingField(p.Name));
            a.Fields[p.Name] = new AField { Name = p.Name, Type = pt, IsStatic = p.Static || a.IsStatic, Owner = a, Field = backing?.Field, Getter = get, Setter = set };
        }
        foreach (var n in d.Nested)
        {
            var va = VariantOf(a, n.Sym) ?? throw new IlUnsupportedException("Synth:nested", null, n.Name);
            SynthDeclare(va, n);
        }
    }

    AMethod SynthMethod(AliceT a, AirMethodDecl md)
    {
        bool ctor = md.Is(AirMethodFlags.Ctor);
        bool cctor = ctor && md.Is(AirMethodFlags.Static);
        bool st = md.Is(AirMethodFlags.Static) || a.IsStatic && !ctor;
        var ps = md.Params.Select(p => AirClr(p.Type, null)).ToArray();
        var ilps = ps.Select(Types.Il).ToArray();
        var ret = ctor ? typeof(void) : AirClr(md.Ret, null);
        IlMethodDef def;
        if (ctor) def = a.Def.DefineConstructor(Vis(md.Vis) | MethodAttributes.HideBySig, ilps, isStatic: cctor);
        else
        {
            bool virt = md.Is(AirMethodFlags.Virtual) || md.Is(AirMethodFlags.Override) || md.Is(AirMethodFlags.Abstract);
            var attrs = Vis(md.Vis) | MethodAttributes.HideBySig
                | (st ? MethodAttributes.Static : 0)
                | (virt ? MethodAttributes.Virtual : 0)
                | (virt && !md.Is(AirMethodFlags.Override) ? MethodAttributes.NewSlot : 0)
                | (md.Is(AirMethodFlags.Final) ? MethodAttributes.Final : 0)
                | (md.Is(AirMethodFlags.Abstract) ? MethodAttributes.Abstract : 0)
                | (md.Is(AirMethodFlags.SpecialName) ? MethodAttributes.SpecialName : 0);
            var rt = Types.Il(ret);
            if (md.Is(AirMethodFlags.InitOnly)) rt = rt.WithModifier(Module.Import(typeof(System.Runtime.CompilerServices.IsExternalInit)), required: true);
            def = a.Def.DefineMethod(md.Name, attrs, rt, ilps);
        }
        for (int i = 0; i < md.Params.Count; i++) def.DefineParameter(i + 1, md.Params[i].Name);
        var am = new AMethod
        {
            Name = cctor ? ".cctor" : ctor ? ".ctor" : md.Name, Owner = a, Ret = ret, Params = ps,
            ParamNames = [.. md.Params.Select(p => p.Name)], IsStatic = st || cctor, IsCtor = ctor && !cctor,
            IsVirtual = (def.Attributes & MethodAttributes.Virtual) != 0, IsAbstract = md.Is(AirMethodFlags.Abstract), Def = def, Scope = Types.Scope,
        };
        if (md.Implements is { } impl)
        {
            var it = AirClr(impl.Interface, null);
            var im = InterfaceMethod(it, impl.Method, ps) ?? throw new IlUnsupportedException("Synth:implements", null, it.Name + "." + impl.Method);
            a.Def.MethodOverrides.Add((def, Types.Method(im)));
        }
        if (am.IsCtor) a.Ctors.Add(am);
        else if (!cctor) a.Methods.Add(am);
        return am;
    }

    IlType SynthIl(AirType t) => Types.Il(AirClr(t, null));

    /// <summary>S3: the synthesized bodies (after the user bodies; replaces SyntheticCtor and StaticCtor).</summary>
    void SynthBodies()
    {
        while (emittedS < synthBodies.Count)
        {
            var (a, md, am) = synthBodies[emittedS++];
            Guard(a + "::" + am.Name, () =>
            {
                Types.Scope = am.Scope;
                try { SynthBody(a, md, am); } finally { Types.Scope = null; }
            });
        }
    }

    void SynthBody(AliceT a, AirMethodDecl md, AMethod am)
    {
        {
            {
                var body = SmTree.CloneList(md.Body!);
                if (md.Synth is AirSynth.TypeInitializer or AirSynth.DefaultCtor or AirSynth.StructCtor or AirSynth.PrimaryCtor)
                {
                    // initializers are user expressions: the same MIR pipeline as any body
                    Optimizer.Pipeline.Run(body);
                    DynLower(body);
                    IlLower(body);
                }
                bool isCctor = am.Name == ".cctor";
                var b = new IlBody(this, a, isCctor ? null : am, Body(am.Def), am.Ret, am.IsStatic,
                    am.ParamNames.Select((n, i) => (n, am.Params[i])).ToList());
                b.Body(SmAdapt(body));
            }
        }
    }

    /// <summary>S4: a user constructor's prologue: the type's FieldInits, the base constructor (with `super`
    /// arguments), then `initialize(@x)` auto assignments.</summary>
    void SynthCtorPrologue(IlBody b, AMethod ctor, MethodDecl? d)
    {
        var a = ctor.Owner;
        var il = ctor.Def.Il;
        if (a.Sym.Synth is { FieldInits.Count: > 0 } decl)
        {
            var inits = SmTree.CloneList(decl.FieldInits);
            Optimizer.Pipeline.Run(inits);
            DynLower(inits);
            IlLower(inits);
            b.EmitStmts(SmAdapt(inits));
        }
        if (!a.IsValueType)
        {
            il.EmitLoadArg(0);
            if (d?.SuperInit is { } sup)
            {
                var args = sup.Select(x => Init(x.Value!, a.Sym)).ToList();
                var infos = args.Select(x => new ArgInfo(b.Infer(x), null, false, "")).ToList();
                if (a.Base != null)
                {
                    var cand = Types.Pick(a.Base.Ctors.Select(IlTypes.FromAlice), infos) ?? throw new IlUnsupportedException("Ctor:super", Sp(d), a.ToString());
                    for (int i = 0; i < args.Count; i++) b.Emit(args[i], cand.Params[i]);
                    il.EmitCall(cand.Alice!.Def);
                }
                else
                {
                    var bt = a.ClrBase ?? typeof(object);
                    var cand = Types.Pick(bt.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(x => !x.IsPrivate).Select(x => IlTypes.FromClr(x)), infos)
                        ?? throw new IlUnsupportedException("Ctor:super", Sp(d), bt.Name);
                    for (int i = 0; i < args.Count; i++) b.Emit(args[i], cand.Params[i]);
                    il.EmitCall(Types.Method(cand.Clr!));
                }
            }
            else BaseDefaultCtor(a, il);
        }
        if (d != null && ctor.Sym != null)
            for (int i = 0; i < ctor.Sym.Params.Count; i++)
            {
                var p = ctor.Sym.Params[i];
                if (!p.Auto) continue;
                var f = a.FindField(Bare(Binder.Mangle(p.Name))) ?? throw new IlUnsupportedException("Ctor:auto-field", Sp(d), p.Name);
                il.EmitLoadArg(0);
                il.EmitLoadArg(i + 1);
                if (f.Field != null) il.EmitStoreField(f.Field); else il.EmitCall(f.Setter!);
            }
    }
}

internal sealed partial class IlBody
{
    /// <summary>A statement list in the current scope (constructor prologues).</summary>
    internal void EmitStmts(List<MStmt> list) => Stmts(list, scope: false);
}
