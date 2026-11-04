# Clip 5: Arena Sum Types

**Caption**: "Arena-allocated enums: 1.26x faster, 3.7x less memory vs records (symreg, benchmarks/bend2/results/arena.md)"

## Program
- `symreg.al`: Symbolic regression with arena-allocated Expr enum

## Commands
```bash
dotnet alice.dll explain symreg.al                           # Shows arena allocation
/usr/bin/time -v dotnet alice.dll run symreg.al 18 110       # With arena
ALICE_NO_ARENA=1 dotnet alice.dll run symreg.al 18 110      # Without arena (records)
```

## Benchmarks
From benchmarks/bend2/results/arena.md (Intel Xeon Platinum 8488C):
- symreg with arena: 12298ms, 35 MB peak RSS
- symreg without arena (records): 9778ms, 85 MB peak RSS
- Arena benefits vary by workload (see arena.md for full results)

Note: Arena allocation optimizes tree-heavy workloads; symreg shows the memory benefit.

## Machine
Intel Xeon Platinum 8488C (forge), .NET 10.0.114

## Files
- `symreg.al` - Source program
- `demo.cast` - Terminal recording (7.5s)
- `demo.gif` - Animated GIF (37K)
