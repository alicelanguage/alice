# ADR 0019: .NET names for a library's public API

Status: Accepted (0.12.3)

Alice code uses Ruby spelling (`total_area`, `empty?`) and compiles members under that name. A C# or F# project
consuming an Alice library therefore saw `Geometry.total_area` and `point.x`, which no .NET API looks like and
which analyzers flag. F# has the same mismatch between its own style and .NET's, and solves it by compiling to
.NET names.

Decision: `[package] naming = "dotnet"` in `alice.toml` (default `"alice"`, unchanged output). Public members
compile to their .NET spelling; Alice source keeps calling them by the Alice name.

| Alice | .NET |
|---|---|
| `total_area`, `item_count` | `TotalArea`, `ItemCount` |
| record parameter `x` | `X` (the positional property, as in C# records) |
| `empty?` | `IsEmpty`; `has_key?` / `can_x?` / `contains_x?` keep their verb (`HasKey`) |
| `save!` | `Save` |
| `Dispose`, `GetEnumerator` (already .NET) | unchanged |
| private / protected members, locals | unchanged |
| Ruby hooks (`initialize`, `to_s`, `method_missing`, `each`, `<=>`, `call`...) | unchanged |

One function, `Alice.Runtime.Names.Dotnet`, defines the mapping. The compiler uses it for declarations, and
`send`/`respond_to?` use it as a fallback, so dynamic calls by Alice name still work against renamed members.
It is per package, not per file: a library has one public surface. Applications rarely need it; libraries
published with `alice publish` should set it.
