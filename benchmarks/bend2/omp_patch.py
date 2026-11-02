#!/usr/bin/env python3
"""Copy Bend's serial C twins to c/<name>.c and add an OpenMP fork path under #ifdef _OPENMP.

Without -fopenmp the file compiles to exactly Bend's serial program. With -fopenmp the same
recursion forks with `omp task` over the top CUT levels (CUT=10, as in ../bend-vs), main runs it
inside `omp parallel` + `omp single`. Usage: omp_patch.py <bend runtime dir> <out c dir>
"""
import pathlib, sys

SRC = pathlib.Path(sys.argv[1])
OUT = pathlib.Path(sys.argv[2])
HDR = "#ifdef _OPENMP\n#include <omp.h>\n#define CUT 10 /* fork the top CUT levels */\nstatic int fork_min;\n#endif\n"


def sub(s, old, new, name):
    assert s.count(old) == 1, (name, old)
    return s.replace(old, new)


def fork(name, s, base_case_end, a_call, b_call, combine, ty="uint32_t", var="k", setup=""):
    """Insert a task fork right after the base case of a binary recursion."""
    block = (f"{base_case_end}\n#ifdef _OPENMP\n  if ((int){var} > fork_min) {{\n{setup}    {ty} a, b;\n"
             f"#pragma omp task shared(a)\n    a = {a_call};\n    b = {b_call};\n#pragma omp taskwait\n"
             f"    return {combine};\n  }}\n#endif")
    return sub(s, base_case_end, block, name)


def par_main(name, s, call_line, call, top, ty="uint32_t", fmt='printf("%u\\n", r);'):
    block = (f"#ifdef _OPENMP\n  {{\n    {ty} r;\n    fork_min = (int)({top}) - CUT;\n#pragma omp parallel\n"
             f"#pragma omp single\n    r = {call};\n    {fmt}\n    return 0;\n  }}\n#endif\n{call_line}")
    return sub(s, call_line, block, name)


def first_include(s):
    i = s.index("#include")
    j = s.index("\n", s.rindex("#include")) + 1
    return s[:j] + HDR + s[j:]


patches = {}


def bfs(s):
    s = fork("bfs", s, "    return maze_run(i);\n  }", "batch_run(k - 1u, i)", "batch_run(k - 1u, i + (1u << (k - 1u)))", "a + b")
    return par_main("bfs", s, '  printf("%u\\n", batch_run(DEPTH, 0u));', "batch_run(DEPTH, 0u)", "DEPTH")


def editdist(s):
    s = fork("editdist", s, "    return pair_run(p);\n  }", "batch_run(d - 1u, p)", "batch_run(d - 1u, p + (1u << (d - 1u)))", "a + b", var="d")
    return par_main("editdist", s, '  printf("%u\\n", batch_run(DEPTH, 0u));', "batch_run(DEPTH, 0u)", "DEPTH")


def gameoflife(s):
    s = fork("gameoflife", s, "    return chunk_run(64u, i, g, 0u, 0u, 0u, 0u);\n  }", "batch_run(d - 1u, i, g)",
             "batch_run(d - 1u, i + (64u << (d - 1u)), g)", "census_zip(a, b)", ty="Census", var="d")
    return par_main("gameoflife", s, '  printf("%u\\n", census_fin(batch_run(size, 0u, gens)));',
                    "census_fin(batch_run(size, 0u, gens))", "size")


def hashmap(s):
    s = sub(s, "static Table tab;\n", "static Table tab;\n#ifdef _OPENMP\n#pragma omp threadprivate(tab)\n#endif\n", "hashmap")
    s = fork("hashmap", s, "    return table_run(t, d);\n  }", "batch_run(p - 1u, t, d)", "batch_run(p - 1u, t + (1u << (p - 1u)), d)", "a + b", var="p")
    return par_main("hashmap", s, '  printf("%u\\n", batch_run(TABLES, 0u, KEYS));', "batch_run(TABLES, 0u, KEYS)", "TABLES")


