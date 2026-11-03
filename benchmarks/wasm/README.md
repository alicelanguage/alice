# Native wasm backend (`runtime = "alice"`, ADR 0034): size and startup

The Bend 2 suite programs compiled by `AirToWasm` (src/Alice.Compiler/Backend/Wasm) to a wasm32-wasi module and
run under Node (node:wasi) and wasmtime. Output is byte-identical to `alice run` (the C# backend) at the default
sizes, and at the test sizes in tests/Alice.Tests/Backends/WasmTests.cs (the differential vs the C# oracle).

```
alice build --target wasm32 benchmarks/bend2/alice/nbody.al     # nbody.wasm + nbody.mjs (Node runner)
alice run   --target wasm32 benchmarks/bend2/alice/nbody.al     # Node, or wasmtime (--runner wasmtime)
node nbody.mjs 17                                               # arguments pass through
wasmtime run -W exceptions=y nbody.wasm 17                      # wasm EH is required (raise/rescue)
```

## Measured (2026-11-02, the forge box: 32 cores, Node 26.13.0, wasmtime 50.0.1)

Default sizes (nbody 17, queens 17, editdist 15). Median of 5 wall-clock runs. Module size includes the `name`
section (function and local names); there is no wasm-opt pass.

| program  | .wasm bytes | instantiate to first call (Node) | Node wall | wasmtime wall | `alice run` wall (32 cores) | `alice run`, 1 core | Node, 1 core |
|----------|------------:|------:|------:|------:|------:|------:|------:|
| hello    |  6 772 | 5.8 ms |  36 ms |    9 ms | 1 923 ms (compile + run) | | |
| nbody    | 11 879 | 2.7 ms | 9 588 ms |  9 799 ms | 830 ms | 8 900 ms | 9 223 ms |
| queens   |  9 564 | 5.3 ms | 8 865 ms | 18 732 ms | 691 ms | 8 989 ms | 9 179 ms |
| editdist |  9 620 | 5.7 ms | 9 766 ms |  9 747 ms | 874 ms | 12 823 ms | 10 232 ms |

- `node -e 0` alone is 26 ms, so a hello module adds about 10 ms to Node's own startup; wasmtime starts in 9 ms.
- The wasm backend runs `par`/fork forms serially (core profile; no wasm threads yet), so the fair comparison is the
  1-core column (`taskset -c 2`): wasm under Node is within 4% of .NET on nbody and queens, and 20% faster on
  editdist. On 32 cores .NET is 10-13x faster because it runs the fork trees in parallel.
- queens under wasmtime is 2x slower than under Node; the cause is
  not investigated.
- "instantiate to first call" is `WebAssembly.instantiate` (compile + link) to `_start`, printed by the generated
  runner with `ALICE_TTFC=1`.

## Memory model (core profile)

Linear memory (src/Alice.Core.Wasm/rt.wat): a size-class allocator over 64 KiB pages (bump within a class page,
free lists per class, large objects as page runs), a shadow stack for struct and reference locals
(`WasmOptions.StackSize`, 1 MiB), and a non-moving mark-sweep collector that scans the shadow stack and the statics
conservatively (any aligned word that is the address of a live object keeps it). Strings and number arrays are
`noscan`. A collection runs once 4 MiB were allocated since the last one. `GC.GetTotalAllocatedBytes` counts this allocator's bytes; `GC.GetTotalPauseDuration` is 0 (pauses are not timed yet).

## Reproduce

```
cd /tmp/w && cp benchmarks/bend2/alice/{nbody,queens,editdist}.al .
for p in nbody queens editdist; do alice build --target wasm32 $p.al; done
for p in nbody queens editdist; do diff <(node $p.mjs) <(alice run $p.al 2>/dev/null); done
ALICE_TTFC=1 node nbody.mjs 2>&1 >/dev/null
```

## Arena enums (ADR 0037)

Arena sum types are u64 terms in linear-memory slabs (src/Alice.Compiler/Backend/Wasm/WasmBody.Arena.cs, the arena
section of rt.wat): one slab per slot width shared by every arena enum of that width, one lane, a free list
threaded through freed slots, bump allocation with doubling growth (slot indices survive it), recursive Drop that
loops along the last child, and regions that save and restore every cursor. `ALICE_NO_ARENA=1` (or
`WasmOptions.Arena = false`) keeps them heap records.

Default sizes, Node 26, median of 3 wall-clock runs (2026-11-03). Output identical to `alice run` in both modes.
Alloc and Take/Borrow are inline (free-list pop or bump, word copies, free-list push); growth, Drop and regions
call rt.wat. The stale check (slot index below the cursor) is emitted unless the slab is proven live, as in the IL
backend; Drop always checks.

    program      arena .wasm   records .wasm   arena     records   .NET 1 core
    tree-radix   12 439 B      12 415 B        16.8 s    20.6 s    17.4 s
    merkle       17 216 B      17 205 B        13.3 s    12.9 s    10.2 s

tree-radix is 18% faster with the arena. merkle is 3% slower (13.3 s against 12.9 s; it was 9% slower when Alloc and
Take were calls into rt.wat). The remaining gap is not measured further.
