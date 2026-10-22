# 0008 - Compile-time data-race safety
Status: accepted
Context: Goal is Rust-level safety on .NET without a borrow checker. The GC already prevents use-after-free; data races remain.
Decision:
| Concurrent context | spawn bodies; blocks given to Par.*, Parallel.*, Task.Run/Factory, ThreadPool, PLINQ, par_map/par_sum/par_each; Thread/Task/Actor constructors |
|---|---|
| Rejected (AL0202) | write to captured local or field; mutating call (List.Add, Alice method that writes its fields, ...) on a captured non-thread-safe value; element write at a non-disjoint index; `ref`/`out` of shared vars except Interlocked |
| Allowed | reads; writes to block-locals; `out[i] = ...` where `i` is the concurrent block's own parameter; Mutex(T) (`m.lock { |v| ... }`), Atomic, Channel(T), Actor(T), System.Collections.Concurrent/Immutable |
Auto-parallelization (ADR 0007 lowering) only fires for blocks the purity checker proves race-free.
Counted loops also need iteration independence: an array the body writes at the loop index is read only at that index
(`a[i] = a[i - 1] + 1` is a scan and stays serial), and a local array read at a shifted index (a stencil) is a different
object from every written array of a compatible element type, checked with `ReferenceEquals` before the parallel path.
An iteration's own cells are `a[i]` and the row-major `a[i * w + j]` with `j` counting `0...w` in a nested loop and `w` invariant.
Consequences: Shared-state bugs become compile errors with a help hint. Conservative: some safe patterns (e.g. disjoint List writes) need Mutex/Atomic or arrays. Escape via C# interop is not checked.
