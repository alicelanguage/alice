# dataflow.al: the acceptance pipeline in Alice (ADR 0038, P3.3)

Same text, token hashes and checksum as `../Program.cs`. Build: `alice build dataflow.al -o out/dataflow.dll`, run
`DOTNET_gcServer=1 dotnet out/dataflow.dll [GiB] [variants]`. Forge, 32 cores (shared, load ~7), Release compiler:

| Variant | Alice 1.06 GiB | hand C# (`../Program.cs`) 1.06 GiB |
|---|---:|---:|
| serial | 2.175 s | 2.246 s |
| dataflow (`dataflow do`, 3 stages) | 2.861 s | 2.602 s |
| dataflow-par (`stage(par: true, workers: 16, batch: 1024)`) | 1.088 s | 1.029 s |

Channel(T) + spawn form (`channels`) at 0.25 GiB: 30.21 s vs 0.881 s for `dataflow` (34x). Checksums match
(`175516228 7dc0fac93e7cd404` at 1.06 GiB). Allocation is per run (293 KiB / 4.4 MiB), not per item.
