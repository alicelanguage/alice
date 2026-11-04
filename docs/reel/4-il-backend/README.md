# Clip 4: IL vs C# Backend

**Caption**: "IL backend: 0.89x of C# (lexer); geomean 0.96x across bend2 suite (benchmarks/bend2/results/il-vs-csharp.md)"

## Program
- `lexer.al`: Tokenizer benchmark from bend2 suite

## Commands
```bash
dotnet alice.dll emit --il lexer.al                # Show IL output snippet
dotnet alice.dll run --backend il lexer.al         # Run with IL backend
dotnet alice.dll run --backend csharp lexer.al     # Run with C# backend
```

## Benchmarks
From benchmarks/bend2/results/il-vs-csharp.md (Intel Xeon Platinum 8488C):
- lexer: IL 0.889x of C# (1982ms vs 2188ms)
- Geomean across suite: IL 0.96x of C# (Round 2)
- Checksums identical for all workloads

## Machine
Intel Xeon Platinum 8488C (forge), .NET 10.0.114

## Files
- `lexer.al` - Source program  
- `demo.cast` - Terminal recording (18.4s)
- `demo.gif` - Animated GIF (240K)
