# Alice language reference

Alice is a .NET language with Crystal/Ruby syntax. Files use `.al`. The compiler (pure C#) parses, binds, lowers to MIR, prints C#, and Roslyn emits Release IL (ADR 0007). Alice types are ordinary CLR types.

Status: unmarked = implemented and covered by `tests/Alice.Tests/Compiler/`. **(planned)** = not implemented.

## 0. Goals

| Goal | From | How |
|---|---|---|
| Readable | Ruby | blocks, `name?`, implicit return, symbols, `send` |
| Typed | Crystal | local + return-type inference; annotate when you want |
| Safe | Rust | null-safety errors, `val`, spawn data-race check |
| Fast | C | inlined block loops, static dispatch, SIMD runtime, NativeAOT |
| Parallel | Elixir | `spawn`, `Channel(T)`, `Actor(T)`, `par_map` |
| Ecosystem | .NET | full BCL and NuGet; C# can call Alice |

```text
file.al -> Syntax (AST) -> Semantics (HIR) -> Lowering (MIR) -> Backend (C# + #line) -> Roslyn -> IL
```

## 1. Program structure

- Top-level statements form the entry point.
- Newline ends a statement; `#` comments to end of line.
- Blocks close with `end` (or `}` for brace blocks).
- `namespace Demo ... end`; `Demo::Calc.go` (`::` -> `.`).
- `using System.Text` imports a .NET namespace.

## 2. Literals

| Literal | Type |
|---|---|
| `42` | `Int32` |
| `3.14` | `Float64` |
| `true`, `false` | `Bool` |
| `"text"`, `"x=#{x}"` | `String` (interpolation) |
| `:name` | symbol (prints `name`; usable as hash key) |
| `nil` | nullable |
| `[1, 2, 3]` | `Array(Int32)` (`int[]`) |
| `{"a" => 1}` | `Hash(String, Int32)` |
| `0...n` / `1..n` | range (exclusive / inclusive) |

## 3. Types

| Alice | .NET |
|---|---|
| `Int8/16/32/64`, `UInt8/16/32/64` | integer types |
| `Float32` / `Float64` | `float` / `double` |
| `Bool`, `Char`, `String`, `Void` | same |
| `Any` | `dynamic` |
| `T?` | nullable |
| `Array(T)` | `T[]` (`Array(Float64).new(4)` = `new double[4]`) |
| `List(T)`, `Dictionary(K, V)` | BCL generics |
| `Channel(T)`, `Actor(T)` | runtime concurrency |

Any other .NET type is used by name.

### Inference

- Locals: from initializer. `s : String? = nil` annotates.
- Method returns: inferred Crystal-style when `: Type` is omitted, including calls to defs declared later. Unresolvable -> `dynamic`.
- Untyped params are generic: `def ident(x)` works for `5` and `"s"`.
- Locals are Ruby function-scoped: a local first assigned inside `if`/loop is hoisted and visible after it.

### Null safety

Nullable reference types are on; null warnings are compile errors.

```crystal
s : String? = nil
puts s.nil?        # ok
puts s.Length      # error CS8602
```

`s.try &.Length` **(planned)**.

### Any (dynamic)

```crystal
def len(x : Any)
  x.Length         # bound at runtime
end
puts len("abcd")   # 4
puts len([1, 2])   # 2
```

### Immutables

```crystal
val k = 3
k = 4              # compile error: immutable
```

## 4. Operators

| Group | Operators |
|---|---|
| Arithmetic | `+ - * / % **` |
| Comparison | `== != < <= > >=` |
| Logical | `&& \|\| !`, `and or not` |
| Assignment | `= += -= *= /=` |
| Ternary | `c ? a : b` |

Integer `/` on ints is integer division. User operators: `def +(o : V2) : V2`.

## 5. Methods

```crystal
def add(a : Int32, b : Int32 = 1)   # return type inferred: Int32
  a + b
end

def sign(x : Int32) : Int32
  return -1 if x < 0
  1
end

def ok?(x : Int32) : Bool           # predicate names
  x > 0
end

def first<T>(xs : Array(T)) : T     # generic def
  xs[0]
end

greet "bob"                         # command call, no parens
```

### Blocks and yield

```crystal
def apply(x : Int32, &f : Int32 -> Int32) : Int32
  yield x
end
puts apply(3) { |v| v * 10 }        # 30

def twice(&block : Int32 ->)        # no result
  yield 1
  yield 2
end
```

### Lambdas

```crystal
sq = ->(x : Int32) { x * x }
puts sq.call(7)
puts sq.(3)
```

## 6. Control flow

`if/elsif/else`, `unless`, postfix `if`/`unless`, `while`, `until`, `for x in xs`, `for i in 0...n`, `for k, v in hash`, `next`, `break`, `case/when/else` (multiple values per `when`, `then` form).

`if` and `case` are expressions:

```crystal
y = if x > 3
  "big"
else
  "small"
end
z = case x
when 5 then 1
else 0
end
```

Exceptions: `begin / rescue e : Exception / ensure / end`, `raise "msg"`.

## 7. Dynamic (Ruby)

```crystal
puts "abc".respond_to?(:ToUpper)   # true
puts "abc".send(:ToUpper)          # ABC
puts "a-b".send(:Replace, "-", "+")
```

Implemented by `Prelude.Dynamic` via reflection.

## 8. Types: classes, structs, modules

```crystal
class Point
  getter x : Int32
  property v : Int32 = 1
  def initialize(@x : Int32)
  end
  def to_s : String
    "(#{x})"
  end
end

class B < A
  def hi : String
    super + "B"
  end
end
```

| Form | Meaning |
|---|---|
| `getter` / `property` | read-only / read-write property |
| `def initialize(@x : T)` | constructor assigning field |
| `def self.m` | static method |
| `X.new(...)` | constructor call |
| `struct S` | value type |
| `@[Stack] struct S` | `ref struct` (stack only) |
| `module M` | static class |
| `interface I` + `class C < I` | interface implementation |
| `enum Color ... end` | enum; `Color::Green` |
| `abstract`, `private`, `protected`, `include` | parsed; not covered by tests |

Methods are `virtual` only when the class has an Alice subclass.

Generic classes `class Box(T)` and constraints **(planned)**.

## 9. Attributes

| Attribute | Effect |
|---|---|
| `@[Inline]` | `MethodImplOptions.AggressiveInlining` |
| `@[NoInline]` | `MethodImplOptions.NoInlining` |
| `@[Hot]` | `MethodImplOptions.AggressiveOptimization` |
| `@[Stack]` on struct | `ref struct` |
| `@[Link("lib")]` on `lib` | P/Invoke (`fun` -> `DllImport`) |
| any other `@[Name(args)]` | .NET attribute |

```crystal
@[Link("libm.so.6")]
lib LibM
  fun cos(x : Float64) : Float64
end
```

## 10. Concurrency (ADR 0005)

```crystal
ch = Channel(Int32).new
spawn do
  for i in 1..10
    ch.send(i)
  end
  ch.close
end
total = 0
ch.each { |v| total += v }

t = spawn { 6 * 7 }
puts t.wait                          # 42

a = Actor(Int32).new(->(m : Int32) { sum += m })
a.send(1)
a.stop                               # drains mailbox

puts [1, 2, 3].par_map { |x| x + 1 }.sum
r = Par.Map([1, 2, 3], ->(x : Int32) { x * 2 })
```

Data-race check: a spawned block that assigns a captured local is a compile error (`data race`). Communicate via channels.

## 11. .NET interop

Full BCL: `StringBuilder`, `Math`, `Path`, `DateTime`, `Regex`, `JsonSerializer`, LINQ (`[3, 1, 2].OrderBy(->(x : Int32) { x }).First()`). .NET members use their declared names (`sb.Append`, `xs.Count`). Compile errors from .NET binding map back to `file.al(line)`.

## 12. Runtime library

| Type | Use |
|---|---|
| `Simd` | Sum, Dot, Add, Multiply, Scale, Min, Max (`Vector<T>`) |
| `Par` | parallel For, Map, Sum |
| `AMath` | MatMul, Mean, Variance, Norm, Axpy |
| `CsrMatrix<T>`, `SparseVector<T>` | sparse linear algebra |
| `Prelude` | Ruby helpers: `puts`, `each`, `map`, `sum`, `size`, `even?`, `to_s`, `to_i`, `send`, ... |

## 13. Performance model

| Source | Emitted IL shape |
|---|---|
| literal block on `each/map/select/reject/sum/count/any?/all?/find/reduce/times/upto/downto/each_with_index` | inlined `for` loop, no delegate |
| `map/select/reject` chain | fused into one loop (0 allocations, `AllocationTests`) |
| `map` over array | exact-size result array only |
| capture-free lambda | `static` lambda |
| Alice method | non-virtual unless subclassed |
| `"#{x}"` | invariant-culture interpolation, no boxing |
| `for i in a..b` | counted loop |
| `if`/`case` value | typed temp, no closure |

Inspect lowering with `alice emit file.al`.

## 14. Commands

| Command | Result |
|---|---|
| `alice run f.al [-- args]` | compile in memory and run |
| `alice emit f.al` | print lowered C# |
| `alice build f.al [-o out.dll] [-r ref.dll]` | IL assembly |
| `alice build f.al --native` | NativeAOT binary |

See also: [Memory safety and ownership (proposed)](design/memory-safety.md).
