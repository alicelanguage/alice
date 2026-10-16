# 0001 - .NET platform language, single Roslyn/IL backend
Status: accepted
Context: Root `AGENTS.md` assumes a Rust Cargo workspace. The user explicitly overrode this for Alice: it is a .NET language with Crystal/Ruby syntax and full C#/.NET interop (namespaces, classes, structs, interfaces, generics, attributes, NuGet, any assembly; C# can reference Alice assemblies).
Decision:
- .NET 10 solution `Alice.sln`; projects under `src/`, tests under `tests/`. No Rust crates layout.
- One backend: compiler lowers Alice to C# syntax trees; Roslyn emits IL (`.dll`/`.exe`) on the CLR. No second backend.
- `alice build --native` uses .NET NativeAOT.
- AGENTS.md process rules (TDD, ADR, CHANGELOG, versioning `0.1x.x`, `archicture/user/` read-only) still apply. Rust-specific rules (crates, cargo commands) map to .NET projects and `dotnet` commands.
Consequences: Reuses Roslyn optimizer, debugging, tooling and the whole .NET ecosystem. Language semantics are bounded by what C# can express. Compile speed depends on Roslyn.
