# Alice guide

For Alice 0.12.2. Printed by `alice guide`. Written for people and for AI
agents: every example compiles, every rule says what the compiler enforces. Topics: `alice guide performance`, `alice guide proofs`,
`alice guide interop`.

## For AI agents

Add this to your project's `AGENTS.md`:

```text
When using Alice:
- run `alice guide` to learn it
- keep the rules the program must never break in RULES.al
- run `alice prove --check` before committing (fails if a rule fails or PROOF.al is stale)
- run `alice explain file.al` to see which optimizations each method got
- prefer pure functions, owned arrays and `par` so the compiler can parallelize and avoid the GC
```

Workflow: edit code -> `alice run file.al` -> `alice prove` (rewrites PROOF.al) -> commit code and PROOF.al.
A counterexample from `alice prove` names the exact inputs that break a rule: fix the code, not the rule,
unless the rule itself is wrong.

## 1. What Alice is

Alice is a .NET language with Ruby/Crystal syntax. `file.al` compiles to C#, then Roslyn emits IL; Alice types
are CLR types and any .NET library can be used. The base is ergonomic and inferred (types, purity, ownership,
parallelism). On top, explicit keywords (`pure`, `par`, `own`, `borrow`, `scope`, `safe`, `rule`) state what
you want; the compiler proves it or stops with an error. It never silently does something unsafe.

## 2. Basics

```alice
x = 42                      # Int32, inferred
y : Float64 = 1.5           # annotated
val k = 3                   # immutable: `k = 4` is a compile error (AL0203)
name = "alice"
puts "hi #{name}, #{x + 1}" # interpolation
xs = [1, 2, 3]              # Array(Int32)
h = {"a" => 1}              # Hash(String, Int32)
s : String? = nil           # nullable; using it unchecked is a compile error

def add(a : Int32, b : Int32 = 1) : Int32
  a + b                     # last expression is the result
end

def sign(x : Int32) : Int32
  return -1 if x < 0
  1
end

for i in 0...3              # exclusive range: 0, 1, 2
  puts i
end
total = xs.map { |v| v * 2 }.sum
```

- Integer `+ - *` raise on overflow; `&+ &- &*` or an `unchecked ... end` block wrap.
- Integer `/` and `%` are floored: `-7 / 2 == -4`, `-7 % 3 == 2` (proven by exhaustion for every release).
- `if`, `case` are expressions. Blocks end with `end`.

## 3. Types

```alice
class Point
  getter x : Int32
  getter y : Int32
  def initialize(@x : Int32, @y : Int32)
  end
  def norm1 : Int32
    x.abs + y.abs
  end
end

enum Shape                  # sum type
  Circle(r : Float64)
  Rect(w : Float64, h : Float64)
end

def area(s : Shape) : Float64
  case s
  when Shape::Circle(r) then 3.14159 * r * r
  when Shape::Rect(w, h) then w * h
  end                       # missing variants: compile error AL0201
end
```

## 4. Purity and effects

```alice
pure def sq(n : Int32) : Int32   # proven pure, or AL0210
  n * n
end

safe def score(xs : Array(Int32)) : Int32   # no ambient authority, or AL0212
  xs.sum
end

inline def half(x : Float64) : Float64      # AggressiveInlining
  x * 0.5
end
```

- Pure: no I/O, no writes to state it did not allocate, calls only pure code. Writing into arrays it created
  is allowed. Inferred for every method; `pure` makes it a checked contract.
- `safe`: the method and every Alice method it calls cannot reach the machine by name: no .NET statics other
  than `Math`/`Alice.Runtime`, no file, network, process, reflection, threading or interop types, no
  `dynamic`, `@[Native]` or `unchecked`. It can use what callers pass in. A compile-time check, not a sandbox.

## 5. Parallelism (interaction-net style)

Independent pure work runs in parallel without annotations:

```alice
def fib(n : Int32) : Int64
  return n.to_i64 if n < 2
  fib(n - 1) + fib(n - 2)            # two independent redexes: forked near the root
end

pure def work(n : Int64) : Int64
  s = 0_i64
  for i in 0...n
    s += i
  end
  s
end

def both(n : Int64) : Int64
  par a, b = work(n), work(n + 1)    # Bend's parallel let: always forks, or AL0211
  a + b
end
```

- Forks: two consecutive `a = f(x)` / `b = g(y)`, `a, b = f(x), g(y)`, or one arithmetic expression with two
  pure calls, when at least one is recursive. `par` drops the recursion requirement.
- Loops: `for i in 0...n` whose iterations write only `a[i]` (or `a[i * w + j]`), locals of the body and one
  `+=` reduction run on all cores when large. `par for` requires it (AL0209).
- Only proven-pure code is ever parallelized. Data races in `spawn`/`Par` code are compile errors (AL0202).

## 6. Memory: ownership, borrows, lifetimes

Arrays of numbers that never escape are *owned*: their buffers come from a per-thread pool and go back where
the program drops them, so loops stop allocating and the GC has nothing to do. All of this is inferred;
keywords make it a checked contract.

