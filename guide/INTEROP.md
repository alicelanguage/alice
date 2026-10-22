# Interop in Alice

Calling .NET, C libraries and native code from Alice, and limiting what code may reach.

## .NET

Every .NET type is usable by name. Alice spellings of .NET statics are accepted (`Math.sqrt` is `Math.Sqrt`).

```alice
sw = System.Diagnostics.Stopwatch.StartNew
sb = System.Text.StringBuilder.new
sb.Append("a")
sb.Append(42)
puts sb.ToString
puts sw.Elapsed.TotalMilliseconds >= 0.0
```

NuGet packages: `alice add Newtonsoft.Json`, then use the types. C# can reference an Alice library like any
other assembly.

## C libraries: `lib` and `fun`

```alice
@[Link("m")]
lib LibM
  @[Fast]
  fun cos(x : Float64) : Float64
  fun floor(x : Float64) : Float64
end

puts LibM.cos(0.0)
```

- `fun` signatures over numbers and `Bool` are blittable: no marshalling stub, and the JIT can inline the call.
  A `Bool` crosses as one byte (C `bool`).
- `@[Fast]` drops the GC transition (about 20% per call). Use it only for short, non-blocking functions that
  never call back into .NET.
- Other parameter types use .NET's default marshalling.

## Native Alice: `@[Native]`

```alice
@[Native]
def dot(a : Array(Float64), b : Array(Float64)) : Float64
  s = 0.0
  for i in 0...a.size
    s += a[i] * b[i]
  end
  s
end
```

The body is compiled twice: to C (clang `-O3 -march=native`) and to .NET. Arrays are pinned and passed as
pointer plus length, so there is no copy. Without a C compiler, or when two array arguments alias, the
managed version runs. Results are the same either way.

## Limiting reach: `safe`

```alice
safe def total(xs : Array(Int32)) : Int32
  s = 0
  for v in xs
    s += v
  end
  s
end
```

A `safe` method, and every Alice method it calls, may compute with the values it is given but cannot reach the
machine by name: no .NET statics other than `Math` and `Alice.Runtime`, no file, network, process, reflection,
threading or interop types, no `dynamic`, `@[Native]` or `unchecked` (AL0212). Use it for code that handles
untrusted input or plugins. It is a compile-time check, not a sandbox: it does not bound CPU time or memory.
