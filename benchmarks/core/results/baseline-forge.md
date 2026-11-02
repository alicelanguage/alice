# Alice Core baselines on the forge (2026-11-02)

Comparators only: no Alice Core engine exists yet (ADR 0036). These are the numbers the stream-C targets are checked
against. Same machine, same run, same protocol for every engine (`README.md`, "Method"). Raw RESULT lines:
`baseline-forge.raw.txt` (run `20261102T165707Z`).

## Machine and toolchain

| | |
|---|---|
| Host | the forge, AWS c7i.8xlarge: Intel Xeon Platinum 8488C (Sapphire Rapids), 1 socket, 16 cores x 2 threads, L2 32 MiB, L3 105 MiB, AVX-512 |
| Memory | 64,766,008 kB (`/proc/meminfo`) |
| OS | Arch Linux, kernel 7.2.11-arch1-1 |
| .NET | SDK 10.0.114, workstation GC, TieredPGO on, `InvariantGlobalization` |
| Rust | rustc 1.99.1, `-C target-cpu=native`, fat LTO, codegen-units 1; regex 1.13.1 (regex-automata 0.4.18), serde_json 1.0.154, simd-json 0.14.3 |
| C++ | clang 23.1.4, `-O3 -march=native`; simdjson 3.10.1 (single header), active implementation `icelake` (AVX-512) |
| Pinning | `taskset -c 14,30` (both hyperthreads of physical core 14) |
| Load | shared box: 21-50 interactive users and a load average of 7-13 from other agents during the run |

Noise: Rust regex-redux measured 829, 901, 949, 973 and 1284 ms in five runs on the same day (different CPU sets and
load). Differences under about 10-15 % are not significant on this box.

## JSON

Corpus: simdjson-data `jsonexamples` at `4197c425`. GB/s = input bytes / median time.

| DOM parse, GB/s | stj/JsonDocument | stj/JsonNode | serde_json/value | simd-json/borrowed | simdjson/dom |
|---|---:|---:|---:|---:|---:|
| json-dom twitter | 0.730 | 0.663 | 0.287 | 0.522 | 4.639 |
| json-dom canada | 0.325 | 0.290 | 0.267 | 0.292 | 1.017 |
| json-dom citm_catalog | 0.456 | 0.407 | 0.520 | 0.634 | 4.439 |
| json-dom github_events | 0.793 | 0.735 | 0.310 | 0.756 | 4.534 |
| json-dom gsoc-2018 | 0.904 | 0.803 | 0.818 | 0.961 | 3.844 |
| json-dom mesh | 0.308 | 0.280 | 0.318 | 0.297 | 0.793 |

| Full token walk, GB/s | stj/Utf8JsonReader | simdjson/ondemand-walk |
|---|---:|---:|
| json-walk twitter | 0.949 | 1.613 |
| json-walk canada | 0.480 | 0.404 |
| json-walk citm_catalog | 0.499 | 1.610 |
| json-walk github_events | 1.182 | 1.501 |
| json-walk gsoc-2018 | 0.901 | 3.163 |
| json-walk mesh | 0.357 | 0.336 |

| Typed, GB/s | stj/sourcegen | serde_json/typed | simd-json/typed |
|---|---:|---:|---:|
| json-typed twitter | 0.307 | 0.564 | 0.624 |
| json-typed canada | 0.150 | 0.734 | 0.430 |
| json-typed citm_catalog | 0.359 | 0.875 | 0.748 |
| json-serialize twitter | 0.743 |  |  |
| json-serialize canada | 0.154 |  |  |
| json-serialize citm_catalog | 0.990 |  |  |

