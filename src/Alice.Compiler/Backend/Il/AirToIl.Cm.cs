using System.Reflection;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// The Connection-Machine nodes (Mir/MExpr.Cm.cs, ADR 0039; IL form ADR 0040 section 3) as direct calls into
/// <c>Alice.Runtime.Cm</c>: each array operand becomes a span over <c>[0, n)</c> (`newobj Span&lt;T&gt;(T[], int, int)`),
/// the generic kernel is instantiated at the node's element type, and the operator is its <c>CmOp</c> constant. These
/// are the calls the C# backend prints (Backend/Printer.Cm.cs), so the two backends agree by construction; the
/// kernels themselves are the runtime's (vectorized, chunked by n only, 0-alloc, dispatched by KernelPool).
/// </summary>
internal sealed partial class IlBody
{
    static readonly Type CmRt = typeof(Alice.Runtime.Cm);

    /// <summary>The kernel named <paramref name="name"/> whose parameter types (open generic definitions) match.</summary>
    static MethodInfo CmKernel(string name, params Type[] shape) =>
        CmRt.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == name && m.IsGenericMethodDefinition
            && m.GetParameters().Length == shape.Length
            && m.GetParameters().Zip(shape).All(p => Open(p.First.ParameterType) == p.Second));

    static Type Open(Type t) => t.IsGenericType ? t.GetGenericTypeDefinition() : t.IsArray ? typeof(Array) : t;

    Type CmElem(AirType t) => T.Parse(t.ToCs(), span);

    /// <summary>Pushes `new Span&lt;T&gt;(arr, 0, n)` (or `new Span&lt;T&gt;(arr)` without a count); returns the span type.</summary>
    Type CmSpan(Type open, Type elem, MExpr arr, MExpr? n)
    {
        var st = open.MakeGenericType(elem);
        Emit(arr, elem.MakeArrayType());
        if (n == null)
        {
            il.EmitNewObj(c.Module.Import(st.GetConstructor([elem.MakeArrayType()])!));
            return st;
        }
        il.EmitLoadConst(0);
        Emit(n, typeof(int));
        il.EmitNewObj(c.Module.Import(st.GetConstructor([elem.MakeArrayType(), typeof(int), typeof(int)])!));
        return st;
    }

    void CmCall(MethodInfo open, Type elem) => il.EmitCall(c.Module.Import(open.MakeGenericMethod(elem)));

    Type Cm(AirCmExpr e)
    {
        if (e is AirCmDistinct d)
        {
            Emit(d.W, typeof(object));
            Emit(d.R, typeof(object));
            il.Emit(System.Reflection.Metadata.ILOpCode.Ceq);
            il.EmitLoadConst(0);
            il.Emit(System.Reflection.Metadata.ILOpCode.Ceq);
            return typeof(bool);
        }
        var elem = CmElem(e.Elem);
        Type ros = typeof(ReadOnlySpan<>), sp = typeof(Span<>);
        switch (e)
        {
            case AirScan s:
                CmSpan(ros, elem, s.Src, s.Count);
                CmSpan(sp, elem, s.Dst, s.Count);
                il.EmitLoadConst((int)s.Op);
                CmCall(CmKernel(s.Inclusive ? "InclusiveScan" : "ExclusiveScan", ros, sp, typeof(Alice.Runtime.CmOp)), elem);
                return typeof(void);
            case AirSegmented g:
                CmSpan(ros, elem, g.Src, g.Count);
                CmSpan(ros, typeof(bool), g.Flags, g.Count);
                CmSpan(sp, elem, g.Dst, g.Count);
                il.EmitLoadConst((int)g.Op);
                CmCall(CmKernel("SegmentedScan", ros, ros, sp, typeof(Alice.Runtime.CmOp)), elem);
                return typeof(void);
            case AirCombine cb:
                {
                    var idx = CmElem(cb.IdxElem);
                    if (cb.Vals == null)
                    {
                        CmSpan(ros, idx, cb.Idx, cb.Count);
                        CmSpan(sp, elem, cb.Target, null);
                        var h = CmRt.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "Histogram" && m.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<>).MakeGenericType(idx));
                        CmCall(h, elem);
                        return typeof(void);
                    }
                    CmSpan(sp, elem, cb.Target, null);
                    CmSpan(ros, idx, cb.Idx, cb.Count);
                    if (idx != typeof(int))
                    {
                        var cast = typeof(System.Runtime.InteropServices.MemoryMarshal).GetMethods().Single(m => m.Name == "Cast" && m.GetGenericArguments().Length == 2
                            && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>));
                        il.EmitCall(c.Module.Import(cast.MakeGenericMethod(idx, typeof(int))));
                    }
                    CmSpan(ros, elem, cb.Vals, cb.Count);
                    CmCall(CmKernel("ScatterAdd", sp, ros, ros), elem);
                    return typeof(void);
                }
            case AirCompact k:
                Emit(k.Src, elem.MakeArrayType());
                CmSpan(ros, typeof(bool), k.Flags, k.Count);
                Emit(k.Dst, elem.MakeArrayType());
                CmCall(CmKernel("Compact", typeof(Array), ros, typeof(Array)), elem);
                return typeof(int);
            case AirPermute p:
                Emit(p.Src, elem.MakeArrayType());
                CmSpan(ros, typeof(int), p.Perm, p.Count);
                Emit(p.Dst, elem.MakeArrayType());
                CmCall(CmKernel("Permute", typeof(Array), ros, typeof(Array)), elem);
                return typeof(void);
        }
        throw No(e.GetType().Name);
    }
}
