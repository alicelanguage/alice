# ADR 0021: `gpu for` loops through ILGPU

Status: Accepted (0.12.3)

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
  becomes `ILGPU.Atomic.Add` on a one-element device buffer, added to `t` after the launch (float sums may differ in
  the last bits from a serial sum: the order of additions varies, as in any parallel float reduction). Each rejection is a compile error naming the
  cause. Body errors come before the missing-package error.
- Host code uploads every captured array, launches once with `(count, start, views..., scalars...)`, synchronizes and
  copies back only the arrays the body writes. The kernel delegate is compiled once per process; one accelerator is
  shared (`__AliceGpu`).
- Kernel bodies are `unchecked` (no overflow traps on devices) and are not FMA-contracted: the device compiler
  contracts `a*b + c` itself, and an explicit `FusedMultiplyAdd` was rejected by an OpenCL driver.
- Extension classes of ILGPU (`Allocate1D`, `CopyToCPU`, `LoadAutoGroupedStreamKernel`) are found by reflection at
  compile time and called fully qualified, so generated files import nothing.

Consequences: transfers are per launch, so a loop pays off when its work outweighs copying its arrays; keeping data on
the device across loops is future work. Explicit ILGPU kernels (`examples/gpu/ilgpu`) remain the
tool for everything else. Tests: `ParallelizeTests.Gpu_loops_explain_*` and `Integration/SampleTests.Gpu_kernel_*`,
run on an OpenCL GPU and on the CPU accelerator.