Allocated bytes per call (.NET): json-dom twitter stj/JsonDocument 72, json-dom twitter stj/JsonNode 987016, json-dom canada stj/JsonDocument 72, json-dom canada stj/JsonNode 4930464, json-dom citm_catalog stj/JsonDocument 72, json-dom citm_catalog stj/JsonNode 2750056, json-typed twitter stj/sourcegen 356392, json-serialize twitter stj/sourcegen 0, json-typed canada stj/sourcegen 8484014, json-serialize canada stj/sourcegen 0, json-typed citm_catalog stj/sourcegen 1083280, json-serialize citm_catalog stj/sourcegen 0.

Typed schemas are partial and identical across STJ and serde (`baselines/dotnet/Typed.cs`, `baselines/rust/src/typed.rs`);
checksums agree across all three typed engines. Serialize: STJ source-generated `JsonSerializer.Serialize` into a
reused `Utf8JsonWriter`/`ArrayBufferWriter` (GB/s of output); no Rust serialize comparator yet.

## Regex

Match counts agree across all engines on every case. GB/s uses the UTF-8 size of the haystack for every engine; .NET
searches the UTF-16 string (decoded once, outside the timing), so it touches twice the bytes.

| regex-redux, ms (single-threaded) | dotnet/interpreted | dotnet/compiled | dotnet/nonbacktracking | rust/regex |
|---|---:|---:|---:|---:|
| regex-redux fasta-5M | 15657 | 4068 | 5462 | 1284 |

| rebar-en-sampled, GB/s | dotnet/interpreted | dotnet/compiled | dotnet/nonbacktracking | rust/regex |
|---|---:|---:|---:|---:|
| count:literal rebar-en-sampled | 7.469 | 10.300 | 9.280 | 30.720 |
| count:literal-alt rebar-en-sampled | 0.515 | 2.733 | 1.757 | 9.410 |
| count:literal-i rebar-en-sampled | 0.306 | 6.387 | 0.322 | 7.847 |
| count:words rebar-en-sampled | 0.063 | 0.112 | 0.055 | 0.135 |
| count:ing-suffix rebar-en-sampled | 0.045 | 0.127 | 0.129 | 0.496 |
| count:aws-keys rebar-en-sampled | 4.514 | 20.864 | 18.734 | 15.423 |
| count:bounded-repeat rebar-en-sampled | 0.037 | 0.090 | 0.113 | 0.442 |
| count:unicode-upper rebar-en-sampled | 0.186 | 0.462 | 0.199 | 0.308 |
| count:time rebar-en-sampled | 7.569 | 18.931 | 22.651 | 68.607 |

| rebar-sherlock, GB/s | dotnet/interpreted | dotnet/compiled | dotnet/nonbacktracking | rust/regex |
|---|---:|---:|---:|---:|
| count:literal rebar-sherlock | 15.781 | 17.812 | 11.332 | 18.213 |
| count:literal-alt rebar-sherlock | 1.005 | 5.759 | 3.718 | 10.210 |
| count:literal-i rebar-sherlock | 0.314 | 17.706 | 0.321 | 6.678 |
| count:words rebar-sherlock | 0.067 | 0.120 | 0.058 | 0.107 |
| count:ing-suffix rebar-sherlock | 0.048 | 0.132 | 0.139 | 0.476 |
| count:aws-keys rebar-sherlock | 7.579 | 24.383 | 22.707 | 28.914 |
| count:bounded-repeat rebar-sherlock | 0.046 | 0.091 | 0.114 | 0.405 |
| count:unicode-upper rebar-sherlock | 0.172 | 0.959 | 0.190 | 0.403 |
| count:time rebar-sherlock | 14.233 | 25.867 | 28.330 | 57.415 |

regex-redux prints `50833411 / 50000000 / 27388361` for every engine. Patterns: `literal` = `Sherlock Holmes`,
`literal-alt` = 5 names, `literal-i` = `(?i)Sherlock Holmes`, `words` = `\b\w+\b`, `ing-suffix` = `[a-zA-Z]+ing`,
`aws-keys` = `(?:AKIA|ASIA)[A-Z0-9]{16}`, `bounded-repeat` = `[A-Za-z]{8,13}`, `unicode-upper` = `\p{Lu}\p{Ll}+`,
`time` = `[0-9]{1,2}:[0-9]{2}`. All counts allocate 0 bytes on .NET (`Regex.Count(ReadOnlySpan<char>)`); regex-redux
allocates ~555 MB per call on .NET (the `Replace` strings over a 50 MB input).