def lexer(s):
    # main.c runs the lines in one loop; the OpenMP path splits that loop (same sum)
    old = "  char buf[128];\n  uint32_t sum = 0;\n"
    new = ("#ifdef _OPENMP\n  {\n    uint32_t r = 0;\n#pragma omp parallel for reduction(+ : r) schedule(static, 4096)\n"
           "    for (uint32_t i = 0; i < (1u << DEPTH); i++) {\n      char lb[128];\n      r += lex(lb, gen(seed(i), lb));\n    }\n"
           '    printf("%u\\n", r);\n    return 0;\n  }\n#endif\n' + old)
    return sub(s, old, new, "lexer")


def mandelbrot(s):
    s = sub(s, "static uint32_t sel_go", "#ifdef _OPENMP\nstatic int hfold_min, rcol_min;\n#endif\nstatic uint32_t sel_go", "mandelbrot")
    s = sub(s, "    return hchunk(64, i, tn, 0, 0, 0, 0, 0, 0, 0, 0);\n  uint32_t p = d - 1;\n",
            "    return hchunk(64, i, tn, 0, 0, 0, 0, 0, 0, 0, 0);\n  uint32_t p = d - 1;\n#ifdef _OPENMP\n"
            "  if ((int)d > hfold_min) {\n    Hs a, b;\n#pragma omp task shared(a)\n    a = hfold(p, i, tn);\n"
            "    b = hfold(p, i + (64u << p), tn);\n#pragma omp taskwait\n    return hzip(a, b);\n  }\n#endif\n", "mandelbrot")
    s = sub(s, "    return rpix(i, tn, k0, k1, k2, k3, k4, k5, k6, k7);\n  uint32_t p = d - 1;\n",
            "    return rpix(i, tn, k0, k1, k2, k3, k4, k5, k6, k7);\n  uint32_t p = d - 1;\n#ifdef _OPENMP\n"
            "  if ((int)d > rcol_min) {\n    uint32_t a, b;\n#pragma omp task shared(a)\n    a = rcol(p, i, tn, k0, k1, k2, k3, k4, k5, k6, k7);\n"
            "    b = rcol(p, i + (1u << p), tn, k0, k1, k2, k3, k4, k5, k6, k7);\n#pragma omp taskwait\n    return a + b;\n  }\n#endif\n",
            "mandelbrot")
    old = '  printf("%u\\n", rend(18, its()));'
    new = ("#ifdef _OPENMP\n  {\n    uint32_t r;\n    hfold_min = 18 - CUT;\n    rcol_min = 18 + 6 - CUT;\n#pragma omp parallel\n"
           '#pragma omp single\n    r = rend(18, its());\n    printf("%u\\n", r);\n    return 0;\n  }\n#endif\n' + old)
    return sub(s, old, new, "mandelbrot")


def kmeans(s):
    # the stats arena is per call chain: a forked restart gets its own arena
    s = sub(s, "  if (!b)\n    return rst(h, r, d);\n",
            "  if (!b)\n    return rst(h, r, d);\n#ifdef _OPENMP\n  {\n    uint32_t x, y;\n#pragma omp task shared(x)\n"
            "    {\n      Arena ha = {0};\n      x = rbatch(&ha, b - 1, r, d);\n      free(ha.at);\n    }\n"
            "    y = rbatch(h, b - 1, r + (1u << (b - 1)), d);\n#pragma omp taskwait\n    return x + y;\n  }\n#endif\n", "kmeans")
    old = '  printf("%u\\n", rbatch(&h, 6, 0, size()));'
    new = ("#ifdef _OPENMP\n  {\n    uint32_t r;\n#pragma omp parallel\n#pragma omp single\n    r = rbatch(&h, 6, 0, size());\n"
           '    printf("%u\\n", r);\n    free(h.at);\n    return 0;\n  }\n#endif\n' + old)
    return sub(s, old, new, "kmeans")


