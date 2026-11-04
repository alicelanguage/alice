# Clip 2: The Compiler Refuses the Data Race

**Caption**: "Parallel block with shared mutable write → AL0202 diagnostic, then fixed version runs"

## Programs
- `race.al`: Attempts to write to shared mutable variable inside parallel block
- `race_fixed.al`: Fixed version using parallel reduce

## Commands
```bash
dotnet alice.dll compile race.al       # Shows AL0202 error
dotnet alice.dll run race_fixed.al     # Runs successfully
```

## Machine
Intel Xeon Platinum 8488C (forge), .NET 10.0.114

## Files
- `race.al`, `race_fixed.al` - Source programs
- `demo.cast` - Terminal recording (12.1s)
- `demo.gif` - Animated GIF (119K)
