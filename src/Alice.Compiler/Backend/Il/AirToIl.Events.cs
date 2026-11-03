using System.Reflection;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// Events (D1.10): `event clicked : EventHandler` is a private delegate field, `add_clicked`/`remove_clicked` that combine
/// with `Interlocked.CompareExchange` (Roslyn's field-like event), and an event row. Inside the class `clicked` reads the field
/// (`clicked&amp;.Invoke(self, args)`); `obj.clicked += h` / `-= h` calls the accessor (<see cref="IlBody"/> Assign), for CLR events too.
/// </summary>
public sealed partial class AirToIl
{
    void DeclareEvent(AliceT a, AccessorDecl ev)
    {
        var fs = (FieldSym)ev.Sym!;
        var sp = Sp(ev);
        if (a.IsInterface) throw new IlUnsupportedException("Decl:event (interface)", sp, fs.Name);
        var ht = Types.FromAType(fs.Type, sp);
        if (!typeof(Delegate).IsAssignableFrom(ht)) throw new IlUnsupportedException("Decl:event (not a delegate type)", sp, fs.Name);
        string name = Bare(fs.CsName);
        bool st = fs.IsStatic || a.IsStatic;
        var hil = Types.Il(ht);
        var fd = a.Def.DefineField(name, hil, FieldAttributes.Private | (st ? FieldAttributes.Static : 0));
        var attrs = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | (st ? MethodAttributes.Static : MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot);
        var add = a.Def.DefineMethod("add_" + name, attrs, IlType.Void, [hil]);
        var remove = a.Def.DefineMethod("remove_" + name, attrs, IlType.Void, [hil]);
        add.DefineParameter(1, "value");
        remove.DefineParameter(1, "value");
        Accessor(add, fd, ht, hil, st, "Combine");
        Accessor(remove, fd, ht, hil, st, "Remove");
        a.Def.DefineEvent(name, hil, add, remove);
        a.Fields[name] = new AField { Name = name, Type = ht, IsStatic = st, Owner = a, Field = fd, Adder = add, Remover = remove };
    }

    void Accessor(IlMethodDef m, IlFieldDef f, Type ht, IlType hil, bool st, string combine)
    {
        var il = Body(m);
        var cur = il.DeclareLocal(hil);
        var orig = il.DeclareLocal(hil);
        var upd = il.DeclareLocal(hil);
        var loop = il.DefineLabel();
        int arg = st ? 0 : 1;
        if (!st) il.EmitLoadArg(0);
        il.EmitLoadField(f);
        il.EmitStoreLocal(cur);
        il.MarkLabel(loop);
        il.EmitLoadLocal(cur);
        il.EmitStoreLocal(orig);
        il.EmitLoadLocal(orig);
        il.EmitLoadArg(arg);
        il.EmitCall(Module.Import(typeof(Delegate).GetMethod(combine, [typeof(Delegate), typeof(Delegate)])!));
        il.EmitCastClass(hil);
        il.EmitStoreLocal(upd);
        if (!st) il.EmitLoadArg(0);
        il.EmitLoadFieldAddress(f);
        il.EmitLoadLocal(upd);
        il.EmitLoadLocal(orig);
        var cas = typeof(System.Threading.Interlocked).GetMethods().First(x => x.Name == "CompareExchange" && x.IsGenericMethodDefinition && x.GetParameters().Length == 3).MakeGenericMethod(ht);
        il.EmitCall(Types.Method(cas));
        il.EmitStoreLocal(cur);
        il.EmitLoadLocal(cur);
        il.EmitLoadLocal(orig);
        il.EmitBranch(loop, IlCond.NotEq);
        il.EmitReturn();
    }
}

internal sealed partial class IlBody
{
    /// <summary>`target.event += h` / `-= h`: the add_/remove_ accessor of an Alice or CLR event.</summary>
    bool EventAssign(MMember m, Type rt, string name, string op, MExpr value)
    {
        if (op is not ("+=" or "-=")) return false;
        bool add = op == "+=";
        if (T.Alice(rt)?.FindField(name) is { Adder: { } adder, Remover: { } remover, IsStatic: false } af)
        {
            Receiver(m.Target, rt);
            Emit(value, af.Type);
            il.EmitCall(add ? adder : remover, virtualCall: !rt.IsValueType);
            return true;
        }
        if (T.Alice(rt) == null && rt.GetEvent(name, BindingFlags.Public | BindingFlags.Instance) is { } ev && (add ? ev.AddMethod : ev.RemoveMethod) is { } acc && ev.EventHandlerType is { } ht)
        {
            Receiver(m.Target, rt);
            Emit(value, ht);
            EmitInstanceCall(rt, acc);
            return true;
        }
        return false;
    }
}
