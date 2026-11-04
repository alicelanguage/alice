# Alice Demo Reel - Launch Thread

## Tweet 1: Introduction & Install
Alice: secure as Rust, fast as C, parallel as Bend, easy as Ruby. Memory-safe parallel pipelines, compiler-enforced race freedom, formally proven invariants.

Install: curl -fsSL https://alice-lang.org/install.sh | sh

## Tweet 2: Clip 1 - Parallel Fusion
Range pipelines fuse into one parallel loop and run on all cores. `alice explain` shows the fusion, parallel vs serial timing proves it.

[GIF: 1-parallel-fusion/demo.gif]

## Tweet 3: Clip 2 - Data Race Protection
The compiler refuses data races: parallel block with shared mutable write → AL0202 diagnostic. Fix it with parallel reduce, it runs clean.

[GIF: 2-data-race/demo.gif]

## Tweet 4: Clip 3 - Formal Proofs
`alice prove`: false rule → counterexample printed. True rule → proven with Z3 over 2^64 cases, cert generated. `alice-check` verifies independently (<600 lines, no Z3).

[GIF: 3-proof/demo.gif]

## Tweet 5: Clip 4 - IL Backend
IL backend averages 0.96x of C# speed across bend2 suite (benchmarks/bend2/results/il-vs-csharp.md, Intel Xeon Platinum 8488C). Lexer shown: 0.89x, identical checksums.

[GIF: 4-il-backend/demo.gif]

## Tweet 6: Clip 5 - Arena Allocation & Links
Arena-allocated enums cut memory 2-8x on tree workloads (benchmarks/bend2/results/arena.md). symreg: 35 MB vs 85 MB without arena.

[GIF: 5-arena/demo.gif]

Playground: https://playground.alice-lang.org

Docs: docs/proofs.md, INVARIANTS.md
Releases: https://github.com/alice-lang/alice/releases
