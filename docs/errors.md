# Error codes

Alice checks report `AL` codes; .NET binding errors keep `CS` codes mapped to `file.al(line)`. Codes are assigned in `src/Alice.Compiler/Diagnostics/Diagnostic.cs`; hints (en/pt/es) in `src/Alice.Cli/Localization/Strings.cs`.

| Code | Kind | Example | Message | Fix |
|---|---|---|---|---|
| AL0101 | parse | `if x` without `end` | `expected 'end'` | close the block / add the missing token |
| AL0102 | parse | stray `end` | `unexpected 'end'` | remove it or fix nesting |
| AL0103 | parse | `"abc` | `unterminated string` | close the string, interpolation, char or attribute |
| AL0201 | non-exhaustive | `case e` on `enum E` covering only `E::A(x)` | `non-exhaustive ... B` | add the missing `when` branches or an `else` |
| AL0202 | data race | `total = 0`<br>`Par.For(100) { \|i\| total += i }`; or `t = spawn { puts xs.sum }`<br>`xs[0] = 5` before `t.wait` | `data race` | send through `Channel(T)`, use an `Actor`, `Mutex(T)`, `Atomic`, or return it from the task |
| AL0203 | immutable | `val x = 1`<br>`x = 2` | `immutable` | declare with plain assignment `x = ...` |
| AL0204 | cannot infer | `s = nil` | `cannot infer type of 's' from nil` | `s : String? = nil` |
| AL0205 | type required | `getter x` without type or init | `type required for getter x` | `getter x : Int32` |
| AL0206 | yield | `yield` in a def without block param | `yield needs a block parameter` | `def f(&block : A -> R)` |
| AL0207 | owned value escapes | `own a = Array(Int32).new(n)`<br>`@@keep = a` | `owned value \`a\` escapes: ...` | keep `a` local: index it, lend it to a parameter, move it to another owned local or return it; or drop `own` |
| AL0208 | use after move | `own b = a`<br>`a[0]` | `use after move: \`a\` was moved ...` | reassign `a` (`a = Array(Int32).new(n)`) before using it, or read through `b` |
| AL0209 | cannot parallelize | `par for i in 1...n`<br>`  a[i] = a[i - 1] + 1` | `cannot parallelize \`par for\`: ...` | make iterations independent, or use a plain `for` |
| AL0210 | not pure | `pure def f(n : Int32)`<br>`  puts n` | `\`f\` is not pure: ...` | remove the effect, or drop `pure` |
| AL0211 | cannot fork | `par a, b = g(n), g(a)` | `cannot fork \`par\`: ...` | make both calls pure and independent, or drop `par` |
| AL0212 | not safe | `safe def f`<br>`  System.Environment.ProcessorCount`; `write_file(p, s)`; `x.send("m")`; a `lib` fun | `\`f\` is not safe: it reaches .NET ...` / `it calls \`write_file\` ...` | pass the value (or an object that does the I/O) in as an argument, or drop `safe` |
| AL0213 | borrowed value escapes | `def f(borrow xs : Array(Int32))`<br>`  @@keep = xs` | `borrowed value \`xs\` escapes ...` | only index, size, iterate or lend `xs`; or take it with `own` |
| AL0214 | rule domain too large | `rule r(a in 0..100000, b in 0..100000)` | `rule domain of \`r\` has ... cases, more than the ... \`alice prove\` checks exhaustively` | narrow the ranges, split the rule, or prove it for every input with `alice prove --smt` |
| AL0215 | capturing native callback | `k = 1`<br>`LibC.qsort(xs, n, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { k })` | `a callback passed to native code cannot capture variables` | pass state through the C function's user-data argument |
| AL0216 | pointer outside `unsafe` | `p = LibC.malloc(4_u64)` | `pointer operation outside \`unsafe\`...` | wrap it in `unsafe ... end` or write `unsafe def` |
| AL0217 | conflicting borrow | `def copy_into(dst : Array(Int32), src : Array(Int32))` writes `dst`<br>`copy_into(a, a)` | `conflicting borrow: \`a\` is passed as both ...` | pass a copy (`a.dup`) or a different value |
| AL0218 | stack value escapes | `p = P.new(1)` (`@[Stack] struct P`)<br>`f = ->() { p.x }` | `stack value \`p\` ... cannot be captured` | pass the fields it needs; a `@[Stack]` value may not be captured, stored in a heap type's field, or put in a collection |
| AL0219 | use after send | `ch.send(xs)`<br>`xs[0] = 9` | `\`xs\` was sent to another thread ...` | send `xs.dup`, or rebind `xs` before using it again |
| AL0220 | not statically typed (strict mode) | `v * 2.0_f32` on a `System.Numerics.Vector2` under `--strict` | `strict: \`*\` on \`Vector2\` and \`Float32\` has no static type; ...` | annotate it (`w : Vector2 = ...`, `def f : T`), cast it (`x.as(T)`), or declare it `Any` on purpose; only with `[build] strict = true`, `--strict` or `@[Strict]` |
| AL0221 | unassigned field | `class P`<br>`  @name : String`<br>`  def initialize`<br>`  end` | `field \`@name\` (String) is never assigned by every \`initialize\` ...` | assign it in every `initialize`, give it a default (`@name : String = ""`) or make it `String?` |
| AL0230 | message not sendable | `Actor(List(Int32)).new(->(m : List(Int32)) { })`; `Actor(Box(Array(Int32)))`; `Actor(T)` in a generic class | `message type ... is not sendable between actors` | send numbers, String, enums, records/structs of sendable fields, tuples, `System.Collections.Immutable` types or actor handles |
| AL0231 | actor not isolated | `xs = List(Int32).new`<br>`Actor(Int32).new(->(m : Int32) { puts xs.Count })` | `actor is not isolated: it captures ...` | build the state in `init` (`Actor(S, T)`), send it as a message, or share it through `Mutex(T)`, `Atomic` or another actor |
| AL0232 | dynamic dispatch under NativeAOT or wasm (IL backend) | `n : Any = 4`<br>`puts n.abs` with `[build] native = true` or the wasm target | `` `.abs` on an `Any` value needs dynamic dispatch, which `native = true` (NativeAOT) does not support; ... `` | give the value a static type (`n : Int32 = 4`, `def f(x : T)`) or convert it (`n.as(Int32)`); `Any` works on the JIT (`alice run`, `alice build` without `native`) |
| AL0240 | affine payload | `affine enum T`<br>`  A(s : String)` | `affine enum \`T\`: field \`s\` of A is a reference type (string) that is not an affine enum` | use unmanaged fields or other affine enums, or drop `affine` (the type stays records) |
| AL0241 | affine value escapes | `affine enum Tree ...`<br>`puts t` | `affine value \`t\` escapes: passed to .NET \`puts\`` (also: stored in a field, collection or nullable, captured by a block, compared with `==`, `.hash`/`.to_s`, interpolated, reaches `rescue`) | consume it with `case`, move it to an Alice call or return it; or drop `affine` (the type becomes records) |
| AL0242 | use after consume | `a = swap(t)`<br>`b = swap(t)` (`swap` consumes its argument) | `affine value \`t\` read after it was consumed (moved at main.al:12)` | read the fields inside the `case`, or take it as `borrow` in the reading method (a read after a move becomes a Keep) |
| AL0243 | borrow consumed | `def f(borrow t : Tree)`<br>`  case t ... total(l)` | `borrowed affine value \`l\` (a field of borrowed \`t\`) is consumed` | drop `borrow`, or only read `t` (lend it on with `borrow`) |
| AL0244 | stage not isolated (ADR 0038) | `xs = List(Int32).new`<br>`dataflow do ... stage { \|v\| xs << v }` | `dataflow stage captures ...` | build state with `stage(state: ...)` or send it through the stream |
| AL0245 | replicated stage not pure (ADR 0038) | `stage(par: true) { \|t\| puts t }` | `stage(par: true) is not pure: ...` | remove the effect, or drop `par: true` |
| AL0246 | not a static network (ADR 0038) | two `stage`s reading `lines`, or a cycle | `dataflow edge ... has more than one consumer` / `cycle` | split the stream explicitly, or use actors for feedback |
| AL0247 | not CM-admissible (ADR 0039) | `cm for i in 0...n`<br>`  a[i] = a[i - 1] + 1` | `cannot lower \`cm for\`: ...` | write a scan, combine, compaction or permute shape, or use a plain `for` |
| AL0248 | non-associative combine (ADR 0039) | `cm for ... h[k[i]] += x[i]` with Float64 `h` | `combine on floats needs \`ordered\`` | use integers, mark it `ordered`, or use a plain `for` |
| AL0249 | unproven permutation (ADR 0039) | `cm for i ... out[p[i]] = a[i]` | `permutation \`p\` not provably in range and distinct` | produce `p` with `sort_by_key`/`compact`, or mark it `@[Permutation]` |
| AL0250 | JSON mapping (ADR 0036 C2.5) | `JSON.parse(Int32, s)`; `record Bag(items : Set(Int32))` + `to_json(b)`; a variant field named `type` | `JSON mapping: ... needs an Alice record, class or enum` / `cannot map Bag to JSON: ...` / `variant Ev::Click has a field named \`type\`, which is the sum-type tag` | use `JSON.parse(text)` for JSON::Any, map arrays/hashes/nilables of mappable types, or rename the field |
| AL0001 | other | any unclassified compiler error | - | read the message |

