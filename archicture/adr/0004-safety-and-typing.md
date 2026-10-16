# 0004 - Safety and typing
Status: accepted
Context: Goal: Ruby ergonomics, Crystal-style optional types, Rust-like safety, C-like speed.
Decision:
- Static typing with local type inference; annotations optional (`x : T = v`).
- Untyped method params become generic type parameters.
- `Any` maps to `dynamic`; opt-in only, never inferred.
- Nullable reference types enabled; `T?` is nullable. Null-safety diagnostics are errors.
- Value-returning methods declare a return type (`: Type`).
Consequences: Most errors surface at compile time; generated code stays statically typed and fast. `Any` is the explicit escape hatch with dynamic cost.
