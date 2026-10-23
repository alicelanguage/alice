# Production readiness

What Alice 0.12.3 is ready for, the test that backs each claim, and what is not ready yet. Update this page in the
same commit as any change that moves a row.

## Ready

| Area | What works | Evidence |
|---|---|---|
| Compiler | Alice -> C# -> IL through Roslyn; null safety, `val`, data races (AL0202) are compile errors | `tests/Alice.Tests/Compiler/` (1180+ tests, `dotnet test`) |
| Performance | At or ahead of idiomatic C# on the repo suite; 0-byte allocation tests; fork/join with serial clones | `benchmarks/README.md`, `AllocationTests`, `ParallelizeTests` |
| C# interop | Alice types are plain CLR types; C# calls Alice and Alice calls C# both ways | `Integration/SampleTests.Interop_samples_build_and_run` |
| F# interop | F# calls a `naming = "dotnet"` library and matches its sum types | `Integration/FSharpInteropTests` |
| .NET API shapes | generics and constraints, LINQ, async/await, `IAsyncEnumerable`, `await using`, iterators, events, delegates, default interface methods, records with attributes, spans, `out`/`ref`, `lock`, named tuples, method references | `Compiler/CSharpCompatTests`, `examples/dotnet/library_api.al` |
| Libraries | NuGet dependencies, lock file, workspaces; EF Core with LINQ-to-SQL; System.Text.Json (reflection and source-generated) | `Tooling/ManifestTests`, `Integration/SampleTests.Entity_framework_*`, `CSharpCompatTests` |
| Source generators | framework and NuGet generators run on Alice types; `partial def` for `[GeneratedRegex]` / `[LibraryImport]` | `CSharpCompatTests` "System.Text.Json source generator", "partial methods for generators"; `FfiTests.LibraryImport_through_a_partial_def` |
| FFI | `lib`/`fun`: numbers, Bool, UTF-8 strings, borrowed string returns, pinned arrays, `out`/`ref` structs, C callbacks from lambdas, `unsafe` for pointers; no marshalling stubs, works under NativeAOT | `Compiler/FfiTests`, `FfiStressTests`, ADR 0018 |
| Packaging | `alice pack` / `alice publish` for libraries and .NET tools; `Alice.Sdk` MSBuild SDK, `Alice.Cli` tool and `Alice.Runtime` on NuGet | `Integration/PackageTests`, `Integration/SdkPackageTests`, ADR 0020 |
| Deployment | NativeAOT single-file binaries with no trimming warnings; `[runtime] gc` and `globalization` settings | `benchmarks/alice` (all build native), `Tooling/ManifestTests` |
| Tooling | `alice fmt`, `alice test` (RSpec and xUnit styles, JUnit output), LSP, REPL, `alice explain`, `alice prove` | `Tooling/*` |

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
| Incremental compilation | every `alice build` recompiles the package | `alice run` caches unchanged single files |
| ASP.NET Core in CI on machines without the shared framework | `examples/web/aspnet` builds and is checked only where ASP.NET Core is installed | `examples/verify.sh` on a full SDK |
| UI frameworks (WPF, MAUI, Avalonia) | not exercised by tests; XAML code-behind needs `partial` classes, which Alice emits, but no sample exists | write the UI layer in C#, logic in Alice |
| Assignment in `elsif` (`elsif x = f`) | rejected with an explanation | assign before the `if` |
| Interaction-net runtime | explored and parked: slower than fork/join on CPU (`benchmarks/inet/README.md`) | fork/join is the parallel model |
| Roadmap items in the README | language server completion, ownership for collections, GPU offload, `ObjectSpace` | see `README.md#roadmap` |
