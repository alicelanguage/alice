# 0002 - Separate runtime and native kernels
Status: amended (native clang kernels removed; runtime is managed SIMD only)
Context: Fast numeric code needs SIMD and tuned kernels. The compiler must not carry runtime code.
Decision:
- `src/Alice.Runtime` is a separate assembly referenced by compiled programs: `Simd`, sparse (`CsrMatrix<T>`, `SparseVector<T>`), `Par`, `AMath`, `Native`, `Prelude`.
- clang/LLVM is used only for `src/Alice.Runtime/Native/alice_native.c` (`-O3 -march=native`) producing `libalice_native`, called via P/Invoke.
- Every native kernel has a managed `Vector<T>` fallback used when the library is missing.
Consequences: Programs run anywhere .NET runs; native lib is an optional accelerator. Two implementations per native kernel must be kept equivalent by tests.
