using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>P/Invoke bridge to libalice_native (native/alice_native.c). Falls back to managed SIMD when absent.</summary>
public static unsafe partial class Native
{
    const string Lib = "alice_native";

    public static bool IsAvailable { get; } = Probe();

    static bool Probe()
    {
        try { _ = alice_sum_f64(null, 0); return true; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        Simd.SameLength(a.Length, b.Length);
        if (!IsAvailable) return Simd.Dot(a, b);
        fixed (double* pa = a, pb = b) return alice_dot_f64(pa, pb, a.Length);
    }

    public static double Sum(ReadOnlySpan<double> a)
    {
        if (!IsAvailable) return Simd.Sum(a);
        fixed (double* p = a) return alice_sum_f64(p, a.Length);
    }

    [LibraryImport(Lib)] private static partial double alice_dot_f64(double* a, double* b, long n);
    [LibraryImport(Lib)] private static partial double alice_sum_f64(double* a, long n);
}
