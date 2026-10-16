using System.Diagnostics;
using System.Runtime.InteropServices;

// Usage: Bench.CSharp <benchmark> <idiomatic|handopt>
// Prints: RESULT <name> <median_ms> <checksum>
var (name, variant) = (args[0], args[1]);
Func<long> warm, run;
long? fixedCheck = null;   // stateful benchmarks: checksum of a fresh run, timed runs are not re-checked
switch (name, variant)
{
    case ("nbody", "idiomatic"): warm = () => NBody.Kernel(20_000); run = () => NBody.Kernel(1_000_000); break;
    case ("binarytrees", "idiomatic"): warm = () => BinaryTrees.Kernel(10); run = () => BinaryTrees.Kernel(16); break;
    case ("particles", "idiomatic"): { var ps = Particles.MakeObjects(1_000_000); fixedCheck = Particles.Objects(Particles.MakeObjects(1_000_000)); warm = run = () => { Particles.Objects(ps); return 0; }; break; }
    case ("particles", "handopt"): { var ps = Particles.MakeStructs(1_000_000); fixedCheck = Particles.Structs(Particles.MakeStructs(1_000_000)); warm = run = () => { Particles.Structs(ps); return 0; }; break; }
    case ("pipeline", "idiomatic"): { var xs = Pipeline.Data(); warm = run = () => Pipeline.Linq(xs); break; }
    case ("pipeline", "handopt"): { var xs = Pipeline.Data(); warm = run = () => Pipeline.Loop(xs); break; }
    case ("expr", "idiomatic"): { var t = ExprBench.Build(20, 12345); warm = run = () => ExprBench.Kernel(t); break; }
    case ("wordfreq", "idiomatic"): { var s = WordFreq.MakeText(1_000_000); warm = run = () => WordFreq.Idiomatic(s); break; }
    case ("wordfreq", "handopt"): { var s = WordFreq.MakeText(1_000_000); warm = run = () => WordFreq.HandOpt(s); break; }
    case ("mandelbrot", "idiomatic"): warm = () => Mandelbrot.Kernel(200); run = () => Mandelbrot.Kernel(1000); break;
    case ("parallel", "idiomatic"): { var xs = ParallelBench.Data(); warm = run = () => ParallelBench.Plinq(xs); break; }
    case ("parallel", "handopt"): { var xs = ParallelBench.Data(); warm = run = () => ParallelBench.ParallelFor(xs); break; }
    default: Console.WriteLine($"SKIP {name} {variant}"); return;
}
Harness.Run(name, warm, run, fixedCheck);

static class Harness
{
    public static void Run(string name, Func<long> warm, Func<long> run, long? fixedCheck = null)
    {
        long check = run();
        // warm-up: >= 50 calls and >= 1 s so tier-1 + PGO code replaces tier-0/OSR
        var ws = Stopwatch.StartNew();
        for (int i = 0; i < 50 || ws.ElapsedMilliseconds < 1000; i++) warm();
        var times = new double[15];
        for (int r = 0; r < times.Length; r++) times[r] = Measure(run, check);
        Array.Sort(times);
        Console.WriteLine($"RESULT {name} {times[7].ToString(System.Globalization.CultureInfo.InvariantCulture)} {fixedCheck ?? check}");
    }

    static double Measure(Func<long> run, long check)
    {
        var sw = Stopwatch.StartNew();
        long got = run();
        if (got != check) throw new Exception("checksum mismatch");
        return sw.Elapsed.TotalMilliseconds;
    }
}

sealed class Body(double x, double y, double z, double vx, double vy, double vz, double mass)
{
    public double X = x, Y = y, Z = z, Vx = vx, Vy = vy, Vz = vz, Mass = mass;
}

static class NBody
{
    const double Pi = Math.PI, SolarMass = 4 * Pi * Pi, DaysPerYear = 365.24;

    static Body[] MakeBodies()
    {
        const double dp = DaysPerYear, sm = SolarMass;
        Body[] bodies =
        [
            new(0, 0, 0, 0, 0, 0, sm),
            new(4.84143144246472090e+00, -1.16032004402742839e+00, -1.03622044471123109e-01, 1.66007664274403694e-03 * dp, 7.69901118419740425e-03 * dp, -6.90460016972063023e-05 * dp, 9.54791938424326609e-04 * sm),
            new(8.34336671824457987e+00, 4.12479856412430479e+00, -4.03523417114321381e-01, -2.76742510726862411e-03 * dp, 4.99852801234917238e-03 * dp, 2.30417297573763929e-05 * dp, 2.85885980666130812e-04 * sm),
            new(1.28943695621391310e+01, -1.51111514016986312e+01, -2.23307578892655734e-01, 2.96460137564761618e-03 * dp, 2.37847173959480950e-03 * dp, -2.96589568540237556e-05 * dp, 4.36624404335156298e-05 * sm),
            new(1.53796971148509165e+01, -2.59193146099879641e+01, 1.79258772950371181e-01, 2.68067772490389322e-03 * dp, 1.62824170038242295e-03 * dp, -9.51592254519715870e-05 * dp, 5.15138902046611451e-05 * sm),
        ];
        double px = 0, py = 0, pz = 0;
        foreach (var b in bodies) { px += b.Vx * b.Mass; py += b.Vy * b.Mass; pz += b.Vz * b.Mass; }
        bodies[0].Vx = -px / sm; bodies[0].Vy = -py / sm; bodies[0].Vz = -pz / sm;
        return bodies;
    }

