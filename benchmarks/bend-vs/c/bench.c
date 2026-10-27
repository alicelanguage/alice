// Bend-vs suite in C: same algorithms, uint32_t wrapping, float mandelbrot.
// Built twice: serial, and -fopenmp with tasks above a depth cutoff (CUT levels) for the forked recursions.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _OPENMP
#define CUT 10
#define FORK(a, ea, b, eb, depth) \
  if ((depth) < CUT) { _Pragma("omp task shared(a)") a = ea; b = eb; _Pragma("omp taskwait") } else { a = ea; b = eb; }
#else
#define FORK(a, ea, b, eb, depth) { a = ea; b = eb; }
#endif

typedef struct Tree { struct Tree *l, *r; uint32_t v; } Tree;

static uint32_t pow2(int n, int k) {
  if (n == 0) return 1;
  uint32_t a, b; FORK(a, pow2(n - 1, k + 1), b, pow2(n - 1, k + 1), k);
  return a + b;
}
static uint32_t fib(int n, int k) {
  if (n < 2) return (uint32_t)n;
  uint32_t a, b; FORK(a, fib(n - 1, k + 1), b, fib(n - 2, k + 1), k);
  return a + b;
}
static Tree *gen(int d, uint32_t x, int k) {
  Tree *t = malloc(sizeof(Tree));
  if (d == 0) { t->l = t->r = NULL; t->v = x; return t; }
  Tree *a, *b; FORK(a, gen(d - 1, x * 2, k + 1), b, gen(d - 1, x * 2 + 1, k + 1), k);
  t->l = a; t->r = b; return t;
}
static uint32_t total(Tree *t, int k) {
  if (!t->l) return t->v;
  uint32_t a, b; FORK(a, total(t->l, k + 1), b, total(t->r, k + 1), k);
  return a + b;
}
static uint32_t place(uint32_t avail, uint32_t all, uint32_t cols, uint32_t d1, uint32_t d2, int k);
static uint32_t solve(uint32_t all, uint32_t cols, uint32_t d1, uint32_t d2, int k) {
  if (cols == all) return 1;
  return place(all & ~(cols | d1 | d2), all, cols, d1, d2, k);
}
static uint32_t place(uint32_t avail, uint32_t all, uint32_t cols, uint32_t d1, uint32_t d2, int k) {
  if (avail == 0) return 0;
  uint32_t bit = avail & (0u - avail), a, b;
  FORK(a, solve(all, cols | bit, (d1 | bit) << 1, (d2 | bit) >> 1, k + 1), b, place(avail ^ bit, all, cols, d1, d2, k + 1), k);
  return a + b;
}
static uint32_t iter(float cr, float ci) {
  float zr = 0, zi = 0; uint32_t i = 0;
  while (i != 256) {
    float zr2 = zr * zr, zi2 = zi * zi;
    if (zr2 + zi2 > 4.0f) break;
    zi = 2.0f * zr * zi + ci; zr = zr2 - zi2 + cr; i++;
  }
  return i;
}
static uint32_t row(uint32_t y, uint32_t w) {
  float fw = (float)w, ci = (float)y * (2.0f / fw) - 1.0f, dx = 3.0f / fw, cr = -2.0f;
  uint32_t acc = 0;
  for (uint32_t x = 0; x != w; x++) { acc += iter(cr, ci); cr += dx; }
  return acc;
}
static uint32_t rows(int d, uint32_t y, uint32_t w, int k) {
  if (d == 0) return row(y, w);
  uint32_t a, b; FORK(a, rows(d - 1, y * 2, w, k + 1), b, rows(d - 1, y * 2 + 1, w, k + 1), k);
  return a + b;
}
static uint32_t steps(uint32_t x) {
  uint32_t s = 0;
  while (x > 1 && s != 1000) { x = (x & 1) ? x * 3 + 1 : x >> 1; s++; }
  return s;
}
static uint32_t range(int d, uint32_t i, int k) {
  if (d == 0) return steps(i + 1);
  uint32_t a, b; FORK(a, range(d - 1, i * 2, k + 1), b, range(d - 1, i * 2 + 1, k + 1), k);
  return a + b;
}
static uint32_t run(const char *w, int n) {
  if (!strcmp(w, "pow2")) return pow2(n, 0);
  if (!strcmp(w, "fib")) return fib(n, 0);
  if (!strcmp(w, "tree")) return total(gen(n, 1, 0), 0);
  if (!strcmp(w, "nqueens")) return solve((1u << n) - 1, 0, 0, 0, 0);
  if (!strcmp(w, "mandel")) return rows(n, 0, 1u << n, 0);
  if (!strcmp(w, "collatz")) return range(n, 0, 0);
  return 0;
}
int main(int argc, char **argv) {
  uint32_t r;
#ifdef _OPENMP
  #pragma omp parallel
  #pragma omp single
#endif
  r = run(argv[1], atoi(argv[2]));
  printf("%u\n", r);
  return 0;
}
