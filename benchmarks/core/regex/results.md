# Alice Core regex vs System.Text.RegularExpressions

ADR 0036, engine in `src/Alice.Runtime/Core/Text/Regex/`, driver `benchmarks/core/regex/core-driver`, .NET side
`benchmarks/core/baselines/dotnet` (same RESULT protocol, see `../README.md` "Method").

- Machine: the forge, Intel Xeon Platinum 8488C (AVX-512), .NET SDK 10.0.114. The box is shared with other agents
  (load average 7-10 during the run), so read differences under 10 % as noise.
- Date and load at the start and end of the run:
    Model name:                              Intel(R) Xeon(R) Platinum 8488C
    Tue Nov  3 17:35:41 UTC 2026
    17:35:41 up 1 day,  1:33, 21 users,  load average: 8.86, 10.17, 10.31
    17:38:50 up 1 day,  1:36, 22 users,  load average: 6.94, 8.76, 9.73
- Pinning: `taskset -c 9,25` (one physical core). Engines run one after another, never in parallel.
- regex-redux: `WARM_N=3 TIMED=5`, median. rebar counts: warm >= 1 s and >= 50 calls, then the median of 15.
- Inputs: `data/regexredux-input.txt` (fasta 5M, 50,833,411 bytes), rebar `en-sampled` and `sherlock`
  (`fetch-corpus.sh`). .NET runs on `string` (UTF-16), so Alice UTF-16 is the like-for-like column. Alice UTF-8
  searches the bytes without transcoding.
- Commands: `core-regex-bench regex-redux|rebar <file>` (`CASES=a,b` selects rebar cases),
  `core-baselines regex-redux <file> compiled|nonbacktracking`, `core-baselines rebar <file>` (run with `dotnet <dll>`).

## Separate processes (the suite)

| Case | Input | Alice UTF-8 ms | Alice UTF-16 ms | .NET Compiled ms | .NET NonBacktracking ms | Compiled / Alice UTF-16 | Compiled / Alice UTF-8 | Alice B/call (UTF-8, UTF-16) | Checksum |
|---|---|---|---|---|---|---|---|---|---|
| regex-redux | fasta-5M | 551 | 829 | 3981 | 4897 | 4.80x | 7.22x | 3824, 1163257384 | 20941027388361 |
| count:literal | rebar-en-sampled | 0.0272 | 0.0543 | 0.0604 | 0.0946 | 1.11x | 2.22x | 0, 0 | 513 |
| count:literal-alt | rebar-en-sampled | 0.0993 | 0.0864 | 0.2941 | 0.4425 | 3.40x | 2.96x | 0, 0 | 714 |
| count:literal-i | rebar-en-sampled | 0.0535 | 0.0656 | 0.0606 | 2.6 | 0.92x | 1.13x | 0, 0 | 522 |
| count:words | rebar-en-sampled | 5.6 | 5.8 | 7.4 | 15.1 | 1.28x | 1.32x | 0, 0 | 175218 / .NET 175191 |
| count:ing-suffix | rebar-en-sampled | 1.8 | 1.9 | 6.5 | 6.5 | 3.36x | 3.50x | 0, 0 | 4808 |
| count:aws-keys | rebar-en-sampled | 0.0222 | 0.0418 | 0.0451 | 0.0470 | 1.08x | 2.03x | 0, 0 | 0 |
| count:bounded-repeat | rebar-en-sampled | 2.2 | 2.2 | 9.2 | 7.4 | 4.12x | 4.23x | 0, 0 | 11434 |
| count:unicode-upper | rebar-en-sampled | 1.9 | 2.0 | 1.8 | 4.2 | 0.88x | 0.92x | 0, 0 | 33237 |
| count:time | rebar-en-sampled | 0.0219 | 0.0384 | 0.0475 | 0.0424 | 1.24x | 2.17x | 0, 0 | 37 |
| count:literal | rebar-sherlock | 0.0152 | 0.0285 | 0.0272 | 0.0338 | 0.95x | 1.79x | 0, 0 | 91 |
| count:literal-alt | rebar-sherlock | 0.0498 | 0.0450 | 0.0959 | 0.1615 | 2.13x | 1.93x | 0, 0 | 105 |
| count:literal-i | rebar-sherlock | 0.0264 | 0.0322 | 0.0275 | 1.7 | 0.85x | 1.04x | 0, 0 | 96 |
| count:words | rebar-sherlock | 3.7 | 3.7 | 4.5 | 9.6 | 1.23x | 1.24x | 0, 0 | 109222 / .NET 109214 |
| count:ing-suffix | rebar-sherlock | 1.2 | 1.3 | 4.1 | 4.0 | 3.15x | 3.36x | 0, 0 | 2824 |
| count:aws-keys | rebar-sherlock | 0.0141 | 0.0255 | 0.0199 | 0.0223 | 0.78x | 1.41x | 0, 0 | 0 |
| count:bounded-repeat | rebar-sherlock | 1.5 | 1.6 | 6.0 | 4.8 | 3.69x | 4.01x | 0, 0 | 9401 |
| count:unicode-upper | rebar-sherlock | 0.6627 | 0.6669 | 0.5792 | 1.6 | 0.87x | 0.87x | 0, 0 | 9451 |
| count:time | rebar-sherlock | 0.0126 | 0.0167 | 0.0224 | 0.0186 | 1.34x | 1.78x | 0, 0 | 8 |

