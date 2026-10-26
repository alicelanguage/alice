# ADR 0030: Tensor primitives in the runtime

Status: Accepted (0.13.x)

Alice code that does numeric work on large arrays (linear algebra, signal processing, neural-network style math)
had `Simd` and `AMath` over managed arrays: no aligned storage, no strided views, no 16-bit floats, no quantized
storage, and no parallel GEMV/GEMM.

Decision: the runtime ships general-purpose primitives in `src/Alice.Runtime/Tensors/`, in pure C#. They are building
blocks, not an inference engine: no model formats, loaders, caches or samplers.

- `Tensor<T>`: n-d, strided, over 64-byte aligned `NativeMemory`. `Slice`/`Narrow`/`Transpose`/`Reshape` are
  zero-copy views that keep their base alive; `Contiguous()` copies only when needed. `Wrap` views external memory.
- `TensorArena`: an aligned bump allocator for per-step scratch, so loops reuse memory with `Reset()`.
- `BFloat16` (round to nearest even, vectorized widening) and `Half` span conversions.
- `QuantizedMatrix`: 32-value blocks with a `Half` scale, int8 (34 B) and int4 (18 B). The byte layout equals GGML
  Q8_0/Q4_0, so existing quantized data can be mapped as is. Dot products dequantize in registers (fused).
- `MappedFile`: read-only `MemoryMappedFile` exposed as spans, tensors and quantized matrices without copying.
- `Nn`: dot, GEMV (f32, Half, BFloat16, int8/int4 blocks), GEMM (k/n tiled, row-parallel), `MatMulTransposed`,
  elementwise (add, multiply, scale, axpy, fill), reductions (sum, max), exp, softmax, RMS norm, layer norm, silu, gelu,
  tanh, relu, as plain functions over spans.
- SIMD tiers: AVX-512F, AVX2, Vector128 (SSE/NEON), scalar. Quantized kernels use sign/zero-extending loads straight to
  int32 lanes and apply each block scale once. `FastMath.Exp` is a range-reduced degree-6 polynomial (relative error
  below 2e-6), identical across tiers.
- `KernelPool`: persistent workers fed a function pointer and a stack context; the caller works too and waits until
  every woken worker has left the job. Dispatch allocates nothing; nested calls run serially.

Exposure: `using Alice.Runtime` makes `Nn`, `Tensor`, `QuantizedMatrix`, `MappedFile` callable from Alice through the
normal static-member binding (as `Simd.Dot`); `Array(Float32)` passes as a span.

Invariants: 0 managed allocations in steady state (`TensorAllocationTests`), numeric results within stated tolerances
of naive double-precision references (`tests/Alice.Tests/Runtime/Tensors`). The runtime project now allows `unsafe`.

Consequences: native memory is freed by `Dispose` or the finalizer; disposing a base while views are used is a caller
bug. Writing through a tensor over a read-only mapping faults. Half GEMV converts scalarly (no portable vector path).
`benchmarks/tensors` measures GEMV 4096x4096.
