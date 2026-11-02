using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

public static partial class Prelude
{
    [Ambient] public static void puts() => Console.Out.WriteLine();
    [Ambient] public static void puts(string? s) => Console.Out.WriteLine(s);
    [Ambient] public static void puts<T>(T v) => Console.Out.WriteLine(str(v));
    [Ambient] public static void puts(params object?[] vs) { foreach (var v in vs) Console.Out.WriteLine(str(v)); }
    [Ambient] public static void print() { }
    [Ambient] public static void print(string? s) => Console.Out.Write(s);
    [Ambient] public static void print<T>(T v) => Console.Out.Write(str(v));
    [Ambient] public static void print(params object?[] vs) { foreach (var v in vs) Console.Out.Write(str(v)); }
    [Ambient] public static T p<T>(T v) { Console.Out.WriteLine(inspect(v)); return v; }
    [Ambient] public static string? gets() => Console.In.ReadLine();
    [Ambient] public static void exit(int code = 0) => Environment.Exit(code);

    public static Exception raise(string msg) => new(msg);
    public static Exception raise(Exception e) => e;
}
