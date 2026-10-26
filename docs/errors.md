# Error codes

Alice checks report `AL` codes; .NET binding errors keep `CS` codes mapped to `file.al(line)`. Codes are assigned in `src/Alice.Compiler/Diagnostics/Diagnostic.cs`; hints (en/pt/es) in `src/Alice.Cli/Localization/Strings.cs`.

| Code | Kind | Example | Message | Fix |
|---|---|---|---|---|
| AL0101 | parse | `if x` without `end` | `expected 'end'` | close the block / add the missing token |
| AL0102 | parse | stray `end` | `unexpected 'end'` | remove it or fix nesting |
| AL0103 | parse | `"abc` | `unterminated string` | close the string, interpolation, char or attribute |
| AL0201 | non-exhaustive | `case e` on `enum E` covering only `E::A(x)` | `non-exhaustive ... B` | add the missing `when` branches or an `else` |
| AL0202 | data race | `total = 0`<br>`Par.For(100) { \|i\| total += i }` | `data race` | send through `Channel(T)`, use an `Actor`, `Mutex(T)`, `Atomic`, or return it from the task |
| AL0203 | immutable | `val x = 1`<br>`x = 2` | `immutable` | declare with plain assignment `x = ...` |
| AL0204 | cannot infer | `s = nil` | `cannot infer type of 's' from nil` | `s : String? = nil` |
| AL0205 | type required | `getter x` without type or init | `type required for getter x` | `getter x : Int32` |
| AL0206 | yield | `yield` in a def without block param | `yield needs a block parameter` | `def f(&block : A -> R)` |
| AL0207 | owned value escapes | `own a = Array(Int32).new(n)`<br>`@@keep = a` | `owned value \`a\` escapes: ...` | keep `a` local: index it, lend it to a parameter, move it to another owned local or return it; or drop `own` |
| AL0208 | use after move | `own b = a`<br>`a[0]` | `use after move: \`a\` was moved ...` | reassign `a` (`a = Array(Int32).new(n)`) before using it, or read through `b` |
| AL0209 | cannot parallelize | `par for i in 1...n`<br>`  a[i] = a[i - 1] + 1` | `cannot parallelize \`par for\`: ...` | make iterations independent, or use a plain `for` |
| AL0210 | not pure | `pure def f(n : Int32)`<br>`  puts n` | `\`f\` is not pure: ...` | remove the effect, or drop `pure` |
| AL0211 | cannot fork | `par a, b = g(n), g(a)` | `cannot fork \`par\`: ...` | make both calls pure and independent, or drop `par` |
| AL0212 | not safe | `safe def f`<br>`  System.Environment.ProcessorCount` | `\`f\` is not safe: it reaches .NET ...` | pass the value in as an argument, or drop `safe` |
| AL0213 | borrowed value escapes | `def f(borrow xs : Array(Int32))`<br>`  @@keep = xs` | `borrowed value \`xs\` escapes ...` | only index, size, iterate or lend `xs`; or take it with `own` |
| AL0214 | rule domain too large | `rule r(a in 0..100000, b in 0..100000)` | `rule domain of \`r\` has ... cases, more than the ... \`alice prove\` checks exhaustively` | narrow the ranges, split the rule, or prove it for every input with `alice prove --smt` |
| AL0215 | capturing native callback | `k = 1`<br>`LibC.qsort(xs, n, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { k })` | `a callback passed to native code cannot capture variables` | pass state through the C function's user-data argument |
| AL0216 | pointer outside `unsafe` | `p = LibC.malloc(4_u64)` | `pointer operation outside \`unsafe\`...` | wrap it in `unsafe ... end` or write `unsafe def` |
| AL0001 | other | any unclassified compiler error | - | read the message |

Output format (CLI):

```text
error[AL0202]: data race ...
  --> main.al:2:20
help: send the value through a Channel(T), use an Actor, or return it from the task
```

Language: `ALICE_LANG=pt|es|en` or `--lang pt`. Default: OS UI culture.

## Errors from type checking (AL03xx)

The generated C# is type-checked by Roslyn; its errors are reported in Alice terms (Alice type names, no C# ids).

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
