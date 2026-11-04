# ADR 0038: Static dataflow networks over bounded SPSC rings

Status: Accepted (direction; not yet implemented). Amends ADR 0005 (`Channel`/`spawn` lowering) for proven networks;
builds on ADR 0028 (isolation rules) and ADR 0029 (fusion). Ships in 1.0 (Wave 3). Design detail:
`/work/reports/paradigms-plan.md` section 2.

Pipelines are the shape fork/join and fusion do not reach: their stages are dependent and usually stateful (a
tokenizer's mode, a running checksum), so ADR 0029 fusion stops at the first effectful stage and ADR 0015 forks never
apply. Today a pipeline is `Channel(T)` over a lock plus `ValueTask` (`Concurrency/Channel.cs`) and `spawn` blocks on
`Task.Run`, or actors (`ProcessCore`, one CAS per wake, 6-11 M msgs/s). A Kahn process network (sequential processes,
FIFO channels, blocking reads) has an output determined by the program alone, independent of scheduling; Dennis's
static dataflow (one token in, one out per firing) makes buffer sizes computable at compile time.

None of the 16 Bend workloads is a pipeline (each is a binary fork over independent leaves), so this ADR serves
Workstream C (HTTP server, JSON and CSV streaming, log processing) and user code, not the Bend table.

Decision: lower proven acyclic single-producer single-consumer networks to bounded SPSC rings with stage fusion and
replication of pure stages. Deterministic by construction: the output is bit-identical for any thread count.

## Surface

Explicit:

```crystal
dataflow do
  lines  = source { |out| File.each_line(path) { |l| out << l } }        # one producer
  tokens = lines.stage(state: Lexer.new) { |lx, l, out| lx.lex(l, out) }  # stateful, 1:n
  counts = tokens.stage(par: true) { |t| classify(t) }                    # pure: replicated
  counts.sink { |c| total &+= c }                                         # ordered sink
end

stream def tokenize(lines : Iterator(String)) : Iterator(Token)
  lines.each { |l| lex(l).each { |t| yield t } }   # its consumer may run concurrently
end
```

- `dataflow do ... end` is a network; `source`, `stage` and `sink` are its nodes, and each `out` or returned value is
  an edge. `stage(par: true)` asks for replication; the sink is ordered by sequence numbers, so the result equals the
  serial program (`ordered: false` opts out).
- `stream def` declares an iterator whose body runs as a stage concurrently with its consumer; its body follows the
  stage capture rule.
- Inferred (silent): a method body that creates `Channel(T)` values, starts `spawn` blocks that each receive from at
  most one channel and send to at most one, drains the last channel, and whose blocks capture only sendable or
  thread-safe values is lowered to the same static form. Anything else keeps the ADR 0005 lowering.

| Error | Meaning |
|---|---|
| AL0244 | a `dataflow` stage or `stream def` captures a value that is neither sendable nor thread-safe (the ADR 0028 AL0230/AL0231 rule applied to stages) |
| AL0245 | `stage(par: true)` on a block that is not pure (`PureBlock`, `Lowerer.Parallel.cs`) |
| AL0246 | an edge in a `dataflow` block has more than one producer or consumer, or the graph has a cycle (bounded buffers could deadlock; use actors for feedback) |

## Pipeline

1. Analysis, new `Semantics/Binder/Binder.Dataflow.cs`: build the graph (nodes = `source`/`stage`/`sink` or `spawn`
   blocks; edges = channels or the implicit `out`); check SPSC and acyclicity (AL0246), sendability of captures
   (AL0244, reusing `Binder.Actors.cs`), purity for `par: true` (AL0245); classify each stage as static (one in, one
   out per firing) or dynamic (n:m, stateful). Chains of static pure stages are fused and handed to the ADR 0029
   fuser.
2. Lowering, new `Lowering/Lowerer.Dataflow.cs`: each fused stage becomes a static method
   `(state, ReadOnlySpan<TIn>, RingWriter<TOut>)` over a batch. The C# printer emits one `Dataflow.Run(graph)`; no
   lambda per item. `stream def` lowers to a source stage feeding a ring read by the consumer's iterator.
3. AIR nodes added to the ADR 0032 set: `AirDataflow(graph)` with children `AirStage(method, stateInit, parallel,
   ordered)` and `AirRing(T, capacity)`.

Out of scope: software pipelining of loop bodies and wavefront scheduling of 2-D recurrences (a later ADR 0025
amendment using `Optimizer/Analysis/Dependence.cs` if a single large DP appears); feedback cycles (Parks' algorithm);
GPU.

## Runtime

New folder `src/Alice.Runtime/Dataflow/`:

- `Ring.cs`: bounded SPSC ring over `T[]`, power-of-two capacity, cached head and tail, batch publish of up to 64
  items, zero allocations after construction.
- `Stage.cs`: an `IThreadPoolWorkItem` queued when its input ring is non-empty and the stage is idle (one CAS, the
  `ProcessCore` pattern of ADR 0028).
- `Graph.cs`: topological start, completion propagation, exception fan-in (the `ForkJob` `ExceptionDispatchInfo`
  pattern: drain, surface once).
- `Replicate.cs`: a pure stage on k workers reading one ring with sequence numbers; the ordered sink reorders through
  a window of k x batch.

Backpressure is the full ring; acyclic plus SPSC is deadlock-free. Stages are pool items, so the .NET pool balances
them; no custom scheduler (ADR 0005 kept).

### Memory model

Publishing a batch and releasing input slots are plain release stores (`Volatile.Write` of the tail or head), with
no fence and no interlocked operation in the steady state. The waker then reads the other node's `scheduled` or
`blocked` flag without a fence, so a wake can be missed while the store is still buffered. Repair on idle covers
this: a node going idle does `Interlocked.Exchange(ref scheduled, 0)` (a full fence) and then `Nudge`. That wakes
the consumer of a non-empty output ring and a producer that is `blocked` on our input ring
(`src/Alice.Runtime/Dataflow/Stage.cs`). One side always reaches that fence: an idle consumer, a blocked or idle
producer, or `Complete` (interlocked). So a missed wake costs latency, not a deadlock. Each node re-checks its rings
after its own fence before it gives its thread back.

This is verified on x64 only (`DataflowTests`). On weak memory models (arm64) the argument rests only on the
release/acquire semantics of `Volatile` and on the full fence of `Interlocked`. CI must therefore run the dataflow
stress tests (`[Trait("Category", "Stress")]` in `tests/Alice.Tests/Runtime/DataflowTests.cs`) on the macos arm64
runners, for example `dotnet test --filter "Category=Stress"`.

## Targets

Not the 16 Bend workloads (lexer, editdist and symreg are fork + stack arrays / owned rows / ADR 0037 arenas; a
dataflow lowering of lexer would be slower, one 40-token line is too little work per handoff). The acceptance
benchmark is `benchmarks/dataflow/`: a 3-stage file -> tokens -> counts pipeline over a 1 GB input against the
`Channel`/`Task.Run` form and a hand-written `System.Threading.Channels` C# pipeline.

## Consequences

- Per-item handoff dominates for cheap stages. Guard: batch publish and fusion, with a measured dispatch threshold
  like `Par.Offload` (`Parallel/Par.cs`).
- An ordered sink after a replicated stage is a serial point; the window is sized to k x batch.
- Users who expect feedback cycles get AL0246, which points at actors (ADR 0028).
- Existing `Channel`/`spawn`/actor programs keep their semantics; only proven networks change lowering.
- Rejected: doing nothing for 1.0 (Workstream C needs streaming and the determinism invariant); full static dataflow
  with software pipelining and wavefronts (no workload needs it).

Tests: `tests/Alice.Tests/Runtime/DataflowTests.cs` (ring wraparound, backpressure, completion and exception
propagation, 0 allocations per item, >= 50 M items/s on a 3-stage integer pipeline on the forge);
`tests/Alice.Tests/Compiler/DataflowTests.cs` (AL0244-AL0246, fusion of pure chains, inferred `Channel`/`spawn`
networks, output bit-identical across `DOTNET_PROCESSOR_COUNT=1` and 32, `stream def`); `benchmarks/dataflow` beats
the `Task.Run` form 3x and matches the hand-written C# pipeline.

IL lowering (ADR 0040, section 4): stage bodies are static methods `(ref TState, ReadOnlySpan<TIn>, ref
RingWriter<TOut>)` registered through `ldftn` on the runtime function-pointer overloads; items are read by `ref
readonly`, no per-item delegate; the writer fast path and the publish (`volatile.` release store) are emitted inline.

## Amendment 2026-11-04: idle nodes never spin while pool work is pending

Problem (CI): `Long_chain_of_tiny_rings_never_loses_a_wake` hit its 30 s bound on GitHub runners (ubuntu 1m22s,
windows 42s, macOS 30s for three rounds). It was not a lost wake: an idle node spun 40 x `Thread.SpinWait(20)` before
giving its thread back, holding the pool thread or the core that the node it waited for needed. On the forge the same
test took 0.4 s on 16 cores, 24 s on 2, 44 s on 1, and 0.6 s on 2 cores with the spin removed.

Rule: the idle re-check loop in `DataflowNode.Execute` (`src/Alice.Runtime/Dataflow/Stage.cs`) stops as soon as
`ThreadPool.PendingWorkItemCount > 0` (another work item waits for a thread) and calls `Thread.Yield()` between
re-checks instead of `SpinWait`, so a runnable thread on the same core gets it. The wake protocol itself is unchanged
(written down as a state machine above `Execute`).

Measured: 1 s on 2 cores and 0.7-2 s on 1 core (was 24 s / 44 s); 1-3 s on 4 cores with the Runtime tests running in
parallel (was 23 s); `benchmarks/dataflow` on 28 cores unchanged within noise (dataflow 2.58-2.80 s before, 2.67-2.91 s
after; dataflow-par 0.99-1.10 s before, 1.04-1.07 s after; allocation the same).

Pinned by `tests/Alice.Tests/Runtime/DataflowStressTests.cs` (seeded injection points, a stall detector that dumps
the node states, and a variant pinned to two cores that fails before this rule).
