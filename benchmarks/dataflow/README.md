# Dataflow benchmark (ADR 0038, P3.1)

Three stages over ~1 GiB of synthetic text: **generate lines -> tokenize -> count**.

- Text: 63 MiB of lowercase words (1-10 letters, 4-16 words per line), deterministic xorshift, read 17 times (1.06 GiB).
- Edge 1 carries `Line(start, length)` structs (17.6 M); edge 2 carries one `uint` FNV-1a hash per word (175.5 M).
- Count: 4096 bins plus a running sum. Every variant must print the same token count and checksum (`ok`).
- "items/s" = (lines + tokens) / s. "tokens/s" is the busy edge.

| Variant | What it is |
|---|---|
| `serial` | one loop, no handoff (the work itself) |
| `dataflow` | `Graph`: `Source` -> `Stage` (tokenize, 1:n) -> `Sink`, rings of 16384, batch 512 |
| `dataflow-par` | same, tokenize as `Replicate(workers: cores / 2, ordered: true, batch: 1024)` |
| `channels-naive` | 3 x `Task.Run`, bounded `Channel<T>` (16384, single reader/writer), `WriteAsync` + `ReadAllAsync` |
| `channels-tuned` | same, `TryWrite`/`TryRead` first, `await` only when full or empty |

```bash
cd benchmarks/dataflow
dotnet run -c Release -- 1                      # GiB; all variants
dotnet run -c Release -- 1 dataflow,channels-tuned
```

## Results

32 cores (shared box, load average ~9 from other jobs), .NET 10.0.14, 1.06 GiB, one warm-up pass then one timed
run:

| Variant | s | M tokens/s | M items/s | alloc |
|---|---:|---:|---:|---:|
| serial | 2.544 | 69.0 | 75.9 | 33 KiB |
| dataflow | 2.898 | 60.6 | 66.6 | 293 KiB |
| dataflow-par | 0.875 | 200.5 | 220.6 | 4.4 MiB |
| channels-naive | 90.671 | 1.9 | 2.1 | 432 KiB |
| channels-tuned | 53.001 | 3.3 | 3.6 | 424 KiB |

- `dataflow` vs `channels-naive`: 31x; vs `channels-tuned`: 18x. `dataflow-par` vs `channels-naive`: 104x.
- Allocation is per run (rings, slots, slot buffers grown to the largest batch output), not per item: the same
  figures at 0.31 GiB were 232 KiB / 4.4 MiB.
- `dataflow` is within 15% of the handoff-free serial loop: the tokenizer is the bottleneck and the ring costs one
  store per token plus one release store per 64.
- Channels pay one synchronization per item; with a consumer as fast as the producer they also ping-pong between
  full and empty, which is why both forms sit near 2-3 M items/s.