Checksums: every engine prints 50833411 / 50000000 / 27388361 for regex-redux. `words` differs on purpose: Alice
follows Ruby (`\w` and `\b` are ASCII), while .NET's `\w` is Unicode. Allocation: 0 bytes per call on every count.
regex-redux on UTF-8 allocates 3.8 KB per call (the report text). On UTF-16 it allocates the replaced strings, as
.NET does.

## Same process, alternating (the close rows)

Alice and .NET Compiled are run on the same haystack in one process: 1.5 s warm-up each, then the best of 7 x 200
calls. This takes the machine-load noise out of rows that are within 15 %. Harness: a scratch console app (not
committed); it calls `Regex.Count` exactly as the driver does.

| Input | Pattern | Alice UTF-8 ms | Alice UTF-16 ms | .NET Compiled ms | Compiled / Alice UTF-16 |
|---|---|---|---|---|---|
| en-sampled | `Sherlock Holmes` | 0.0324 | 0.0631 | 0.0601 | 0.95x |
| en-sampled | `(?i)Sherlock Holmes` | 0.0621 | 0.0632 | 0.0641 | 1.01x |
| en-sampled | `(?:AKIA|ASIA)[A-Z0-9]{16}` | 0.0256 | 0.0472 | 0.0464 | 0.98x |
| en-sampled | `\p{Lu}\p{Ll}+` | 1.9669 | 1.9896 | 1.8390 | 0.92x |
| en-sampled | `[0-9]{1,2}:[0-9]{2}` | 0.0226 | 0.0398 | 0.0496 | 1.25x |
| sherlock | `Sherlock Holmes` | 0.0171 | 0.0331 | 0.0316 | 0.95x |
| sherlock | `(?i)Sherlock Holmes` | 0.0282 | 0.0333 | 0.0330 | 0.99x |
| sherlock | `(?:AKIA|ASIA)[A-Z0-9]{16}` | 0.0165 | 0.0268 | 0.0230 | 0.86x |
| sherlock | `\p{Lu}\p{Ll}+` | 0.6413 | 0.6321 | 0.6116 | 0.97x |
| sherlock | `[0-9]{1,2}:[0-9]{2}` | 0.0117 | 0.0178 | 0.0221 | 1.24x |

## Reading

- **Faster than Compiled on every row with UTF-8**, except `\p{Lu}\p{Ll}+` (0.87-0.92x). With UTF-16: regex-redux
  4.8x, literal alternations 2.1-3.4x, `[a-zA-Z]+ing` 3.2-3.4x, bounded repeats 3.7-4.1x, `\b\w+\b` 1.2-1.3x, `time`
  1.2-1.3x.
- **UTF-16 rows that are not faster, and why:**
  - `literal` (1.11x on en-sampled in separate processes, 0.95x on sherlock and in one process): both engines
    call the same `IndexOf(string)`. The 5 % is Alice's
    per-match cost around that call (prefilter dispatch and the find loop, 91-513 matches).
  - `literal-i` (0.85-0.92x in separate processes, 0.99-1.01x in one process): both are a packed `OrdinalIgnoreCase`
    search of an ASCII run followed by a check of the whole literal. Alice checks with Unicode simple case folding
    (`ſ`, Kelvin `K`). The gap between processes is load noise.
  - `aws-keys` (0.78-1.08x; 0.86-0.98x in one process): no matches, so this measures the scan alone. Alice packs 64
    UTF-16 units to bytes per step and compares the two rarest offsets. That is the same amount of work as a
    two-character `IndexOf`, but the remaining 2-14 % is still unexplained.
  - `unicode-upper` (0.87-0.97x): a match is 6 units every 27 units. The lazy DFA spends each unit on a load that
    depends on the previous state, and pays a restart per match. .NET's compiled loop tests `\p{Ll}` per character
    with no dependency between steps. The DFA's start-state skip (vectorized search for the units that leave the
    start state) cut this row from 3.5 ms to 1.9 ms. Matching the remaining gap needs a compiled matcher, which is
    the deferred compile-time emission (ADR 0036 Amendment 2).
- Against NonBacktracking (the BCL automaton engine), Alice is faster on every row except `aws-keys` on sherlock
  with UTF-16 (0.87x).

## What changed since the first measurement (same day, before tuning)

The first run had Compiled / Alice UTF-16 at 0.36x / 0.42x for `literal-i`, 0.50x / 0.36x for `unicode-upper`, 0.87x /
0.62x for `aws-keys`, 0.93x / 0.89x for `words` and 0.80x / 0.53x for `time`. The changes:

- `Count` reads only the forward DFA when a match can never be empty (`Engine.EndOnly`): no reverse DFA per match.
- The lazy DFA skips its unanchored start state with a vectorized search (`LazyDfa.AccelFor`). Its stride is a
  power of two, so finding a state's index is a shift, not a division.
- Prefilters are scored by selectivity capped at 16 bits plus scan speed, not by selectivity alone.
- New prefilters: a two-offset scan (`OffsetScan`: the rarest units, never adjacent ones, 64 bytes per step, UTF-16
  packed with `vpackuswb`); ASCII-caseless runs over UTF-16 by packed `SearchValues` with an ASCII re-check; an exact
  UTF-16 search of a whole caseless literal with simple case folding (`CaselessLiteralChars`); first-unit search
  for UTF-16.
