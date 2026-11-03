using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Wasm;

// Bodies the backend writes itself: constructors (field initializers, the base constructor, `initialize(@x)`
// fields), record ToString, the static initializers. Mirrors AirToIl.Decls.cs (CtorPrologue, RecordToString).
public sealed partial class WasmBody
{
    /// <summary>Field initializers, then the base constructor (`super` arguments), then the auto-assigned fields.</summary>
    internal void CtorPrologue(MethodDecl? d)
    {
        var k = owner;
        if (c.instanceInits.TryGetValue(k, out var inits))
            foreach (var (init, f, file) in inits)
            {
                c.File = file;
                var v = c.Init(init, k.Sym!);
                StoreField(f, null, t => () => Emit(v, t));
            }
        if (!k.IsStruct && k.Base != null)
        {
            var args = d?.SuperInit is { } sup ? sup.Select(x => c.Init(x.Value!, k.Sym!)).ToList() : new List<MExpr>();
            if (k.Base.Sym == null)
            {
                // a runtime exception class: Message is the first argument
                if (c.IsException(k.Base))
                {
                    var msg = c.RuntimeClass("System.Exception").FieldsByName["Message"];
                    if (args.Count > 0) StoreField(msg, null, t => () => Emit(args[0], t));
                    else StoreField(msg, null, t => () => { code.I32(c.Literal("Exception of type '" + k.Name + "' was thrown.")); return WT.Str; });
                }
            }
            else
            {
                var ctor = PickCtor(k.Base, args);
                CallCtor(ctor, () => This(false), args);
            }
        }
        if (d != null && self.Sym != null)
            for (int i = 0; i < self.Sym.Params.Count; i++)
            {
                var p = self.Sym.Params[i];
                if (!p.Auto) continue;
                var f = k.FindField(AirToWasm.Bare(Binder.Mangle(p.Name))) ?? throw No("Ctor:auto-field", p.Name);
                var pv = Find(self.ParamNames[i])!;
                StoreField(f, null, t => () => LoadVar(pv));
            }
    }

    /// <summary>A constructor the backend synthesizes: a record's (fields from the parameters) or a default one.</summary>
    internal void SyntheticCtor()
    {
        if (c.recordCtors.TryGetValue(self, out var fields))
        {
            RootParams();
            if (!owner.IsStruct && owner.Base != null && owner.Base.Sym != null && !owner.IsVariant) CallCtor(PickCtor(owner.Base, []), () => This(false), []);
            for (int i = 0; i < fields.Count; i++)
            {
                var pv = Find(self.ParamNames[i])!;
                StoreField(fields[i], null, t => () => LoadVar(pv));
            }
            Epilogue();
            Finish();
            return;
        }
        RootParams();
        CtorPrologue(null);
        Epilogue();
        Finish();
    }

    internal void Synthetic()
    {
        RootParams();
        self.Synthetic!(this);
        Epilogue();
        Finish();
    }

    /// <summary>`Name { a = 1, b = x }` (the C# record ToString; members formatted as ToString).</summary>
    internal void RecordToString()
    {
        var k = owner;
        var fields = k.Sym!.FieldOrder.Select(fn => k.FindField(AirToWasm.Bare(k.Sym.Fields[fn].CsName))!).ToList();
        var acc = FrameTemp(WT.Str);
        code.Get(Fp());
        code.I32(c.Literal(k.Sym.Name + (fields.Count > 0 ? " { " : " { }")));
        code.Mem(Op.I32Store, (uint)acc.Frame);
        for (int i = 0; i < fields.Count; i++)
        {
            code.Get(Fp());
            code.Get(Fp()); code.Mem(Op.I32Load, (uint)acc.Frame);
            code.I32(c.Literal((i > 0 ? ", " : "") + fields[i].Name + " = "));
            code.Call(c.Rt.Func("str_concat"));
            code.Mem(Op.I32Store, (uint)acc.Frame);
            code.Get(Fp());
            code.Get(Fp()); code.Mem(Op.I32Load, (uint)acc.Frame);
            var t = LoadField(fields[i], null);
            ToStr(t, csharp: true);
            code.Call(c.Rt.Func("str_concat"));
            code.Mem(Op.I32Store, (uint)acc.Frame);
        }
        code.Get(Fp()); code.Mem(Op.I32Load, (uint)acc.Frame);
        if (fields.Count > 0) { code.I32(c.Literal(" }")); code.Call(c.Rt.Func("str_concat")); }
        int r = Temp(ValType.I32);
        code.Set(r);
        Epilogue();
        code.Get(r);
        code.Emit(Op.Return);
    }

    /// <summary>Static fields, consts and unit variants in declaration order.</summary>
    internal void StaticInits(List<(WClass Owner, WField F, MExpr? Value, string File)> inits)
    {
        foreach (var (_, f, v, file) in inits)
        {
            if (v == null) continue;
            c.File = file;
            code.I32(f.Address);
            if (f.Type.IsStruct) { StructInto(v, () => code.I32(f.Address), f.Type); code.Emit(Op.Drop); continue; }
            StoreAt(f.Type, () => Emit(v, f.Type));
        }
        Epilogue();
        Finish();
    }
}
