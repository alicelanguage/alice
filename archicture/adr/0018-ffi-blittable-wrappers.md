# ADR 0018: FFI through blittable imports behind inlined wrappers

Status: Accepted (0.12.3)

`lib` `fun` imports used .NET's runtime marshaller for every type other than numbers. A `String` return was then
freed with the marshaller's allocator, but C's `getenv` and `strerror` return memory the library still owns, so the
first call corrupted the heap (`free(): invalid pointer`). String encoding also differed by OS (ANSI on Windows,
UTF-8 elsewhere), and the marshaller generates IL stubs at run time, which NativeAOT has to precompile.

Decision: every `fun` is a `DllImport` with a blittable signature. Where the Alice type is not blittable, the
compiler emits an inlined wrapper that converts:

| Alice | Crosses as | Conversion |
|---|---|---|
| numbers, `Char` | itself | none: the import is the public method |
| `Bool` | `byte` | `b ? 1 : 0`, `!= 0` (C `bool`, not Win32 `BOOL`) |
| `String` / `String?` parameter | `byte*` | `Alice.Runtime.Utf8Arg`: UTF-8 on every OS; strings up to 85 chars use a stack buffer, longer ones native memory freed after the call; `nil` is `NULL`; an embedded NUL raises `ArgumentException` |
| `String?` return | `byte*` | borrowed: decoded as UTF-8, never freed; `NULL` is `nil` |
| `String` return | `byte*` | as above; `NULL` raises `NullReferenceException` |
| `Array(number)` | `T*` | pinned with `fixed`, no copy |
| other (blittable structs, delegates) | runtime marshaller | unchanged |

Costs (12-char string, `strlen`, Release): 15.5 ns per call against 15.6 ns for `[MarshalAs(LPUTF8Str)]`, zero
bytes allocated; the call itself is 2.5 ns. Rejecting NUL closes truncation bugs where C sees a shorter string
than .NET validated (`"safe.txt\0../../etc/passwd"`).

Ownership is explicit in the type: a returned `String` is always borrowed. A C function returning memory the caller
must free should be declared to return a pointer type, so that it is freed with the library's own `free`.
