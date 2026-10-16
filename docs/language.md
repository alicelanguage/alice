# Alice language reference

Alice is a .NET language with Crystal/Ruby syntax. Source files use `.al`. The compiler lowers Alice to C# syntax; Roslyn emits IL. Everything below maps to plain C#/.NET constructs, so Alice code and C# code interoperate both ways.

Status markers: unmarked = implemented in stage0; **(planned)** = designed, not yet guaranteed.

## 0. Why Alice

Every popular language makes you pick a trade-off. Alice tries to drop it:

| Goal | Borrowed from | How Alice does it |
|---|---|---|
| Ergonomic, readable | Ruby | Ruby/Crystal syntax, blocks, `name?`, implicit return, type inference |
| Optional typing | Crystal | annotate when you want; untyped params become generics |
| Safe | Rust | static types, nullable refs enforced, null errors fail the build |
| Fast | C | static dispatch, `Vector<T>` SIMD, parallel kernels, clang-built native code, NativeAOT |
| Massively parallel | Elixir | `spawn`, typed channels, actors, `Par` data parallelism |
| Huge ecosystem | C#/.NET | runs on the CLR; uses any NuGet package; C# can call Alice |

Why .NET and not a new VM:
- Mature JIT, GC, debugger, profiler, NativeAOT for free.
- Zero-cost interop: Alice types are ordinary CLR types.
- One backend (Alice -> C# -> Roslyn -> IL) keeps the compiler small and correct.

Why self-hosting: writing the compiler in Alice proves the language is usable for large programs, and the bootstrap fixed point (stage1 == stage2) catches miscompilation.

How it works end to end:

```text
file.al -> Lexer -> Translator -> C# syntax -> Roslyn -> IL (.dll/.exe) -> CLR (JIT or NativeAOT)
                                                  \-> references Alice.Runtime (SIMD, Par, native kernels)
```

## 1. Program structure

- A file is a sequence of top-level statements and declarations.
- Top-level statements form the program entry point (like C# top-level statements).
- Statements end at newline. `;` separates statements on one line.
- Comments: `# to end of line`.
- Blocks close with `end` (or `}` for brace blocks).

```crystal
# hello.al
name = "Alice"
puts "Hello, #{name}!"
```

## 2. Lexical rules

| Element | Form | Example |
|---|---|---|
| Local / method | `snake_case` | `total_sum` |
| Type / module / const | `CamelCase` | `HttpClient`, `MAX` |
| Instance variable | `@name` | `@count` |
| Predicate method | trailing `?` | `empty?` -> C# `empty_q` |
| Symbol of .NET member | as declared | `sb.Append`, `list.Count` |

Identifiers that are C# keywords are escaped automatically (`@class`).

## 3. Literals

| Literal | Type | C# |
|---|---|---|
| `42`, `1_000` | `Int32` | `int` |
| `42_i64` | `Int64` | `long` |
| `255_u8` | `UInt8` | `byte` |
| `3.14`, `1e-3` | `Float64` | `double` |
| `1.5_f32` | `Float32` | `float` |
| `true`, `false` | `Bool` | `bool` |
| `'a'` | `Char` | `char` |
| `"text"` | `String` | `string` |
| `nil` | nullable | `null` |
| `[1, 2, 3]` | `Array(Int32)` | `int[]` |
| `[] of Int32` | `List(Int32)` | `List<int>` |
| `{"a" => 1}` | `Hash(String, Int32)` | `Dictionary<string,int>` |
| `0...n` / `0..n` | range (exclusive / inclusive) | loop bounds |

### String interpolation

```crystal
x = 3
s = "x=#{x}, sq=#{x * x}"     # any expression inside #{}
```

Lowered to C# interpolated strings. Escapes: `\n \t \" \\ \#`.

## 4. Types

### 4.1 Built-in type names

| Alice | C# |
|---|---|
| `Int8/16/32/64` | `sbyte/short/int/long` |
| `UInt8/16/32/64` | `byte/ushort/uint/ulong` |
| `Float32` / `Float64` | `float` / `double` |
| `Bool` | `bool` |
| `Char` | `char` |
| `String` | `string` |
| `Void` | `void` |
| `Any` | `dynamic` |
| `T?` | `T?` (nullable) |
| `Array(T)` | `T[]` |
| `List(T)` | `List<T>` |
| `Hash(K, V)` | `Dictionary<K,V>` |
| `Channel(T)` | runtime channel **(planned)** |

Any other .NET type is used by its name: `StringBuilder`, `System.IO.File`, `Task(Int32)`.

### 4.2 Inference and annotation

```crystal
a = 10               # Int32
b = a * 2.0          # Float64
c : Int64 = 5        # annotated local
d : String? = nil    # nullable, must be annotated
```

Rules:
- Locals are inferred from their initializer (`var`).
- A local without initializer needs a type.
- Reassignment must keep the type.
- `nil` alone cannot be inferred; annotate `T?`.

### 4.3 Null safety

Nullable reference types are on. Null-safety warnings are compile **errors**.

```crystal
s : String? = find_name
puts s.Length          # error: s may be nil
if s
  puts s.Length        # ok: narrowed
end
puts s.try &.Length    # (planned) safe navigation
```

### 4.4 Any (dynamic)

`Any` is opt-in late binding. It is never inferred.

```crystal
x : Any = 5
x = "now a string"
puts x.Length          # resolved at runtime
```

Cost: runtime binder; no compile-time checks. Use only at dynamic boundaries.

## 5. Operators

| Group | Operators |
|---|---|
| Arithmetic | `+ - * / %` (`**` planned) |
| Comparison | `== != < <= > >=` |
| Logical | `&& \|\| !` (`and or not` planned) |
| Bitwise | `& \| ^ ~ << >>` |
| Assignment | `= += -= *= /= %=` |
| Member | `.` instance, `::` namespace/static path |

Integer `/` on `Int32` is integer division (C# semantics).

## 6. Methods

```crystal
def add(a : Int32, b : Int32 = 1) : Int32
  a + b                 # last expression is returned
end

def log(msg : String)   # no return type -> Void
  puts msg
end

def first(xs)           # untyped param -> generic T
  xs[0]                 # error unless return type given
end

def ident(x) : Any      # explicit
  x
end

def empty?(s : String) : Bool
  s.Length == 0
end
```

Rules:
- Value-returning methods must declare `: Type`.
- Implicit return of the last expression; `return expr` allowed.
- Default parameters map to C# optional parameters.
- Untyped parameters become generic type parameters (`def f(x)` -> `void f<T0>(T0 x)`).
- `name?` methods emit as `name_q`; callers still write `name?`.
- Calls without arguments may omit parens for Alice methods; .NET methods always need parens.

## 7. Control flow

All control-flow forms are statements; `if`/`case` as expressions are **(planned)**.

```crystal
if x > 0
  puts "pos"
elsif x < 0
  puts "neg"
else
  puts "zero"
end

unless ready
  wait
end

puts "big" if x > 100       # postfix if
return unless valid         # postfix unless

while i < 10
  i += 1
end

until i == 0
  i -= 1
end

for i in 0...n              # 0..n-1
  puts i
end
for i in 0..n               # 0..n
end

case code
when 200 then puts "ok"
when 404, 410 then puts "gone"
else puts "other"
end

break / next                # loop control (next -> continue)
```

### Exceptions

```crystal
begin
  risky
rescue ex : IOException
  puts ex.Message
rescue
  puts "other"
ensure
  cleanup                   # always runs (finally)
end

raise ArgumentException.new("bad")
```

`rescue` maps to `catch`, `ensure` to `finally`, `raise` to `throw`.

## 8. Blocks and lambdas

Blocks are passed as the last argument and lower to C# lambdas (`Action`/`Func`).

```crystal
[1, 2, 3].each { |x| puts x }

list.each do |x|
  puts x * 2
end

sq = ->(x : Int32) { x * x }
puts sq.call(4)             # .call -> Invoke

adder = ->(a : Int32, b : Int32) { a + b }
```

Lambda params need types unless inferable from context.

## 9. Classes

```crystal
class Animal
  getter name : String          # read-only property
  property age : Int32          # read/write property

  def initialize(@name : String, @age : Int32)
  end                           # @param = assign field

  def speak : String
    "..."
  end

  def self.create(n : String) : Animal   # static method
    Animal.new(n, 0)
  end
end

class Dog < Animal
  def initialize(name : String)
    super(name, 0)
  end

  def speak : String            # overrides (virtual by default)
    "Woof"
  end
end

d = Dog.new("Rex")
puts d.name
d.age = 3
```

| Alice | C# |
|---|---|
| `class B < A` | `class B : A` |
| `def initialize` | constructor |
| `@x` | private field `_x` / property backing |
| `getter x : T` | `public T X { get; }` |
| `property x : T` | `public T X { get; set; }` |
| `def self.m` | `static` method |
| `X.new(...)` | `new X(...)` |
| `private def m` | `private` method |

### Abstract

```crystal
abstract class Shape
  abstract def area : Float64
end

class Square < Shape
  def initialize(@s : Float64)
  end
  def area : Float64
    @s * @s
  end
end
```

## 10. Structs

Value types; copied on assignment.

```crystal
struct Point
  getter x : Int32
  getter y : Int32
  def initialize(@x : Int32, @y : Int32)
  end
end
```

Use for small immutable data and SIMD-friendly layouts.

## 11. Modules

A module is a static class (no instances).

```crystal
module MathUtil
  PI2 = 6.283185
  def self.half(x : Float64) : Float64
    x / 2
  end
end
puts MathUtil.half(3.0)
```

## 12. Interfaces

```crystal
interface IShape
  def area : Float64
end

class Circle
  include IShape        # implements
  def initialize(@r : Float64)
  end
  def area : Float64
    3.14159 * @r * @r
  end
end
```

`class Circle < IShape` is also accepted when it is the only base.

## 13. Enums

```crystal
enum Color
  Red
  Green
  Blue = 10
end

c = Color::Red
puts c == Color::Red
```

## 14. Generics

```crystal
nums = List(Int32).new
nums.Add(1)

buf = Array(Float64).new(1024)   # new double[1024]
map = Dictionary(String, Int32).new

class Box(T)
  getter value : T
  def initialize(@value : T)
  end
end
b = Box(String).new("x")
```

`Type(A, B)` -> `Type<A, B>`. Constraints **(planned)**: `class Box(T) where T : IComparable`.

## 15. Collections

```crystal
arr = [1, 2, 3]          # int[]
arr[0] = 9
puts arr.Length

list = [] of String      # List<string>
list.Add("a")
puts list.Count

h = {"a" => 1, "b" => 2}
h["c"] = 3
puts h["a"]
```

Prelude adds Ruby-like helpers (`each`, `map`, `select`, `sum`, `size`, `empty?`, ...).

## 16. Namespaces and using

```crystal
using System.Text
using System.Collections.Generic

namespace Demo
  class Calc
    def self.add(a : Int32, b : Int32) : Int32
      a + b
    end
  end
end

puts Demo::Calc.add(1, 2)        # :: -> .
```

## 17. Attributes

```crystal
@[Obsolete("use NewApi")]
def old_api
end

@[Serializable]
class Dto
end
```

`@[Name(args)]` -> `[Name(args)]`. Any .NET attribute works.

## 18. Native interop (P/Invoke)

```crystal
@[Link("libm.so.6")]
lib LibM
  fun cos(x : Float64) : Float64
  fun pow(x : Float64, y : Float64) : Float64
end

puts LibM.cos(0.0)
```

`lib` -> static class; `fun` -> `[DllImport] static extern` method.

## 19. .NET interop

```crystal
using System.Text

sb = StringBuilder.new
sb.Append("a").Append(1)     # .NET methods: parens required
puts sb.Length               # properties: no parens
puts Math.Max(3, 7)
puts DateTime.Now.Year
File.WriteAllText("out.txt", "hi")
```

- Any .NET assembly or NuGet package can be referenced.
- C# projects can reference Alice-built assemblies; Alice types are normal CLR types.
- Member names are used exactly as declared in .NET (PascalCase).

## 20. Concurrency (planned; ADR 0005)

```crystal
ch = Channel(Int32).new

spawn do                     # Task.Run
  10.times { |i| ch.send(i) }
  ch.close
end

while v = ch.receive?
  puts v
end
```

| Construct | Lowering |
|---|---|
| `spawn { }` | `Task.Run` |
| `Channel(T)` | `System.Threading.Channels` |
| actor | object + mailbox channel + consumer task |
| `Par.For/Map/Sum` | runtime data parallelism (available now) |

## 21. Runtime library (Alice.Runtime)

```crystal
xs = Array(Float64).new(1_000_000)
Par.For(0, xs.Length) { |i| xs[i] = i * 0.5 }

puts Simd.Sum(xs)
puts Simd.Dot(xs, xs)
Simd.Scale(xs, 2.0)

puts AMath.Mean(xs)
puts AMath.Variance(xs)
c = AMath.MatMul(a, b, n)        # blocked, parallel, SIMD

m = CsrMatrix(Float64).new(...)  # sparse
y = m.Multiply(v)
```

| Type | Use |
|---|---|
| `Simd` | Sum, Dot, Add, Multiply, Scale, Min, Max with `Vector<T>` |
| `Par` | chunked parallel For, Map, Sum |
| `AMath` | MatMul, Mean, Variance, Norm, Axpy |
| `CsrMatrix<T>`, `SparseVector<T>` | sparse linear algebra |
| `Native` | clang kernels via P/Invoke; managed fallback |
| `Prelude` | Ruby-style helpers, `puts` |

## 22. Compilation

| Command | Result |
|---|---|
| `alice run f.al` | compile in memory and run |
| `alice emit f.al` | print generated C# |
| `alice build f.al` | `.dll`/`.exe` |
| `alice build f.al --native` | NativeAOT binary |
| `alice bootstrap` | self-host fixed-point check |

Use `alice emit` to see exactly how any construct lowers.

See also: [Memory safety and ownership (proposed)](design/memory-safety.md).
