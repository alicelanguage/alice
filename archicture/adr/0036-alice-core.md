# ADR 0036: Alice Core, the standard library

Status: Accepted, implemented in 1.0.0 (`src/Alice.Runtime/Core/`); compile-time regex emission is planned for 1.1
(amendment 2), and `Net.Http` is a client (no own server in 1.0). Applies ADR 0019 inward; uses ADR 0016
(owned buffers), ADR 0017 (`borrow`), ADR 0024 (`--native`), ADR 0028 (actors per connection).

Today regex and JSON are the BCL with Ruby names plus extra allocation. `/re/` goes through a dictionary of
interpreted `Regex` objects (`Prelude.regex`, `src/Alice.Runtime/Prelude/Prelude.Syntax.cs`), `parse_json` boxes every
scalar through `JsonNode` (`Prelude.System.cs`), and nothing uses what the compiler knows at compile time: the literal
pattern and the record's fields. The design study is `/work/reports/stdlib-plan.md` (forge); this ADR records the
decisions taken from it.

## Decision

**Namespace and assembly.** The standard library is `Alice.Core.*`, written in C# with .NET names, inside
`Alice.Runtime.dll` for 1.0 (one assembly; the namespace is the boundary), in `src/Alice.Runtime/Core/<Module>/`.
Runtime invariants are unchanged: pure C#, no compiler dependency, AOT/trim clean, no eager reference-type statics
(`StartupTests`: caches and compiled patterns live in lazy holder classes). Pure statics carry `[Alice.Runtime.Pure]`
so loops over them parallelize (`Lowerer.LoopPar.cs`). No Alice-only types in public signatures: spans,
`IBufferWriter<byte>`, `ValueTask`, struct enumerators, `IUtf8SpanFormattable`. Alice sugar (`$~`, `=~`, `ENV[]`,
`scan` returning arrays) stays in `Prelude` and lowers to the public API, so nothing reachable from Alice is out of
reach for C#.

**Naming.** One API, two spellings (ADR 0019 applied to the library itself). C# sees `IsEmpty`, `ReadAll`, `Save`;
Alice writes `empty?`, `read_all`, `save!`. The binder maps the Alice spelling on members of `Alice.Core` types:
`ClrTypes.ClrSpellings` (`read_all` -> `ReadAll`, `empty?` -> `IsEmpty`/`Empty`, Bool only), then `Names.Dotnet`
(`save!` -> `Save`, `has_key?` -> `HasKey`), on statics and instance members; a Core type's own member wins over a
Prelude helper with the same Alice name (`Binder.CoreSpellings`, `Binder.IsCoreType`). Other CLR types keep their
current rules (statics: snake_case -> PascalCase only). Ruby names that are not spellings of a .NET name (`as_i`,
`to_h`, `dig`) are Prelude extension sugar over the .NET member. `using Alice.Core;` joins the emitted default usings
only once a type exists directly in `Alice.Core` (`Emitter.CoreUsing`), so emitted C# for existing programs is
unchanged.

**Scope for 1.0.**

| Module `Alice.Core.*` | Wrap the BCL (already fast) | Build our own | 1.0 | Deferred |
|---|---|---|---|---|
| `Text` | `Utf8.IsValid`, `Rune`, `SearchValues`, `Ascii` | `Regex` (below), `Utf8String` with SIMD Ruby methods, 0-alloc `format`/`sprintf`/`strftime` | yes | grapheme and normalization tables |
| `Json` | `double.TryFormat`, `Utf8.IsValid` | engine below | yes | BigDecimal, JSON5, multi-GB streaming (1.x) |
| `Csv` | - | RFC 4180 reader on the JSON stage-1 technique, compiler-generated `from_csv_row`, writer | yes | dialect sniffing, Excel quirks |
| `Toml` | - | TOML 1.0 to `TomlAny` + typed mapping; the CLI manifest reader moves to it | parse + write | comment round-trip |
| `Fs` | `File`, `RandomAccess`, `FileSystemWatcher`, `MappedFile` | Ruby surface, `each_line` struct enumerator over a pooled buffer | yes | async sugar beyond `await` |
| `Process` | `Process`, `PosixSignalRegistration` | backticks, `system`, `spawn`, `Process.run`, `capture3`, `trap` | yes | pty |
| `Time` | `DateTimeOffset`, `TimeProvider`, `Stopwatch` | Ruby `Time`, `Duration`, monotonic `Clock`, 0-alloc `strftime` | yes | other calendars |
| `Crypto` | `SHA*`/`HMAC*` `HashData`, `RandomNumberGenerator`, AEADs, `FixedTimeEquals` | streaming `Digest`, CRC32C (`Sse42.Crc32`), XXH3, `Random.secure` | digests, HMAC, CRC32C, random | AEAD and KDF sugar |
| `Net::Socket` | `Socket`, `SslStream`, `Dns` | `TcpServer`, `TcpSocket`, `UdpSocket`, `UnixSocket`, owned ring buffers | yes | QUIC |
| `Net::Http` client | `SocketsHttpHandler` | `HTTP.get/post`, JSON bodies through our reader/writer | yes (wrap) | own client |
| `Net::Http` server | Kestrel (adapter only) | own HTTP/1.1 server, router, WebSocket | own HTTP/1.1 + Kestrel adapter | own HTTP/2 (1.x), HTTP/3 only via Kestrel |
| `Log` | `ILogger` (adapter both ways) | interpolated-string handler that formats only when enabled, sinks | yes | OTel exporter |
| `Cli` | - | typed option builder, `--help`, `@[Cli]` record mapping | yes | shell completions |
| `Env`, `System` | `Environment`, `RuntimeInformation` | `ENV`, `ARGV`, `Platform` | yes | - |
| `Collections` | BCL collections | Ruby `Set`, `Deque`, `BitSet`, `Ring` | yes | persistent collections |

