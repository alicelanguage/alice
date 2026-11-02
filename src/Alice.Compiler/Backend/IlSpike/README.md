# IlSpike: THROWAWAY spike (backend plan D1.1)

**Throwaway code.** This folder answers the D1.1 questions of `/work/reports/backend-plan.md` (S1 gate: "nbody,
queens, editdist bit-identical, within 3%") with `System.Reflection.Emit.PersistedAssemblyBuilder`. It is not the
IL backend: D1.2 builds `Backend/Il/IlBuilder.cs` on System.Reflection.Metadata. Delete this folder when D1.4 lands.
Nothing outside this folder depends on it except the hidden CLI switch (CompileCommands.cs, 2 lines) and
`tests/Alice.Tests/Backends/IlSpikeTests.cs` (`[Trait("Category","Spike")]`).

## Run it

```
alice emit --il-spike <file.al> [-o dir] [--bounds m1,m2] [--tail] [--tail-all] [--stack] [--stack-dirty]
                                [--no-hot] [--no-stack-schedule] [--force-inline m]
    writes dir/<name>.il.dll (this spike) and dir/<name>.cs.dll (C# backend, same sequential lowering) + runtimeconfigs
alice emit --il-spike --census [--sequential] <dir-or-file>...     node-kind and blocker tables (markdown)
alice emit --il-spike --bench <dll> <reps> [args]                   in-process steady state (wall + process CPU)
alice emit --il-spike --il-dump <dll> <method>                      minimal IL listing (compare both backends)
```

## How it works

- `IlSpike.cs` `IlSpikeCompiler.Compile`: parse, bind, lower every top-level `def` and the main statements with the
  normal `Lowerer` and `Optimizer.Pipeline` (post-optimizer MIR), then `PersistedAssemblyBuilder` -> `Program` type
  with static methods, Alice `struct` / `record struct` as value types (public fields + positional ctor), entry
  point, `[module: SkipLocalsInit]` and `InitLocals = false` as the C# backend.
- **Lowering is `Sequential = true`** (the wasm mode, ADR 0024) on both sides. Fork / parallel-for helpers exist only
  as C# text in `Lowerer.Extra` (Lowerer.Fork.cs:103, Lowerer.LoopPar.cs:124) and need closures (D1.5), so the
  timings compare serial IL with serial C#.
- `IlBody.cs`: one MIR body -> ILGenerator. Statements MDecl, MAssign (=, op=, on locals/args/array elements/struct
  fields), MExprStmt, MIf, MWhile, MFor (`++`, `--`, `+= n`), MReturn, MBreak, MContinue, MBlock (unchecked/unsafe),
  MDeconstruct (tuple literal or ValueTuple), MThrow. Expressions MCode (literal, local, parameter, static path),
  MBinary (checked per node + `unchecked(...)` call + MBlock.Unchecked), MUnary, MCond, MCast (checked explicit
  conversions), MCall (Alice methods of the module, Prelude statics, CLR statics, CLR instance methods, Alice.Runtime
  extension methods with generic inference), MMember (fields, properties, static members, `Length`), MIndex (arrays,
  string, indexers), MNewArray, MNew (Alice structs, CLR types), MTuple.
- **Types**: `MExpr.Type` was never set at 3b6c7df (census: 0 typed nodes), so the spike infers everything itself
  (`IlBody.Infer`): parameters/returns from `MethodSym` (`AType.Cs`), locals from their initializer, literals from their
  C# shape (`ClrResolve.TryLiteral`), declared types from C# text (`MDecl.TypeCs`, `MCast.TypeCs`, `MNew.TypeCs`,
  `MNewArray.ElemCs`, `MFor.VarType`) through a C# type-name parser (`ClrResolve.Parse`), members by reflection, C#
  binary numeric promotion with literal adoption (`ClrResolve.Promote`), overloads by a small scorer (`ClrResolve.Pick`).
