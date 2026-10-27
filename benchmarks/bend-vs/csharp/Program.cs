// Bend-vs suite in C#: same algorithms, uint wrapping, float mandelbrot.
// PAR=1 forks with Parallel.Invoke above depth Cut; otherwise plain recursion.
using System;
using System.Threading.Tasks;

abstract class Tree { }
sealed class Leaf : Tree { public readonly uint V; public Leaf(uint v) => V = v; }
sealed class Node : Tree { public readonly Tree L, R; public Node(Tree l, Tree r) { L = l; R = r; } }

static class B
{
    const int Cut = 10;
    static readonly bool Par = Environment.GetEnvironmentVariable("PAR") == "1";

    static (T, U) Fork<T, U>(int k, Func<T> a, Func<U> b)
    {
        T x = default; U y = default;
        Parallel.Invoke(() => x = a(), () => y = b());
        return (x, y);
    }

    static uint Pow2(int n, int k)
    {
        if (n == 0) return 1;
        if (Par && k < Cut) return Pow2Fork0(n, k);
        return Pow2(n - 1, k + 1) + Pow2(n - 1, k + 1);
    }
    static uint Fib(int n, int k)
    {
        if (n < 2) return (uint)n;
        if (Par && k < Cut) return FibFork1(n, k);
        return Fib(n - 1, k + 1) + Fib(n - 2, k + 1);
    }
    static Tree Gen(int d, uint x, int k)
    {
        if (d == 0) return new Leaf(x);
        if (Par && k < Cut) return GenFork2(d, x, k);
        return new Node(Gen(d - 1, x * 2, k + 1), Gen(d - 1, x * 2 + 1, k + 1));
    }
    static uint Total(Tree t, int k)
    {
        if (t is Leaf l) return l.V;
        var n = (Node)t;
        if (Par && k < Cut) return TotalFork3(t, k);
        return Total(n.L, k + 1) + Total(n.R, k + 1);
    }
    static uint Solve(uint all, uint cols, uint d1, uint d2, int k)
    {
        if (cols == all) return 1;
        return Place(all & ~(cols | d1 | d2), all, cols, d1, d2, k);
    }
    static uint Place(uint avail, uint all, uint cols, uint d1, uint d2, int k)
    {
        if (avail == 0) return 0;
        uint bit = avail & (0u - avail);
        if (Par && k < Cut) return PlaceFork4(avail, all, cols, d1, d2, k);
        return Solve(all, cols | bit, (d1 | bit) << 1, (d2 | bit) >> 1, k + 1) + Place(avail ^ bit, all, cols, d1, d2, k + 1);
    }
    static uint Iter(float cr, float ci)
    {
        float zr = 0, zi = 0; uint i = 0;
        while (i != 256)
        {
            float zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 > 4.0f) break;
            zi = 2.0f * zr * zi + ci; zr = zr2 - zi2 + cr; i++;
        }
        return i;
    }
    static uint Row(uint y, uint w)
    {
        float fw = w, ci = y * (2.0f / fw) - 1.0f, dx = 3.0f / fw, cr = -2.0f;
        uint acc = 0;
        for (uint x = 0; x != w; x++) { acc += Iter(cr, ci); cr += dx; }
        return acc;
    }
    static uint Rows(int d, uint y, uint w, int k)
    {
        if (d == 0) return Row(y, w);
        if (Par && k < Cut) return RowsFork5(d, y, w, k);
        return Rows(d - 1, y * 2, w, k + 1) + Rows(d - 1, y * 2 + 1, w, k + 1);
    }
    static uint Steps(uint x)
    {
        uint s = 0;
        while (x > 1 && s != 1000) { x = (x & 1) != 0 ? x * 3 + 1 : x >> 1; s++; }
        return s;
    }
    static uint Range(int d, uint i, int k)
    {
        if (d == 0) return Steps(i + 1);
        if (Par && k < Cut) return RangeFork6(d, i, k);
        return Range(d - 1, i * 2, k + 1) + Range(d - 1, i * 2 + 1, k + 1);
    }
    static uint Pow2Fork0(int n, int k)
    {
        var (a, b) = Fork(k, () => Pow2(n - 1, k + 1), () => Pow2(n - 1, k + 1)); return a + b;
    }
    static uint FibFork1(int n, int k)
    {
        var (a, b) = Fork(k, () => Fib(n - 1, k + 1), () => Fib(n - 2, k + 1)); return a + b;
    }
    static Tree GenFork2(int d, uint x, int k)
    {
        var (a, b) = Fork(k, () => Gen(d - 1, x * 2, k + 1), () => Gen(d - 1, x * 2 + 1, k + 1)); return new Node(a, b);
    }
    static uint TotalFork3(Tree t, int k)
    {
        var n = (Node)t;
        var (a, b) = Fork(k, () => Total(n.L, k + 1), () => Total(n.R, k + 1)); return a + b;
    }
    static uint PlaceFork4(uint avail, uint all, uint cols, uint d1, uint d2, int k)
    {
        uint bit = avail & (0u - avail);
        var (a, b) = Fork(k, () => Solve(all, cols | bit, (d1 | bit) << 1, (d2 | bit) >> 1, k + 1), () => Place(avail ^ bit, all, cols, d1, d2, k + 1));
            return a + b;
    }
    static uint RowsFork5(int d, uint y, uint w, int k)
    {
        var (a, b) = Fork(k, () => Rows(d - 1, y * 2, w, k + 1), () => Rows(d - 1, y * 2 + 1, w, k + 1)); return a + b;
    }
    static uint RangeFork6(int d, uint i, int k)
    {
        var (a, b) = Fork(k, () => Range(d - 1, i * 2, k + 1), () => Range(d - 1, i * 2 + 1, k + 1)); return a + b;
    }
    static void Main(string[] args)
    {
        int n = int.Parse(args[1]);
        uint r = args[0] switch
        {
            "pow2" => Pow2(n, 0),
            "fib" => Fib(n, 0),
            "tree" => Total(Gen(n, 1, 0), 0),
            "nqueens" => Solve((1u << n) - 1, 0, 0, 0, 0),
            "mandel" => Rows(n, 0, 1u << n, 0),
            "collatz" => Range(n, 0, 0),
            _ => 0,
        };
        Console.WriteLine(r);
    }
}