Existing `Testing`, `Concurrency`, `Parallel`, `Tensors`, `Simd`, `Memory`, `Actors` stay where they are.

**Regex: one front-end, three tiers.** `RegexSyntax` parses Ruby/Onigmo syntax into `RegexNode` and computes
`RegexFeatures`; `RegexFeaturesExtensions.Tier` is the single tier rule.

- **Tier R (regular)**: no feature in `RegexFeatures.BacktrackingOnly` (backreferences, lookaround, atomic groups,
  possessive quantifiers, `\G`, counted repeats above 32). Our engine: literal extraction and SIMD prefilters
  (`IndexOf`, `SearchValues`, Teddy on `Vector256/512<byte>`), lazy DFA generic over `IInput<TUnit>` (UTF-8 and
  UTF-16 without transcoding), reverse DFA for the start, Pike VM on the matched slice for captures. For a literal
  pattern whose minimized DFA has at most 256 states the compiler emits the matcher as C# (prefilter prologue +
  `switch` DFA over `ReadOnlySpan<byte>` tables in the PE: no Reflection.Emit, AOT and wasm clean).
- **Tier B (backtracking)**: the AST is translated to .NET syntax and run by `System.Text.RegularExpressions`.
  **Default: `RegexOptions.Compiled`, built lazily at runtime** (no compile-time cost). **`[GeneratedRegex]` only under
  `alice build --native`** (NativeAOT has no Reflection.Emit, so `Compiled` would silently fall back to the
  interpreter there). Captures are copied into our `MatchData`.
- **Tier D (dynamic)**: `Regex.new(str)` parses at runtime with the same front-end and picks R or B the same way.

Constructs we do not support are a compile error, never a silent semantic change. `match?` and `=~` without `$~`
readers allocate 0 bytes. Semantics are tested once, on the AST: Onigmo parity fuzzer (Ruby on the forge),
differential Tier R vs BCL, rebar's test corpus.

**JSON: own engine, STJ at the boundary and as the oracle.** Stage 1 structural index (64-byte blocks, prefix-xor
quote masks with carry-less multiply, `Avx512BW`/`Avx2`/`Ssse3`/`AdvSimd` shuffle classification, scalar on wasm;
`Utf8.IsValid`); stage 2 simdjson-style tape. `JSON.parse` **returns `JsonAny`** (`JSON::Any`, a readonly struct:
document + tape index, zero-copy strings) rather than Ruby's boxed Hash/Array; `to_h`/`to_a` give the Ruby shape.
`JsonIterator` is the on-demand reader that compiler-generated `T.from_json` uses (`__json_read_T`, field names
compared with `"name"u8`, unknown fields skipped by index jumps); `JsonWriter` writes into `IBufferWriter<byte>` or a
span and backs generated `to_json`. Mapping is emitted once per type into `Lowerer.Extra`: no reflection, no
generators. Interop: `ToJsonElement()`, `JsonConverter<T>` for `naming = "dotnet"` libraries. Differential tests
against `System.Text.Json` on the corpus. 1.0 is RFC 8259 strict plus `AllowComments`.

**HTTP.** Own HTTP/1.1 server on `Socket` with a per-connection owned ring buffer: SIMD request-line and header
parsing, `HttpRequest`/`HttpResponse` ref-struct views that materialize nothing unless asked, keep-alive, pipelining,
chunked, WebSocket, graceful shutdown. **HTTP/2, HTTP/3 and TLS in 1.0 through a Kestrel transport adapter**
(optional package referencing `Microsoft.AspNetCore.App`) behind the same views: own HTTP/2 (HPACK, flow control) is
XL and a security surface, deferred to 1.x. The client wraps `SocketsHttpHandler`.

**Contracts frozen now** (`src/Alice.Runtime/Core/Abstractions/`). Dependent tasks implement against these and
change them only by amending this ADR:

