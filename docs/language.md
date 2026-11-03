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
- `require "./lib/math"` loads `lib/math.al` relative to the requiring file; transitive, each file once. Missing file: `cannot find` error. Works in single-file CLI mode (`alice run main.al`). `require_relative "lib/math"` parses as `require "./lib/math"`.
- A `\` at the end of a line continues the statement on the next line.
- Compile-time names: `__FILE__` and `__DIR__` (absolute path of the source file / its directory, as in Crystal),
  `__LINE__` (Int32), `__method__` (the enclosing `def`'s name as a String, `nil` outside a def).

## 2. Literals

| Literal | Type |
|---|---|
| `42` | `Int32` |
| `3.14` | `Float64` |
| `true`, `false` | `Bool` |
| `"text"`, `"x=#{x}"` | `String` (interpolation) |
| `'text'` | `String`, Ruby single quotes: no interpolation, only `\\` and `\'` are escapes |
| `'a'`, `'\n'`, `'\u{41}'` | `Char` (one character or one escape) |
| `"a" "b"` | adjacent literals are one `String` (also across a `\` line continuation) |
| `%q(raw)` / `%Q(x=#{x})` / `%(x=#{x})` | `String`; delimiters `( [ { < \| !` (brackets nest). `%q` is raw, `%Q` and `%()` interpolate |
| `:name` | symbol (prints `name`; usable as hash key) |
| `:+`, `:<=>`, `:[]`, `:<<` | operator symbol (a String); `inject(:+)` / `reduce(1, :*)` take it as the block `{ \|a, b\| a + b }` |
| `nil` | nullable |
| `[1, 2, 3]` | `Array(Int32)` (`int[]`) |
| `{"a" => 1}` | `Hash(String, Int32)` |
| `0...n` / `1..n` | range (exclusive / inclusive) |
| `xs[1..]`, `xs[..2]`, `xs[...2]` | open index range: `xs[1..-1]`, `xs[0..2]`, `xs[0...2]` |
| `xs[1..3]`, `s[1...3]`, `s[1, 3]` | slice of an Array, List or String (a new Array/List/String); negative bounds count from the end; a start past the end gives an empty slice (Ruby: `nil`) |
| `xs[-1]` | literal negative index on an Array, List or String counts from the end (`xs[xs.size - 1]`); a computed negative index (`xs[i]`, `i < 0`) raises `IndexOutOfRangeException`, as in Crystal: counting from the end at run time would add a sign check to every index |
| `(1..)` | endless range, counts up lazily (to `Int32::MAX`) |
| `when 4..`, `in ..3` | open range in a pattern (bounded by `Int32::MIN`/`MAX`); `..b` elsewhere is an error |
| `%w[a b]` / `%i[a b]` | `Array(String)` of words / symbols; also `%w{}`, `%w()`, `%w<>`, `%w\|\|` |
| `/\d+/i` | regex literal, flags `i m x s`, compiled once and cached |
| `<<~EOS ... EOS` | heredoc, common indent stripped, interpolates |
| `<<-EOS ... EOS` | heredoc, indented terminator, text kept as is |
| `<<EOS ... EOS` | plain heredoc: terminator at column 0, text kept as is (only where an operand starts and an `EOS` line follows) |
| `<<~'EOS' ... EOS` | raw heredoc (any of the forms above): no interpolation, no escapes |

String and char escapes: `\n \t \r \0 \a \b \f \v \e \s \\ \" \'`, `\uXXXX`, `\u{1F600}` (any code point; `\u{48 49}`
is several), `\xHH`, octal `\101`, and backslash-newline (nothing). An unknown `\c` is `c`, as in Ruby. A code
point above U+FFFF does not fit in a `Char`. `alice fmt` keeps heredoc bodies byte for byte.

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

.NET members answer to their declared name or the Alice spelling: `Console.write_line`, `sw.elapsed.total_milliseconds`,
`sb.append_line(x)`, `d.contains_key?(k)` (a `?` name tries `IsX`, then `X`, and must return `Bool`). Where a Prelude
method of the same name fits the receiver (`split`, `sum`, `wait`), the Prelude method wins.

### Inference

- Locals: from initializer. `s : String? = nil` annotates.
- Method returns: inferred Crystal-style when `: Type` is omitted, including calls to defs declared later. Unresolvable -> `dynamic` (an error under [strict typing](#strict-typing-al0220)).
- Untyped params are monomorphized per call-site types: `def len(x)` called with `String` and `Array(Int32)` emits `int len(string x)` and `int len(int[] x)`; no `dynamic`.
- Locals are Ruby function-scoped: a local first assigned inside `if`/loop is hoisted and visible after it.

### Null safety

Nullable reference types are on; null warnings are compile errors.

```crystal
s : String? = nil
puts s.nil?        # ok
puts s.Length      # error CS8602
```

Conditions use Ruby truthiness: only `nil` and `false` are falsy, so `if s`, `unless s`, `while s`, `s ? a : b`
and `!s` are nil checks on a nullable. Inside `if n`, and after a guard clause (`return unless n`, `break unless n`,
`next unless n`, `raise ... unless n`), a nullable number such as `n : Int32?` reads as `Int32` (narrowing stops
where the branch reassigns `n`). A .NET generic method whose result is `T?` (`JsonSerializer.Deserialize<Array(Tea)>(text)`)
returns a nullable, checked the same way (`if loaded`, `loaded || fallback`). An assignment can be the condition, as in Ruby:

```crystal
if user = find(id)        # user : User? here, non-nil inside the branch
  puts user.name
end
while line = reader.ReadLine
  puts line.size
end
```

`s.try &.size` is the safe call `s&.size` (Crystal spelling): nil when `s` is nil.

### Any (dynamic)

```crystal
def len(x : Any)
  x.Length         # bound at runtime
end
puts len("abcd")   # 4
puts len([1, 2])   # 2
```

How it runs: the C# backend compiles `Any` to C# `dynamic`. The IL backend (ADR 0033) gives every dynamic operation
(call, member read or write, index, operator, conversion, `.()`) its own call site, served by `Alice.Runtime.Dyn`. The
first call resolves the member by reflection for the receiver's runtime type and caches it: public members only; the
compiled name first, then its .NET spelling (`x.total_area` finds `TotalArea`); overloads by the arguments' runtime
types with numeric widening; nil fits reference and nullable parameters; then `define_method` bodies and
`method_missing`. A later call with the same types costs a type check per argument and a cached reflection invoke,
with no allocation besides boxing value-type arguments and results. A site keeps 5 receiver/argument type
combinations; past that it re-resolves from a per-type member cache. Operators on numbers mean what they mean in
typed code: checked arithmetic, floored `/` and `%` (`-7 / 2` is -4). Under `native = true` (NativeAOT) and on wasm a
dynamic site is error AL0232: type the value, or convert it with `x.as(T)`.

### Strict typing (AL0220)

By default a value the compiler cannot type becomes `dynamic` (see Inference). Strict mode makes that a compile
error instead, so every expression, local, parameter and return has a static type:

- `[build] strict = true` in `alice.toml`, or `--strict` on `alice build`, `run`, `check` and `emit`: the whole program;
- `@[Strict]` on a method, or on a class, struct or module (its own methods): just that code.

```crystal
def len(x : Any)       # Any written on purpose: allowed, and so is what is computed from it
  x.Length
end
v = System.Numerics.Vector2.new(1.0_f32, 2.0_f32)
w = v * 2.0_f32        # error[AL0220]: strict: `*` on `Vector2` and `Float32` has no static type
```

Each error points at the root cause (not at the locals and calls typed from it) and names what could not be typed:
a member on which type, a call to which method, a block parameter, an operator. Fix it by annotating (`w : Vector2 =
...`, `def f : T`, `|k : Int32|`), casting (`x.as(T)`), or writing `Any` where dynamic dispatch is wanted. The C# of
a program that already typed is the same with and without strict mode.

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

#### Single-owner update (0.12.2)

An owned array is the only reference to its buffer, so rebinding it to a `map` of itself
with the same element type overwrites it in place (Bend's single-owner update): no new array.

```crystal
xs = Array(Float64).new(n, 1.5)
xs = xs.map { |v| v * v }   # for (i...) xs[i] = xs[i] * xs[i]; the buffer is reused
```

The block must be one expression that does not mention `xs`. A shared array (`b = xs` first) is not owned and
its map copies as before.

#### `own` and `borrow` parameters

```crystal
def scale(own xs : Array(Float64), k : Float64) : Array(Float64)
  xs = xs.map { |v| v * k }   # the callee owns xs: in place
  xs
end

def total(borrow xs : Array(Int64)) : Int64
  s = 0_i64
  for v in xs
    s += v
  end
  s
end

own a = Array(Float64).new(n, 1.5)
b = scale(a, 2.0)   # a moves into scale; reading a now is AL0208
```

- `own p`: the caller hands over its array. The argument must be an owned local, which is moved, or a fresh
  `Array(T).new`; an array others may reference is AL0207. Inside, `p` is an owned local, released at exit
  unless returned.
- `borrow p`: the callee promises not to keep `p`. It may index, size, iterate and lend `p` to another borrowing
  parameter, but not assign, store, return, capture or pass it where it may be kept (AL0213). The caller's
  owned array stays owned across the call. Without `borrow`, the same property is inferred silently.

#### Lifetimes: `scope ... end`

A `scope` is a lifetime region. Locals declared inside are not visible after `end`, every unmanaged array
created inside must be owned (AL0207 otherwise), and its buffer goes back to the pool at `end` rather than at
method exit. A move to an outer local takes the array out of the region.

```crystal
scope
  tmp = Array(Int32).new(n, 1)
  for v in tmp
    s += v
  end
end                              # tmp released here
out = Array(Int32).new(n)        # can reuse tmp's buffer
```

Measured: a 3-stage `map` pipeline over 1M `Float64` runs at 4.0 ms and 3 bytes per call in Alice. Hand-written
C# loops take 9.3 ms and 32 MB per call; LINQ `Select().ToArray()` takes 11.7 ms and 32 MB.

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

User-definable operators: `+ - * / % == != < <= > >= <=> << >> & | ^`, unary `-@` / `+@` / `~` (or `def -` with no
parameters), `[]` (indexer) and `[]=` (its setter: `g[i] = v`). `def <<(x)` used as a statement (`log << "a" << "b"`)
works too. `def ===` compiles (method `op_CaseEq`) but `when`/`===` do not dispatch to it yet; `def call` is an
ordinary method, `obj.call(x)` / `obj.(x)` on a user class are not supported yet.

Integer `/` on ints is integer division; `a/2` is division, not a regex. Signed `/` and `%` are floored (`-7 / 2 == -4`, `-7 % 3 == 2`, like `divmod`); `tdiv`/`remainder` truncate. Integer overflow raises `OverflowException`; `&+ &- &*` wrap. `Int8`/`UInt8`/`Int16`/`UInt16` operands promote to `Int32` before any arithmetic (as in C#), so wrapping happens at the operator's type: `12.to_i8 &* 12.to_i8` is the `Int32` 144, and only an explicit narrowing wraps into the small type (`144.to_i8!` is -112; `144.to_i8` raises). Floats print as `2.0`, `1.0e-07`, `1.0e+16` (ADR 0013). Limits: `Int32::MAX`, `UInt8::MIN`, `Float64::INFINITY`, `Float64::NAN`. User operators: `def +(o : V2) : V2`.

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

A writer is `def name=(v : T)`; `obj.name = v` and `obj.name += v` call it (it may sit beside a `getter name`
or a `def name`). Crystal's `def name=(@name : T)` assigns the field. It compiles to a method `__set_name`.

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

A block without `|params|` may use numbered parameters `_1`..`_9` (`xs.sort { _2 <=> _1 }`) or `it` (Ruby 3.4,
`xs.map { it * 2 }`); they become the block's parameters. Mixing `it` and `_1`, or `_1` outside a block, is a parse
error. A `do ... end` block may have `rescue` / `else` / `ensure` clauses before its `end`.

### Named tuples

```crystal
t = {name: "ada", age: 36}          # C# (string name, int age): a value, no allocation
puts t[:name], t.age
def bounds(xs : Array(Int32)) : {min: Int32, max: Int32}
  {min: xs.min, max: xs.max}
end
```

`{k: v}` is a named tuple (as in Crystal). Hashes use `=>`: `{"k" => v}` or `{:k => v}`.

### Iterators

A method whose return type is `IEnumerable(T)`, `IEnumerator(T)` or `IAsyncEnumerable(T)` and that has no block
parameter is an iterator: `yield x` is C#'s `yield return`, a bare `return` ends the sequence, and the body runs
lazily as the caller enumerates.

```crystal
def squares : IEnumerable(Int64)
  i = 0_i64
  while true
    yield i * i
    i += 1
  end
end
puts squares.Take(3).ToList().join(",")   # 0,1,4

class Trio < IEnumerable(Int32)          # the non-generic IEnumerable.GetEnumerator is generated
  def GetEnumerator : IEnumerator(Int32)
    yield 1
    yield 2
    yield 3
  end
end

async def ticks : IAsyncEnumerable(Int32)   # consumed with `for await`
  yield 1
  await Task.Yield
  yield 2
end
```

### Lambdas

```crystal
sq = ->(x : Int32) { x * x }
puts sq.call(7)
puts sq.(3)
cube = lambda { |x : Int32| x * x * x }
f = ->sq                             # method reference: parameter types from `def sq`
g = ->sq(Int32)                      # explicit types (needed when sq is overloaded)
h = ->shape.area                     # bound to a receiver; ->Geometry.dist for a module function
[1, 2, 3].map(->sq)                  # works anywhere a lambda, Func or delegate is expected
xs.sum(&->sq)                        # as the block: inlined into a plain loop, like a literal block
xs.map(&f)                           # a lambda in a variable as the block (one-argument iterators)
inc = proc { |x : Int32| x + 1 }
puts ["a", "b"].map(&:upcase).join  # &:sym -> { |x| x.upcase }
```

## 6. Control flow

`if/elsif/else`, `unless`, postfix `if`/`unless`, `while`, `until`, `for x in xs`, `for i in 0...n`, `for k, v in hash`, `next`, `break`, `case/when/else` (multiple values per `when`, `then` form).

Loops, Ruby forms:

```crystal
k += 1 while k < 5                  # modifier while / until
begin                               # body runs first, then the test (do-while)
  k += 1
end while k < 3
r = loop do                         # loop with a value: `break v` gives it
  i += 1
  break i * 10 if i == 3
end
```

`break value` keeps its value only in `x = loop do ... end` (or `{ }`); in another loop a literal or variable value
is dropped and any other value is an error; in a block (`xs.each { |x| break x }`) it is an error.

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

```crystal
def load(path : String) : String    # def-level rescue/ensure: no begin needed
  File.ReadAllText(path)
rescue FileNotFoundException, DirectoryNotFoundException => e
  "missing: #{e.message}"           # one catch per listed type
rescue e : IOException | UnauthorizedAccessException
  raise                             # bare raise re-raises the rescued exception
rescue => e                         # any Exception
  ""
else
  ""                                # runs when nothing was raised (statement position only)
ensure
  puts "done"
end
n = Int32.Parse(s) rescue 0         # rescue modifier (also `(expr rescue v)`)
raise ArgumentException, "bad"      # raise Klass, "message" = raise Klass.new("message")
```

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
| `getter a, b : Int32` | several at once (without a type: inferred from `@a = ...`) |
| `getter? ok : Bool` / `property? ok : Bool` | reader `ok?` (property also writes `a.ok = v`) |
| `getter! v : T` / `property! v : T` | field `@v : T?`; `v` raises when nil, `v?` may be nil |
| `class_getter n : Int32 = 0` / `class_property` / `class_setter` | class variable `@@n` (needs a value) with class methods `A.n`, `A.n = v`, `A.n += 1` |
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
| `record Span(start : Int32, @[JsonPropertyName("end")] stop : Int32)` | parameter attributes target the property (what serializers read); `@[param: X]` targets the parameter |
| `class Box(T)` / `class Node(T) where T : IComparable(T)` | generic class + constraint |
| `abstract`, `private def` | abstract is checked; `private` / `protected` are enforced (AL0313) |
| `private` / `protected` / `public` on its own line | visibility of every later `def` in this body (not `def self.`) |
| `private :a, :b` / `private_class_method :s` | sets the visibility of methods defined above |

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

Variants are constructed with `Type::Variant(args)` (lowered to sealed nested records). `Type(args)` also constructs ordinary classes and records (`Point(1, 2)` is `Point.new(1, 2)`); with type arguments it names a generic type (`List(Int32).new`).

#### `affine enum`: sum types in an arena (ADR 0037)

A sum type whose payload fields are all unmanaged (numbers, `Bool`, `Char`, plain enums, records of those) or other
such sum types (recursion allowed) is **arena-eligible**: the compiler can keep its values in a per-type struct arena
instead of one GC object per node. A variant with no fields or one field of at most 32 bits (`Leaf(v : UInt32)`,
`Emp`) is **packed** into the 64-bit term itself and never allocates.

The proof is whole-program and silent. Every value must be used **affinely**: consumed by one `case`, passed once to
an Alice method, returned, assigned, or built into another node. Per `case` arm the compiler decides:

| Decision | When | Effect |
|---|---|---|
| Take | the scrutinee is owned, not used again on any path from the arm, and the arm has no guard | fields are read, the node is freed |
| Borrow | the scrutinee is used later on some path, or it is a `borrow` parameter | fields are read in place; they stay borrowed until the scrutinee dies, when its slot is dropped and the fields own their nodes |
| Keep | `affine enum` only: the value was moved into a call, node or local and is read again | Bend 2's counted redirect: the receiver and the local each hold a count; the local's last read releases its count |

A local that holds a packed value is copied, not moved, so `Tree::Node(leaf, leaf)` is fine. A `borrow p`
parameter (ADR 0017) only reads: its `case` arms Borrow and the caller keeps ownership. The compiler also **infers**
`borrow` (Bend 2's borrow inference): a parameter whose body only reads it (every `case` arm on it Borrows, it is not
moved, returned or built into a node, and no field read from it is consumed) lends instead of moving, to a
whole-program fixpoint. So `total(t)` twice is fine when `total` only reads `t`; the caller still owns `t` and drops it
after its last read. Written `borrow` stays a contract (AL0243); an inferred one silently becomes owned.

Any escape **demotes the type** to sealed records everywhere in the program: storing a value in a field, a
collection, a nullable (`Tree?`) or a generic type; capturing it in a block or lambda; passing it to .NET (`puts t`);
`==`, `hash`, `to_s` or string interpolation; using it in `rescue`/`ensure`; or using it after it was moved. The
program still runs, at record speed. `affine enum` makes the same sites compile errors:

```crystal
affine enum Tree               # arena form, or a compile error
  Leaf(v : UInt32)
  Node(l : Tree, r : Tree)
end

def total(t : Tree) : UInt32
  case t                       # Take: t is not used again, its node is freed
  when Tree::Leaf(v) then v
  when Tree::Node(l, r) then total(l) &+ total(r)
  end
end

def size(borrow t : Tree) : UInt32
  case t                       # Borrow: the caller still owns t
  when Tree::Leaf(v) then 1_u32
  when Tree::Node(l, r) then size(l) &+ size(r)
  end
end

t = Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32))
n = size(t)                    # lends t
puts total(t) &+ n             # consumes t
```

| Code | Under `affine enum` |
|---|---|
| AL0240 | a payload field is a reference type that is not an affine enum (`A(s : String)`) |
| AL0241 | an affine value escapes (the list above) |
| AL0242 | an affine value is used after a `case` consumed it or it was moved into a call (`swap(t)` twice, where `swap` consumes); a read after a move is a Keep instead |
| AL0243 | a `borrow` parameter, or a field read from it, is consumed |

Calls move their arguments unless the parameter is written or inferred `borrow`.

**Lowering** (`Lowering/Lowerer.Affine.cs`, ADR 0040). A value of an arena type is one 64-bit term: bit 63 packed,
bits 56-62 the variant tag, bits 32-55 the lane (thread) that allocated it, bits 0-31 a packed payload or a slot
index. A slot variant's fields live in its thread's slab as `ulong` words. `case` dispatches on the top byte of the
term; a Take reads the fields and pushes the slot on the free list, so a constructor in the same arm reuses it; a
Borrow only reads. Where an owned value dies unconsumed (the Drops of the analysis, and the fields a Take arm does
not bind) its whole subtree is freed, except children a borrowing arm bound to locals (they own them now). Only the
thread that allocated a node frees it; a node read on another thread returns when the enclosing region ends.
`scope ... end` in the main program is a region: every slot allocated inside is reset at `end`. The C# backend calls
`Arena<TSlot>.Alloc/Take/Borrow`; the IL backend inlines them (lane-cursor slot access without bounds checks).
`ALICE_NO_ARENA=1` compiles sum types to records, `ALICE_ARENA_DEEP=0` frees only the dead node's own slot.
A value passed straight from a call into a borrowing parameter (`total(build(n))`) is a temporary with no local, so
nothing drops it: bind it to a local (`t = build(n)`, `total(t)`) to free it after the call. `alice explain` lists every sum type as `arena` or `record (demoted: ... at file:line: why)`. `affine` is a
modifier only in front of `enum`; it stays an ordinary name elsewhere.

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
- Class discovery for frameworks and plugins: `Controller.subclasses`, `Controller.descendants`,
  `ObjectSpace.each_object(typeof(Controller)) { |c| ... }` over the program's classes (types in assemblies compiled
  against Alice.Runtime). Enumerating live objects, Ruby's other `ObjectSpace` use, is not offered on the .NET GC.
- Refinements scope a reopening to the files that ask for it: `module Shouting; refine String do def shout ... end end end`,
  then `using Shouting` in a file. Without the `using`, `"hi".shout` is an undefined method there.

### gpu for

```crystal
def saxpy(a : Float32, xs : Array(Float32), ys : Array(Float32))
  gpu for i in 0...xs.size          # the body is compiled to a GPU kernel (ILGPU: CUDA, OpenCL, or CPU)
    ys[i] = a * xs[i] + ys[i]
  end
end
```

Needs `alice add ILGPU`. The body may capture arrays of numbers and numbers, and use arithmetic, indexing, `Math.*`
and number conversions; each iteration writes its own element. Anything else is a compile error that says why.
`ALICE_GPU=cpu` runs on ILGPU's CPU accelerator. A `gpu ... end` region keeps the arrays of its `gpu for` loops on the
device until `end` (uploaded once, written ones copied back once); reading them on the CPU inside the region is an
error. A reduction `t += e` adds in registers and once per thread group, not per iteration. Write-only arrays
(every write `a[i] = e`, no read) are not uploaded, and read-only ones are not copied back. Design: [ADR 0021](../archicture/adr/0021-gpu-loops.md).

### lock

`lock obj ... end` is C#'s `lock` (a `System.Threading.Lock` or any object). It is for .NET APIs that need Monitor
locking. It does not exempt writes from the race checker (AL0202): shared Alice data uses `Mutex(T)`, which ties the
lock to the data it guards.

### Source generators

Roslyn source generators run on Alice code: Alice types are emitted as `partial`, so a generator can add the other
half. In a package build they come from MSBuild (framework and NuGet generators alike); single-file `alice run` uses
the framework's (System.Text.Json, Regex, LibraryImport, logging). A framework generator runs only when its attribute
appears, so other programs compile no slower.

A body-less `partial def` is a declaration a generator completes:

```crystal
module Patterns
  @[GeneratedRegex("[0-9]+")]
  partial def self.digits : Regex           # compiled at build time, no runtime Regex parsing
end
```

```crystal
@[JsonSerializable(typeof(Order))]
class AppJson < JsonSerializerContext     # reflection-free, NativeAOT-safe JSON
end
json = JsonSerializer.Serialize(order, AppJson.Default.Order)
```

### Events, delegates, default interface methods

```crystal
delegate Op(a : Int32, b : Int32) : Int32        # public delegate int Op(int a, int b)

class Button
  event clicked : EventHandler                   # public event EventHandler? clicked
  def click
    clicked&.Invoke(self, EventArgs.Empty)       # nil until someone subscribes
  end
end
b = Button.new
b.clicked += ->(s : Object?, e : EventArgs) { puts "hi" }

interface IGreet
  def name : String                              # abstract
  def greet : String                             # a body on the next line: default implementation
    "hi " + name
  end
end
```

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

| `fun` type | Crosses as |
|---|---|
| numbers, `Char` | themselves (direct `DllImport`) |
| `Bool` | 1-byte C `bool` |
| `String`, `String?` (parameter) | NUL-terminated UTF-8 on every OS: stack buffer up to 85 chars, no allocation; `nil` is `NULL`; an embedded NUL raises `ArgumentException` |
| `String?` (return) | borrowed `char*` (never freed), UTF-8; `NULL` is `nil`. `String` raises on `NULL` |
| `Array(number)` | pointer to the pinned elements, no copy |
| `Callback(A, B, R)` | C function pointer. Pass a lambda literal: it is lifted to a static `[UnmanagedCallersOnly]` method (no delegate, no allocation); captures are error AL0215; an exception inside is rethrown after the C call returns |
| `out x : T` / `ref x : T` | pointer to the caller's variable, pinned for the call: C out-parameters and structs by pointer without `unsafe` (`LibC.clock_gettime(0, out ts)`) |
| `struct` of numbers | by value, C layout (fields in declaration order) |
| `Pointer(T)` | raw pointer, only inside `unsafe` (AL0216) |
| `@[Fast]` on `fun` | no GC transition (short, non-blocking C functions only) |

```crystal
@[Link("libc.so.6")]
lib LibC
  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void
end
xs = [5, 3, 9, 1]
LibC.qsort(xs, 4_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })   # callback bodies may use pointers
```

`Pointer(T)` values (`fun malloc(n : UInt64) : Pointer(UInt8)`, `p[i]`) are allowed only inside `unsafe ... end` or an
`unsafe def`; elsewhere they are error AL0216. Strings, arrays and numbers need no `unsafe`.

Design and measurements: [ADR 0018](../archicture/adr/0018-ffi-blittable-wrappers.md). Tests: `FfiTests`.

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

### Processes, links and supervisors (ADR 0028)

An actor is a BEAM-style process: isolated state, a lock-free mailbox, no thread while idle. A crash kills only
that actor; its exit travels to linked actors, `on_exit` monitors and its supervisor, which restarts it in place
(same object, fresh state, empty mailbox).

```crystal
sup = Supervisor.one_for_one(3, 5000)          # at most 3 restarts in 5 s, then the supervisor exits
counter = sup.supervise(Actor(Int32, Int32).new(-> { 0 }, ->(s : Int32, m : Int32) {
  raise "negative" if m < 0                    # let it crash
  s + m                                        # next state
}))
counter.send(5)
counter.send(-1)                               # crashes; restarted with init's state 0
counter.join                                   # waits for that exit
counter.send(2)
puts counter.finish                            # 2: drains, stops, returns the final state

a = Actor(Int32).new(->(m : Int32) { raise "boom" })
b = Actor(Int32).new(->(m : Int32) { })
a.link(b)                                      # abnormal exits kill linked actors (supervisors trap them)
a.on_exit { |r| puts r.kind }                  # monitor: crash
a.send(1)
puts b.join.kind                               # linked
```

| Member | Meaning |
|---|---|
| `Actor(T).new(handler)` | stateless actor; handler `->(m : T) { }` |
| `Actor(S, T).new(init, handler)` | state built by `init` inside the actor (and on every restart); handler returns the next state |
| `send(m)` | enqueue; dropped (counted in `dropped_messages`) when dead or stopping |
| `stop` / `finish` | drain, exit normally, wait; `finish` returns the final state |
| `kill`, `join`, `join(ms)`, `alive?`, `exit_reason`, `restarts`, `id` | lifecycle |
| `link(p)`, `unlink(p)`, `on_exit { \|r\| }` | links and monitors; `r.kind` is normal, crash, killed, shutdown, linked or max_restarts |
| `Supervisor.one_for_one(max, ms)` / `one_for_all(max, ms)` | restart strategy and intensity; `supervise(p)` returns `p`; supervisors nest |

Isolation is a compile error, not a convention: a message type must be sendable (AL0230: no arrays, lists,
mutable classes or blocks) and actor blocks may not capture locals of mutable type (AL0231). Writes to captured
state stay AL0202. Normal exits are not restarted (transient children).

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

### Data-parallel loop shapes and `cm for` (ADR 0039)

A counted loop `for i in 0...n` that `par for` cannot prove independent may still be one of the
Connection-Machine communication shapes. The compiler recognizes them after the `par for` check and lowers
them to the runtime kernels of `Alice.Runtime.Cm` (chunked by `n` only, vectorized, dispatched on the kernel
pool, no GC allocation once warm):

| Body (after pure body-local definitions) | Kernel |
|---|---|
| `acc = acc &+ e(i)` then `out[i] = acc` (also `&*`, `&`, `\|`, `^`; `out[i] = acc` first is exclusive) | scan |
| `if f(i) then acc = 0 end` before such a scan | segmented scan |
| one or more `h[k(i)] = h[k(i)] &+ v(i)` (`&+=`), each on its own table `h` not read in the loop | combine (histogram / scatter-add) |
| `if c(i) then out[m] = xs[i]; m += 1 end`, `m` written only there | compaction |
| `out[p[i]] = xs[i]` | permutation |

Inference never changes a result: it takes integer scans and combines only with wrapping or bitwise operators
(checked `+` could raise where a kernel wraps), Int32/UInt32/Int64/UInt64 elements (Float32/Float64 for
compaction and permutation, which copy), at least 16,384 iterations, and it keeps the plain loop for every
case a kernel cannot reproduce: aliasing arrays (checked at run time), a compaction whose counter does not start
at 0 or whose destination is too short, a permutation with a repeated or out-of-range target (the loop then
runs as written: the last write wins, or it raises), a key of the form `base + i` (distinct cells, not a
combine). An element `e(i)` that is not a plain array read is materialized first by a map pass, which itself
runs as an ordinary (possibly parallel) loop. `alice explain` lists the kernels a method uses.

`cm for` makes a shape a requirement, at any trip count, and widens it:

```crystal
cm for i in 0...n                 # a float scan: chunked, the same bits for any core count
  acc += xs[i]
  pre[i] = acc
end

cm for j in 0...keys.size         # histogram: private tables merged in order
  row[keys[j].clamp(0, 255)] += 1_u32
end
```

- Float scans run with the fixed-chunking contract: chunks of `max(4096, n / 1024)` elements, so results are
  bit-identical for any `DOTNET_PROCESSOR_COUNT` on the same machine, and may differ from the serial left fold in
  the last bits. Write a plain `for` when the serial bit pattern matters.
- Integer `+` and `*` in a `cm for` scan or combine wrap, like `&+` and `&*`.
- A float combine is AL0248 unless written `cm ordered for`, which runs it in index order (the plain loop).
- A permutation is AL0249: the compiler has no proof that `p` is a permutation (use a plain `for`, checked at
  run time).
- Any other body is AL0247, which names the statement that is not a shape.

### Declared effects and explicit forks (0.12.2)

Inference stays the default. These modifiers let a program say what it wants, so the compiler optimizes
without heuristics and a failed proof is a compile error instead of a silent fallback:

| Syntax | Meaning | Error |
|---|---|---|
| `pure def f ...` | `f` has no effects: no I/O, no writes to state it did not allocate, calls only pure code. Pure methods fork and parallelize | AL0210 |
| `par a, b = f(x), g(y)` | Bend's parallel let: fork the two calls even when neither is recursive | AL0211 when they are impure or dependent |
| `inline def f ...` | same as `def f @[Inline]`: `AggressiveInlining` | - |
| `safe def f ...` | secure execution: `f` and every Alice method it calls (constructors and field initializers included) hold no ambient authority. An allow-list, see below | AL0212 |

```crystal
pure def work(n : Int64) : Int64
  s = 0_i64
  for i in 0...n
    s += i
  end
  s
end

def both(n : Int64) : Int64
  par a, b = work(n), work(n + 1)   # always forks (below the fork depth cutoff)
  a + b
end

safe def score(xs : Array(Int32)) : Int32   # may compute on what it is given, nothing else
  xs.sum
end
```

Modifiers combine: `safe pure def`, `private inline def`. `safe` is capability-style: instance members of
values the method receives are allowed, since the caller chose to hand them over; reaching the machine by name
is not. It is a compile-time check, not a sandbox: it does not limit memory or time.

`safe` is an allow-list. Every node of the body is checked, including `using`, `spawn`, blocks, lambdas and
`if`/`case` used as values. A safe body may use:

- Alice code: methods, constructors and field initializers, checked the same way, transitively;
- members of values it was given, and of values it creates;
- statics of math, number, string and collection helpers (`Math`, `MathF`, `Int32::MAX`, `String`, `Array`,
  `Enumerable`, `Convert`, `BitConverter`, `System.Numerics`...);
- the Alice runtime, except its ambient types (native libraries, mapped files, `ObjectSpace`, the test host) and
  members marked `[Ambient]`;
- Prelude functions that only compute: `format`, `sprintf`, `str`, `inspect`, `hash`, `raise`, `rand`, `pow`,
  `to_json`, `parse_json`, `assert`...;
- `new` of collections, text, numbers, exceptions and non-ambient runtime types.

Everything else is AL0212. That includes `puts`/`print`/`gets`, `read_file`/`write_file`/`file_exists?`, `env`,
`exit`, `sleep` and `now`; `lib` funs; `send`, `public_send`, `respond_to?`, `define_method`,
`instance_variable_get/set`, `instance_variables`, `methods` and `method_missing` bodies that do any of these;
`dynamic`; any value of a reflection type (`x.class`, `GetType`, `MethodInfo`); members inherited from a .NET
base class; calls on values of unknown type; `@[Native]`; `unchecked`. To give a safe method I/O, pass it an
object that does the I/O.

Expressions fork too, HVM-style: when a statement is arithmetic over pure reads and exactly two forkable calls
(`fib(n - 1) + fib(n - 2)`), the two calls run as a fork. See [fork/join](fork-join.md).

### Wrapping regions: `unchecked`

Integer `+ - *` raise `OverflowException` on overflow (ADR 0013). Inside `unchecked ... end` they wrap, the
region form of `&+ &- &*` for hashes and PRNGs (inside the region the operands of `&+ &- &*` wrap too:
`(a + 1) &+ 1` is `Int32::MIN + 1` for `a = Int32::MAX`; outside it, `a + 1` still raises):

```crystal
h = 0
unchecked
  for c in s.chars
    h = h * 31 + c.ord
  end
end
```

### Dataflow networks: `dataflow do` (ADR 0038)

```crystal
dataflow(capacity: 16384, batch: 512) do            # options: ring capacity, items per body call
  lines  = source(state: idx) { |all, out|         # one producer; `out << v` emits
    for k in 0...all.size
      out << all[k]
    end
  }
  hashes = lines.stage(state: text) { |t, l, out| out << hash(t, l) }   # stateful, 1:n
  scores = hashes.stage(par: true, workers: 16, batch: 1024) { |h| score(h) }   # pure: replicated, ordered
  scores.sink { |s| total += s }                   # sink writes are copied back after the run
end
```

Each node is a static method over a batch, `(ref state, ReadOnlySpan(TIn), ref RingWriter(TOut))`, registered by
function pointer: no delegate or lambda per item, no allocation per item. Edges are bounded SPSC rings; the
output equals the serial program for every thread count (`ordered: false` lets a replicated stage merge batches as
they finish). A stage block is `|x, out|`, `|x|` (its value is the output) or with `state:` `|st, x, out|`; a
source is `|out|`/`|st, out|` and runs as an iterator (bounded memory), so `out << v` must be a statement of its
body or of its loops. Errors: AL0244 (a node captures a non-sendable or written local; only the sink may write
locals or own a mutable one; no `self`), AL0245 (`par: true` on a block that is not pure), AL0246 (two consumers or
producers, a cycle, an edge without consumer, anything but nodes in the block). `stream def f(...) : Iterator(T)`
declares an iterator whose consumer may run concurrently; today it runs as a plain iterator (same output).

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
| `Par` | parallel For, Map, Sum, Reduce, Sort; fork/join jobs ([fork-join.md](fork-join.md)) |
| `Channel(T)`, `Actor`, `Mutex(T)`, `Atomic` | message passing and guarded shared state (AL0202) |
| `AMath` | MatMul, Mean, Variance, Norm, Axpy |
| `CsrMatrix<T>`, `SparseVector<T>` | sparse linear algebra |
| `Nn`, `Tensor<T>`, `QuantizedMatrix`, `MappedFile` | tensor kernels: dot, GEMV/GEMM, softmax, norms, activations; aligned strided tensors; int8/int4 blocks; mapped files (ADR 0030) |
| `Cm` | Connection-Machine primitives (ADR 0039): `InclusiveScan`/`ExclusiveScan`/`Reduce` (`CmOp` `+ * & \| ^ min max`), `SegmentedScan`/`SegmentedReduce`, `Histogram`, `ScatterAdd`, `Compact`, `Permute`, `SortByKey`; chunked by length only, so floats are bit-identical for any worker count on one machine (float scans follow the SIMD tier: AVX-512, AVX2, Vector128 or scalar hardware may round differently) |
| `Prelude` | Ruby helpers: `puts`, `each`, `map`, `sum`, `size`, `even?`, `to_s`, `to_i`, `send`, ... |

### Rules and proofs

```crystal
rule clamp_stays_in_range(x in -1000..1000, lo in -50..50)
  c = clamp(x, lo, lo + 10)
  c >= lo && c <= lo + 10
end
```

A `rule` is a pure method returning `Bool` over finite domains (`x in lo..hi`, `x in lo...hi`, `b : Bool`; at most
2^32 cases, AL0214). `alice prove` checks every case and writes `PROOF.al`; `alice prove --smt` proves rules over
whole `Int32`/`Int64` ranges with Z3. A rule fails when it returns `false` or raises (overflow, division by zero)
on some input, and `alice prove` prints the first such input in domain order:

```text
rule percent_in_range FAILS: part = 0, total = 0 -> raised DivideByZeroException
```

`alice prove --check` (or `alice build --prove`, or `[build] prove = true`) exits 1 when a rule fails or `PROOF.al`
is stale, so CI and agents gate on the rules; `--format json` and `--timeout` are for scripts. See
[guide/PROOFS.md](../guide/PROOFS.md) (a worked example) and [tooling.md](tooling.md#rules-and-proofs-alice-prove).

## 13. Performance model

See [performance.md](performance.md). Inspect lowering with `alice emit file.al`.

## 14. Commands

See [tooling.md](tooling.md). Error codes: [errors.md](errors.md).

See also: [Memory safety and ownership](design/memory-safety.md) (design notes; implemented parts are marked).

## Standard library

Prelude (`Alice.Runtime.Prelude`), Ruby/Crystal names. `name?` = C# `name_q`.

| Type | Methods |
|---|---|
| String | `size center ljust rjust chomp chop strip squeeze count(set) index rindex starts_with? ends_with? includes? include? sub gsub(str\|Regex) tr delete lines each_line each_char chars scan(Regex\|pattern) match? repeat(n) to_sym succ bytes hex oct to_i to_f upcase downcase capitalize swapcase title reverse split lstrip rstrip bytesize codepoints blank? camelcase camelcase(lower: true) underscore to_i(base) to_i?(base = 10) to_f? ljust/rjust/center(n, String) match(Regex) gsub/sub(Regex) { \|m\| } gsub(Regex, Hash) dup clone hash eql? equal?`; `each_char` without a block is an `IEnumerable(Char)` |
| MatchData | `s.match(/re/)` / `re.match(s)` (nil when no match): `m[0]` whole match, `m[1]`, `m["name"]` (nil for a group that did not take part), `begin(i) end(i) pre_match post_match captures named_captures to_a size to_s` |
| Char | `upcase downcase letter? digit? number? alphanumeric? whitespace? uppercase? lowercase? ascii? ascii_letter? hex? succ pred ord to_i` |
| Array / Enumerable | `each each_with_index map select reject find find_index count sum min max minmax min_by max_by sort sort{\|a,b\|} sort_by uniq uniq{} reverse reverse_each first(n) last(n) take drop take_while drop_while each_slice each_cons zip flatten compact group_by partition tally chunk_while flat_map product sample shuffle(Random?) rotate reduce inject each_with_object step(n) cycle(n) combination(k) permutation(k) join to_a to_h include? cover? flatten(depth) max(n) min(n) max_by(n) min_by(n) slice_when to_set grep(Regex) grep_v(Regex) find_index(value) transpose to_h([[k, v]]) to_h { \|x\| {k, v} } sum(init) filter_map dup clone eql? hash` |
| Hash | `each each_pair map select reject fetch dig merge key? has_key? value? keys values invert to_a min_by max_by sort_by transform_values transform_keys group_by sum count any? all? fetch(k) { \|k\| } delete(k) delete(k) { \|k\| } each_key each_value dig(a, b) dig(a, b, c) include? includes? member? has_value? key(v) store(k, v) length empty? update(h) update(h) { \|k, old, new\| } merge(h) { \|k, old, new\| } filter_map to_h { \|k, v\| {k2, v2} } values_at slice except filter clear dup clone` (blocks get `\|k, v\|`; `delete`, `dig`, `key` return nil when absent) |
| Hash pair (`KeyValuePair`) | `first last key value to_a` (`to_a` is the tuple `{k, v}`) |
| DefaultHash | `DefaultHash(K, V).new(default)` / `DefaultHash(K, V).new { \|h, k\| ... }`: a Hash whose missing keys read as the default (not stored) or the block's value (`h[k] += 1` counts) |
| Enum | `to_i value to_i32 to_i64`; `@[Flags]`: `includes?(flag) has_flag(flag)`; on the type: `Color.values Color.names Color.parse(name) Color.parse?(name) Color.from_value(v) Color.from_value?(v)` (`parse` ignores case; the non-`?` forms raise ArgumentException) |
| Tuple | `size to_a`; prints as `{1, "one"}` |
| Every value | `hash eql? equal?` (identity, reference types); `dup clone` on Array, List, Hash, Set, String (shallow) |
| Int / Float | `gcd lcm digits divmod clamp between? round(n) floor(n) ceil(n) abs even? odd? zero? positive? negative? times upto downto step(limit, by) to_s(base) sqrt cbrt log log2 log10 truncate nan? infinite? finite? fdiv sign tdiv remainder popcount leading_zeros_count trailing_zeros_count bit_length to_i8..to_u64 to_i32 to_f64 to_f32 (checked) to_X! (wrapping)` |
| Range | Enumerable methods plus `step size include? cover? to_a sum`; `("a".."e")` and `('a'..'d')` iterate with `succ`; a Range of another Comparable (`(1.0..2.0)`, dates) has `include? includes? cover? step(Float64)` and raises when iterated; `reverse_each` and `Int#step(limit, by)` without a block return an `IEnumerable` |
| Free functions | `puts print p gets rand exit format(fmt, ...) read_file write_file file_exists? read_lines sleep(sec) now env(name) to_json(obj) parse_json(text)`; `p a, b` (returns the array), `p nil`, `pp`, `at_exit { }` (last registered runs first, also on `exit`) |
| JSON (`Alice.Core.Json`, ADR 0036) | `JSON.parse(text)` (RFC 8259 strict; `JSON.parse(text, true)` also accepts `//` and `/* */` comments) returns `JSON::Any` (`JsonAny`): `any["key"]`, `any[0]`, `any[-1]` (missing key or index: an undefined value, `nil?` is true), `dig("a", 0, "b")`, `as_i as_f as_s as_bool as_a as_h nil? size keys each each_pair to_h to_a to_json`; integers are Int64, then UInt64, then Float64 on overflow; duplicate keys: the last wins. `JSON.generate(obj)` / `to_json(obj)` and `JSON.pretty_generate(obj)` write Ruby's shape (`1.0` stays `1.0`, only `"`, `\` and control characters are escaped). `parse_json(text)` parses with the same engine and returns a `JsonNode` tree. C# API: `JsonDocument.Parse`, `JsonIterator` (on-demand), `JsonWriter`, `JsonMap` (mapping pieces for generated `from_json`/`to_json`) |
| Globals | `ENV` (a `Hash(String, String)` of the environment; `ENV[k] = v` also sets the process variable), `STDOUT STDERR STDIN` (`puts print write flush gets read read_lines each_line`) |
| Range | Enumerable methods plus `step size include? cover? to_a sum` |
| Array (CM, parallel) | `prefix_sum` `scan{\|a,b\|}` (associative block; one call per element, slower than `prefix_sum`) `segmented_sum(flags)` (one sum per segment) `segmented_prefix_sum(flags)` `histogram(k)` (Int32/UInt32 keys in `0...k`) `compact{\|x\| pred}` `scatter_add(idx, vals)` / `scatter_add(target, idx, vals)` (updates the target) `permute(perm)` (`out[perm[i]] = xs[i]`, checked) `sort_by_key(vals)` (stable radix, returns `{keys, values}`). Integers exact; floats bit-identical for any thread count, may differ from a serial loop in the last bits; blocks must be pure |
| Free functions | `puts print p gets rand exit format(fmt, ...) read_file write_file file_exists? read_lines sleep(sec) now env(name) to_json(obj) parse_json(text)` |

`format`: `%d %i %s %f %.Nf %e %x %X %o %b %c %%`, flags `-0+ `, width. A Hash prints as `{"a" => 1}`, a Tuple as `{1, "one"}`. Runtime halves of forms that still need a compiler lowering: `Prelude.hash_new` / `hash_get` (`Hash(K, V).new(default)`; until then write `DefaultHash(K, V).new(default)`), `arr_mul` (`[1, 2] * 2`, `[1, 2] * ","`), `String.build { |io| }` (a C# 14 static extension, waiting for the binder to keep the block). Tests: `Runtime/StdlibTests`, `Runtime/StdlibCompletionTests` (covers a subset of each row).

### Alice Core modules (`Alice.Core.*`, ADR 0036)

`using Alice.Core.Time` (one namespace per module) makes a module's types visible. Core is written with .NET names
and read with Alice spelling through the binder (`Fs.exist?(p)` calls `Fs.Exist`, `Fs.mkdir_p` calls `MkdirP`), so C#
and F# call the same API. Hot paths work on spans and struct enumerators and allocate nothing (allocation tests in
`tests/Alice.Tests/Runtime/Core*Tests.cs`; numbers in `benchmarks/core/README.md`, "Library modules").

| Module | Types and members |
|---|---|
| `Text` | `Utf8String` (immutable UTF-8: `count delete squeeze tr upcase downcase strip index rindex include? start_with? end_with? each_line split length bytesize +`; positions are byte offsets), `Utf8Text` (the same over spans: AVX-512/AVX2 kernels), `Utf8ByteSet` (`"a-z"`, `"^aeiou"`; ASCII specifications, Ruby results on UTF-8 text), `Utf8Translation`; `Format.sprintf / try_format / try_format_utf8(fmt, args...)` with `FormatArg` (no boxing): `%d %i %u %f %e %E %g %G %x %X %o %b %B %s %p %c %%`, flags `- + space 0 #`, width and precision as digits or `*`, `Inf`/`NaN`, two-digit exponents |
| `Time` | `Time.now utc_now utc(y, m, d, h, mi, s) local at(sec) parse with_offset`, `strftime try_strftime(span) iso8601(digits) to_s to_i to_f year month day hour minute second usec nsec wday yday utc_offset zone utc? to_utc to_local add subtract since` (`+ - < ==` from C#); `Duration` (`5.minutes`, `1.5.seconds`, `2.hours.ago`, `from_now`, `total_seconds`, `to_s` as `1h2m3.5s`); `Clock.timestamp since monotonic`; `Time.measure { }`. `strftime`: `%Y %C %y %m %B %b %h %d %e %j %H %k %I %l %P %p %M %S %L %N %z %:z %::z %Z %A %a %u %w %G %g %V %U %W %s %Q %n %t %% %c %D %F %T %R %r %x %X %v %+`, flags `- _ 0 ^ #` and width (`%3N` = 3 fraction digits). `%Z` is `UTC` or the offset (.NET has no zone abbreviations) |
| `Csv` | `CsvReader(bytes)` rows as views (`CsvRow`, `CsvField`: `raw quoted? has_escapes? to_s copy_to try_get_int64 try_get_double`), `CsvWriter(IBufferWriter)` (quotes only when needed), `Csv.parse read foreach count_rows generate generate_line`; RFC 4180, LF or CRLF, a UTF-8 BOM is skipped, `CsvOptions(separator, quote)`. Text after a closing quote is not decoded (malformed input) |
| `Toml` | `Toml.parse(text or bytes) parse_file try_parse write` give a `TomlTable` of `TomlAny` (`[key] [i] dig exists? as_string as_table as_array get_string get_int64 get_double get_boolean get_date_time_offset get_date_time get_date get_time to_object`), `TomlArray`, `TomlParseException` (`line column reason`). TOML 1.0 with its table rules; passes the toml-test suite (208 valid, 509 invalid files); comments are not kept. `alice.toml` is read with it (`Projects/ManifestToml.cs`) |
| `Fs` | `Fs.read read_bytes read_lines write append open(path, mode) { \|f\| } open_stream each_line(path, chomp) (struct enumerator of UTF-8 spans, 0 bytes per line) each_line(path) { \|line\| } count_lines map (a `MappedFile`) exist? file? directory? size zero? mtime basename(path, suffix) dirname extname join expand_path delete rename copy touch mkdir_p rm_rf children each_child glob match` (`glob`: `* ? [a-z] [!x] {a,b} **/`, dot files only for dot patterns) |
| `Process` | `Shell.capture(cmd)` (backticks), `system(cmd)` / `system(program, args...)` (no shell), `spawn`, `wait(pid)`, `kill`, `run(program, args, input:, output:)`, `last_status` (`$?`: `exit_code pid success?`); `Open3.capture3(cmd, stdin_data) capture2e`; `Signals.trap("INT" \| "TERM" \| "HUP" \| "QUIT" \| "EXIT") { }` |
| `Crypto` | `Crc32C.compute append combine hex_digest` (SSE4.2 or ARM64 instruction, three streams), `XxHash3.hash64(data, seed)` (bit-exact with xxHash 0.8), `Digests.sha256 sha512 hash hex_digest`, `Digester.sha256 / create_hmac` (`update`, `digest`, `hex_digest`, `try_finish`), `Hmac.compute sha256 hex_digest verify` (constant time), `SecureRandom.hex random_bytes base64 urlsafe_base64 uuid random_number alphanumeric fill` |
| `Collections` | `Set(T)` (`\| & - ^ <= < >= include? add delete merge subset? disjoint? to_a`; prints `#<Set: {1, 2}>`), `Deque(T)` (`push pop shift unshift first last [i]`), `Ring(T)` (fixed capacity, overwrites the oldest), `BitSet` (`[i] set clear flip pop_count next_set_bit and or xor and_not set_all`; `each` gives the set bits) |
| `Env` | `Env.get set fetch(name) fetch(name, default) has_key? delete fetch_int fetch_bool to_h keys` (the live environment), `Argv.values count at program_name`, `Platform.os arch cores linux? windows? mac_os? runtime hostname pid triple` |
| `Net` | `TcpServer.listen(port, host)` then `serve { \|conn\| }` (one thread-pool handler per connection, closed when it returns) or `serve_async`; `TcpSocket.connect`, `UnixServer` / `UnixSocket`, `UdpSocket` (`bind send send_to receive last_sender`); connections are `StreamSocket` (`read write read_exactly read_async write_async puts gets try_read_line close_write close`, lines through an owned `RingBuffer`) |
| `Net.Http` | `HttpSession.new(base_url, headers, timeout)` over `SocketsHttpHandler` (`get post put delete post_json put_json get_async post_async`, `retries` for idempotent requests), `HttpReply` (`status success? body text header content_type ensure_success json(reader)`: the body stays in a pooled buffer), `Http.get get_string post post_json` (shared session). `json(reader)` / `post_json(writer)` take the JSON reader and writer hooks of `Alice.Core.Json` |

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
| `in ^x` | pin: compare with the existing value of `x`; inside an array or hash pattern too, also a name bound earlier in the same pattern (`in [a, ^a]`: two equal elements) |
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
