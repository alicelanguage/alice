using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il.Opt;

/// <summary>
/// Proven-in-bounds array accesses (reports/il-shapes.md R2): <see cref="Air.Passes.IlOpt.InBounds"/> marks the
/// <see cref="MIndex"/> nodes whose index it proved inside the array's exact length; AirToIl emits those as
/// `ld array; Head; ld index; Tail` = `GetArrayDataReference(a)` then `Unsafe.Add(ref, (nuint)index)`, a byref the
/// caller loads (`ldobj`) or stores (`stobj`) through. No bounds check, no `movsxd` (the index is proven
/// non-negative, so it is zero-extended), and still ILVerify clean (calls, no byref arithmetic). Unmarked nodes keep
/// `ldelem`/`stelem`.
/// </summary>
public static class UncheckedIndex
{
    static readonly ConditionalWeakTable<MIndex, object> proven = new();
    static readonly object Mark = new();

    /// <summary>Whether <paramref name="ix"/> was proven in bounds (and the pass is on).</summary>
    public static bool Proven(MIndex ix) => IlOpt.On(IlOptPass.InBounds) && proven.TryGetValue(ix, out _);

    internal static void Prove(MIndex ix) => proven.AddOrUpdate(ix, Mark);

    static readonly MethodInfo ArrayData = typeof(MemoryMarshal).GetMethods()
        .Single(m => m.Name == "GetArrayDataReference" && m.IsGenericMethodDefinition && m.GetParameters()[0].ParameterType.IsArray);

    static readonly MethodInfo AddNuint = typeof(Unsafe).GetMethods()
        .Single(m => m.Name == "Add" && m.IsGenericMethodDefinition && m.GetParameters() is [{ ParameterType.IsByRef: true }, { ParameterType: var p }] && p == typeof(nuint));

    /// <summary>Stack `T[]` to `ref T` (the array's first element; the JIT folds it to `array + data offset`).</summary>
    public static void Head(IlBuilder il, IlModule module, IlType elem) => il.EmitCall(module.Import(ArrayData).MakeGeneric(elem));

    /// <summary>Stack `ref T, int32 index` (proven in [0, length)) to `ref T` at that index.</summary>
    public static void Tail(IlBuilder il, IlModule module, IlType elem)
    {
        il.Emit(ILOpCode.Conv_u);
        il.EmitCall(module.Import(AddNuint).MakeGeneric(elem));
    }
}
