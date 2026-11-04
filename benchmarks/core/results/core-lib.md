# Alice Core library modules: forge run 1 (2026-11-03)

## Re-run on 1.0.1 (2026-11-04, commit b2e41c8 (v1.0.1))

`taskset -c 9,25 dotnet lib/bin/Release/net10.0/core-lib-bench.dll data` (every module), default protocol. Raw: [`1.0.1/lib.txt`](1.0.1/lib.txt). Machine:
the forge, AWS c7i.8xlarge, Intel Xeon Platinum 8488C (Sapphire Rapids, AVX-512), 32 vCPU, 61 GiB, Arch Linux (kernel 7.2.11), .NET SDK 10.0.114 (runtime 10.0.14); shared box, load average 13.6-14.3. Each engine's checksum is checked against its own first call on every
timed call. Core and comparator checksums agree except tr, squeeze and delete: Core works on the UTF-8 bytes and the
Prelude on the UTF-16 string (the input has some non-ASCII words), so lengths and the probed element differ.

| engine | case | input | median ms | MB/s | alloc B/call | checksum |
|---|---|---|---:|---:|---:|---|
| alice-core | count:a-m | words-1MB | 0.0901 | 11900.0 | 0 | 424531 |
| prelude | count:a-m | words-1MB | 10.2382 | 104.7 | 848 | 424531 |
| alice-core | tr:a-y/b-z | words-1MB | 0.0820 | 13075.5 | 0 | 98 |
| prelude | tr:a-y/b-z | words-1MB | 8.4083 | 127.5 | 4194976 | 119 |
| alice-core | squeeze | words-1MB | 0.8334 | 1286.5 | 0 | 989598 |
| prelude | squeeze | words-1MB | 3.5894 | 298.7 | 4029232 | 965989 |
| alice-core | delete:aeiou | words-1MB | 0.7722 | 1388.5 | 0 | 765681 |
| prelude | delete:aeiou | words-1MB | 11.7255 | 91.4 | 1484816 | 742072 |
| alice-core | char-count | words-1MB | 0.0216 | 49638.4 | 0 | 1048580 |
| bcl | char-count | words-1MB | 0.2996 | 3578.7 | 0 | 1048580 |
| alice-core | format:5-specs-x1000 | - | 0.3561 | 2.8 | 0 | 38508 |
| prelude | format:5-specs-x1000 | - | 2.3763 | 0.4 | 1859648 | 39508 |
| alice-core | strftime-x1000 | - | 0.1320 | 7.6 | 0 | 29000 |
| bcl | strftime-x1000 | - | 0.1145 | 8.7 | 0 | 29000 |
| alice-core | crc32c | random-100MB | 9.8062 | 10693.0 | 0 | 2562150259 |
| alice-core | xxh3-64 | random-100MB | 12.8963 | 8130.8 | 0 | 1331740475 |
| alice-core | sha256 | random-100MB | 66.4822 | 1577.2 | 0 | 217 |
| bcl | sha256 | random-100MB | 66.5407 | 1575.8 | 0 | 217 |
| alice-core | xxh3-64-x1000 | 64B | 0.0205 | 3122.0 | 0 | 1417958080 |
| alice-core | csv-rows-fields | csv-100MB | 61.4216 | 1628.1 | 0 | 88363068 |
| alice-core | csv-rows-typed | csv-100MB | 44.1746 | 2263.7 | 0 | 960412131953 |
| alice-core | csv-count-rows | csv-100MB | 33.8952 | 2950.3 | 0 | 1385440 |
| alice-core | each-line | csv-100MB | 32.4802 | 3078.8 | 304 | 100000086 |
| bcl | each-line:File.ReadLines | csv-100MB | 106.4724 | 939.2 | 231426064 | 99723273 |
| alice-core | count-lines:mapped | csv-100MB | 8.5557 | 11688.1 | 1592 | 1385440 |
| alice-core | hmac-sha256-x1000 | 64B | 1.1013 | 58.1 | 0 | 121549 |
| bcl | hmac-sha256-x1000 | 64B | 1.0925 | 58.6 | 0 | 121549 |
| alice-core | deque-fifo-1M | long | 3.3522 | 2386.5 | 8388864 | 499999500000 |
| bcl | queue-fifo-1M | long | 6.2851 | 1272.9 | 8388904 | 499999500000 |
| alice-core | deque-both-ends-1M | long | 6.1621 | 1298.3 | 16777528 | 499999500000 |
| bcl | linkedlist-both-ends-1M | long | 61.2690 | 130.6 | 48000040 | 499999500000 |
| alice-core | set-add-contains-1M | long | 19.4271 | 411.8 | 25983272 | 800018 |
| bcl | hashset-add-contains-1M | long | 18.7169 | 427.4 | 25983248 | 800018 |
| alice-core | toml-parse | 5000-packages | 23.4596 | 42.5 | 24298642 | 2 |
| alice-core | toml-roundtrip | 5000-packages | 38.5970 | 25.8 | 35833204 | 1001966 |
| alice-core | echo-256conns-64B | loopback | 562.3654 | 11.7 | 0.000B/msg | 51200 |
| bcl-socket | echo-256conns-64B | loopback | 558.4208 | 11.7 | 0.000B/msg | 51200 |

## Previous runs

The sections below are the earlier runs, kept as published.

