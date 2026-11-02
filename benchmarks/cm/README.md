# CM primitives benchmark (ADR 0039, P2.1)

```bash
dotnet run -c Release --project benchmarks/cm            # n = 2^26
dotnet run -c Release --project benchmarks/cm -- 22      # n = 2^22
DOTNET_PROCESSOR_COUNT=1 dotnet benchmarks/cm/bin/Release/net10.0/Bench.Cm.dll 22   # the det hashes must match the default run
```

Median of 15 timed runs after a 0.5 s warm-up; GB/s = input bytes / time. `det` lines hash float results bit for bit.

Forge (32 cores, Sapphire Rapids, AVX-512), .NET 10, n = 2^26:

| kernel | ms | GB/s (input) |
|---|---:|---:|
| prefix_sum Int32, serial loop | 70.49 | 3.8 |
| prefix_sum Int32, Cm.InclusiveScan | 16.42 | 16.4 |
| prefix_sum Int32, Cm.InclusiveScan in place | 8.94 | 30.0 |
| prefix_sum Int32, Prelude xs.prefix_sum (allocates result) | 13.07 | 20.5 |
| histogram 64K bins, serial loop | 81.47 | 3.3 |
| histogram 64K bins, Cm.Histogram | 13.68 | 19.6 |
| sort_by_key 2^24 UInt32+Int32, Array.Sort(keys, items) | 1787.26 | 0.1 |
| sort_by_key 2^24 UInt32+Int32, Cm.SortByKey | 66.66 | 2.0 |
| compact Int32 (half kept), serial loop | 444.87 | 0.6 |
| compact Int32 (half kept), Cm.Compact(flags) | 59.59 | 4.5 |
