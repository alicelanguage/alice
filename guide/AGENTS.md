## Alice

This project is written in Alice (`.al` files), a .NET language with Ruby/Crystal-like syntax. It is not Ruby
and not Crystal: code that looks right in those languages often does not compile or behaves differently here.

Before writing Alice:

- Run `alice guide` and read it. Topics: `alice guide performance`, `alice guide proofs`, `alice guide interop`.
- Check your work with `alice check` (types only) and `alice run file.al`.
- Run `alice prove --check` before committing when the project has a `RULES.al`. If a rule fails, fix the code;
  change the rule only if the rule itself is wrong, and say so. Commit the regenerated `PROOF.al`.
- Run `alice explain file.al` after changing hot code: it lists the optimizations each method got.

### Not Ruby, not Crystal

| You may write (Ruby/Crystal habit) | In Alice |
|---|---|
| `xs << 1` / `xs.push(1)` on an array | `Array(T)` is a fixed-size .NET array. Use `List(T)` (`xs = List(Int32).new`, then `xs << 1`) |
| `h["missing"]` returns `nil` | raises `KeyNotFoundException`. Use `h.fetch(k, default)` or check `h.has_key?(k)` |
| `h[k] += 1` on a new key | raises. Initialise first: `h[k] = h.fetch(k, 0) + 1` |
| `s << "x"` on a String | strings are immutable .NET strings. Use `s += "x"`, interpolation, or `System.Text.StringBuilder` |
| big integers grow forever | `Int32`/`Int64` raise `OverflowException`. Use `Int64` or `&+ &- &*` (wrapping) on purpose |
| `Array<Int32>`, `Hash<K, V>` | generics use parentheses: `Array(Int32)`, `Hash(String, Int32)` |
| `require "json"` (a gem) | `require` loads `.al` files. Libraries are NuGet packages: `alice add <Package>`, then use the .NET API |
| `x.size` on a value that may be `nil` | compile error AL0310. Check `if x`, `x.nil?`, use `x || default`, or `x.not_nil!` |
| `-7 / 2 == -3` (C#/Java habit) | integer `/` and `%` are floored, as in Ruby: `-7 / 2 == -4`, `-7 % 3 == 2` |
| monkey-patching hot paths, `method_missing`, `send` | they work but run through reflection. Prefer plain methods in performance-sensitive code |
| untyped `Any`/`Object` containers | they box and disable optimizations. Use typed arrays and collections |

### Writing Alice the compiler can optimize

- Hot data: arrays of numbers (`Array(Float64)`), allocated in the method that uses them. They become owned:
  pooled, no GC.
- Use `own`, `borrow`, `scope`, `pure`, `par` and `par for` to make the compiler prove what you intend. A
  failed proof is a compile error that explains why. Do not delete the keyword to silence the error.
- Divide and conquer as two independent calls to pure functions runs in parallel. So do loops whose
  iterations write only their own index.
- Do not hand-write threads, locks or `Parallel.For` for work the compiler can parallelize.

### Error codes you will meet

AL0201 non-exhaustive `case`, AL0202 data race, AL0203 `val` reassigned, AL0207 owned value escapes,
AL0208 use after move, AL0209 `par for` not independent, AL0210 not pure, AL0211 `par` cannot fork,
AL0212 `safe` reaches ambient authority, AL0213 borrow escapes, AL0214 rule domain too large, AL0310 maybe nil.
