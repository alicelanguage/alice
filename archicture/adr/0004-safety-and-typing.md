# 0004 - Safety and typing
Status: accepted
Context: Goal: Ruby ergonomics, Crystal-style optional types, Rust-like safety, C-like speed.
Decision:
- Static typing with local type inference; annotations optional (`x : T = v`).
- Untyped method params become generic type parameters.
- `Any` maps to `dynamic`; opt-in only, never inferred.
- Nullable reference types enabled; `T?` is nullable. Null-safety diagnostics are errors.
- Value-returning methods declare a return type (`: Type`).
- Amended (2026-11, strict typing AL0220): an override of a .NET virtual without `: Type` keeps the base method's return type when the body is untyped or the base returns `void` (a newly typed tail never changes the override's signature).
Consequences: Most errors surface at compile time; generated code stays statically typed and fast. `Any` is the explicit escape hatch with dynamic cost.
