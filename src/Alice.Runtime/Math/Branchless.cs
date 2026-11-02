using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Branch-free select for integers: `c ? a : b` without a jump. RyuJIT turns a ternary into `cmov` only outside
/// loops (its if-conversion skips blocks with a loop weight), so inside a loop, or in a small helper inlined into
/// one, a data-dependent select becomes a mispredicted branch. The condition is materialized with `setcc`
/// (`Unsafe.BitCast&lt;bool, byte&gt;`) and widened to an all-ones or all-zeros mask. Both operands are evaluated:
/// the compiler emits these only for operands that cannot throw or have effects (Optimizer/Passes/BranchlessSelect).
/// </summary>
public static class Branchless
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int I32(bool c, int a, int b) => b ^ ((a ^ b) & -(int)Unsafe.BitCast<bool, byte>(c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint U32(bool c, uint a, uint b) => b ^ ((a ^ b) & (0u - Unsafe.BitCast<bool, byte>(c)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long I64(bool c, long a, long b) => b ^ ((a ^ b) & -(long)Unsafe.BitCast<bool, byte>(c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong U64(bool c, ulong a, ulong b) => b ^ ((a ^ b) & (0ul - Unsafe.BitCast<bool, byte>(c)));
}
