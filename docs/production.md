# Production readiness

What Alice 0.13.0 is ready for, the test that backs each claim, and what is not ready yet. Update this page in the
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
| ASP.NET Core in CI on machines without the shared framework | `examples/web/aspnet` builds and is checked only where ASP.NET Core is installed | `examples/verify.sh` on a full SDK |
| WPF and MAUI code-behind | Avalonia XAML + Alice code-behind is tested; WPF (Windows-only) and MAUI are not exercised here | Avalonia for cross-platform UI, or XAML code-behind in C# with Alice view models |
| Interaction-net runtime | explored and parked: slower than fork/join on CPU (`benchmarks/inet/README.md`) | fork/join is the parallel model |
| References and rename on unbound code | where the binder could not resolve a name (code with errors), matching falls back to the name | fix the errors first, or review the edit preview |
| Ownership of fields and arrays of objects | collections stored in fields, and arrays of references, stay with the GC (local arrays of numbers and local lists and hashes of any type are pooled) | preallocate and reuse explicitly |
| `class_eval` / `eval` with strings | not offered: it needs the compiler at run time, defeats NativeAOT and is an injection risk in production code | refinements, `define_method`, or a plugin assembly found with `subclasses` |
| GPU: multiple devices, async overlap | one preferred device per process; a region synchronizes at its end | explicit ILGPU code for multi-device or stream overlap |
| NativeAOT `alice` compiler binary | the CLI needs the .NET runtime (it hosts Roslyn) | `dotnet tool install -g Alice.Cli` or the release archives |
| Rules with loops or recursion under `--smt` | `alice prove --smt` (Z3) proves straight-line integer rules over full Int32/Int64 ranges; others fall back to enumeration | keep such rules finite |

## Security limits

Known limits of the toolchain and supply chain; see [SECURITY.md](../SECURITY.md) for reporting and workspace trust.

| Limit | Impact | Workaround |
|---|---|---|
| Release archives are checked by SHA-256 from the same release, not signed | integrity against corruption, no authenticity if the release itself is replaced | install a pinned version over https; compare the archive with a checksum obtained separately |
| `alice publish --api-key KEY` and `--dotnet-push` put the key in a process's arguments | other local users can read it from the process list while the command runs | set `NUGET_API_KEY` and use the default push; use a short-lived key scoped to the package |
| `@[Native]` builds look up `clang`/`cc`/`gcc` by bare name | in an untrusted checkout a compiler planted in the working directory can run | do not build `@[Native]` code from untrusted checkouts |
| Building runs repo content (MSBuild files, NuGet build targets, analyzers, source generators) | `alice build`/`run`/`test` in an untrusted checkout execute its code, as `dotnet build` does | build untrusted projects in a container or VM |
| `obj/refs.txt` is read by the LSP to load reference metadata | repo-controlled paths are opened by the editor server (metadata only) | open untrusted checkouts without the Alice extension |
| The lock file pins NuGet versions, not content hashes | a changed package on the feed is not detected | a private feed, or `packages.lock.json` with `RestoreLockedMode` in CI |
| The telemetry collector limits per install id and globally, never per IP | one client can use up the global budget; it bounds storage, not fairness | run the collector behind a reverse proxy with per-IP limits |
| The run cache key and HMAC key live in the same user's cache directory | protects against other users and tampering by other tools, not against code already running as you | `ALICE_NO_CACHE=1` |
