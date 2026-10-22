| bench | GC | variant | ms | MiB/run | GCs (7 runs) | pause ms | peak MiB |
|---|---|---|---:|---:|---:|---:|---:|
| rows | workstation | Alice (inferred) | 39.1 | 0.06 | 0 | 0.0 | 40 |
| rows | workstation | Alice (explicit own) | 39.2 | 0.06 | 0 | 0.0 | 40 |
| rows | workstation | C# | 36.8 | 61.37 | 74 | 3.6 | 40 |
| scratch | workstation | Alice (inferred) | 284.1 | 3.81 | 18 | 2.1 | 42 |
| scratch | workstation | Alice (explicit own) | 264.8 | 3.81 | 18 | 2.1 | 42 |
| scratch | workstation | C# | 291.9 | 596.62 | 704 | 38.6 | 56 |
| frames | workstation | Alice (inferred) | 223.6 | 0.00 | 0 | 0.0 | 43 |
| frames | workstation | Alice (explicit own) | 266.4 | 0.00 | 0 | 0.0 | 43 |
| frames | workstation | C# | 218.6 | 484.00 | 1002 | 149.2 | 72 |
| trees | workstation | Alice (inferred) | 36.1 | 67.99 | 87 | 85.6 | 54 |
| trees | workstation | C# | 38.5 | 67.99 | 86 | 89.6 | 54 |
| rows | server | Alice (inferred) | 40.1 | 0.06 | 0 | 0.0 | 38 |
| rows | server | Alice (explicit own) | 40.3 | 0.06 | 0 | 0.0 | 38 |
| rows | server | C# | 50.0 | 61.37 | 174 | 11.8 | 36 |
| scratch | server | Alice (inferred) | 278.1 | 3.81 | 18 | 2.3 | 39 |
| scratch | server | Alice (explicit own) | 281.2 | 3.81 | 18 | 2.1 | 40 |
| scratch | server | C# | 473.2 | 596.62 | 1681 | 107.8 | 40 |
| frames | server | Alice (inferred) | 210.5 | 0.00 | 0 | 0.0 | 44 |
| frames | server | Alice (explicit own) | 216.8 | 0.00 | 0 | 0.0 | 47 |
| frames | server | C# | 424.6 | 498.96 | 243 | 113.8 | 106 |
| trees | server | Alice (inferred) | 34.6 | 67.99 | 90 | 47.9 | 78 |
| trees | server | C# | 33.2 | 67.99 | 90 | 46.1 | 74 |