`benchmarks/core/lib` (`core-lib-bench`), `taskset -c 3,19`, default protocol (warm >= 1 s and >= 50 calls, median of
15; 100 MB cases warm >= 5 calls). Raw output: `results/raw/core-lib-run1.txt` (not committed). The box was shared:
load average 12-29 during the run, so read differences under ~10 % as noise. MB/s is input bytes per second; alloc
is bytes per call on the calling thread (echo: process-wide bytes per message over the least of 7 windows).

| Case | Input | Alice Core | Comparator | Ratio | Alice alloc/call | Target (stdlib plan) |
|---|---|---|---|---|---|---|
| `count("a-m")` | 1 MB words | 0.090 ms (11.9 GB/s) | Prelude `count` 10.05 ms | 111x | 0 | >= 4x: met |
| `tr("a-y", "b-z")` | 1 MB words | 0.075 ms (14.3 GB/s) | Prelude `tr` 10.34 ms | 138x | 0 | >= 4x: met |
| `squeeze` | 1 MB words | 1.26 ms (851 MB/s) | Prelude `squeeze` 4.51 ms | 3.6x (4.3x at load 8, 0.78 ms) | 0 | >= 4x: met at load 8, 3.6x at load 29 |
| `delete("aeiou")` | 1 MB words | 1.28 ms | Prelude `delete` 12.11 ms | 9.5x | 0 | - |
| character count | 1 MB words | 0.022 ms (48 GB/s) | `Encoding.UTF8.GetCharCount` 0.41 ms | 18x | 0 | - |
| `format`, 5 specs x 1000 | - | 0.345 ms | Prelude `format` 1.89 ms (4.37 KB/call) | 5.5x | 0 | 0 bytes into a span: met |
| `strftime` x 1000 | - | 0.130 ms | `DateTimeOffset.TryFormat` 0.122 ms | 0.94x | 0 | 0 bytes into a span: met |
| CRC-32C | 100 MB random | 8.6 GB/s (load 29); 10.3, 10.7, 11.2 GB/s in three reruns (load 17-19) | - | - | 0 | >= 10 GB/s: met at load <= 19 |
| XXH3-64 | 100 MB random | 8.9 GB/s (7.3-8.1 in reruns) | - | - | 0 | - |
| SHA-256 (`Digests.Sha256`) | 100 MB random | 66.7 ms | `SHA256.HashData` 67.4 ms | 1.01x | 0 | within 1.05x: met |
| CSV rows, every field touched | 100 MB, 1.39 M rows x 8 | 62.6 ms (1.60 GB/s) | - | - | 0 | >= 1.5 GB/s, 0 bytes/row: met |
| CSV rows, two typed fields | 100 MB | 44.4 ms (2.25 GB/s) | - | - | 0 | - |
| CSV row count | 100 MB | 32.6 ms (3.06 GB/s) | - | - | 0 | - |
| `Fs.each_line` | 100 MB file | 32.3 ms (3.09 GB/s) | `File.ReadLines` 104.1 ms (231 MB/call) | 3.2x | 288 per file, 0 per line | 0 bytes/line: met |
| `Fs.count_lines` (mapped) | 100 MB file | 7.5 ms (13.3 GB/s) | - | - | 1544 per file | - |
| echo, 256 connections, 64 B | loopback, 51,200 msgs | 566 ms | raw `Socket` echo 559 ms | 1.01x | 0.000 B/msg | 0 bytes/msg, within 1.1x of raw `Socket`: met |

Not measured here: the HTTP client against `HttpClient` (it is a wrapper over the same handler),
UDP/Unix sockets.

Collections, HMAC and TOML were added after run 1 (`core-lib-bench collections|hmac|toml`, `taskset -c 15,31
ALICE_PAR=0`, 2026-11-03, load average 30 on the shared box: read differences under ~15 % as noise):

| Case | Input | Alice Core | Comparator | Ratio | Alloc/call | Target |
|---|---|---|---|---|---|---|
| Deque FIFO (push, shift) | 1 M `long` | 4.40 ms | `Queue<long>` 6.76 ms | 1.54x | 8.4 MB (ring growth) vs 8.4 MB | - |
| Deque both ends (push, unshift, pop, shift) | 1 M `long` | 6.77 ms | `LinkedList<long>` 59.0 ms | 8.7x | 16.8 MB vs 48.0 MB | - |
| `Set` add + include | 1 M `long` | 19.3 ms | `HashSet<long>` 19.1 ms | 0.99x (wrapper) | 26.0 MB, equal | parity: met |
| HMAC-SHA256 x 1000 | 64 B | 1.112 ms | `HMACSHA256.HashData` 1.131 ms | 1.02x (wrapper) | 0 | parity: met |
| TOML parse | 5000 `[[package]]` tables, 1.0 MB | 22.9 ms (43.5 MB/s) | none in the BCL | - | 24.3 MB (tree of `TomlAny`) | toml-test 100 % |
| TOML parse + write | same | 40.3 ms (24.8 MB/s) | none in the BCL | - | 35.8 MB | - |

`Set` and `Hmac` are thin wrappers over `HashSet<T>` and `HMACSHA256`, so parity is the target; `Deque` is an owned
power-of-two ring and beats `Queue` and `LinkedList`. `Toml` has no BCL comparator (toml-test is its correctness gate);
the row records the baseline for later changes. Not measured: HTTP client, UDP/Unix sockets.