def merkle(s):
    # build: the serial arena push order is post-order, so a subtree at offset o of depth d puts its
    # left half at o, its right half at o + size(d-1) and itself at o + 2 size(d-1): tasks write
    # disjoint slots of one preallocated arena, same layout as the serial build
    s = sub(s, "// build the Merkle tree over blocks b .. b + 2^d - 1\n",
            "#ifdef _OPENMP\nstatic uint32_t mt_build_at(MtArena *a, uint32_t d, uint32_t b, uint32_t o) {\n"
            "  if (d == 0u) {\n    a->items[o] = (Mt){MT_LEAF, leaf_hash(b), 0, 0};\n    return o;\n  }\n"
            "  uint32_t sz = (1u << d) - 1u;\n  uint32_t l, r;\n  if ((int)d > fork_min) {\n#pragma omp task shared(l)\n"
            "    l = mt_build_at(a, d - 1u, b, o);\n    r = mt_build_at(a, d - 1u, b + (1u << (d - 1u)), o + sz);\n#pragma omp taskwait\n"
            "  } else {\n    l = mt_build_at(a, d - 1u, b, o);\n    r = mt_build_at(a, d - 1u, b + (1u << (d - 1u)), o + sz);\n  }\n"
            "  uint32_t n = o + 2u * sz;\n  a->items[n] = (Mt){MT_NODE, hash_join(a->items[l].h, a->items[r].h), l, r};\n  return n;\n}\n#endif\n"
            "// build the Merkle tree over blocks b .. b + 2^d - 1\n", "merkle")
    s = fork("merkle", s, "    return leaf_hash(block);", "hash_tree(depth - 1u, block)", "hash_tree(depth - 1u, block + (1u << (depth - 1u)))",
             "hash_join(a, b)", var="depth")
    s = sub(s, "\ntypedef struct Path {", """
#ifdef _OPENMP
// audit with the depth known: fork the top levels, the serial audit below
static uint32_t mt_audit_par(const MtArena *a, uint32_t t, uint32_t d) {
  Mt n = a->items[t];
  if (n.tag == MT_LEAF || (int)d <= fork_min)
    return mt_audit(a, t);
  uint32_t x, y;
#pragma omp task shared(x)
  x = mt_audit_par(a, n.l, d - 1u);
  y = mt_audit_par(a, n.r, d - 1u);
#pragma omp taskwait
  uint32_t m = hash_join(x, y);
  return m + (m ^ n.h);
}
#endif
typedef struct Path {""", "merkle")
    old = "  uint32_t t = mt_build(&arena, SIZE, 0);\n"
    new = """#ifdef _OPENMP
  {
  uint32_t t, root, prf;
  fork_min = SIZE - CUT;
  arena.capacity = arena.length = (2u << SIZE) - 1u;
  arena.items = malloc(arena.capacity * sizeof(Mt));
#pragma omp parallel
#pragma omp single
  {
    t = mt_build_at(&arena, SIZE, 0, 0);
    root = mt_audit_par(&arena, t, SIZE);
    prf = path_gen(&paths, SIZE, PROBE, 0);
  }
  printf("%u\\n", hash_join(root, path_verify(&paths, prf, leaf_hash(PROBE))));
  free(arena.items);
  return 0;
  }
#endif
""" + old
    s = sub(s, old, new, "merkle")
    return s


for name, f in [("bfs", bfs), ("editdist", editdist), ("gameoflife", gameoflife), ("hashmap", hashmap),
                ("lexer", lexer), ("mandelbrot", mandelbrot), ("kmeans", kmeans), ("merkle", merkle)]:
    src = (SRC / name / "main.c").read_text()
    (OUT / f"{name}.c").write_text(first_include(f(src)))
    print("patched", name)