    static void Advance(Body[] bodies, double dt)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            var a = bodies[i];
            for (int j = i + 1; j < bodies.Length; j++)
            {
                var b = bodies[j];
                double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                double mag = dt / (d2 * Math.Sqrt(d2));
                a.Vx -= dx * b.Mass * mag; a.Vy -= dy * b.Mass * mag; a.Vz -= dz * b.Mass * mag;
                b.Vx += dx * a.Mass * mag; b.Vy += dy * a.Mass * mag; b.Vz += dz * a.Mass * mag;
            }
        }
        foreach (var b in bodies) { b.X += dt * b.Vx; b.Y += dt * b.Vy; b.Z += dt * b.Vz; }
    }

    static double Energy(Body[] bodies)
    {
        double e = 0;
        for (int i = 0; i < bodies.Length; i++)
        {
            var a = bodies[i];
            e += 0.5 * a.Mass * (a.Vx * a.Vx + a.Vy * a.Vy + a.Vz * a.Vz);
            for (int j = i + 1; j < bodies.Length; j++)
            {
                var b = bodies[j];
                double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                e -= a.Mass * b.Mass / Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
        return e;
    }

    public static long Kernel(int steps)
    {
        var bodies = MakeBodies();
        for (int s = 0; s < steps; s++) Advance(bodies, 0.01);
        return Convert.ToInt64(Energy(bodies) * 1e9);
    }
}

sealed class Node(Node? left, Node? right)
{
    public int Check() => left is null || right is null ? 1 : 1 + left.Check() + right.Check();
}

static class BinaryTrees
{
    static Node BottomUp(int depth) => depth > 0 ? new Node(BottomUp(depth - 1), BottomUp(depth - 1)) : new Node(null, null);

    public static long Kernel(int maxDepth)
    {
        const int minDepth = 4;
        long total = BottomUp(maxDepth + 1).Check();
        var longLived = BottomUp(maxDepth);
        for (int d = minDepth; d <= maxDepth; d += 2)
        {
            int iters = 1 << (maxDepth - d + minDepth);
            int c = 0;
            for (int i = 0; i < iters; i++) c += BottomUp(d).Check();
            total += c;
        }
        return total + longLived.Check();
    }
}

static class Particles
{
    public sealed class P { public double X, Y, Vx, Vy; }
    public struct S { public double X, Y, Vx, Vy; }

    public static List<P> MakeObjects(int n)
    {
        var l = new List<P>(n);
        for (int i = 0; i < n; i++) l.Add(new P { X = (i % 1000) * 0.01, Y = (i % 777) * 0.02, Vx = (i % 13) * 0.5 - 3.0, Vy = (i % 7) * 0.25 });
        return l;
    }

    public static S[] MakeStructs(int n)
    {
        var a = new S[n];
        for (int i = 0; i < n; i++) a[i] = new S { X = (i % 1000) * 0.01, Y = (i % 777) * 0.02, Vx = (i % 13) * 0.5 - 3.0, Vy = (i % 7) * 0.25 };
        return a;
    }

    public static long Objects(List<P> ps)
    {
        const double dt = 0.01, g = 9.81;
        for (int step = 0; step < 10; step++)
        {
            foreach (var p in ps) p.Vy -= g * dt;
            foreach (var p in ps) p.X += p.Vx * dt;
            foreach (var p in ps) p.Y += p.Vy * dt;
        }
        return Convert.ToInt64(ps.Sum(p => p.X + p.Y));
    }

    public static long Structs(S[] ps)
    {
        const double dt = 0.01, g = 9.81;
        for (int step = 0; step < 10; step++)
        {
            for (int i = 0; i < ps.Length; i++) ps[i].Vy -= g * dt;
            for (int i = 0; i < ps.Length; i++) ps[i].X += ps[i].Vx * dt;
            for (int i = 0; i < ps.Length; i++) ps[i].Y += ps[i].Vy * dt;
        }
        double sum = 0;
        for (int i = 0; i < ps.Length; i++) sum += ps[i].X + ps[i].Y;
        return Convert.ToInt64(sum);
    }
}

static class Pipeline
{
    public static double[] Data() => Enumerable.Range(0, 10_000_000).Select(i => (i % 1000) * 0.001).ToArray();

    public static long Linq(double[] xs) => Convert.ToInt64(xs.Select(x => x * 1.5 + 0.25).Where(x => x > 0.75).Sum());

    public static long Loop(double[] xs)
    {
        double sum = 0;
        foreach (var x in xs)
        {
            var y = x * 1.5 + 0.25;
            if (y > 0.75) sum += y;
        }
        return Convert.ToInt64(sum);
    }
}

abstract record Expr;
sealed record Num(long V) : Expr;
sealed record Add(Expr A, Expr B) : Expr;
sealed record Mul(Expr A, Expr B) : Expr;
sealed record Neg(Expr E) : Expr;

static class ExprBench
{
    static long Eval(Expr e) => e switch
    {
        Num n => n.V,
        Add a => Eval(a.A) + Eval(a.B),
        Mul m => Eval(m.A) * Eval(m.B),
        Neg n => -Eval(n.E),
        _ => throw new InvalidOperationException(),
    };

    public static Expr Build(int d, int seed)
    {
        if (d == 0) return new Num(seed % 7 + 1);
        int s1 = (seed * 31 + 7) % 1000003, s2 = (seed * 17 + 3) % 1000003;
        return (seed % 5) switch
        {
            0 => new Neg(Build(d - 1, s1)),
            1 => new Mul(Build(d - 1, s1), Build(d - 1, s2)),
            _ => new Add(Build(d - 1, s1), Build(d - 1, s2)),
        };
    }

    public static long Kernel(Expr tree)
    {
        long acc = 0;
        for (int i = 0; i < 10; i++) acc += Eval(tree);
        return acc;
    }
}

static class WordFreq
{
    public static string MakeText(int n)
    {
        var sb = new System.Text.StringBuilder();
        long seed = 42;
        for (int i = 0; i < n; i++)
        {
            seed = (seed * 1103515245 + 12345) % 2147483648;
            long r = seed / 65536;
            long idx = (r % 100) * (r / 100 % 100);
            sb.Append('w').Append(idx).Append(' ');
        }
        return sb.ToString().TrimEnd();
    }

    public static long Idiomatic(string text)
    {
        var counts = new Dictionary<string, int>();
        foreach (var w in text.Split(' '))
        {
            if (counts.TryGetValue(w, out var c)) counts[w] = c + 1;
            else counts[w] = 1;
        }
        return counts.Values.Max() * 100000L + counts.Count;
    }

    public static long HandOpt(string text)
    {
        // No substring allocation: span-based lookup + single hash probe per word.
        var counts = new Dictionary<string, int>();
        var lookup = counts.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var range in text.AsSpan().Split(' '))
        {
            var w = text.AsSpan(range);
            if (lookup.TryGetValue(w, out var c)) lookup[w] = c + 1;
            else lookup[w] = 1;
        }
        int top = 0;
        foreach (var v in counts.Values) if (v > top) top = v;
        return top * 100000L + counts.Count;
    }
}

