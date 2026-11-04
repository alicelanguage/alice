# Clip 3: Prove, Counterexample, Certificate

**Caption**: "False rule → counterexample printed; fixed rule → proven with cert, verified independently"

## Programs
- `rules_false.al`: False rule (subtraction commutativity)
- `rules_true.al`: True rule (addition commutativity)

## Commands
```bash
dotnet alice.dll prove rules_false.al              # Shows counterexample
dotnet alice.dll prove --smt rules_true.al         # Proves with Z3, generates cert
dotnet alice-check.dll PROOF.cert PROOF.al         # Independent verification
```

## Details
- False rule fails immediately with counterexample (e.g., a=1, b=0)
- True rule proven symbolically over all Int32×Int32 (2^64 cases)
- Certificate verified by alice-check (<600 lines, no Z3 dependency)

## Machine
Intel Xeon Platinum 8488C (forge), .NET 10.0.114

## Files
- `rules_false.al`, `rules_true.al` - Source programs
- `demo.cast` - Terminal recording (16.1s)
- `demo.gif` - Animated GIF (190K)
