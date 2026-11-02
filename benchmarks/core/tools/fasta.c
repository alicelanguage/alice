/* Benchmarks Game "fasta" (reference algorithm: LCG IM=139968 IA=3877 IC=29573, 60-column lines).
   Single-threaded, used only to generate the regex-redux input: `fasta 5000000 > input.txt`.
   Expected for n=5000000: 50833411 bytes (regex-redux prints 50833411 / 50000000 / 27388361). */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define IM 139968
#define IA 3877
#define IC 29573
#define WIDTH 60

static unsigned last = 42;
static double gen_random(double max) { last = (last * IA + IC) % IM; return max * last / IM; }

struct aa { char c; double p; };

static void make_cumulative(struct aa *t, int n) {
  double acc = 0; for (int i = 0; i < n; i++) { acc += t[i].p; t[i].p = acc; }
}

static void repeat_fasta(const char *id, const char *desc, const char *s, long n) {
  printf(">%s %s\n", id, desc);
  long len = (long)strlen(s), pos = 0; char line[WIDTH + 1];
  while (n > 0) {
    int m = n < WIDTH ? (int)n : WIDTH;
    for (int i = 0; i < m; i++) { line[i] = s[pos++]; if (pos == len) pos = 0; }
    line[m] = '\n'; fwrite(line, 1, m + 1, stdout); n -= m;
  }
}

static void random_fasta(const char *id, const char *desc, struct aa *t, int nt, long n) {
  printf(">%s %s\n", id, desc);
  char line[WIDTH + 1];
  while (n > 0) {
    int m = n < WIDTH ? (int)n : WIDTH;
    for (int i = 0; i < m; i++) {
      double r = gen_random(1.0); int k = 0;
      while (k < nt - 1 && r >= t[k].p) k++;
      line[i] = t[k].c;
    }
    line[m] = '\n'; fwrite(line, 1, m + 1, stdout); n -= m;
  }
}

static const char *alu =
  "GGCCGGGCGCGGTGGCTCACGCCTGTAATCCCAGCACTTTGG"
  "GAGGCCGAGGCGGGCGGATCACCTGAGGTCAGGAGTTCGAGA"
  "CCAGCCTGGCCAACATGGTGAAACCCCGTCTCTACTAAAAAT"
  "ACAAAAATTAGCCGGGCGTGGTGGCGCGCGCCTGTAATCCCA"
  "GCTACTCGGGAGGCTGAGGCAGGAGAATCGCTTGAACCCGGG"
  "AGGCGGAGGTTGCAGTGAGCCGAGATCGCGCCACTGCACTCC"
  "AGCCTGGGCGACAGAGCGAGACTCCGTCTCAAAAA";

int main(int argc, char **argv) {
  long n = argc > 1 ? atol(argv[1]) : 1000;
  static char buf[1 << 16]; setvbuf(stdout, buf, _IOFBF, sizeof buf);
  struct aa iub[] = {
    {'a',0.27},{'c',0.12},{'g',0.12},{'t',0.27},{'B',0.02},{'D',0.02},{'H',0.02},{'K',0.02},
    {'M',0.02},{'N',0.02},{'R',0.02},{'S',0.02},{'V',0.02},{'W',0.02},{'Y',0.02}};
  struct aa homo[] = {{'a',0.3029549426680},{'c',0.1979883004921},{'g',0.1975473066391},{'t',0.3015094502008}};
  make_cumulative(iub, 15); make_cumulative(homo, 4);
  repeat_fasta("ONE", "Homo sapiens alu", alu, n * 2);
  random_fasta("TWO", "IUB ambiguity codes", iub, 15, n * 3);
  random_fasta("THREE", "Homo sapiens frequency", homo, 4, n * 5);
  return 0;
}
