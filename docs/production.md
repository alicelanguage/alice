# Production readiness

What Alice 0.12.3 is ready for, the test that backs each claim, and what is not ready yet. Update this page in the
same commit as any change that moves a row.

## Ready

| Area | What works | Evidence |
|---|---|---|
| Compiler | Alice -> C# -> IL through Roslyn; null safety, `val`, data races (AL0202) are compile errors | `tests/Alice.Tests/Compiler/` (1180+ tests, `dotnet test`) |
| Performance | At or ahead of idiomatic C# on the repo suite; 0-byte allocation tests; fork/join of 2 and 3 calls with serial clones; pooled arrays, lists and hashes of numbers | `benchmarks/README.md`, `AllocationTests`, `ParallelizeTests`, `OwnershipTests` |
| C# interop | Alice types are plain CLR types; C# calls Alice and Alice calls C# both ways | `Integration/SampleTests.Interop_samples_build_and_run` |
| F# interop | F# calls a `naming = "dotnet"` library and matches its sum types | `Integration/FSharpInteropTests` |
| Ruby features | blocks, mixins, `case`/`in`, refinements, class discovery (`subclasses`, `ObjectSpace.each_object`), `send`/`respond_to?`, assignment in conditions, truthiness | `Compiler/Ruby*Tests`, `CSharpCompatTests` |
| GPU | `gpu for` loops compiled to kernels, and Alice functions as explicit ILGPU kernels, on CUDA, OpenCL or the CPU accelerator (ADR 0021) | `Integration/SampleTests.Gpu_kernel_*`, `examples/gpu/ilgpu` (ran on OpenCL) |
| UI | Avalonia windows with XAML and Alice code-behind; MVVM view models | `Integration/SampleTests.Avalonia_window_*`, `examples/ui/avalonia` |
| .NET API shapes | generics and constraints, LINQ, async/await, `IAsyncEnumerable`, `await using`, iterators, events, delegates, default interface methods, records with attributes, spans, `out`/`ref`, `lock`, named tuples, method references | `Compiler/CSharpCompatTests`, `examples/dotnet/library_api.al` |
| Libraries | NuGet dependencies, lock file, workspaces; EF Core with LINQ-to-SQL; System.Text.Json (reflection and source-generated) | `Tooling/ManifestTests`, `Integration/SampleTests.Entity_framework_*`, `CSharpCompatTests` |
| Source generators | framework and NuGet generators run on Alice types; `partial def` for `[GeneratedRegex]` / `[LibraryImport]` | `CSharpCompatTests` "System.Text.Json source generator", "partial methods for generators"; `FfiTests.LibraryImport_through_a_partial_def` |
| FFI | `lib`/`fun`: numbers, Bool, UTF-8 strings, borrowed string returns, pinned arrays, `out`/`ref` structs, C callbacks from lambdas, `unsafe` for pointers; no marshalling stubs, works under NativeAOT | `Compiler/FfiTests`, `FfiStressTests`, ADR 0018 |
| Packaging | `alice pack` / `alice publish` for libraries and .NET tools; `Alice.Sdk` MSBuild SDK, `Alice.Cli` tool and `Alice.Runtime` on NuGet | `Integration/PackageTests`, `Integration/SdkPackageTests`, ADR 0020 |
| Deployment | NativeAOT single-file binaries with no trimming warnings; `[runtime] gc` and `globalization` settings | `benchmarks/alice` (all build native), `Tooling/ManifestTests` |
| Tooling | `alice fmt`, `alice test` (RSpec and xUnit styles, JUnit output), REPL, `alice explain`, `alice prove`; incremental package builds | `Tooling/*`, `Integration/PackageTests.Builds_are_incremental_*` |
| Editor (LSP) | diagnostics, completion, hover, signature help, go-to-definition, document symbols, formatting, code actions, find references, rename | `Tooling/Lsp*Tests` |

## Recommended settings

| Workload | `alice.toml` |
|---|---|
| web API, service, long-running parallel jobs | defaults (`gc = "auto"` picks server GC when the code is parallel) |
| short-lived CLI that must start fastest | `[runtime] gc = "workstation"`, or `[build] native = true` |
| a library published for C# / F# | `[package] naming = "dotnet"` |
| containers with tight memory | `[runtime] gc = "low-memory"`, `globalization = "invariant"` |

## Not ready yet

| Gap | Impact | Workaround |
|---|---|---|
| Incremental compilation within a package | a changed file regenerates the whole package's C# (unchanged packages are skipped: a no-op build takes 1.0 s) | split large code bases into workspace members |
| ASP.NET Core in CI on machines without the shared framework | `examples/web/aspnet` builds and is checked only where ASP.NET Core is installed | `examples/verify.sh` on a full SDK |
| WPF and MAUI code-behind | Avalonia XAML + Alice code-behind is tested; WPF (Windows-only) and MAUI are not exercised here | Avalonia for cross-platform UI, or XAML code-behind in C# with Alice view models |
| Interaction-net runtime | explored and parked: slower than fork/join on CPU (`benchmarks/inet/README.md`) | fork/join is the parallel model |
| References and rename on unbound code | where the binder could not resolve a name (code with errors), matching falls back to the name | fix the errors first, or review the edit preview |
| Ownership of fields and arrays of objects | collections stored in fields, and arrays of references, stay with the GC (local arrays of numbers and local lists and hashes of any type are pooled) | preallocate and reuse explicitly |
| `class_eval` / `eval` with strings | not offered: it needs the compiler at run time, defeats NativeAOT and is an injection risk in production code | refinements, `define_method`, or a plugin assembly found with `subclasses` |
| GPU: multiple devices, async overlap | one preferred device per process; a region synchronizes at its end | explicit ILGPU code for multi-device or stream overlap |
| NativeAOT `alice` compiler binary | the CLI needs the .NET runtime (it hosts Roslyn) | `dotnet tool install -g Alice.Cli` or the release archives |
| Rules over unbounded domains | `alice prove` checks rules exhaustively over finite ranges only | keep rule domains finite |
