/* C baselines for the Alice benchmarks (clang -O3 -march=native). Usage: bench <name> <serial|parallel>
   Prints: RESULT <name> <median_ms> <checksum>   (same checksums as the Alice and C# programs) */
#define _GNU_SOURCE
#include <math.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static int workers(void) { long n = sysconf(_SC_NPROCESSORS_ONLN); return n > 0 ? (int)n : 1; }
static double now_ms(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec * 1e3 + t.tv_nsec / 1e6; }

/* ---- generic pthreads parallel-for with per-thread partials ---- */
typedef struct { int lo, hi; void *ctx; double dsum; long long isum; void (*fn)(void *, int, int, double *, long long *); } job;
static void *run_job(void *p) { job *j = p; j->fn(j->ctx, j->lo, j->hi, &j->dsum, &j->isum); return NULL; }
struct wctx { job *js; int chunks, t, id; };
static void *worker(void *p) { struct wctx *w = p; for (int c = w->id; c < w->chunks; c += w->t) run_job(&w->js[c]); return NULL; }
static void par_for(int n, int chunks, void *ctx, void (*fn)(void *, int, int, double *, long long *), double *dsum, long long *isum)
{
    int t = workers();
    if (chunks < t) chunks = t;
    if (chunks > n) chunks = n > 0 ? n : 1;
    job *js = calloc(chunks, sizeof(job));
    pthread_t *th = calloc(t, sizeof(pthread_t));
    /* static round-robin of chunks over threads: chunk c handled by thread c % t (simple, balanced enough) */
    for (int c = 0; c < chunks; c++) { js[c].lo = (int)((long long)n * c / chunks); js[c].hi = (int)((long long)n * (c + 1) / chunks); js[c].ctx = ctx; js[c].fn = fn; }
    struct wctx *wc = calloc(t, sizeof(*wc));
    for (int i = 0; i < t; i++) { wc[i] = (struct wctx){ js, chunks, t, i }; pthread_create(&th[i], NULL, worker, &wc[i]); }
    for (int i = 0; i < t; i++) pthread_join(th[i], NULL);
    double ds = 0; long long is = 0;
    for (int c = 0; c < chunks; c++) { ds += js[c].dsum; is += js[c].isum; }
    if (dsum) *dsum = ds;
    if (isum) *isum = is;
    free(js); free(th); free(wc);
}

