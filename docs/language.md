# Alice language reference

Alice is a .NET language with Crystal/Ruby syntax. Files use `.al`. The compiler (pure C#) parses, binds, lowers to MIR, prints C#, and Roslyn emits Release IL (ADR 0007). Alice types are ordinary CLR types.

Status: unmarked = implemented and covered by `tests/Alice.Tests/Compiler/`. **(planned)** = not implemented.

## 0. Goals

| Goal | From | How |
|---|---|---|
| Readable | Ruby | blocks, `name?`, implicit return, symbols, `send` |
| Typed | Crystal | local + return-type inference; annotate when you want |
| Safe | Rust | null-safety errors, `val`, compile-time data-race check (AL0202) |
| Fast | C | inlined block loops, static dispatch, SIMD runtime, NativeAOT |
| Parallel | Elixir | `spawn`, `async`/`await`, `Channel(T)`, `Actor(T)`, auto-parallel loops |
| Ecosystem | .NET | full BCL and NuGet; C# can call Alice |

```text
file.al -> Syntax (AST) -> Semantics (HIR) -> Lowering (MIR) -> Backend (C# + #line) -> Roslyn -> IL
```

## 1. Program structure

- Top-level statements form the entry point.
- Newline ends a statement; `#` comments to end of line.
- Blocks close with `end` (or `}` for brace blocks).
- `namespace Demo ... end`; `Demo::Calc.go` (`::` -> `.`).
- `using System.Text` imports a .NET namespace; `using Tools` (an Alice module) imports its functions (`using static`).
- `require "./lib/math"` loads `lib/math.al` relative to the requiring file; transitive, each file once. Missing file: `cannot find` error. Works in single-file CLI mode (`alice run main.al`).

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
| `%w[a b]` / `%i[a b]` | `Array(String)` of words / symbols |
| `/\d+/i` | regex literal, flags `i m x s`, compiled once and cached |
| `<<~EOS ... EOS` | heredoc, common indent stripped, interpolates |
| `<<-EOS ... EOS` | heredoc, indented terminator, text kept as is |

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
| `Hash(K, V)` | `Dictionary<K, V>` |
| `Channel(T)`, `Actor(T)`, `Mutex(T)`, `Atomic` | runtime concurrency |
| `SoA(T)` | columnar store of an `@[SoA]` record struct |

Any other .NET type is used by name.

### Inference

- Locals: from initializer. `s : String? = nil` annotates.
- Method returns: inferred Crystal-style when `: Type` is omitted, including calls to defs declared later. Unresolvable -> `dynamic`.
- Untyped params are monomorphized per call-site types: `def len(x)` called with `String` and `Array(Int32)` emits `int len(string x)` and `int len(int[] x)`; no `dynamic`.
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

### Ownership (ADR 0016)

A local array of unmanaged elements that is only indexed, sized, iterated, lent to a
parameter that does not keep it, moved to another such local or returned is *owned*:
the compiler hands its buffer back to a per-thread pool where the program drops it, so
loops that build a fresh array per iteration stop allocating. This is inferred; `own`
turns it into a checked contract.

```crystal
own prev = Array(Int32).new(n + 1)
for i in 1..n
  own row = Array(Int32).new(n + 1)   # reuses the buffer released below
  row[0] = i
  # ... fill row from prev ...
  prev = row                          # move: prev's old buffer is released, row is moved
end
prev[n]                               # prev released after the read
```

`own a` that escapes (stored, captured, passed to .NET) is AL0207; reading a moved
local before reassigning it is AL0208.

## 4. Operators

| Group | Operators |
|---|---|
| Arithmetic | `+ - * / % **` |
| Comparison | `== != < <= > >=` |
| Logical | `&& \|\| !`, `and or not` |
| Assignment | `= += -= *= /=` |
| Ternary | `c ? a : b` |
| Regex | `s =~ /re/` (match index or `nil`), `s !~ /re/`, `s.match?(/re/)` |
| Append | `xs << 1 << 2` on `List`/`StringBuilder` (chainable) |
| Array | `a + b`, `a - b`, `a & b` (intersection), `a \| b` (union) |
| String | `"%05d\|%.2f" % [42, 3.14]` (format), `"ab" * 3` (repeat) |

Integer `/` on ints is integer division; `a/2` is division, not a regex. Signed `/` and `%` are floored (`-7 / 2 == -4`, `-7 % 3 == 2`, like `divmod`); `tdiv`/`remainder` truncate. Integer overflow raises `OverflowException`; `&+ &- &*` wrap. Floats print as `2.0`, `1.0e-07`, `1.0e+16` (ADR 0013). Limits: `Int32::MAX`, `UInt8::MIN`, `Float64::INFINITY`, `Float64::NAN`. User operators: `def +(o : V2) : V2`.

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

def biggest(a : T, b : T) : T forall T where T : IComparable(T)
  a.CompareTo(b) >= 0 ? a : b
end

greet "bob"                         # command call, no parens

def sq(x : Int32) : Int32 = x * x   # endless def
def hi(n : String) = "hi #{n}"
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
cube = lambda { |x : Int32| x * x * x }
inc = proc { |x : Int32| x + 1 }
puts ["a", "b"].map(&:upcase).join  # &:sym -> { |x| x.upcase }
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

Exceptions: `begin / rescue e : Exception / ensure / end`, `raise "msg"`. `retry` inside `rescue` re-runs the `begin` body.

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
| `attr_reader :x` / `attr_accessor :x, :y` / `attr_writer :x` | Ruby forms of `getter` / `property` / `setter`; type inferred from `@x = ...` |
| `@x = v` in a method | declares field `@x`, type inferred (Ruby style) |
| `alias hi hello` | `hi` calls `hello` |
| `def initialize(@x : T)` | constructor assigning field |
| `def self.m` | static method |
| `X.new(...)` | constructor call |
| `struct S` | value type |
| `@[Stack] struct S` | `ref struct` (stack only) |
| `module M` | static class; see [Modules](#modules) |
| `interface I` + `class C < I` | interface implementation |
| `enum Color ... end` | enum; `Color::Green` |
| `record Point(x : Int32, y : Int32)` | `record` class: value equality, `Point { x = 1, y = 2 }` |
| `record struct Vec(x : Float64, y : Float64) ... end` | `record struct`, body may add methods |
| `class Box(T)` / `class Node(T) where T : IComparable(T)` | generic class + constraint |
| `abstract`, `private` | parsed; not covered by tests |

Leaf classes are `sealed`; methods are `virtual` only when the class has an Alice subclass.

### Sum types

Enum variants with fields. `case` destructures; a missing variant without `else` is AL0201. Plain enums (`enum S A B end`) are checked the same way.

```crystal
enum Shape
  Circle(r : Float64)
  Rect(w : Float64, h : Float64)
  Empty
end

def area(s : Shape) : Float64
  case s
  when Shape::Circle(r) then 3.0 * r * r
  when Shape::Rect(w, h) then w * h
  when Shape::Empty then 0.0
  end
end
puts Shape::Rect(1.0, 2.0) == Shape::Rect(1.0, 2.0)   # true
```

Variants are constructed with `Type::Variant(args)` (lowered to sealed nested records). `Type(args)` sugar for ordinary classes **(planned)**.

### Modules

```crystal
module Greeter                 # mixin: instance methods reference the host (`name`)
  def greet : String
    "hi #{name}"
  end
end

module Factory
  def make_default : String
    "made"
  end
end

class Person
  include Greeter              # Person#greet
  extend Factory               # Person.make_default
  getter name : String
  def initialize(@name : String)
  end
end

module Geo
  PI2 : Float64 = 6.28
  module Units
    def self.km(m : Float64) : Float64
      m / 1000.0
    end
  end
  def self.circle(r : Float64) : Float64
    PI2 * r
  end
end
puts Geo::Units.km(2500.0)     # 2.5
```

| Form | Meaning |
|---|---|
| `def self.f` in a module | module function: `M.f` |
| `def f` in a module | mixin template: not compiled on the module; re-bound in each class that includes it |
| `include M` | copies `M`'s instance methods into the class (may call class members) |
| `extend M` | copies `M`'s methods as class (static) methods |
| `using M` | call `M`'s functions unqualified |
| `M::Inner` | nested module |

Tests: `ModuleTests`.

### Reopening and monkey patching

- Reopening an Alice class merges members.
- Reopening a .NET type (`class String`, `struct Int32`) emits extension methods: `"hey".shout`, `21.double`.

### Inheriting .NET types

- `class MyList < List(Int32)`: inherited members callable unqualified (`Add(5)`, `Count`).
- Overriding a .NET virtual (incl. `protected`): `def Dispose(disposing : Bool) ... super end`.
- Arrays of mixed .NET subclasses unify to the common base (`Stream[]`).

### SoA records

```crystal
@[SoA]
record struct Particle(x : Float64, v : Float64, m : Float64)

ps = SoA(Particle).new
ps.add(Particle.new(0.0, 1.0, 2.0))
ps.each { |p| p.x += p.v * 0.5 }     # column-wise, vectorized
puts ps.sum { |p| p.m * p.v }
ps.remove_at(0)                      # swap-remove: last element moves to 0
```

Members: `add`, `[i]`, `remove_at`, `size`, `clear`, `each`, `sum`, `count`. Each field is one array column (`double[] x`).

## 9. Attributes

| Attribute | Effect |
|---|---|
| `@[Inline]` | `MethodImplOptions.AggressiveInlining` |
| `@[NoInline]` | `MethodImplOptions.NoInlining` |
| `@[Hot]` | `MethodImplOptions.AggressiveOptimization` |
| `@[Stack]` on struct | `ref struct` |
| `@[SoA]` on record struct | enables `SoA(T)` columnar store |
| `@[Link("lib")]` on `lib` | P/Invoke (`fun` -> `DllImport`) |
| any other `@[Name(args)]` | .NET attribute |

```crystal
@[Link("libm.so.6")]
lib LibM
  fun cos(x : Float64) : Float64
end
```

## 10. Concurrency (ADR 0005, 0008)

```crystal
ch = Channel(Int32).new
t = spawn { 5.times { |i| ch.send(i) }
  ch.close }
total = 0
ch.each { |v| total += v }
t.wait

m = Mutex(List(Int32)).new(List(Int32).new)
Par.For(100) { |i| m.lock { |l| l.Add(i) } }

c = Atomic.new(0)
Par.For(1000) { |i| c.add(1) }
puts c.get                           # 1000

seen = Atomic.new(0)
a = Actor(Int32).new(->(m : Int32) { seen.add(m) })
a.send(1)
a.stop                               # drains mailbox
```

`Channel(T)`: `send`, `receive`, `try_receive`, `close`, `each`, `send_async`, `receive_async`, `wait_async`. `Channel(T).new` is unbuffered: `send` returns once a receiver has taken the value. `Channel(T).new(n)` buffers up to `n` values; `send` blocks only when full. After `close`, pending values still drain, `send` raises `ClosedChannelError`, and `receive` raises once the channel is empty; `each` stops. Blocking `send`/`receive` allocate nothing. `Actor(T)` runs on an async mailbox: 2000 actors hold no threads.

### Async

```crystal
async def twice(x : Int32) : Int32
  await Task.Delay(1)
  x * 2
end
async def main_async
  puts await twice(4)
end
main_async.wait
```

Return type inferred as `Task<T>`.

### Explicit parallel loops: `par for`

Counted loops already run in parallel when the compiler proves their iterations independent and the trip
count is large (65,536+, or 2+ when the body has nested loops or calls). `par for` drops the size heuristic
and makes the proof a requirement:

```crystal
par for y in 0...h
  for x in 0...w
    img[y * w + x] = shade(x, y)   # iteration y owns row y
  end
end
```

If the iterations are not provably independent (a write outside `a[i]` / `a[i * w + j]`, a loop-carried
read such as `a[i - 1]` of the written array, an impure call) it is compile error AL0209 rather than a silent
serial loop.

### Wrapping regions: `unchecked`

Integer `+ - *` raise `OverflowException` on overflow (ADR 0013). Inside `unchecked ... end` they wrap, the
region form of `&+ &- &*` for hashes and PRNGs:

```crystal
h = 0
unchecked
  for c in s.chars
    h = h * 31 + c.ord
  end
end
```

### Race safety (AL0202)

Concurrent contexts: `spawn`, `Par.For`, `par_each`, `Task.Run` lambdas, `Actor` handlers.

| Rejected | Allowed |
|---|---|
| assign / `+=` captured local | read captured values |
| mutating call on captured collection (`xs.Add`) | `arr[i] = ...` disjoint index writes in `Par.For` |
| `acc[0] = i` constant index | `Mutex(T)#lock`, `Atomic`, `Channel`, `Actor` |
| method that mutates `@field` | `System.Collections.Concurrent.*` |
| `@v = ...` inside `spawn` | `Interlocked.*(ref x, ...)` |
| `Volatile.Write(ref x, ...)` | return a value from the task |

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

See [performance.md](performance.md). Inspect lowering with `alice emit file.al`.

## 14. Commands

See [tooling.md](tooling.md). Error codes: [errors.md](errors.md).

See also: [Memory safety and ownership (proposed)](design/memory-safety.md).

## Standard library

Prelude (`Alice.Runtime.Prelude`), Ruby/Crystal names. `name?` = C# `name_q`.

| Type | Methods |
|---|---|
| String | `size center ljust rjust chomp chop strip squeeze count(set) index rindex starts_with? ends_with? includes? include? sub gsub(str\|Regex) tr delete lines each_line each_char chars scan(Regex\|pattern) match? repeat(n) to_sym succ bytes hex oct to_i to_f upcase downcase capitalize swapcase title reverse split` |
| Array / Enumerable | `each each_with_index map select reject find find_index count sum min max minmax min_by max_by sort sort{\|a,b\|} sort_by uniq uniq{} reverse reverse_each first(n) last(n) take drop take_while drop_while each_slice each_cons zip flatten compact group_by partition tally chunk_while flat_map product sample shuffle(Random?) rotate reduce inject each_with_object step(n) cycle(n) combination(k) permutation(k) join to_a to_h include? cover?` |
| Hash | `each each_pair map select reject fetch dig merge key? has_key? value? keys values invert to_a min_by max_by sort_by transform_values transform_keys group_by sum count any? all?` (blocks get `\|k, v\|`) |
| Int / Float | `gcd lcm digits divmod clamp between? round(n) floor(n) ceil(n) abs even? odd? zero? positive? negative? times upto downto step(limit, by) to_s(base) sqrt cbrt log log2 log10 truncate nan? infinite? finite? fdiv sign tdiv remainder popcount leading_zeros_count trailing_zeros_count bit_length to_i8..to_u64 to_i32 to_f64 to_f32 (checked) to_X! (wrapping)` |
| Range | Enumerable methods plus `step size include? cover? to_a sum` |
| Free functions | `puts print p gets rand exit format(fmt, ...) read_file write_file file_exists? read_lines sleep(sec) now env(name) to_json(obj) parse_json(text)` |

`format`: `%d %i %s %f %.Nf %e %x %X %o %b %c %%`, flags `-0+ `, width. Tests: `Runtime/StdlibTests` (covers a subset of each row).

## Ruby core mixins and `case ... in`

| Form | You write | You get |
|---|---|---|
| `include Comparable` | `def <=>(o : T) : Int32` | `< > <= >= ==`, `between?`, `clamp`, `IComparable(T)` (so `sort`/`max`/`min` work) |
| `include Enumerable` | `def each(&block : E ->)` | the class is an `IEnumerable(E)`: every iterator (`map`, `select`, `sum`, `sort`, `include?`, ...), LINQ, `for x in obj` |
| `case x in ...` | `in Shape::Circle(r)`, `in [a, b]`, `in [first, *rest]`, `in []` | Ruby 3 pattern matching; array patterns check length and bind elements; a case is exhaustive when every length below the shortest `*rest` pattern is covered |

Tests: `tests/Alice.Tests/Compiler/RubyMixinTests.cs`.

### More patterns and Ruby metaprogramming (compiled statically)

| Form | Meaning |
|---|---|
| `in x` / `in _` | bind the subject / match anything (catch-all makes the case exhaustive) |
| `in ^x` | pin: compare with the existing value of `x` |
| `in 1 \| 2 \| 3` | alternatives |
| `in Shape::Circle(r) if r > 10.0` | guard (bound names are visible in the guard) |
| `in {"lang" => "pt", "name" => n}` | hash pattern: keys must exist, names bind, other values must be equal |
| `define_method :name do \|a\| ... end` | defines a real method (untyped params are specialized per call site) |
| `Point = Struct.new(:x, :y)` | value-equality record with dynamically typed fields |
| `def method_missing(name : String, args : Array(Object))` | calls to undefined methods on the class are routed to it at compile time |

Tests: `tests/Alice.Tests/Compiler/RubyDynamicTests.cs`.

## Ruby DSL support

| Ruby | Alice | How it compiles |
|---|---|---|
| `def tag(n); yield; end` | same (no `&block` needed) | implicit block param typed from `yield` args |
| `obj.instance_eval { name "x" }` | same | inlined; receiverless calls go to `obj` (static type must be known) |
| `obj.instance_exec(4) { \|k\| v * k }` | same | same, block params bound to args |
| `field "name"` in a class body | same | statements → static `__class_body`, run by a module initializer in declaration order |
| `@@registry ||= []` | same | static field, type from first assignment |
| `[...].each { \|r\| define_method("#{r}_path") { ... } }` | same | runtime registry; call via `send`/`public_send`, seen by `respond_to?` |
| `method_missing(name, args)` | same | static routing for direct calls; `send` falls back to it |
| `obj.Event += block` | same | adapted to the CLR delegate type; `-=` removes the same handler |

Limits: runtime-defined methods are reachable only dynamically (`send`), not as static calls; `@ivars` inside `instance_eval` are the receiver's (accessor, else reflection).