| Contract | File | Implemented by |
|---|---|---|
| `RegexNode` record hierarchy, `RegexPattern`, `RegexFlags`, `RegexFeatures`, `RegexTier` | `RegexNode.cs` | complete (pure data) |
| `IInput<TUnit>`, `Utf8Input`, `Utf16Input` | `Input.cs` | complete |
| `JsonDocument`, `JsonAny`, `JsonProperty`, `JsonKind`, `JsonParseOptions`, `JsonParseException` | `Json.cs` | C2.3 |
| `ref struct JsonIterator`, `ref struct JsonWriter` | `Json.cs` | C2.4 |
| `HttpRequest`, `HttpResponse` (ref-struct views), `IHttpResponseSink`, `HttpField`, `HttpRequestLine` | `Http.cs` | views complete; transports C4.3, C4.4 |

The JSON types are `partial`: `Json.cs` holds only declarations and docs; `Abstractions/Stubs/Json.Stub.cs` holds
placeholder bodies (`NotImplementedException`). An implementing task deletes its part of the stub and adds the real
part under `Core/Json/`, so contract and implementation files stay disjoint.

**Allocation and benchmark policy.** Every hot path has an allocation test at 0 bytes or a stated bound (the result
graph for `from_json`), with the `AllocationTests` harness for Alice code and runtime-level tests for the C# API.
Benchmarks live in `benchmarks/core/` (`run.sh`, `fetch-corpus.sh`): simdjson-data JSON corpus, rebar haystacks,
regex-redux 5M. One protocol for every engine (warm >= 1 s and >= 50 calls, then the median of 15; cold = one call
per process through hyperfine), MB/s and allocated bytes per call, comparators in the same run on the same machine
(System.Text.Json, .NET `Regex` interpreted/Compiled/NonBacktracking, Rust `regex`, `serde_json`, `simd-json`,
simdjson). Never compare across machines. Targets from the plan (for example JSON DOM >= 3x `JsonDocument.Parse`,
typed >= 2x source-generated `JsonSerializer`, regex-redux within 1.3x of Rust) are provisional until checked against
`benchmarks/core/results/baseline-forge.md`. `ALICE_DISABLE_PASSES=core-regex,core-json,core-format` gives the A/B
baseline for each compiler-recognized idiom, and each idiom keeps a runtime fallback with identical semantics.

## Consequences

- Regex and JSON become engines we maintain: a long correctness tail (Unicode folding, UTF-8 boundaries, number
  parsing), bounded by the BCL fallback (Tier B) and STJ as the test oracle.
- `Alice.Runtime.dll` grows. Splitting out `Alice.Core.dll` later is a packaging change: the namespace is already the
  boundary.
- Ruby spellings resolve against .NET names, so a Core API is written once. The cost is that every new Core member's
  name must be a valid `Names.Dotnet` image of its Alice name, or get explicit Prelude sugar.
- Names that collide with BCL types (`JsonDocument`, `HttpRequest`) need an alias in C# files that import both
  namespaces.

## Amendment 1 (2026-11-03): `scoped` span parameters on `JsonWriter`

`WritePropertyName`, `WriteString` (both overloads each) and `WriteRawValue` take `scoped ReadOnlySpan<T>`. Without
it, passing a span over a local (`stackalloc`, a `u8` slice of a local buffer) to a writer reached through
`ref JsonWriter` is CS8350, because the compiler must assume the writer could keep the span. The writer copies the
bytes and never stores the span, so `scoped` states the real contract; callers that compiled before still compile.

## Amendment 2 (2026-11-03): Tier B is Alice's own backtracker; compile-time regex emission deferred to 1.1

- **Tier B runs on `Backtracker.cs`** (`src/Alice.Runtime/Core/Text/Regex/`), a memoizing backtracker over the same
  `RegexNode` AST, not `System.Text.RegularExpressions` with `RegexOptions.Compiled`. Reasons: match-only calls
  allocate 0 bytes on both tiers (copying BCL `Match` groups into `MatchData` allocates on every match), the
  semantics are Ruby/Onigmo on both tiers without a translation to .NET syntax (ASCII `\w \d \s \b`, `^ $` as line
  anchors, `m` = dot-all, named groups turning off unnamed captures), UTF-8 input needs no transcoding, and nothing
  depends on Reflection.Emit (NativeAOT and wasm behave the same as the JIT). The BCL stays the test oracle
  (`RegexParityTests`), not a runtime fallback.
- **Compile-time emission of literal patterns (the C# `switch` DFA above) is deferred to 1.1.** In 1.0 a regex
  literal is parsed and compiled on first use at runtime and kept in the `Prelude.regex` cache
  (`Prelude.Syntax.cs`); the cache stays when emission lands, for `Regex.new` and for patterns over the state limit.