/* ---- nbody (serial only: steps depend on each other) ---- */
typedef struct { double x, y, z, vx, vy, vz, mass; } body;
static long long nbody(int steps)
{
    const double pi = 3.141592653589793, sm = 4 * pi * pi, dp = 365.24;
    body b[5] = {
        {0, 0, 0, 0, 0, 0, sm},
        {4.84143144246472090e+00, -1.16032004402742839e+00, -1.03622044471123109e-01, 1.66007664274403694e-03 * dp, 7.69901118419740425e-03 * dp, -6.90460016972063023e-05 * dp, 9.54791938424326609e-04 * sm},
        {8.34336671824457987e+00, 4.12479856412430479e+00, -4.03523417114321381e-01, -2.76742510726862411e-03 * dp, 4.99852801234917238e-03 * dp, 2.30417297573763929e-05 * dp, 2.85885980666130812e-04 * sm},
        {1.28943695621391310e+01, -1.51111514016986312e+01, -2.23307578892655734e-01, 2.96460137564761618e-03 * dp, 2.37847173959480950e-03 * dp, -2.96589568540237556e-05 * dp, 4.36624404335156298e-05 * sm},
        {1.53796971148509165e+01, -2.59193146099879641e+01, 1.79258772950371181e-01, 2.68067772490389322e-03 * dp, 1.62824170038242295e-03 * dp, -9.51592254519715870e-05 * dp, 5.15138902046611451e-05 * sm}};
    double px = 0, py = 0, pz = 0;
    for (int i = 0; i < 5; i++) { px += b[i].vx * b[i].mass; py += b[i].vy * b[i].mass; pz += b[i].vz * b[i].mass; }
    b[0].vx = -px / sm; b[0].vy = -py / sm; b[0].vz = -pz / sm;
    const double dt = 0.01;
    for (int s = 0; s < steps; s++)
    {
        for (int i = 0; i < 5; i++)
            for (int j = i + 1; j < 5; j++)
            {
                double dx = b[i].x - b[j].x, dy = b[i].y - b[j].y, dz = b[i].z - b[j].z;
                double d2 = dx * dx + dy * dy + dz * dz;
                double mag = dt / (d2 * sqrt(d2));
                b[i].vx -= dx * b[j].mass * mag; b[i].vy -= dy * b[j].mass * mag; b[i].vz -= dz * b[j].mass * mag;
                b[j].vx += dx * b[i].mass * mag; b[j].vy += dy * b[i].mass * mag; b[j].vz += dz * b[i].mass * mag;
            }
        for (int i = 0; i < 5; i++) { b[i].x += dt * b[i].vx; b[i].y += dt * b[i].vy; b[i].z += dt * b[i].vz; }
    }
    double e = 0;
    for (int i = 0; i < 5; i++)
    {
        e += 0.5 * b[i].mass * (b[i].vx * b[i].vx + b[i].vy * b[i].vy + b[i].vz * b[i].vz);
        for (int j = i + 1; j < 5; j++)
        {
            double dx = b[i].x - b[j].x, dy = b[i].y - b[j].y, dz = b[i].z - b[j].z;
            e -= b[i].mass * b[j].mass / sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
    return llrint(e * 1e9);
}

/* ---- binary trees: malloc'd nodes, freed after each check ---- */
typedef struct node { struct node *l, *r; } node;
static node *bottom_up(int d) { node *n = malloc(sizeof(node)); n->l = d > 0 ? bottom_up(d - 1) : NULL; n->r = d > 0 ? bottom_up(d - 1) : NULL; return n; }
static int check(node *n) { return (n->l && n->r) ? 1 + check(n->l) + check(n->r) : 1; }
static void release(node *n) { if (n->l) release(n->l); if (n->r) release(n->r); free(n); }
static void bt_chunk(void *ctx, int lo, int hi, double *ds, long long *is) { (void)ds; int d = *(int *)ctx; long long c = 0; for (int i = lo; i < hi; i++) { node *t = bottom_up(d); c += check(t); release(t); } *is = c; }
static long long binarytrees(int maxd, int par)
{
    long long total = 0;
    node *t = bottom_up(maxd + 1); total += check(t); release(t);
    node *ll = bottom_up(maxd);
    for (int d = 4; d <= maxd; d += 2)
    {
        int iters = 1 << (maxd - d + 4);
        long long c = 0;
        if (par) par_for(iters, workers() * 8, &d, bt_chunk, NULL, &c);
        else bt_chunk(&d, 0, iters, NULL, &c);
        total += c;
    }
    total += check(ll); release(ll);
    return total;
}

/* ---- pipeline: sum of (x*1.5+0.25) over x where the mapped value > 0.75 ---- */
static double *pipe_xs;
static void pipe_chunk(void *ctx, int lo, int hi, double *ds, long long *is) { (void)ctx; (void)is; double s = 0; for (int i = lo; i < hi; i++) { double y = pipe_xs[i] * 1.5 + 0.25; if (y > 0.75) s += y; } *ds = s; }
static long long pipeline(int par)
{
    double s = 0;
    if (par) par_for(10000000, workers() * 8, NULL, pipe_chunk, &s, NULL);
    else pipe_chunk(NULL, 0, 10000000, &s, NULL);
    return llrint(s);
}

/* ---- mandelbrot: count points inside after 100 iterations ---- */
static int mb_size;
static void mb_chunk(void *ctx, int lo, int hi, double *ds, long long *is)
{
    (void)ctx; (void)ds;
    long long inside = 0;
    for (int py = lo; py < hi; py++)
    {
        double ci = 2.0 * py / mb_size - 1.0;
        for (int px = 0; px < mb_size; px++)
        {
            double cr = 2.0 * px / mb_size - 1.5, zr = 0, zi = 0;
            int it = 0;
            while (it < 100 && zr * zr + zi * zi <= 4.0) { double t = zr * zr - zi * zi + cr; zi = 2.0 * zr * zi + ci; zr = t; it++; }
            if (it == 100) inside++;
        }
    }
    *is = inside;
}
static long long mandelbrot(int size, int par)
{
    mb_size = size;
    long long c = 0;
    if (par) par_for(size, workers() * 16, NULL, mb_chunk, NULL, &c);
    else mb_chunk(NULL, 0, size, NULL, &c);
    return c;
}

/* ---- particles: SoA arrays, 10 steps x 3 passes, then sum x + y ---- */
#define NP 1000000
static double *P_x, *P_y, *P_vx, *P_vy;
static void part_init(void)
{
    for (int i = 0; i < NP; i++) { P_x[i] = (i % 1000) * 0.01; P_y[i] = (i % 777) * 0.02; P_vx[i] = (i % 13) * 0.5 - 3.0; P_vy[i] = (i % 7) * 0.25; }
}
static void part_chunk(void *ctx, int lo, int hi, double *ds, long long *is)
{
    (void)ctx; (void)is;
    const double dt = 0.01, g = 9.81;
    for (int s = 0; s < 10; s++)
    {
        for (int i = lo; i < hi; i++) P_vy[i] = P_vy[i] - g * dt;
        for (int i = lo; i < hi; i++) P_x[i] = P_x[i] + P_vx[i] * dt;
        for (int i = lo; i < hi; i++) P_y[i] = P_y[i] + P_vy[i] * dt;
    }
    double sum = 0;
    for (int i = lo; i < hi; i++) sum += P_x[i] + P_y[i];
    *ds = sum;
}
static long long particles(int par)
{
    double s = 0;
    if (par) par_for(NP, workers() * 8, NULL, part_chunk, &s, NULL);
    else part_chunk(NULL, 0, NP, &s, NULL);
    return llrint(s);
}

/* ---- harness ---- */
static int par;
static long long nb(void) { return nbody(1000000); }
static long long bt(void) { return binarytrees(16, par); }
static long long pl(void) { return pipeline(par); }
static long long mb(void) { return mandelbrot(1000, par); }
static long long pt(void) { return particles(par); }
static int cmp(const void *a, const void *b) { double x = *(const double *)a, y = *(const double *)b; return (x > y) - (x < y); }

int main(int argc, char **argv)
{
    if (argc < 3) { fprintf(stderr, "usage: bench <name> <serial|parallel>\n"); return 2; }
    const char *name = argv[1];
    par = strcmp(argv[2], "parallel") == 0;
    if (!strcmp(name, "nbody") && par) { printf("SKIP nbody parallel\n"); return 0; }
    pipe_xs = malloc(sizeof(double) * 10000000);
    for (int i = 0; i < 10000000; i++) pipe_xs[i] = (i % 1000) * 0.001;
    P_x = malloc(sizeof(double) * NP); P_y = malloc(sizeof(double) * NP); P_vx = malloc(sizeof(double) * NP); P_vy = malloc(sizeof(double) * NP);
    long long (*k)(void) = NULL;
    if (!strcmp(name, "nbody")) k = nb; else if (!strcmp(name, "binarytrees")) k = bt; else if (!strcmp(name, "pipeline")) k = pl;
    else if (!strcmp(name, "mandelbrot")) k = mb; else if (!strcmp(name, "particles")) k = pt;
    else { printf("SKIP %s %s\n", name, argv[2]); return 0; }
    int stateful = !strcmp(name, "particles");   /* simulation mutates state: re-init outside the timed region */
    if (stateful) part_init();
    long long check_ = k();
    double ws = now_ms();
    for (int i = 0; i < 3 || now_ms() - ws < 1000; i++) { if (stateful) part_init(); k(); }
    double t[15];
    for (int r = 0; r < 15; r++)
    {
        if (stateful) part_init();
        double s = now_ms();
        long long got = k();
        t[r] = now_ms() - s;
        if (got != check_) { fprintf(stderr, "checksum mismatch\n"); return 1; }
    }
    qsort(t, 15, sizeof(double), cmp);
    printf("RESULT %s %.4f %lld\n", name, t[7], check_);
    return 0;
}
