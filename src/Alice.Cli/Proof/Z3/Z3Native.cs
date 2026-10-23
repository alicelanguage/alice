using System.Runtime.InteropServices;

namespace Alice.Cli.Proof.Z3;

/// <summary>
/// Z3's C API, loaded from the system libz3 at first use (ADR 0022). No managed binding: Microsoft.Z3 is x64-only, the
/// C API is stable and the same on every architecture. Exports are resolved once and called through unmanaged
/// function pointers.
/// </summary>
static unsafe class Z3Native
{
    static IntPtr lib;
    static readonly Dictionary<string, IntPtr> exports = new();

    /// <summary>The system library (`pacman -S z3`, `apt install libz3-4`, `brew install z3`) or ALICE_Z3=/path.</summary>
    public static bool Load()
    {
        if (lib != IntPtr.Zero) return true;
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("ALICE_Z3") is { Length: > 0 } custom) candidates.Add(custom);
        candidates.AddRange(OperatingSystem.IsWindows() ? ["libz3.dll", "z3.dll"]
            : OperatingSystem.IsMacOS() ? ["libz3.dylib", "/opt/homebrew/lib/libz3.dylib", "/usr/local/lib/libz3.dylib"]
            : ["libz3.so", "libz3.so.4"]);
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, out lib)) return true;
        return false;
    }

    static IntPtr F(string name)
    {
        lock (exports)
        {
            if (!exports.TryGetValue(name, out var p)) exports[name] = p = NativeLibrary.GetExport(lib, name);
            return p;
        }
    }

    static string? Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    // ---- context ----
    public static IntPtr MkConfig() => ((delegate* unmanaged<IntPtr>)F("Z3_mk_config"))();
    public static void SetParamValue(IntPtr cfg, string k, string v)
    {
        IntPtr pk = Marshal.StringToCoTaskMemUTF8(k), pv = Marshal.StringToCoTaskMemUTF8(v);
        try { ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)F("Z3_set_param_value"))(cfg, pk, pv); }
        finally { Marshal.FreeCoTaskMem(pk); Marshal.FreeCoTaskMem(pv); }
    }
    public static IntPtr MkContext(IntPtr cfg) => ((delegate* unmanaged<IntPtr, IntPtr>)F("Z3_mk_context"))(cfg);
    public static void DelConfig(IntPtr cfg) => ((delegate* unmanaged<IntPtr, void>)F("Z3_del_config"))(cfg);
    public static void DelContext(IntPtr c) => ((delegate* unmanaged<IntPtr, void>)F("Z3_del_context"))(c);
    // the default handler exits the process; with none, a misuse leaves an error code instead
    public static void SetErrorHandler(IntPtr c) => ((delegate* unmanaged<IntPtr, IntPtr, void>)F("Z3_set_error_handler"))(c, IntPtr.Zero);

    // ---- sorts and constants ----
    public static IntPtr BoolSort(IntPtr c) => ((delegate* unmanaged<IntPtr, IntPtr>)F("Z3_mk_bool_sort"))(c);
    public static IntPtr BvSort(IntPtr c, uint w) => ((delegate* unmanaged<IntPtr, uint, IntPtr>)F("Z3_mk_bv_sort"))(c, w);
    public static IntPtr Const(IntPtr c, string name, IntPtr sort)
    {
        IntPtr pn = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var sym = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F("Z3_mk_string_symbol"))(c, pn);
            return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)F("Z3_mk_const"))(c, sym, sort);
        }
        finally { Marshal.FreeCoTaskMem(pn); }
    }
    public static IntPtr Int64(IntPtr c, long v, IntPtr sort) => ((delegate* unmanaged<IntPtr, long, IntPtr, IntPtr>)F("Z3_mk_int64"))(c, v, sort);

    // ---- terms: one, two, three operands, and the n-ary and/or ----
    public static IntPtr Op0(string f, IntPtr c) => ((delegate* unmanaged<IntPtr, IntPtr>)F(f))(c);
    public static IntPtr Op1(string f, IntPtr c, IntPtr a) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F(f))(c, a);
    public static IntPtr Op2(string f, IntPtr c, IntPtr a, IntPtr b) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)F(f))(c, a, b);
    public static IntPtr Op2Signed(string f, IntPtr c, IntPtr a, IntPtr b, bool signed) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, IntPtr>)F(f))(c, a, b, signed ? (byte)1 : (byte)0);
    public static IntPtr Op3(string f, IntPtr c, IntPtr a, IntPtr b, IntPtr d) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)F(f))(c, a, b, d);
    public static IntPtr Ext(string f, IntPtr c, uint i, IntPtr a) => ((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr>)F(f))(c, i, a);
    public static IntPtr Extract(IntPtr c, uint hi, uint lo, IntPtr a) => ((delegate* unmanaged<IntPtr, uint, uint, IntPtr, IntPtr>)F("Z3_mk_extract"))(c, hi, lo, a);
    public static IntPtr Nary(string f, IntPtr c, IntPtr[] args)
    {
        fixed (IntPtr* p = args) return ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr>)F(f))(c, (uint)args.Length, p);
    }

    // ---- solver ----
    public static IntPtr MkSolver(IntPtr c)
    {
        var s = ((delegate* unmanaged<IntPtr, IntPtr>)F("Z3_mk_solver"))(c);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)F("Z3_solver_inc_ref"))(c, s);
        return s;
    }
    public static void SolverTimeout(IntPtr c, IntPtr s, uint ms)
    {
        var p = ((delegate* unmanaged<IntPtr, IntPtr>)F("Z3_mk_params"))(c);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)F("Z3_params_inc_ref"))(c, p);
        IntPtr pn = Marshal.StringToCoTaskMemUTF8("timeout");
        try
        {
            var sym = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F("Z3_mk_string_symbol"))(c, pn);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)F("Z3_params_set_uint"))(c, p, sym, ms);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)F("Z3_solver_set_params"))(c, s, p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pn);
            ((delegate* unmanaged<IntPtr, IntPtr, void>)F("Z3_params_dec_ref"))(c, p);
        }
    }
    public static void Assert(IntPtr c, IntPtr s, IntPtr a) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)F("Z3_solver_assert"))(c, s, a);
    /// <summary>Z3_lbool: -1 unsat, 0 unknown, 1 sat.</summary>
    public static int Check(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, int>)F("Z3_solver_check"))(c, s);
    public static string ReasonUnknown(IntPtr c, IntPtr s) => Str(((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F("Z3_solver_get_reason_unknown"))(c, s)) ?? "unknown";
    public static void DelSolver(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, void>)F("Z3_solver_dec_ref"))(c, s);

    /// <summary>The value the model gives `t`, as Z3 prints a numeral (unsigned decimal for bit vectors), or "true"/"false".</summary>
    public static string Eval(IntPtr c, IntPtr s, IntPtr t)
    {
        var m = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F("Z3_solver_get_model"))(c, s);
        ((delegate* unmanaged<IntPtr, IntPtr, void>)F("Z3_model_inc_ref"))(c, m);
        try
        {
            IntPtr v;
            if (((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, IntPtr*, byte>)F("Z3_model_eval"))(c, m, t, 1, &v) == 0) return "?";
            if (IsBool(c, v)) return AstString(c, v);
            return Str(((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F("Z3_get_numeral_string"))(c, v)) ?? AstString(c, v);
        }
        finally { ((delegate* unmanaged<IntPtr, IntPtr, void>)F("Z3_model_dec_ref"))(c, m); }
    }

    /// <summary>Z3_get_numeral_string on a Bool sets an error; ask the sort kind first (Z3_BOOL_SORT = 1).</summary>
    public static bool IsBool(IntPtr c, IntPtr t)
    {
        var sort = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F("Z3_get_sort"))(c, t);
        return ((delegate* unmanaged<IntPtr, IntPtr, int>)F("Z3_get_sort_kind"))(c, sort) == 1;
    }
    public static string AstString(IntPtr c, IntPtr t) => Str(((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)F("Z3_ast_to_string"))(c, t)) ?? "?";
}
