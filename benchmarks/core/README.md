# Alice Core benchmarks (ADR 0036)

Regex and JSON: the Alice Core engines against the .NET BCL, Rust and simdjson, all measured in the same run on the
same machine. Baselines (no Alice engine yet): [`results/baseline-forge.md`](results/baseline-forge.md).

## Run

```bash
benchmarks/core/fetch-corpus.sh          # once: JSON corpus, rebar haystacks, regex-redux input (data/, gitignored)
benchmarks/core/run.sh build             # build comparators + every regex/*.al and json/*.al, then run everything
benchmarks/core/run.sh json              # or one suite: json | regex | cold
```

Needs `dotnet` (10), `cargo`, `clang++`, `cc`, `hyperfine`, `curl`, `git`. No cmake: simdjson is built from its
single-header amalgamation. Output: `results/raw/<stamp>.txt` (RESULT lines), `results/raw/cold-*.json` (hyperfine),
`results/latest.md` (table).

| Path | Content |
|---|---|
| `fetch-corpus.sh` | `twitter`, `canada`, `citm_catalog`, `github_events`, `gsoc-2018`, `mesh` from simdjson-data (pinned commit); rebar `opensubtitles/en-sampled.txt` and `sherlock.txt` (pinned); `fasta 5000000` (50,833,411 bytes) |
| `tools/fasta.c` | Benchmarks Game fasta (reference LCG), generates the regex-redux input |
| `tools/baseline-tables.py` | pivots a RESULT file into the tables of `results/baseline-forge.md` |
| `baselines/dotnet/` | System.Text.Json (`JsonDocument`, `JsonNode`, `Utf8JsonReader`, source-generated `JsonSerializer`), .NET `Regex` (interpreted = what `Prelude.regex` uses today, `Compiled`, `NonBacktracking`) |
| `baselines/rust/` | `regex` (bytes API), `serde_json` (`Value`, typed), `simd-json` (borrowed value, typed) |
| `baselines/simdjson/` | simdjson DOM parse and On-Demand full walk (`build.sh` clones the pinned tag) |
| `regex/*.al`, `json/*.al` | Alice benchmarks (none yet): built by `run.sh build`, run as `dotnet <dll> data/`, same RESULT protocol |

## Method

- Protocol (every driver): `RESULT <engine> <case> <input> <median_ms> <MB_per_s> <alloc_bytes_per_call|-> <checksum>`.
- Warm: one checked call, then warm-up for >= `WARM_MS` (1000) and >= `WARM_N` (50) calls so .NET reaches tier-1 +
  PGO code, then `TIMED` (15) calls, median reported. regex-redux (seconds per call) uses `WARM_N=3 TIMED=5`.
- Cold: `ONCE=1` runs one call without warm-up; `run.sh cold` measures whole processes with hyperfine (runtime start
  and tier-0 JIT included).
- Checksums: every timed call must return the reference checksum (no dead-code elimination). Engines must agree:
  regex match counts are equal across .NET and Rust on every case; JSON typed checksums are equal across STJ, serde
  and simd-json; regex-redux prints 50833411 / 50000000 / 27388361.
- Allocation: `GC.GetAllocatedBytesForCurrentThread` delta, mean over the timed calls (.NET only; `-` elsewhere).
- MB/s is input bytes (UTF-8 file size) per second for parse and search, output bytes for serialize.
- Pinning: `taskset -c 14,30` (both hyperthreads of one physical core of the forge). Never one logical CPU: with
  `ProcessorCount == 1` .NET runs `JsonDocument.Parse` about 3.4x slower (measured on the forge, see the baseline).
- Typed schemas are partial (the same fields in `baselines/dotnet/Typed.cs` and `baselines/rust/src/typed.rs`;
  unknown fields skipped by every serializer).
- regex-redux is single-threaded here (engine comparison), unlike the multi-threaded Benchmarks Game entries.
- The rebar-like cases are counts of non-overlapping matches over one haystack, not rebar's KLV harness (the rebar
  runner is task C1.x).

## Machine

The forge: AWS c7i.8xlarge, Intel Xeon Platinum 8488C (Sapphire Rapids, AVX-512), 32 vCPU (16 cores x 2), 61 GiB,
Arch Linux, kernel 7.2.11, .NET SDK 10.0.114, rustc 1.99.1, clang 23.1.4. The box is shared with other build agents:
read differences below about 10 % as noise. Never compare numbers across machines.

## Cases

| Suite | Case | Inputs | Engines |
|---|---|---|---|
| json | `json-dom` (parse into a DOM) | 6 corpus files | STJ `JsonDocument`, `JsonNode`; serde_json `Value`; simd-json borrowed; simdjson DOM |
| json | `json-walk` (visit every token) | 6 corpus files | STJ `Utf8JsonReader`; simdjson On-Demand |
| json | `json-typed` (deserialize into records) | twitter, canada, citm_catalog | STJ source-gen; serde_json; simd-json serde |
| json | `json-serialize` (records to UTF-8) | twitter, canada, citm_catalog | STJ source-gen (into a reused `ArrayBufferWriter`) |
| regex | `regex-redux` | fasta 5M | .NET interpreted, Compiled, NonBacktracking; Rust `regex` |
| regex | `count:<case>` (literal, literal-alt, literal-i, words, ing-suffix, aws-keys, bounded-repeat, unicode-upper, time) | rebar en-sampled, sherlock | same |

## Library modules

`lib/` (`core-lib-bench`, same RESULT protocol) measures the other Core modules against the BCL and the current
Prelude: `cd benchmarks/core && dotnet build lib -c Release && taskset -c 3,19 dotnet
lib/bin/Release/net10.0/core-lib-bench.dll data [text format hash csv lines echo collections toml hmac]` (the 100 MB CSV is generated into
`data/` on first use). Results: [`results/core-lib.md`](results/core-lib.md).

| Suite | Case | Input | Comparator | Target |
|---|---|---|---|---|
| text | `count tr squeeze delete` with a set, character count | 1 MB words (some non-ASCII) | Prelude String methods; `Encoding.UTF8` | >= 4x the Prelude, 0 bytes |
| format | `Format.try_format` 5 specs, `Time#try_strftime` | x 1000 | Prelude `format`; `DateTimeOffset.TryFormat` | 0 bytes into a span |
| collections | `Deque` FIFO and both ends, `Set` add/include | 1 M `long` | `Queue`, `LinkedList`, `HashSet` | Deque faster, Set parity |
| hmac | `Hmac.Sha256` x 1000 | 64 B | `HMACSHA256.HashData` | parity |
| toml | parse, parse + write | 5000 `[[package]]` tables | none in the BCL | baseline only; toml-test is the gate |
| hash | CRC-32C, XXH3-64, SHA-256 | 100 MB random | `SHA256.HashData` | CRC-32C >= 10 GB/s; SHA-256 within 1.05x |
| csv | rows with every field, two typed fields, row count | 100 MB generated CSV | - | >= 1.5 GB/s, 0 bytes per row |
| lines | `Fs.each_line`, `Fs.count_lines` | the same file | `File.ReadLines` | 0 bytes per line |
| echo | 256 connections x 64 B messages, `TcpServer.serve_async` | loopback | raw `Socket` accept loop with the same handler | 0 bytes per message, within 1.1x |
