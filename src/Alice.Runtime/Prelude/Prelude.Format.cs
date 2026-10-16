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
    public static string str(double v) => v.ToString(Inv);
    public static string str<T>(T v) => v switch
    {
        null => "",
        string s => s,
        bool b => str(b),
        IFormattable f => f.ToString(null, Inv),
        IEnumerable e => "[" + string.Join(", ", Items(e)) + "]",
        _ => v.ToString() ?? "",
    };

    public static string inspect<T>(this T v) => v switch
    {
        null => "nil",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
        char c => "'" + c + "'",
        IEnumerable e => "[" + string.Join(", ", Items(e)) + "]",
        _ => str(v),
    };

    static IEnumerable<string> Items(IEnumerable e) { foreach (var x in e) yield return inspect(x); }
}