- **Typed AIR nodes (after main c0ea2ae)**: `IlBody.Lower` maps AirRent/AirRelease/AirLength/AirArrayLength/
  AirTripCount/AirOffset to the core forms from their fields; `IlBody.Leaf` reads AirLocalRef/AirParamRef/AirConst/
  AirDefault/AirSizeOf/AirNull by field and rejects AirRawCs, AirThis/AirBase, AirFieldRef, narrowed AirLocalRef.
  AirName is accepted only when it names a parameter/local or a CLR static (Main's `ARGV` arrives as AirName).
  The census numbers below are from 3b6c7df (before those nodes).
- **C# semantics replicated by hand** (each one was a bug before it was right): shift counts masked to 31/63 (IL leaves
  larger counts undefined), float `>=`/`<=` as `!(clt.un)`, negated float branches use `.un` (NaN), explicit numeric
  casts are `conv.ovf.*` in a checked context, compound assignment converts back with overflow check, `c ? 1 : 0` is
  the comparison value, `x == 0` is `brfalse`.
- **The C# backend's `[MethodImpl]` decisions** (Kernel/LoopLeaves in Emitter.cs, `inline def`) are made inside
  Emitter text; the spike copies them by regex from the C# output (SpikeCli.cs, "MethodImplOptions"). Before it
  copied `AggressiveInlining` (`inline def`), gameoflife ran 2.5x slower steady (3.7x cold) and hashmap 1.3x.

## Checksum parity (bit-identical stdout, C# backend vs spike, both sequential)

| workload | args | checksum (both) |
|---|---|---|
| queens | default (17) | 2063750025 |
| nbody | default (17) | 3516450380 |
| editdist | default (15) | 2229810577 |
| mandelbrot | default (18 51) | 3101455856 |
| queens / editdist / nbody / mandelbrot | 14 / 11 / 14 / 16 | 2063750025 / 2203349698 / 1155112709 / 1987531637 |
| bfs / hashmap / raytrace / gameoflife | 16 / 10 / 10 2000 / 15 16 | 2901322616 / 836726638 / 156397177 / 648501952 |

8 of the 16 benchmarks/bend2 programs compile completely and match; all optimization variants below match too.

## Timing: IL vs C# (forge, 32 cores, pinned to core 31; machine shared, load average 8-25 during runs)

