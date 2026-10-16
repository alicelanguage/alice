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
    case ("nbody", "handopt"): warm = () => NBodyOpt.Kernel(20_000); run = () => NBodyOpt.Kernel(1_000_000); break;
    case ("binarytrees", "idiomatic"): warm = () => BinaryTrees.Kernel(10); run = () => BinaryTrees.Kernel(16); break;
    case ("binarytrees", "handopt"): System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; warm = () => BinaryTreesOpt.Kernel(10); run = () => BinaryTreesOpt.Kernel(16); break;
    case ("expr", "handopt"): { var t = ExprOpt.Build(20, 12345); warm = run = () => ExprOpt.Kernel(t); break; }
    case ("mandelbrot", "handopt"): warm = () => MandelbrotOpt.Kernel(200); run = () => MandelbrotOpt.Kernel(1000); break;
    case ("particles", "idiomatic"): { var ps = Particles.MakeObjects(1_000_000); fixedCheck = Particles.Objects(Particles.MakeObjects(1_000_000)); warm = run = () => { Particles.Objects(ps); return 0; }; break; }
    case ("particles", "handopt"): { var ps = Particles.MakeSoA(1_000_000); fixedCheck = Particles.SoAKernel(Particles.MakeSoA(1_000_000)); warm = run = () => { Particles.SoAKernel(ps); return 0; }; break; }
    case ("pipeline", "idiomatic"): { var xs = Pipeline.Data(); warm = run = () => Pipeline.Linq(xs); break; }
    case ("pipeline", "handopt"): { var xs = Pipeline.Data(); warm = run = () => Pipeline.VecPar(xs); break; }
    case ("expr", "idiomatic"): { var t = ExprBench.Build(20, 12345); warm = run = () => ExprBench.Kernel(t); break; }
    case ("wordfreq", "idiomatic"): { var s = WordFreq.MakeText(1_000_000); warm = run = () => WordFreq.Idiomatic(s); break; }
    case ("wordfreq", "handopt"): { var s = WordFreq.MakeText(1_000_000); warm = run = () => WordFreq.HandOptParallel(s); break; }
    case ("mandelbrot", "idiomatic"): warm = () => Mandelbrot.Kernel(200); run = () => Mandelbrot.Kernel(1000); break;
    case ("parallel", "idiomatic"): { var xs = ParallelBench.Data(); warm = run = () => ParallelBench.Plinq(xs); break; }
    case ("parallel", "handopt"): { var xs = ParallelBench.Data(); warm = run = () => ParallelBench.ParallelVec(xs); break; }
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

    public sealed class Soa(int n) { public double[] X = new double[n], Y = new double[n], Vx = new double[n], Vy = new double[n]; }

    public static Soa MakeSoA(int n)
    {
        var s = new Soa(n);
        for (int i = 0; i < n; i++) { s.X[i] = (i % 1000) * 0.01; s.Y[i] = (i % 777) * 0.02; s.Vx[i] = (i % 13) * 0.5 - 3.0; s.Vy[i] = (i % 7) * 0.25; }
        return s;
    }

    // SoA columns, parallel chunks, Vector<double>; per element all 10 steps run in registers (elements are independent,
    // per-element operation order is unchanged, so state is bit-identical to the 3-pass loop).
    public static long SoAKernel(Soa ps) => Convert.ToInt64(Chunks.Sum(ps.X.Length, (lo, hi) =>
    {
        const double dt = 0.01, g = 9.81;
        var gdt = new System.Numerics.Vector<double>(g * dt); var vdt = new System.Numerics.Vector<double>(dt);
        var X = System.Runtime.InteropServices.MemoryMarshal.Cast<double, System.Numerics.Vector<double>>(ps.X.AsSpan(lo, hi - lo));
        var Y = System.Runtime.InteropServices.MemoryMarshal.Cast<double, System.Numerics.Vector<double>>(ps.Y.AsSpan(lo, hi - lo));
        var VX = System.Runtime.InteropServices.MemoryMarshal.Cast<double, System.Numerics.Vector<double>>(ps.Vx.AsSpan(lo, hi - lo));
        var VY = System.Runtime.InteropServices.MemoryMarshal.Cast<double, System.Numerics.Vector<double>>(ps.Vy.AsSpan(lo, hi - lo));
        var acc = System.Numerics.Vector<double>.Zero;
        for (int k = 0; k < X.Length; k++)
        {
            var x = X[k]; var y = Y[k]; var vx = VX[k]; var vy = VY[k];
            for (int step = 0; step < 10; step++) { vy -= gdt; x += vx * vdt; y += vy * vdt; }
            X[k] = x; Y[k] = y; VY[k] = vy;
            acc += x + y;
        }
        double s = System.Numerics.Vector.Sum(acc);
        for (int i = lo + X.Length * System.Numerics.Vector<double>.Count; i < hi; i++)
        {
            double x = ps.X[i], y = ps.Y[i], vx = ps.Vx[i], vy = ps.Vy[i];
            for (int step = 0; step < 10; step++) { vy -= g * dt; x += vx * dt; y += vy * dt; }
            ps.X[i] = x; ps.Y[i] = y; ps.Vy[i] = vy; s += x + y;
        }
        return s;
    }));

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

    public static long VecPar(double[] xs) => Convert.ToInt64(Chunks.Sum(xs.Length, (lo, hi) =>
    {
        var span = xs.AsSpan(lo, hi - lo);
        var acc = System.Numerics.Vector<double>.Zero;
        var a = new System.Numerics.Vector<double>(1.5); var b = new System.Numerics.Vector<double>(0.25); var t = new System.Numerics.Vector<double>(0.75);
        int i = 0, w = System.Numerics.Vector<double>.Count;
        var vs = System.Runtime.InteropServices.MemoryMarshal.Cast<double, System.Numerics.Vector<double>>(span);
        foreach (var v in vs)
        {
            var y = v * a + b;
            acc += System.Numerics.Vector.ConditionalSelect(System.Numerics.Vector.GreaterThan(y, t), y, System.Numerics.Vector<double>.Zero);
        }
        i = vs.Length * w;
        double s = System.Numerics.Vector.Sum(acc);
        for (; i < span.Length; i++) { var y = span[i] * 1.5 + 0.25; if (y > 0.75) s += y; }
        return s;
    }));

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

    // Partition at spaces into one chunk per core, span + alternate lookup per chunk, merge counts.
    public static long HandOptParallel(string text)
    {
        int workers = Environment.ProcessorCount;
        var bounds = new int[workers + 1];
        bounds[workers] = text.Length;
        for (int w = 1; w < workers; w++)
        {
            int b = Math.Max(bounds[w - 1], (int)((long)text.Length * w / workers));
            while (b < text.Length && text[b] != ' ') b++;
            bounds[w] = Math.Min(text.Length, b);
        }
        var parts = new Dictionary<string, int>[workers];
        Parallel.For(0, workers, w =>
        {
            int lo = bounds[w], hi = bounds[w + 1];
            if (w > 0 && lo < hi && text[lo] == ' ') lo++;
            var d = new Dictionary<string, int>(16384);
            var lk = d.GetAlternateLookup<ReadOnlySpan<char>>();
            var span = text.AsSpan(lo, hi - lo);
            while (span.Length > 0)
            {
                int k = span.IndexOf(' ');
                var word = k < 0 ? span : span[..k];
                CollectionsMarshal.GetValueRefOrAddDefault(lk, word, out _)++;
                span = k < 0 ? default : span[(k + 1)..];
            }
            parts[w] = d;
        });
        var counts = parts[0];
        for (int w = 1; w < workers; w++)
            foreach (var (k, v) in parts[w]) CollectionsMarshal.GetValueRefOrAddDefault(counts, k, out _) += v;
        int top = 0;
        foreach (var v in counts.Values) if (v > top) top = v;
        return top * 100000L + counts.Count;
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

    // Math.Sin/Cos/Sqrt have no Vector<double> equivalent with identical results; vectorized Sqrt + scalar sin/cos
    // would be honest but sqrt is not the bottleneck. Per-chunk: 4 independent accumulators to break the add chain.
    public static long ParallelVec(double[] xs) => Convert.ToInt64(Chunks.Sum(xs.Length, (lo, hi) =>
    {
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        int i = lo;
        for (; i + 4 <= hi; i += 4) { s0 += F(xs[i]); s1 += F(xs[i + 1]); s2 += F(xs[i + 2]); s3 += F(xs[i + 3]); }
        for (; i < hi; i++) s0 += F(xs[i]);
        return (s0 + s1) + (s2 + s3);
    }));

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

static class NBodyOpt
{
    // Struct-of-arrays in fixed-size locals, fully unrolled 10 pairs, no allocation in the step loop.
    // No FMA: it changes rounding and the checksum (energy * 1e9) must match bit-for-bit semantics.
    public static long Kernel(int steps)
    {
        const double pi = Math.PI, sm = 4 * pi * pi, dp = 365.24, dt = 0.01;
        Span<double> x = [0, 4.84143144246472090e+00, 8.34336671824457987e+00, 1.28943695621391310e+01, 1.53796971148509165e+01];
        Span<double> y = [0, -1.16032004402742839e+00, 4.12479856412430479e+00, -1.51111514016986312e+01, -2.59193146099879641e+01];
        Span<double> z = [0, -1.03622044471123109e-01, -4.03523417114321381e-01, -2.23307578892655734e-01, 1.79258772950371181e-01];
        Span<double> vx = [0, 1.66007664274403694e-03 * dp, -2.76742510726862411e-03 * dp, 2.96460137564761618e-03 * dp, 2.68067772490389322e-03 * dp];
        Span<double> vy = [0, 7.69901118419740425e-03 * dp, 4.99852801234917238e-03 * dp, 2.37847173959480950e-03 * dp, 1.62824170038242295e-03 * dp];
        Span<double> vz = [0, -6.90460016972063023e-05 * dp, 2.30417297573763929e-05 * dp, -2.96589568540237556e-05 * dp, -9.51592254519715870e-05 * dp];
        Span<double> m = [sm, 9.54791938424326609e-04 * sm, 2.85885980666130812e-04 * sm, 4.36624404335156298e-05 * sm, 5.15138902046611451e-05 * sm];
        double px = 0, py = 0, pz = 0;
        for (int i = 0; i < 5; i++) { px += vx[i] * m[i]; py += vy[i] * m[i]; pz += vz[i] * m[i]; }
        vx[0] = -px / sm; vy[0] = -py / sm; vz[0] = -pz / sm;
        for (int s = 0; s < steps; s++)
        {
            Pair(x, y, z, vx, vy, vz, m, 0, 1, dt); Pair(x, y, z, vx, vy, vz, m, 0, 2, dt); Pair(x, y, z, vx, vy, vz, m, 0, 3, dt); Pair(x, y, z, vx, vy, vz, m, 0, 4, dt);
            Pair(x, y, z, vx, vy, vz, m, 1, 2, dt); Pair(x, y, z, vx, vy, vz, m, 1, 3, dt); Pair(x, y, z, vx, vy, vz, m, 1, 4, dt);
            Pair(x, y, z, vx, vy, vz, m, 2, 3, dt); Pair(x, y, z, vx, vy, vz, m, 2, 4, dt);
            Pair(x, y, z, vx, vy, vz, m, 3, 4, dt);
            for (int i = 0; i < 5; i++) { x[i] += dt * vx[i]; y[i] += dt * vy[i]; z[i] += dt * vz[i]; }
        }
        double e = 0;
        for (int i = 0; i < 5; i++)
        {
            e += 0.5 * m[i] * (vx[i] * vx[i] + vy[i] * vy[i] + vz[i] * vz[i]);
            for (int j = i + 1; j < 5; j++)
            {
                double dx = x[i] - x[j], dy = y[i] - y[j], dz = z[i] - z[j];
                e -= m[i] * m[j] / Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
        return Convert.ToInt64(e * 1e9);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static void Pair(Span<double> x, Span<double> y, Span<double> z, Span<double> vx, Span<double> vy, Span<double> vz, Span<double> m, int i, int j, double dt)
    {
        double dx = x[i] - x[j], dy = y[i] - y[j], dz = z[i] - z[j];
        double d2 = dx * dx + dy * dy + dz * dz;
        double mag = dt / (d2 * Math.Sqrt(d2));
        double mi = m[i], mj = m[j];
        vx[i] -= dx * mj * mag; vy[i] -= dy * mj * mag; vz[i] -= dz * mj * mag;
        vx[j] += dx * mi * mag; vy[j] += dy * mi * mag; vz[j] += dz * mi * mag;
    }
}

static class BinaryTreesOpt
{
    // Real tree per iteration; iterations spread over cores with per-worker partial sums (benchmarks-game style).
    public static long Kernel(int maxDepth)
    {
        const int minDepth = 4;
        long total = BinaryTrees_BottomUp(maxDepth + 1).Check();
        var longLived = BinaryTrees_BottomUp(maxDepth);
        for (int d = minDepth; d <= maxDepth; d += 2)
        {
            int iters = 1 << (maxDepth - d + minDepth);
            long c = 0;
            int depth = d;
            Parallel.For(0, iters, () => 0L, (_, _, acc) => acc + BinaryTrees_BottomUp(depth).Check(), acc => Interlocked.Add(ref c, acc));
            total += c;
        }
        return total + longLived.Check();
    }

    static Node BinaryTrees_BottomUp(int depth) => depth > 0 ? new Node(BinaryTrees_BottomUp(depth - 1), BinaryTrees_BottomUp(depth - 1)) : new Node(null, null);
}

sealed class XNode(int tag, long v, XNode? a, XNode? b)
{
    public readonly int Tag = tag; public readonly long V = v; public readonly XNode? A = a, B = b;
}

static class ExprOpt
{
    // Int type tag + switch, fields instead of properties; Neg stores child in A.
    const int TNum = 0, TAdd = 1, TMul = 2, TNeg = 3;

    static long Eval(XNode e) => e.Tag switch
    {
        TNum => e.V,
        TAdd => Eval(e.A!) + Eval(e.B!),
        TMul => Eval(e.A!) * Eval(e.B!),
        _ => -Eval(e.A!),
    };

    public static XNode Build(int d, int seed)
    {
        if (d == 0) return new XNode(TNum, seed % 7 + 1, null, null);
        int s1 = (seed * 31 + 7) % 1000003, s2 = (seed * 17 + 3) % 1000003;
        return (seed % 5) switch
        {
            0 => new XNode(TNeg, 0, Build(d - 1, s1), null),
            1 => new XNode(TMul, 0, Build(d - 1, s1), Build(d - 1, s2)),
            _ => new XNode(TAdd, 0, Build(d - 1, s1), Build(d - 1, s2)),
        };
    }

    public static long Kernel(XNode tree)
    {
        long acc = 0;
        for (int i = 0; i < 10; i++) acc += Eval(tree);
        return acc;
    }
}

static class MandelbrotOpt
{
    // Parallel over rows, Vector256<double> (4 lanes) over x with an active mask; same arithmetic per lane as scalar.
    public static long Kernel(int size)
    {
        long inside = 0;
        Parallel.For(0, size, () => 0L, (py, _, acc) => acc + Row(py, size), acc => Interlocked.Add(ref inside, acc));
        return inside;
    }

    static long Row(int py, int size)
    {
        double ci0 = 2.0 * py / size - 1.0;
        var ci = System.Runtime.Intrinsics.Vector256.Create(ci0);
        var four = System.Runtime.Intrinsics.Vector256.Create(4.0);
        long count = 0;
        int px = 0;
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            for (; px + 4 <= size; px += 4)
            {
                var cr = System.Runtime.Intrinsics.Vector256.Create(2.0 * px / size - 1.5, 2.0 * (px + 1) / size - 1.5, 2.0 * (px + 2) / size - 1.5, 2.0 * (px + 3) / size - 1.5);
                var zr = System.Runtime.Intrinsics.Vector256<double>.Zero;
                var zi = zr;
                var active = System.Runtime.Intrinsics.Vector256<double>.AllBitsSet;
                for (int it = 0; it < 100; it++)
                {
                    var zr2 = zr * zr; var zi2 = zi * zi;
                    active &= System.Runtime.Intrinsics.Vector256.LessThanOrEqual(zr2 + zi2, four);
                    if (active == System.Runtime.Intrinsics.Vector256<double>.Zero) break;
                    var t = zr2 - zi2 + cr;
                    var nzi = (zr + zr) * zi + ci;
                    // frozen lanes keep their values (escaped lanes are already excluded from the mask)
                    zr = System.Runtime.Intrinsics.Vector256.ConditionalSelect(active, t, zr);
                    zi = System.Runtime.Intrinsics.Vector256.ConditionalSelect(active, nzi, zi);
                }
                count += BitOps(System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(active));
            }
        }
        for (; px < size; px++)
        {
            double cr = 2.0 * px / size - 1.5, zr = 0, zi = 0;
            int it = 0;
            while (it < 100 && zr * zr + zi * zi <= 4.0) { double t = zr * zr - zi * zi + cr; zi = 2.0 * zr * zi + ci0; zr = t; it++; }
            if (it == 100) count++;
        }
        return count;
    }

    static int BitOps(uint m) => System.Numerics.BitOperations.PopCount(m);
}

static class Chunks
{
    // Static even partition, one chunk per core; body returns a partial, partials summed in chunk order.
    public static double Sum(int n, Func<int, int, double> body)
    {
        int workers = Environment.ProcessorCount;
        int chunk = (n + workers - 1) / workers;
        var partial = new double[workers];
        Parallel.For(0, workers, w => { int lo = Math.Min(n, w * chunk), hi = Math.Min(n, lo + chunk); partial[w] = body(lo, hi); });
        double s = 0;
        foreach (var v in partial) s += v;
        return s;
    }
}
