/* Alice native kernels. Built with -O3 -march=native so the compiler emits AVX2/AVX-512.
   ABI: plain C, double*, int64 length. Loaded by Alice.Runtime.Native via P/Invoke. */
#include <stdint.h>

#if defined(_WIN32)
#define ALICE_API __declspec(dllexport)
#else
#define ALICE_API __attribute__((visibility("default")))
#endif

ALICE_API double alice_dot_f64(const double *restrict a, const double *restrict b, int64_t n) {
    double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
    int64_t i = 0;
    for (; i + 4 <= n; i += 4) {
        s0 += a[i] * b[i]; s1 += a[i + 1] * b[i + 1];
        s2 += a[i + 2] * b[i + 2]; s3 += a[i + 3] * b[i + 3];
    }
    for (; i < n; i++) s0 += a[i] * b[i];
    return (s0 + s1) + (s2 + s3);
}

ALICE_API double alice_sum_f64(const double *restrict a, int64_t n) {
    double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
    int64_t i = 0;
    for (; i + 4 <= n; i += 4) { s0 += a[i]; s1 += a[i + 1]; s2 += a[i + 2]; s3 += a[i + 3]; }
    for (; i < n; i++) s0 += a[i];
    return (s0 + s1) + (s2 + s3);
}
