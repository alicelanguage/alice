# Interaction nets on .NET

Question: can an HVM2-style interaction-net reducer run on .NET, and does it parallelize recursion better than
Alice's fork/join?

`Net.cs` is a real reducer: one-`ulong` ports (3 tag bits), binary nodes CON/DUP/OPR/SWI, nullary ERA/NUM/REF,
wires as var cells linked with `Interlocked.Exchange` (HVM2's lock-free `link`), the ANNI/COMM/ERAS/CALL/OPER/SWITCH
rules, per-thread allocation regions with free lists. `Scheduler.cs` runs one depth-first redex stack per thread
and shares the bottom half of a stack when the shared queue runs low. Definitions are built by hand
(`DefBuilder`); there is no Alice-to-net compiler.

Workload: `pow2(n) = pow2(n-1) + pow2(n-1)` (2^n leaves), the fork/join example of
[docs/fork-join.md](../../docs/fork-join.md). 12 cores, best of 5, heap allocated and pre-faulted outside the
timed region. `dotnet run -c Release -- 22 5` (add `noinet` to skip the net).

| pow2(22) | ms | vs plain |
|---|---:|---:|
| plain recursion | 12-16 | 1.00x |
| fork/join, `Par.Forkable` read on every call (Alice ≤ 0.12.2) | 24-32 | 0.5x |
| fork/join + serial clone below the fork depth (Alice 0.12.3) | 2.0-2.1 | 6.3x |
| interaction net, 1 thread | 670-1050 | 0.015x |
| interaction net, 12 threads | 225-470 | 0.03-0.05x |

pow2(26): plain 132-169 ms, check per call 267-321 ms, serial clone 18.7-26.5 ms.

Real compiled Alice (`pow2.al`, `alice build --release`), old compiler against new: pow2(22) 6.1 -> 1.8 ms,
pow2(26) 51.3 -> 15.7 ms.

## Findings

1. **It runs.** The reducer is correct on every size tried, single and multi-threaded, at 60-100 million
   interactions per second on one thread and 130-280 on twelve (HVM2 in C/CUDA reports more per thread; .NET's
   bounds checks and the lack of a tuned allocator are part of the gap). Scaling is poor and noisy: 3-4x on 12
   threads. Each interaction touches cold memory, and the shared queue and var exchanges contend.
2. **No gain over fork/join on CPU.** pow2 needs about 15 interactions per call, each costing several ns,
   against one native add for the recursive call. The net exposes the same tree parallelism fork/join already
   uses, so it pays about 50x the work to find parallelism the compiler can see statically. With 12 cores the
   net stays 17-30x slower than *serial* code. It would win only where the parallelism is not visible in the
   program's shape (higher-order, lazy, or optimal-sharing workloads), which is not what Alice targets on
   .NET.
3. **The experiment found a real fork/join cost.** Alice's fork/join was 2x *slower* than plain recursion on
   this workload. A thread-static `Par.Forkable` read on every one of the 2^n calls cost more than the forks
   gained. 0.12.3 emits `m__serial`, a clone of the forking method with no forks, which the sequential branch
   calls. Below the fork depth, recursion runs at plain speed, and only the top levels pay for forking. That
   is BendRT's lazy task creation without the per-call check.

Conclusion: keep fork/join (now with the serial clone) as Alice's parallel model. An interaction-net runtime
on .NET is feasible, but it is a slower way to get the same parallelism for the programs Alice compiles.

## HVM2 port (`Hvm2.cs`, `dotnet run -c Release -- hvm2 [n] [reps] [threads]`), parked

Follows the HVM2 paper: 32-bit ports, one-word nodes, the atomic linker, safe-REF copying, a private stack plus a
stealable shared bag per thread, and a chunked global heap (from BendRT). Correct on pow2 and on a tree built and
consumed in the same net. Single-threaded it reaches 70-84 MIPS (the paper reports 400 in C on an M3 Max). Code
specialized per definition did not help: the profile then points at memory stalls, not the expansion copier.
Multi-threaded runs are erratic (60-266 MIPS). Parked: the BendRT paper's own authors dropped interaction nets for
affine ownership plus a two-world (sequential/parallel) evaluator, which Alice already has as fork/join with
`m__serial` clones. What came out of this work went into the compiler: forks over sum types, and `gc = "auto"`.
