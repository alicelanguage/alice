# Clip 1: One Fused Parallel Loop

**Caption**: "Range pipeline fuses into one parallel loop, runs on all cores"

## Program
- `pipeline.al`: map-filter-reduce pipeline over 10M elements

## Command
```bash
dotnet alice.dll explain pipeline.al
dotnet alice.dll run pipeline.al
```

## Timings
Machine: Intel Xeon Platinum 8488C (forge), .NET 10.0.114
- Serial (ALICE_PAR=0): baseline
- Parallel (default): faster on multiple cores

## Files
- `pipeline.al` - Source program
- `demo.cast` - Terminal recording (14.6s)
- `demo.gif` - Animated GIF (203K)
