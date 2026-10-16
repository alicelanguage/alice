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
| AL0001 | other | any unclassified compiler error | - | read the message |

Output format (CLI):

```text
error[AL0202]: data race ...
  --> main.al:2:20
help: send the value through a Channel(T), use an Actor, or return it from the task
```

Language: `ALICE_LANG=pt|es|en` or `--lang pt`. Default: OS UI culture.