Cold = `hyperfine -N -w 1 -r 5 "dotnet x.dll args"` (includes runtime start and JIT). Steady = per-run delta of
`perf stat` over 7 vs 1 in-process runs of the entry point (`--bench`). Instruction counts are deterministic to
~0.1%; wall and cycles move by up to 10-30% between identical runs on this shared machine (nbody IL cold measured
2220 ms in the table run and 1685 ms vs C# 1689 ms in a rerun).

| workload / variant | checksum | cold wall ms | cold Gcycles | cold Ginstr | steady Gcycles/run | steady Ginstr/run |
|---|---|---|---|---|---|---|
| queens C# | 2063750025 | 7812 ± 142 | 26.405 | 58.546 | 21.982 | 37.319 |
| queens IL | 2063750025 | 7839 ± 96 | 26.154 | 57.256 | 21.718 | 37.686 |
| editdist C# | 2203349698 | 757 ± 12 | 2.540 | 5.219 | 2.449 | 5.107 |
| editdist IL | 2203349698 | 794 ± 50 | 2.564 | 5.218 | 2.453 | 5.107 |
| nbody C# | 1155112709 | 1557 ± 83 | 5.191 | 12.034 | 3.807 | 5.404 |
| nbody IL | 1155112709 | 2220 ± 36 (rerun 1685 ± 54 vs C# 1689 ± 172) | 5.842 | 11.061 | 3.705 | 5.707 |
| mandelbrot C# | 1987531637 | 874 ± 42 | 2.893 | 13.779 | 2.808 | 13.377 |
| mandelbrot IL | 1987531637 | 888 ± 26 | 2.915 | 13.754 | 2.753 | 13.364 |
| bfs C# | 2901322616 | 2151 ± 34 | 7.068 | 17.837 | 4.237 | 5.626 |
| bfs IL | 2901322616 | 1691 ± 40 | 5.633 | 15.471 | 3.695 | 7.055 |
| hashmap C# | 836726638 | 786 ± 50 | 2.550 | 2.874 | 2.400 | 2.746 |
| hashmap IL | 836726638 | 740 ± 8 | 2.507 | 2.870 | 2.443 | 2.742 |
| raytrace C# | 156397177 | 1044 ± 44 | 3.478 | 14.279 | 3.372 | 13.998 |
| raytrace IL | 156397177 | 2295 ± 60 | 6.808 | 27.529 | 3.752 | 19.215 |
| raytrace IL + AggressiveInlining(sphere_isect) | 156397177 | 1023 ± 13 | 3.426 | 14.294 | 3.292 | 14.023 |
| gameoflife C# | 648501952 | 1184 ± 42 | 3.948 | 12.413 | 3.845 | 12.245 |
| gameoflife IL | 648501952 | 1308 ± 79 | 4.200 | 12.869 | 3.880 | 12.704 |

Steady state IL vs C#: queens -1.2% cycles, editdist +0.2%, nbody -2.7%, mandelbrot -2.0%, hashmap +1.8%,
gameoflife +0.9% (+3.7% instructions: `step_cell` fell back to long branches, see "Framework bug"), bfs -12.8% cycles
with +25% instructions (not investigated), raytrace +11% cycles / +37% instructions.

**raytrace**: `sphere_isect` is 130 IL bytes from the spike and 125 from Roslyn (Roslyn keeps single-use values on
the evaluation stack instead of in locals); the C# build inlines it into `ray_trace`, the IL build does not. Forcing
`AggressiveInlining` on it closes the gap (row above). My reading is that the size crosses RyuJIT's 128-byte IL limit
for inlining with profile data (not verified against JIT source). The spike has a one-statement stack scheduler
(`IlBody.Stmts`/`FirstLeaf`); it is not enough for this method, where Roslyn keeps values on the stack across
several statements.

Compile time (indicative only, both in one process, C# first so Roslyn pays the cold JIT): spike 63-160 ms per
program, C# backend (TranspileFull + Roslyn Emit) 1.4-2.2 s.

## The three IL-only optimizations

(a) **Bounds-check-free indexing** (`--bounds pair`, `IlBody.ElemAddr`: `MemoryMarshal.GetArrayDataReference` +
`idx * sizeof` + `ldind`). The MIR carries no in-bounds facts (OwnedBounds rewrites loop bounds to `.Length`, it does
not mark accesses), so the proof is **hard-coded** for editdist `pair`: every index is below 256 for `a`/`b` and
below `257 + 256 + 1 = 514` for `rows`, with `po`/`co` in {0, 257}. Result vs IL baseline: -2.7% instructions
(5.107 -> 4.971 G), +2.7% cycles (2.453 -> 2.519). No gain: the DP loop is latency-bound on the `rows` dependency chain, so removing
the compare+branch saves only instructions. (C# could do the same with `Unsafe.Add(ref
MemoryMarshal.GetArrayDataReference(a), i)`; it is not IL-only.)

(b) **`tail.` on self-recursive tail calls** (`--tail`; `--tail-all` adds every Alice call in return position). No
benchmarks/bend2 workload has a self-recursive tail call. On `bench/tailrec.al` (collatz in tail-recursive form):
with the C# backend's `AggressiveOptimization` (it marks self-recursive primitive kernels) the JIT already turns the
recursion into a loop and `tail.` changes nothing (3.759 vs 3.768 G instructions). Without it (`--no-hot`, the
method starts at tier 0), `sum_to(10^8)` overflows the stack, and with `tail.` it runs (test
`Tail_prefix_runs_deep_self_recursion_at_tier0` runs 10^7 deep), and cold time drops 2379 -> 848 ms. `--tail-all`
on the workloads, steady vs IL baseline: queens +2.7% instructions / -0.4% cycles, nbody -12.5% instructions /
-6.4% cycles, raytrace +61% cycles (`tail.` blocks inlining), editdist +7.4% cycles, mandelbrot +2.1% cycles. Value: guaranteed constant stack for Alice tail recursion at every
tier, which C# cannot express; not a speedup.

(c) **`localloc` stack buffers + SkipLocalsInit** (`--stack`: `Own<T>.Rent(constant)` -> `localloc`, zeroed with
`initblk`; `--stack-dirty`: not zeroed, valid for editdist because every element is written before it is read).
Elements are pointer loads (no bounds checks), `Release` disappears. editdist vs IL baseline: -13.3% instructions
(5.107 -> 4.430 G), +0.4% cycles zeroed (2.463), +2.2% dirty (2.506; that build hit the framework bug and fell back
to long branches).
C# can express this with `stackalloc` (Lowerer.StackArrays already does for some arrays).

None of the three moves these workloads by more than noise. They are worth keeping as AIR facts (in-bounds,
tail position, stack-allocatable) because they cut instructions, but the measured win is zero on these kernels.

## Coverage census (`--census benchmarks/bend2/alice benchmarks/alice`, 29 files, 266 bodies)

Default lowering (forks on) emits 66 bodies; sequential lowering emits 98 and 8 programs completely
(bfs, editdist, gameoflife, hashmap, mandelbrot, nbody, queens, raytrace from bend2; where the census shows n-1/n
bodies with no blocker, the uncounted body is a struct's empty `initialize`, emitted as a synthesized constructor). Node kinds over all lowered bodies (default lowering; MCode split by leaf shape):

| node kind | count | spike |
|---|---|---|
| MCode:name | 4899 | yes (local/param) |
| MCode:literal | 2761 | yes |
| MBinary | 2607 | yes except Coalesce |
| MMember | 1586 | yes (fields, properties, statics, Length) except `?.` |
| MCall | 1163 | yes for module statics, CLR statics/instance, Alice.Runtime extensions; no Alice instance methods, `?.`, MArg |
| MDecl | 1053 | yes |
| MCode:this/base | 823 | no (instance code) |
| MAssign | 814 | yes (`??=` no) |
| MCall:unchecked() | 530 | yes |
| MIf | 488 | yes |
| MExprStmt | 297 | yes |
| MReturn | 295 | yes |
| MIndex | 275 | yes (single index) |
| MIs | 254 | no |
| MCode:type/static path | 242 | yes when it names a CLR type/static member |
| MCode:C# call/expr text | 195 | no (`__fork_x(..)`, `(int)((uint)i % 1000u)`, `stackalloc long[2]`, `default(T)`, `Vector<T>` exprs) |
| MNew | 153 | yes for structs/CLR types; no initializers |
| MCond | 136 | yes |
| MCast | 103 | yes for numeric/reference/unbox |
| MFor | 102 | yes for `++`/`--`/`+= n`; no for step text ` = unchecked(i + 1)` |
| MCode:default | 92 | yes |
| MCode:other text | 78 | no |
| MWhile | 74 | yes |
| MUnary | 60 | yes except AddressOf/Await |
| MDeconstruct | 59 | yes (tuple literal or ValueTuple value) |
| MThrow | 48 | yes |
| MNewArray | 35 | yes except jagged and `new[]` |
| MInterp | 18 | no |
| MBreak | 15 | yes |
| MForeach | 5 | no |
| MTuple | 3 | yes |
| MLambda | 3 | no (fork helpers are C# text, so lambdas are undercounted) |
| MBlock | 3 | yes except lock |
| (MExpr.Type set) | 0 | - |

Not seen in the corpus MIR: MInvoke, MPair, MArg, MTry, MUsing, MContinue.

### Blockers, ranked (first unsupported thing per body, default lowering)

| rank | blocker | bodies | what it needs |
|---|---|---|---|
| 1 | instance methods of classes (`TypeMember:instance(class)` 62, `TypeDecl:class` 7, `Ctor(class)` 5; 823 `this` leaves) | 74 | class/record declarations, `this`, virtual dispatch (D1.3/D1.6); 61 are celeste_update |
| 2 | `Lowerer.Extra` C# text helpers: `__fork` 42, `__parfor` 9, `__shared` 1, others | 55 | helpers as MIR + closures (D1.5); then `MCall:unresolved-free` (30, calls to the blocked forked functions: batch 9, kernel 7, ...) disappears |
| 3 | Alice reference types as types (`TypeUnresolved`: Expr, Tree, St, Mat, Vec, Map, Body[], Particle.Store...) | 19 (45 sequential) | classes / sum types / SoA stores in the declaration model (D0.8, D1.6) |
| 4 | `MFor.Step` text ` = unchecked(i_1 + 1)` (InductionVariables) | 6 (12 sequential) | Step as an AIR node (S) |
| 5 | C# expression text in MCode (casts, `default(T)`, `stackalloc`, `x.Length`, `Vector<double>` exprs) | 6 (12 sequential) | the D0.4-D0.7 MCode removal |
| 6 | parameter kinds (default values, out/ref, splat, block) | 5 | DefaultParams pass, byref signatures (D1.7) |
| 7 | module members (`TypeMember:static(module)` 3, `TypeDecl:module` 2), record struct members 2 | 7 | static types other than Program, struct methods (D1.6) |
| 8 | `@[Native]` 2, iterator 1, MIs 1, MLambda 1 | 5 | D1.10, D1.8, D1.7, D1.5 |

## Findings for D1.2-D1.8

1. **IL shape is performance.** First naive IL was 3x slower on mandelbrot. Fixed by: compact encodings
   (`ldarg.0`, `ldc.i4.s`; ILGenerator emits exactly the opcode given), short branches (two-pass: a scratch method
   measures offsets, `IlBody.ShortPlan`), compare-and-branch fusion (`blt.un.s`), Roslyn's `c ? a : b` block order
   (RyuJIT if-converts it to setcc/cmov; the other order kept branches), `c ? 1 : 0` as the comparison value (editdist
   random `ai != b[j]`: a branch mispredicted), typed `ldelem.u4`/`stelem.i4`, no dead `br` after `return`, no
   unreachable epilogue (an 11-byte `throw` tail kept `asr8` above the always-inline size). Inlining is decided on IL
   bytes, so D1.4 needs Roslyn-equivalent stack scheduling (raytrace above) or it inherits silent 10-40% losses.
2. **Framework bug (PersistedAssemblyBuilder, .NET 10):** with mixed short/long branches some method bodies are
   serialized with a byte dropped after a short branch (editdist `pair` with `--stack-dirty`: the `ldloc.s` opcode at
   the `else` label of `m < dg ? m : dg` vanished, the body ended in a stray `nop`, the runtime threw
   InvalidProgramException). The offsets ILGenerator reported (`ILOffset`, logged with `ILSPIKE_DEBUG=<method>`)
   were consistent; the bytes were not. Not isolated to ILGeneratorImpl vs SRM ControlFlowBuilder. Workaround here:
   `IlSpikeCompiler.CorruptMethods` decodes every body after serialization and rebuilds the affected methods with long
   branches (hit `pair` in 2 variants and gameoflife `step_cell`). D1.2 should keep a decode check like this in its
   ILVerify tests, since its ControlFlowBuilder path may share the bug.
3. **Fork/parallel code is invisible MIR**: helpers are C# strings, so an IL backend cannot compile any forking
   program before D1.5 converts them; that is the largest blocker after classes.
4. **MIR leaves still need a C# parser**: MCode names, `unchecked(...)` as a call named "unchecked", `MFor.Step`
   text, `TypeCs` strings, `global::Alice.Runtime.Own<uint>` as a call target. Every one needed code here
   (`ClrResolve`), which the typed AIR nodes of D0.4-D0.7 replace.
5. **Emitter decides method attributes in text** (`AggressiveOptimization` for kernels/loop leaves,
   `AggressiveInlining` for `inline def`); D0.8 must move these into the declaration model, or the IL backend loses
   2-4x on loop-heavy code.
6. Checked arithmetic is easy per node (`MBinary.Checked`), but the context also applies to compound assignment, `++`
   in `for` steps and explicit casts (`conv.ovf`); AIR should carry the flag on those too.

## Files

- `IlSpike.cs`: driver, struct types, two-pass emission, serialization check, census.
- `IlBody.cs`: statements/expressions -> IL, type inference, branch shortening, stack scheduling, the three optimizations.
- `ClrResolve.cs`: C# type text, literals, numeric promotion, overload picking, extension methods.
- `SpikeCli.cs`: `alice emit --il-spike` (build both backends, census, bench, IL dump).
- `bench/tailrec.al`: micro for optimization (b).
- `tests/Alice.Tests/Backends/IlSpikeTests.cs`: parity of the four workloads, optimizations, checked/NaN/shift semantics, tail calls.
