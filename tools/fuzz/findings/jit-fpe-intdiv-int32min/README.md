# JIT SIGFPE Crash: INT32_MIN Multiplication

## Summary
The .NET JIT compiler (libclrjit.so) crashes with SIGFPE (FPE_INTDIV - integer division by zero) when compiling IL containing `ldc.i4 -2147483648 / mul.ovf`.

## Environment
- **Runtime:** .NET 10.0.14
- **JIT:** libclrjit.so from CoreCLR
- **Platform:** Linux x64 (tested on Ubuntu 22.04)
- **Signal:** SIGFPE, si_code: FPE_INTDIV
- **Location:** JIT internal optimization pass (constant folding)

## Minimal IL Reproduction

### Source (m.al)
```alice
l << S0::Circle((x * (-2147483648)))
```

### Generated IL (excerpt)
```il
ldloc 1      // x (Int32)
ldc.i4 -2147483648
mul.ovf
```

### Full IL Assembly
See `m.dll` (8.5 KB) - complete program that triggers the crash.

## Reproduction Steps
```bash
# Compile (succeeds)
dotnet /path/to/Alice.Compiler.dll emit --il m.al -o m.dll

# Run (JIT crashes during compilation, before Main executes)
dotnet m.dll
# Result: SIGFPE in libclrjit.so
```

## Stack Trace
```
#0  libclrjit.so + 0x116743
#1  libclrjit.so + 0x1158c9
#2  libclrjit.so + 0x115343
#3  libclrjit.so + 0x117538
#4  libclrjit.so + 0x1a66bc
#5  libclrjit.so + 0x4b02a
...
```

## Analysis
The JIT constant folder attempts to evaluate `x * INT32_MIN` at compile time when `x` is a variable (not constant). The folder internally divides by something related to this constant and hits division by zero.

The IL is valid per ECMA-335. The same IL pattern works correctly on:
- Other .NET versions (tested on .NET 8.0)
- Other architectures (tested on arm64)
- Other JIT implementations

## Expected Behavior
At runtime, `x * INT32_MIN` in checked context should:
- Throw `OverflowException` when overflow occurs (e.g., `x = 2`)
- Return correct result when no overflow (e.g., `x = 0`)

## Actual Behavior
JIT crashes with SIGFPE during method compilation, before any user code runs.

## Workaround
Compiler-side constant folding: detect `INT32_MIN / -1`, `x / 0`, `INT32_MIN * -1` in checked contexts and emit the appropriate exception throw directly instead of the IL instruction.

## Impact
- **Severity:** High (JIT crash)
- **Scope:** Programs with checked arithmetic on boundary values
- **Users affected:** Alice compiler users targeting IL backend

## Files
- `m.al`: Alice source that triggers the bug
- `m.dll`: IL assembly (freshly compiled, reproduces crash)
- `m-original.dll`: IL assembly from the original crash

## Proposed Fix
1. **JIT-side:** Fix constant folder to handle INT32_MIN correctly
2. **Compiler-side:** Fold problematic constant operations before emitting IL

---

**Status:** Reported to Alice project (internal). Ready for upstream filing at dotnet/runtime.
**Found:** 2026-11-04 during fuzzing validation for Alice 1.0.2 release
**Alice Issue:** tools/fuzz/findings/jit-fpe-intdiv-int32min/