static class Mandelbrot
{
    public static long Kernel(int size)
    {
        long inside = 0;
        for (int py = 0; py < size; py++)
        {
            double ci = 2.0 * py / size - 1.0;
            for (int px = 0; px < size; px++)
            {
                double cr = 2.0 * px / size - 1.5, zr = 0, zi = 0;
                int it = 0;
                while (it < 100 && zr * zr + zi * zi <= 4.0)
                {
                    double t = zr * zr - zi * zi + cr;
                    zi = 2.0 * zr * zi + ci;
                    zr = t;
                    it++;
                }
                if (it == 100) inside++;
            }
        }
        return inside;
    }
}

static class ParallelBench
{
    public static double[] Data() => Enumerable.Range(0, 10_000_000).Select(i => i * 0.0001).ToArray();

    static double F(double x) => Math.Sqrt(x) * Math.Sin(x) + Math.Cos(x * 0.5);

    public static long Plinq(double[] xs) => Convert.ToInt64(xs.AsParallel().Select(F).Sum());

    public static long ParallelFor(double[] xs)
    {
        int workers = Environment.ProcessorCount;
        var partial = new double[workers];
        int chunk = (xs.Length + workers - 1) / workers;
        Parallel.For(0, workers, w =>
        {
            int lo = w * chunk, hi = Math.Min(xs.Length, lo + chunk);
            double s = 0;
            for (int i = lo; i < hi; i++) s += F(xs[i]);
            partial[w] = s;
        });
        return Convert.ToInt64(partial.Sum());
    }
}
