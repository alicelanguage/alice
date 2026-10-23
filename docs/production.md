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
| UI frameworks (WPF, MAUI, Avalonia) | XAML code-behind in Alice is untested; view models are tested (`INotifyPropertyChanged`, `ObservableCollection`) | XAML and code-behind in C#, view models and logic in Alice |
| Interaction-net runtime | explored and parked: slower than fork/join on CPU (`benchmarks/inet/README.md`) | fork/join is the parallel model |
| References and rename are name-based | top-level names are renamed project-wide by name, locals within their `def`; two unrelated methods with the same name in different types rename together | review the edit preview |
| Ownership beyond numbers | collections of references, records and fields stay with the GC (arrays, lists and hashes of numbers are pooled) | preallocate and reuse explicitly |
| Roadmap items in the README | GPU offload, `ObjectSpace` | see `README.md#roadmap` |
