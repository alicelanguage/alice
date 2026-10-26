# ADR 0021: `gpu for` loops through ILGPU

Status: Accepted (0.13.0; launch shape, reductions and transfers revised in 0.13.2)

`par for` spreads independent iterations over CPU cores. Data-parallel loops over large arrays of numbers often run
faster on a GPU, but writing CUDA or OpenCL means leaving the language. ILGPU is a pure-C# compiler from .NET IL to
CUDA PTX, OpenCL and a CPU accelerator, so it fits the single-backend rule (ADR 0007): Alice still emits C#, and ILGPU
compiles the kernel method at run time.

Decision:

- `gpu for i in a...b ... end` lowers its body to a static kernel method and launches it on the preferred device
  (`ALICE_GPU=cpu` forces ILGPU's CPU accelerator). The program references ILGPU (`alice add ILGPU`), and the runtime
  stays free of it.
- Admission is the `par for` proof (each iteration writes only its own element, ADR 0008 race checks apply) plus GPU
  rules: captured values are arrays of numbers or numbers; the body uses arithmetic, indexing, `Math.*` and number
  conversions; no calls, allocation, strings or objects. A reduction `t += e` into an outer Int32/Int64/Float32/Float64
  is reduced on the device (see below) into a one-element device buffer, added to `t` after the launch (float sums may differ in
  the last bits from a serial sum: the order of additions varies, as in any parallel float reduction). Each rejection is a compile error naming the
  cause. Body errors come before the missing-package error.
- Host code uploads the captured arrays the body reads, launches once with `(config, start, count, views...,
  scalars...)`, synchronizes and copies back only the arrays the body writes. The kernel delegate is compiled once per process; one accelerator is
  shared (`__AliceGpu`).
- Kernel bodies are `unchecked` (no overflow traps on devices) and are not FMA-contracted: the device compiler
  contracts `a*b + c` itself, and an explicit `FusedMultiplyAdd` was rejected by an OpenCL driver.
- Extension classes of ILGPU (`Allocate1D`, `CopyToCPU`, `MemSetToZero`, `LoadStreamKernel`) are found by reflection at
  compile time and called fully qualified, so generated files import nothing.

A `gpu ... end` region keeps the arrays of the `gpu for` loops inside on the device: each is uploaded on first use, and
written ones are copied back once at `end` (launches queue on the device's stream, so the region synchronizes once).
Any CPU use of such an array inside the region, other than its length, is a compile error, since it would see stale
data. 51 launches over 1M floats: 29 ms in a region, 132 ms with per-launch transfers (OpenCL, warm).

Launch shape, reductions and transfers (0.13.2):

- Kernels are explicitly grouped (`LoadStreamKernel` + `KernelConfig`) and loop grid-stride over `[start, start +
  count)`. The group size is ILGPU's occupancy estimate (`EstimateGroupSize`; 256 where the backend has none, as on
  OpenCL), clamped to the device and rounded down to a power of two that is a multiple of `WarpSize`. The grid is
  `ceil(count / group)` capped at `NumMultiprocessors x resident groups per multiprocessor x 2`; the estimate runs
  once per kernel (`__AliceGpu.Grid`, cached in two static ints).
- A reduction accumulates in a per-thread register. After the loop the group reduces it: `Warp.ShuffleDown` within
  each warp, then a shared-memory tree over the warps' partials (dynamic shared memory, one element per warp, with
  `Group.Barrier`), and the group's first thread does the one `Atomic.Add`. ILGPU.Algorithms' group reductions would
  be the same shape; core ILGPU's primitives avoid a second package. Integer sums are exact; float sums are reordered.
- The device accumulator is a `[ThreadStatic]` cached one-element buffer, zeroed with `MemSetToZero` and read back
  into a local (`CopyToCPU(ref v, 1)`): a launch allocates nothing on the host or device for it.
- Read/write sets: an array whose every write is a top-level `a[i] = e` at the loop index and that the body never
  reads is allocated on the device without an upload, and only `[start, start + count)` is copied back. Read-only
  arrays are never copied back. In a region, `Region.Get(array, upload, written)` skips the first upload when the
  loop overwrites the whole array (`0...a.size`); the device copy stays the valid one until `end` copies written
  arrays back.
- Measured on an Intel Iris Xe (OpenCL), 10M Int64 sum, warm: 4-6 ms with a group reduction against 18 ms with an
  atomic per iteration; on the CPU accelerator 0.3-2 s against 31-91 s. Tests: `Compiler/GpuLoopTests` (ILGPU CPU accelerator: exact Int64/Int32 sums against serial
  sums, write-only and partial-range writes, regions).

Consequences: outside a region, transfers are per launch, so a loop pays off when its work outweighs copying its arrays. Explicit ILGPU kernels (`examples/gpu/ilgpu`) remain the
tool for everything else. Tests: `ParallelizeTests.Gpu_loops_explain_*` and `Integration/SampleTests.Gpu_kernel_*`,
run on an OpenCL GPU and on the CPU accelerator.