## Cold (one call per process, hyperfine, 7 runs)

| command | mean ms | min ms |
|---|---:|---:|
| STJ `JsonDocument` + `JsonNode` + `Utf8JsonReader` twitter (one process) | 61.1 | 57.3 |
| serde_json + simd-json DOM twitter (both in one process) | 11.0 | 10.4 |
| simdjson DOM + On-Demand twitter (both in one process) | 4.3 | 3.8 |
| STJ source-gen typed + serialize twitter | 132.6 | 116.3 |
| serde_json + simd-json typed twitter | 7.6 | 6.8 |
| .NET `Compiled` regex-redux | 8771.4 | 8426.0 |
| .NET `NonBacktracking` regex-redux | 10954.9 | 10496.5 |
| Rust `regex` regex-redux | 1793.3 | 1693.9 |

Each cold process runs every engine of its driver mode once (`ONCE=1`), so most rows include two or three parses. .NET cold time is dominated by runtime start and tier-0 JIT (twitter parse warm: 0.87 ms).

## Findings for the targets

- simdjson DOM is 6.4x `JsonDocument.Parse` on twitter (4.64 vs 0.73 GB/s), 9.7x on citm_catalog, 3.1x on canada
  (numbers). The plan's ">= 3x JsonDocument" DOM target is reachable only with a simdjson-class stage 1.
- Rust's DOM parsers are *not* faster than `JsonDocument` on this box (serde_json `Value` 0.29 GB/s on twitter;
  `JsonDocument` builds an index, not a tree).
- Typed: source-generated STJ is 1.8x (twitter) to 4.9x (canada) slower than serde_json and allocates 8.5 MB per
  canada parse (`List<List<double[]>>`).
- Regex: `Compiled` is 3.8x the interpreter (what `Prelude.regex` uses today) on regex-redux; Rust is 3.2x
  `Compiled` (4068 vs 1284 ms in this run, 829-973 ms in re-runs). `Compiled` wins on case-insensitive literals
  (sherlock `literal-i` 17.7 vs 6.7 GB/s) and `unicode-upper`; Rust wins 3.5-5x on `ing-suffix` and
  `bounded-repeat` (likely DFA vs backtracking; not profiled) and 1.8-3.4x on literal alternation. `NonBacktracking` is the best .NET engine
  only on `bounded-repeat`, `time` and (within noise) `ing-suffix`.
- **.NET with `ProcessorCount == 1` is slow**: pinned to one logical CPU (or `DOTNET_PROCESSOR_COUNT=1`),
  `JsonDocument.Parse(twitter)` takes 3.08 ms and allocates 152 B per call, vs 0.81-0.90 ms and 72 B with 2 or more
  CPUs (`DOTNET_TieredCompilation=0` and `TieredPGO=0` do not change it). The first run of this suite, pinned with
  `taskset -c 30`, measured canada typed at 48 ms instead of 15 ms. `run.sh` therefore pins to a hyperthread pair.
  `benchmarks/bend-vs/run.sh` runs its `alice/1` and `csharp/1` rows with `DOTNET_PROCESSOR_COUNT=1`.

## Not measured yet

- rebar's own harness (KLV runner) and its curated set; the cases above are rebar-like counts on rebar haystacks.
- RE2, PCRE2-JIT, .NET `[GeneratedRegex]` (source generator).
- Rust serialize (`serde_json::to_writer`) and simdjson typed/On-Demand typed mapping.
- simdjson's own `benchmark` binary (needs cmake, not installed); our driver links the amalgamation instead.
- Allocated bytes for Rust and C++ (no counting allocator).
