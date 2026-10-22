using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

public static partial class Prelude
{
    public static string str(string? s) => s ?? "";
    public static string str(bool b) => b ? "true" : "false";
    public static string str(char c) => c.ToString();
    public static string str(int v) => v.ToString(Inv);
    public static string str(long v) => v.ToString(Inv);
    public static string str(double v) => FormatFloat(v.ToString("R", Inv), double.IsNaN(v), double.IsInfinity(v));
    public static string str<T>(T v)
    {
        // value types: typeof(T) tests fold per instantiation and (X)(object)v is not a box, so a number formats
        // straight to its string; the IFormattable arm below would box it first
        if (typeof(T) == typeof(int)) return ((int)(object)v!).ToString(Inv);
        if (typeof(T) == typeof(long)) return ((long)(object)v!).ToString(Inv);
        if (typeof(T) == typeof(double)) return str((double)(object)v!);
        if (typeof(T) == typeof(float)) return str((float)(object)v!);
        if (typeof(T) == typeof(bool)) return (bool)(object)v! ? "true" : "false";
        if (typeof(T) == typeof(char)) return ((char)(object)v!).ToString();
        if (typeof(T) == typeof(byte)) return ((byte)(object)v!).ToString(Inv);
        if (typeof(T) == typeof(sbyte)) return ((sbyte)(object)v!).ToString(Inv);
        if (typeof(T) == typeof(short)) return ((short)(object)v!).ToString(Inv);
        if (typeof(T) == typeof(ushort)) return ((ushort)(object)v!).ToString(Inv);
        if (typeof(T) == typeof(uint)) return ((uint)(object)v!).ToString(Inv);
        if (typeof(T) == typeof(ulong)) return ((ulong)(object)v!).ToString(Inv);
        return StrSlow(v);
    }

    static string StrSlow<T>(T v) => v switch
    {
        null => "",
        string s => s,
        bool b => str(b),
        double d => str(d),
        float f32 => str(f32),
        IFormattable f => f.ToString(null, Inv),
        IEnumerable e => "[" + string.Join(", ", Items(e)) + "]",
        _ => v.ToString() ?? "",
    };

    public static string inspect<T>(this T v) => typeof(T).IsValueType && typeof(T) != typeof(char) && !typeof(T).IsGenericType
        ? str(v)   // numbers and bools inspect as they print; folds per instantiation, no box
        : InspectSlow(v);

    static string InspectSlow<T>(T v) => v switch
    {
        null => "nil",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
        char c => "'" + c + "'",
        IEnumerable e => "[" + string.Join(", ", Items(e)) + "]",
        _ => str(v),
    };

    static IEnumerable<string> Items(IEnumerable e) { foreach (var x in e) yield return inspect(x); }
}
