# Alice Core standard library modules

ADR 0036. Each module lives in `src/Alice.Runtime/Core/<Module>/` (namespace `Alice.Core.<Module>`), is written in C# with
.NET names and read with Alice spelling, and has to match or beat its BCL equivalent on `benchmarks/core`.
The member tables are in [`language.md`](language.md) ("Alice Core modules");
this page is the index with the baselines.

| Module | Replaces | Result against the BCL (forge, `taskset -c 15,31 ALICE_PAR=0`) | Tests |
|---|---|---|---|
| `Text` | `string` Ruby methods, `string.Format` | `count`/`tr` 111-138x over the Prelude, `format` 5.5x with 0 bytes, UTF-8 char count 18x over `GetCharCount` | `CoreTextTests` |
| `Text.Regex` | `System.Text.RegularExpressions` | regex-redux 4.8x over `Compiled` (7.2x on UTF-8); literal alternations, `[a-zA-Z]+ing`, bounded repeats 2.1-4.1x; `\b\w+\b` 1.2-1.3x; on UTF-8 every rebar row is faster except `\p{Lu}\p{Ll}+` (0.87-0.92x); on UTF-16 the literal, caseless-literal and `aws-keys` rows are 0.78-1.1x and `\p{Lu}\p{Ll}+` is 0.87-0.97x, each gap explained in the results; 0 bytes per match-only call (`benchmarks/core/regex/results.md`). The roadmap's compile-time matcher for literal patterns is deferred to 1.1 (ADR 0036 Amendment 2): literals are compiled once at runtime and cached | `RegexParityTests`, `RegexRubyTests`, `RegexScanTests`, `RegexAllocationTests` |
| `Time` | `DateTime` formatting | `strftime` into a span 0.94x of `DateTimeOffset.TryFormat`, 0 bytes | `CoreTimeTests` |
| `Csv` | none in the BCL | 1.6 GB/s every field touched, 0 bytes per row | `CoreCsvTests` |
| `Toml` | none in the BCL | 43 MB/s parse into `TomlAny`, toml-test passes; `alice.toml` is read with it | `CoreTomlTests`, `CoreManifestTests` |
| `Fs` | `File.ReadLines` | `each_line` 3.2x faster, 0 bytes per line | `CoreFsProcessTests` |
| `Process` | `Process.Start` | thin wrapper (`Shell`, `Open3`, `Signals.trap`) | `CoreFsProcessTests` |
| `Crypto` | `Crc32`, `SHA256`, `HMACSHA256` | CRC-32C 10-11 GB/s, SHA-256 1.01x, HMAC 1.02x (wrappers) | `CoreCryptoTests` |
| `Collections` | `Queue`, `LinkedList`, `HashSet` | `Deque` 1.5x over `Queue`, 8.7x over `LinkedList`; `Set` parity | `CoreCollectionsTests` |
| `Env` | `Environment` | wrapper | `CoreFsProcessTests` |
| `Net` | `Socket` | echo, 256 connections: 1.01x of raw `Socket`, 0 bytes per message | `CoreNetTests` |
| `Net.Http` | `HttpClient` | same `SocketsHttpHandler`; JSON body hooks | `CoreNetTests` |

Raw numbers and the protocol: `benchmarks/core/results/core-lib.md`, `benchmarks/core/regex/results.md` (regex, own pinning `taskset -c 9,25`), `benchmarks/core/README.md` ("Library modules").
