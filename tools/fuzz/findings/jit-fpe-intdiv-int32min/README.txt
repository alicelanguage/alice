JIT SIGFPE crash found during alice-fuzz testing

Crash info (from coredumpctl 3310116):
- Signal: FPE_INTDIV (integer division by zero)
- Location: libclrjit.so (JIT compiler itself, not runtime)
- Command: alice-fuzz exec --dir /work/wt/fuzz-triage/.scratch/jit/js
- Timestamp: 2026-11-04 22:23:32 UTC

Root cause:
The program contains x * INT32_MIN (-2147483648) in a checked context.
Our IL backend emits: ldc.i4 -2147483648 / mul.ovf

The JIT compiler crashes with FPE_INTDIV while compiling this IL,
likely due to constant folding or optimization attempting division
by zero internally.

Files:
- m.al: Alice source
- m-original.dll: IL assembly from the crash
- m.dll: Freshly recompiled (reproduces the crash)

Status: JIT bug, not a compiler bug. The IL is valid.
