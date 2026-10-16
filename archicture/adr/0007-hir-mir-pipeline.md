# 0007 - HIR/MIR pipeline for allocation-free IL
Status: accepted
Context: The single-pass string translator could not type expressions, infer returns, or remove the closures/arrays that Ruby-style blocks imply. Goal: the best IL .NET can run.
Decision:
| Stage | Folder | Output |
|---|---|---|
| Lexer + Parser | `Syntax/` | AST |
| Binder | `Semantics/` | HIR = AST annotated with types/symbols; .NET members via reflection; return inference to fixpoint; Ruby local hoisting; `val` + spawn data-race checks |
| Lowerer | `Lowering/` | MIR (`Mir/`): literal blocks on iterators -> loops; map/select/reject chains fused into one loop; if/case values -> typed temps; capture-free lambdas `static` |
| Emitter + Printer | `Backend/` | C# with `#line`; Roslyn emits Release IL |
Consequences: Iterator kernels allocate 0 bytes (AllocationTests). Virtual dispatch only where Alice subclasses need it. Unknown types fall back to `dynamic`/C# inference. Self-hosting deferred (ADR 0003 superseded).
