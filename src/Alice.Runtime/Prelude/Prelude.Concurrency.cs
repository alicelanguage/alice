using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

public static partial class Prelude
{
    public static Task spawn(Action f) => Task.Run(f);
    public static Task<T> spawn<T>(Func<T> f) => Task.Run(f);
    public static void wait(this Task t) => t.GetAwaiter().GetResult();
    public static T wait<T>(this Task<T> t) => t.GetAwaiter().GetResult();
    public static R[] par_map<T, R>(this T[] a, Func<T, R> f) => Par.Map(a, f);
    public static void par_each(this int n, Action<int> f) => Par.For(n, f);
}
