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
| AL0224 | a `dataflow` stage or `stream def` captures a value that is neither sendable nor thread-safe (the ADR 0028 AL0230/AL0231 rule applied to stages) |
| AL0225 | `stage(par: true)` on a block that is not pure (`PureBlock`, `Lowerer.Parallel.cs`) |
| AL0226 | an edge in a `dataflow` block has more than one producer or consumer, or the graph has a cycle (bounded buffers could deadlock; use actors for feedback) |

## Pipeline

1. Analysis, new `Semantics/Binder/Binder.Dataflow.cs`: build the graph (nodes = `source`/`stage`/`sink` or `spawn`
   blocks; edges = channels or the implicit `out`); check SPSC and acyclicity (AL0226), sendability of captures
   (AL0224, reusing `Binder.Actors.cs`), purity for `par: true` (AL0225); classify each stage as static (one in, one
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

## Targets

Not the 16 Bend workloads (lexer, editdist and symreg are fork + stack arrays / owned rows / ADR 0037 arenas; a
dataflow lowering of lexer would be slower, one 40-token line is too little work per handoff). The acceptance
benchmark is `benchmarks/dataflow/`: a 3-stage file -> tokens -> counts pipeline over a 1 GB input against the
`Channel`/`Task.Run` form and a hand-written `System.Threading.Channels` C# pipeline.

## Consequences

- Per-item handoff dominates for cheap stages. Guard: batch publish and fusion, with a measured dispatch threshold
  like `Par.Offload` (`Parallel/Par.cs`).
- An ordered sink after a replicated stage is a serial point; the window is sized to k x batch.
- Users who expect feedback cycles get AL0226, which points at actors (ADR 0028).
- Existing `Channel`/`spawn`/actor programs keep their semantics; only proven networks change lowering.
- Rejected: doing nothing for 1.0 (Workstream C needs streaming and the determinism invariant); full static dataflow
  with software pipelining and wavefronts (no workload needs it).

Tests: `tests/Alice.Tests/Runtime/DataflowTests.cs` (ring wraparound, backpressure, completion and exception
propagation, 0 allocations per item, >= 50 M items/s on a 3-stage integer pipeline on the forge);
`tests/Alice.Tests/Compiler/DataflowTests.cs` (AL0224-AL0226, fusion of pure chains, inferred `Channel`/`spawn`
networks, output bit-identical across `DOTNET_PROCESSOR_COUNT=1` and 32, `stream def`); `benchmarks/dataflow` beats
the `Task.Run` form 3x and matches the hand-written C# pipeline.
