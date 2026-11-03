# ADR 0041: Slot generations in arena terms

Status: Proposed. Amends ADR 0037 (term layout) and ADR 0040 (raw access) if accepted.

## Context

Review round 1 (F4) showed that the Release runtime did not detect a use-after-Take: a stale term read another
node, a double Take corrupted the free list and two Allocs aliased. Since 1.0.0-rc.1 every build keeps one freed
bit per slot (Arena.cs): a double Take, and a Take or Borrow of a slot that is still free, throw. One case remains
silent: a stale term whose slot a later Alloc already reused names the new, live node. The bit cannot tell the two
apart, because the term word carries no history: all 64 bits are used (packed flag 1, tag 7, lane 24, slot 32).
The affine checker (ADR 0037) is the intended guard; this ADR covers code it does not see (hand-written C#,
`unsafe` escapes, compiler bugs).

## Decision (proposed)

Take 8 bits from the lane field for a per-slot generation: lane 16 bits (65,536 lanes alive at once, enough now
that an exited thread's lane is reused), generation 8 bits (bits 32-39), slot 32 bits unchanged.

- Each slab chunk gets a `byte[]` of generations next to its freed-bit bitmap (1 byte per slot; 16-byte slots
  grow by 6%).
- `Alloc` writes the slot's current generation into the term. `Take` compares, then increments the generation
  (wrapping at 256) and sets the freed bit. `Borrow` and the foreign read compare. A mismatch throws "stale term".
- A region reset (PopMark) leaves generations alone: a bump over a reset slot uses its current generation, so a
  term from the ended region mismatches as soon as the slot has been taken once.
- The IL inline paths (ADR 0040) load the generation byte beside the freed bit; `RawCheck`/`RawFreed` take the term.

## Consequences

- A stale term after reuse is detected unless the slot cycled a multiple of 256 times (probability 1/256 per
  stale use, never a false alarm). Freed-bit checks stay as they are.
- Term ABI change: `Term.Lane`/`MaxLane` shrink to 16 bits, `ArenaLane.MaxLanes` to 2^16 (fail-stop beyond),
  generated code and serialized terms from older builds are incompatible (the compiler is the only producer).
- Cost estimate: one more byte load and compare per Take/Borrow, one byte store per Take. Measured cost of the
  freed bit alone was +2-16% on benchmarks/arena tree(23); the generation should add less than that again. To be
  measured with `ALICE_PAR=0 taskset -c 11,27` before acceptance.

## Alternatives

- Keep the freed bit only (current): no ABI change; stale-after-reuse stays the verifier's job.
- Take bits from the slot field (2^24 slots per lane): keeps 2^24 lanes but caps a lane at 16 M nodes, below the
  tree(23) workload's 8 M doubling headroom.
- A side table of live terms per slot (full 64-bit term stored per slot): exact, but 8 bytes per slot and a 64-bit
  compare on every access.
