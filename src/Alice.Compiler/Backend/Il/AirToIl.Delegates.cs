using System.Reflection;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// Alice delegate types (<c>delegate Op(a : Int32) : Int32</c>, D1.10): a sealed MulticastDelegate TypeDef with the
/// runtime-implemented constructor and Invoke, plus the reflection proxy (<see cref="IlTypes.MakeDelegateProxy"/>) the
/// emitter types with. Values come from a method group, a `def` reference or (once closures are converted) an
/// AirDelegateCreate.
/// </summary>
public sealed partial class AirToIl
{
    void MakeDelegate(AliceT a, DelegateDecl dg, string ns)
    {
        if (a.Proxy != null) return;
        var inv = ((TypeSym)dg.Sym!).Methods["Invoke"][0];
        var sp = Sp(dg);
        var ret = Types.FromAType(inv.Ret, sp);
        var ps = inv.Params.Select(p => Types.FromAType(p.Type, sp)).ToArray();
        a.Proxy = Types.MakeDelegateProxy(a, ret, ps);
        a.Def = Module.DefineType(ns, a.Sym.Name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.AutoClass, Module.CoreType("MulticastDelegate"));
        var rt = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
        var ctorDef = a.Def.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, [Types.Il(typeof(object)), Types.Il(typeof(IntPtr))]);
        ctorDef.ImplAttributes = rt;
        ctorDef.DefineParameter(1, "object");
        ctorDef.DefineParameter(2, "method");
        var invDef = a.Def.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            Types.Il(ret), [.. ps.Select(Types.Il)]);
        invDef.ImplAttributes = rt;
        for (int i = 0; i < ps.Length; i++) invDef.DefineParameter(i + 1, Bare(inv.Params[i].CsName));
        a.Ctors.Add(new AMethod { Name = ".ctor", Owner = a, Params = [typeof(object), typeof(IntPtr)], ParamNames = ["object", "method"], IsCtor = true, Def = ctorDef });
        a.Methods.Add(new AMethod
        {
            Name = "Invoke", Owner = a, Ret = ret, Params = ps, ParamNames = [.. inv.Params.Select(p => Bare(p.CsName))], IsVirtual = true, Def = invDef, Sym = inv,
        });
    }
}

internal sealed partial class IlBody
{
    /// <summary>A bare method name used as a value (`->add`, a `def` reference): resolved against the current type and the Program.</summary>
    bool IsMethodGroup(MExpr e) =>
        e is MCode mc && IsIdentifier(mc.Text.Trim()) && !IsVar(mc.Text.Trim()) && Implicit(mc.Text.Trim()) is null
        && GroupHosts().Any(h => h.FindMethods(Bare(mc.Text.Trim())).Any(m => !m.IsCtor));

    static bool IsIdentifier(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '?' || ch == '!');

    IEnumerable<AliceT> GroupHosts() => owner == c.ProgramT ? [c.ProgramT] : [owner, c.ProgramT];

    /// <summary>`ldnull/this; ldftn target; newobj Delegate::.ctor` for a method group converted to <paramref name="want"/>.</summary>
    bool MethodGroup(MExpr e, Type want)
    {
        if (!IsMethodGroup(e) || want.GetMethod("Invoke") is not { } inv) return false;
        string name = Bare(((MCode)e).Text.Trim());
        var infos = inv.GetParameters().Select(p => new ArgInfo(p.ParameterType, null, false, "")).ToList();
        foreach (var host in GroupHosts())
        {
            var cand = T.Pick(host.FindMethods(name).Where(m => !m.IsCtor).Select(IlTypes.FromAlice), infos);
            if (cand?.Alice is not { } am || !(am.IsStatic || host == owner && !isStatic)) continue;
            if (am.Def == null) continue;
            if (am.IsStatic) il.EmitLoadNull(); else il.EmitLoadArg(0);
            il.EmitLoadFunction(am.Def);
            il.EmitNewObj(T.Alice(want) is { } da ? da.Ctors[0].Def : T.Method(want.GetConstructors()[0]));
            return true;
        }
        return false;
    }
}

public sealed partial class AirToIl
{
    /// <summary>The constant value of an enum member initializer: literals, earlier members (`A | B`), arithmetic and shifts
    /// (null: not a constant the emitter can fold).</summary>
    static long? FoldEnumValue(MExpr e, Dictionary<string, long> known, string enumName)
    {
        switch (e)
        {
            case AirConst { Value: { } cv } when cv is int or long or uint or ulong or short or ushort or byte or sbyte or char: return System.Convert.ToInt64(cv);
            case MBinary b when FoldEnumValue(b.L, known, enumName) is { } l && FoldEnumValue(b.R, known, enumName) is { } r:
                return b.Op switch
                {
                    AirBinOp.Add => l + r, AirBinOp.Sub => l - r, AirBinOp.Mul => l * r, AirBinOp.Div when r != 0 => l / r, AirBinOp.Mod when r != 0 => l % r,
                    AirBinOp.Shl => l << (int)r, AirBinOp.Shr => l >> (int)r, AirBinOp.UShr => (long)((ulong)l >> (int)r),
                    AirBinOp.BitAnd => l & r, AirBinOp.BitOr => l | r, AirBinOp.BitXor => l ^ r,
                    _ => null,
                };
            case MUnary u when FoldEnumValue(u.E, known, enumName) is { } x:
                return u.Op switch { AirUnOp.Neg => -x, AirUnOp.BitNot => ~x, _ => null };
            case MCode c:
                {
                    string t = c.Text.Trim();
                    if (IlTypes.TryLiteral(t, out _, out var lit) && lit is int or long or uint or ulong) return System.Convert.ToInt64(lit);
                    if (known.TryGetValue(t, out var k)) return k;
                    if (t.StartsWith(enumName + ".") && known.TryGetValue(t[(enumName.Length + 1)..], out var k2)) return k2;
                    return null;
                }
        }
        return null;
    }
}