Every code in the table is reported by the 1.0 compiler. AL0222-AL0229 are unassigned (the paradigm codes were
drafted there and moved to AL0240-AL0249, clear of the live AL0220/AL0221).

Output format (CLI):

```text
error[AL0202]: data race ...
  --> main.al:2:20
help: send the value through a Channel(T), use an Actor, or return it from the task
```

Language: `ALICE_LANG=pt|es|en` or `--lang pt`. Default: OS UI culture.

## Errors from type checking (AL03xx)

The generated C# is type-checked by Roslyn; its errors are reported in Alice terms (Alice type names, no C# ids).

AL0309 (use before assignment), AL0310 (possible nil) and AL0300 constant overflow (`The operation overflows at compile time in checked mode`, `Constant value ... cannot be converted`) come from the binder, so every backend reports them (IL included). On the C# backend Roslyn's nullable warnings are only an internal assertion: one that fires prints `alice: flow analysis gap (CS86xx) at file:line` on stderr instead of failing the build.

Two cases are errors although Roslyn never saw them (the generated C# initializes every declared local with `default!`): a reference-typed local declared without a value and read before every path assigns it (`x : String` then `puts x.size`) is AL0309, and a nilable local declared without a value holds nil (`x : String?` then `x.size` is AL0310).

| Code | Meaning | Example message |
|---|---|---|
| AL0301 | operator not defined for the operand types | `'-' is not defined for Int32 and String` |
| AL0302 | no such method/member | `undefined method 'open' for Box` |
| AL0303 | unknown name | `undefined name 'totl'` |
| AL0304 | type mismatch | `type mismatch: expected String, got Int32` |
| AL0305 | wrong argument type | `argument 1 of f: expected Int32, got String` |
| AL0306 | wrong argument count | `wrong number of arguments for f (given 2)` |
| AL0307 | unknown type | `unknown type 'Foo' (missing require, using or a package?)` |
| AL0308 | missing return value | `not every path returns a value` |
| AL0309 | use before assignment | `'x' may be used before it is assigned` |
| AL0310 | possible nil | `value may be nil here; check it ... or x.not_nil!` |
| AL0311 | ambiguous call | `ambiguous call: ...` |
| AL0312 | generic inference failed | `cannot infer type arguments for 'f'; give them explicitly` |
| AL0313 | private member | `'x' is private here` |
| AL0314 | read-only member | `'x' is read-only (declare it with property)` |
| AL0315 | instance required | `'x' needs an instance` |
| AL0300 | anything else from type checking | message with Alice type names |

## Editor diagnostics (AL04xx)

| Code | Meaning |
|---|---|
| AL0401 | `alice lsp`: an open `alice.toml` or `.alproj` does not parse (TOML or XML error at the reported line) |

## CLI errors

Usage mistakes print `error: ...` plus a `hint:` line and exit with 2 (usage) or 1 (missing file/dir, not a dependency).
Internal failures exit with 70 and print one line; `--verbose` shows the stack trace.
