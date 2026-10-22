| bench | GC | variant | ms | MiB/run | GCs (7 runs) | pause ms | peak MiB |
|---|---|---|---:|---:|---:|---:|---:|
| rows | workstation | Alice (inferred) | 35.0 | 0.06 | 0 | 0.0 | 40 |
| rows | workstation | Alice (explicit own) | 35.7 | 0.06 | 0 | 0.0 | 40 |
| rows | workstation | C# | 33.4 | 61.37 | 74 | 3.2 | 40 |
| scratch | workstation | Alice (inferred) | 250.2 | 0.00 | 0 | 0.0 | 44 |
| scratch | workstation | Alice (explicit own) | 284.6 | 0.00 | 0 | 0.0 | 44 |
| scratch | workstation | C# | 260.3 | 596.62 | 704 | 29.2 | 55 |
| frames | workstation | Alice (inferred) | 204.3 | 0.00 | 0 | 0.0 | 43 |
| frames | workstation | Alice (explicit own) | 228.6 | 0.00 | 0 | 0.0 | 43 |
| frames | workstation | C# | 207.5 | 484.00 | 1002 | 153.1 | 72 |
| trees | workstation | Alice (inferred) | 21.5 | 33.99 | 42 | 35.4 | 50 |
| trees | workstation | C# | 28.9 | 67.99 | 86 | 74.8 | 54 |
| rows | server | Alice (inferred) | 39.0 | 0.06 | 0 | 0.0 | 38 |
| rows | server | Alice (explicit own) | 38.6 | 0.06 | 0 | 0.0 | 38 |
| rows | server | C# | 37.3 | 61.37 | 174 | 8.0 | 36 |
| scratch | server | Alice (inferred) | 258.8 | 0.00 | 0 | 0.0 | 42 |
| scratch | server | Alice (explicit own) | 267.1 | 0.00 | 0 | 0.0 | 42 |
| scratch | server | C# | 315.0 | 596.62 | 1672 | 92.2 | 40 |
| frames | server | Alice (inferred) | 197.5 | 0.00 | 0 | 0.0 | 49 |
| frames | server | Alice (explicit own) | 201.1 | 0.00 | 0 | 0.0 | 49 |
| frames | server | C# | 471.2 | 484.00 | 192 | 131.6 | 131 |
| trees | server | Alice (inferred) | 21.6 | 33.99 | 83 | 32.7 | 53 |
| trees | server | C# | 29.5 | 67.99 | 63 | 46.9 | 84 |