```alice
def source(n : Int32) : Array(Int32)
  xs = Array(Int32).new(n)           # owned
  for i in 0...n
    xs[i] = i * 3
  end
  xs                                 # moved out to the caller, which owns it
end

def scale(own xs : Array(Float64), k : Float64) : Array(Float64)
  xs = xs.map { |v| v * k }          # owned: updated in place, no new array
  xs
end

def total(borrow xs : Array(Int64)) : Int64   # promise: xs is not kept (AL0213)
  s = 0_i64
  for v in xs
    s += v
  end
  s
end

def demo(n : Int32) : Float64
  own a = Array(Float64).new(n, 1.5) # contract: a escapes -> AL0207
  b = scale(a, 2.0)                  # a moves into scale; reading a now is AL0208
  s = 0.0
  scope                              # lifetime region
    tmp = Array(Float64).new(n, 1.0) # must be owned; released at `end`
    for v in tmp
      s += v
    end
  end
  s + b[0]
end
```

| Construct | Meaning | Error |
|---|---|---|
| `own x = ...` | `x` is the only reference to its array | AL0207 escape, AL0208 use after move |
| `own p` parameter | the caller moves its array in | AL0207 if the argument may be shared |
| `borrow p` parameter | the callee never keeps `p` | AL0213 |
| `scope ... end` | arrays created inside die at `end` | AL0207 |
| `xs = xs.map { }` | in place when `xs` is owned | - |

Objects (class instances) stay with the .NET GC. Immutable objects built from constants (`Leaf.new(nil, nil)`)
are shared when nothing in the program can tell two of them apart: a binary tree allocates half as much.

When each of these is worth using (and when it is not): `alice guide performance`, "When to use what".

## 7. Rules and proofs (RULES.al, PROOF.al)

Alice's take on Bend's `LAWS.bend`. Put the properties your program must never break in `RULES.al`:

```alice
# RULES.al
rule clamp_stays_in_range(x in -1000..1000, lo in -50..50)
  c = clamp(x, lo, lo + 10)
  c >= lo && c <= lo + 10
end

rule toggle_twice_is_identity(b : Bool, n in 0...64)
  toggle(toggle(b, n), n) == b
end
```

- A rule is a `pure` method returning `Bool` (enforced, AL0210). Its variables range over explicit finite
  domains: `x in lo..hi` / `x in lo...hi` with integer literals, or `b : Bool`.
- `alice prove` compiles the program, runs every rule on every case of its domain (in parallel), and writes
  `PROOF.al`. Because rules are pure, this is a proof by exhaustion, not a sample. A domain over 2^32 cases is
  rejected (AL0214): narrow it.
- A failing rule prints a counterexample: `fails at x = -3, lo = 0`. An exception is a failure too.
- `PROOF.al` records each rule's result and a SHA-256 of every source. `alice prove --check` re-proves and fails
  if any rule fails or if PROOF.al no longer matches the sources. Run it before committing; commit PROOF.al.
- Scope of the guarantee: the rules hold for the program as this Alice version compiles it, on the stated
  domains. Values outside a domain are not covered, and the compiler itself is trusted, not proven.

## 8. Interop

```alice
@[Link("m")]
lib LibM
  @[Fast]                            # no GC transition: short, non-blocking C functions only
  fun cos(x : Float64) : Float64
end
puts LibM.cos(0.0)

@[Native]                            # compiled with clang -O3; managed fallback without a C compiler
def dot(a : Array(Float64), b : Array(Float64)) : Float64
  s = 0.0
  for i in 0...a.size
    s += a[i] * b[i]
  end
  s
end

sw = System.Diagnostics.Stopwatch.StartNew   # any .NET API by name
```

`fun` signatures over numbers and `Bool` are blittable: no marshalling stub, inlinable calls.

## 9. Tooling

| Command | Purpose |
|---|---|
| `alice run file.al` | compile and run; unchanged programs reuse a cached image (~0.1 s start) |
| `alice build file.al` / `alice build --native` | IL assembly / NativeAOT executable |
| `alice check` | type-check only |
| `alice explain file.al` | per method: forks, parallel loops, owned arrays, shared objects, SIMD |
| `alice prove [--check]` | prove RULES.al, write or verify PROOF.al |
| `alice emit file.al` | print the generated C# |
| `alice fmt`, `alice test`, `alice new`, `alice add <pkg>` | format, tests, projects, NuGet |
| `alice guide [topic]` | this guide; topics: `performance`, `proofs`, `interop` |
| `alice agents [--write]` | the Alice block for your project's AGENTS.md (`alice new` writes it, plus a CLAUDE.md that imports it) |

`ALICE_NO_CACHE=1` disables the run cache.

## 10. Performance checklist

1. Keep hot data in arrays of numbers, not `Object` containers.
2. Let arrays be owned: allocate them locally, index them, lend them to `borrow` parameters, return them.
   Check with `alice explain` ("owned array").
3. Write recursive divide-and-conquer as two independent calls of pure functions; check "fork/join".
4. Write loops whose iterations are independent; check "parallel loop". Use `par for` to require it.
5. Use `@[SoA]` record structs for many small records processed field by field.
6. When `alice explain` shows nothing for a hot method, add the explicit keyword (`pure`, `own`, `par`) to get
   an error that says why.

## 11. Error codes

| Code | Meaning |
|---|---|
| AL0201 | `case` misses sum-type variants |
| AL0202 | data race in concurrent code |
| AL0203 | assignment to a `val` |
| AL0207 | owned value escapes (`own`, `own` parameter, `scope`) |
| AL0208 | use after move |
| AL0209 | `par for` iterations not provably independent |
| AL0210 | `pure def` / `rule` is not pure |
| AL0211 | `par a, b = ...` cannot fork |
| AL0212 | `safe def` reaches ambient authority |
| AL0213 | `borrow` parameter escapes |
| AL0214 | rule domain too large to prove by exhaustion |

The compiler prints the code with the message and a hint; `alice check` lists every error in a project.
