| bench | GC | variant | ms | MiB/run | GCs (7 runs) | pause ms | peak MiB |
|---|---|---|---:|---:|---:|---:|---:|
| rows | workstation | Alice (inferred) | 39.4 | 0.00 | 0 | 0.0 | 40 |
| rows | workstation | Alice (explicit own) | 42.1 | 0.00 | 0 | 0.0 | 40 |
| rows | workstation | C# | 39.8 | 61.37 | 74 | 5.0 | 40 |
| scratch | workstation | Alice (inferred) | 301.3 | 0.00 | 0 | 0.0 | 44 |
| scratch | workstation | Alice (explicit own) | 284.7 | 0.00 | 0 | 0.0 | 44 |
| scratch | workstation | C# | 338.0 | 596.62 | 704 | 49.1 | 55 |
| frames | workstation | Alice (inferred) | 224.3 | 0.00 | 0 | 0.0 | 43 |
| frames | workstation | Alice (explicit own) | 244.0 | 0.00 | 0 | 0.0 | 43 |
| frames | workstation | C# | 263.1 | 484.00 | 1002 | 169.5 | 70 |
| trees | workstation | Alice (inferred) | 25.6 | 33.99 | 42 | 41.4 | 50 |
| trees | workstation | C# | 35.8 | 67.99 | 86 | 84.4 | 54 |
| rows | server | Alice (inferred) | 43.0 | 0.00 | 0 | 0.0 | 38 |
| rows | server | Alice (explicit own) | 42.0 | 0.00 | 0 | 0.0 | 38 |
| rows | server | C# | 50.0 | 61.37 | 172 | 16.0 | 36 |
| scratch | server | Alice (inferred) | 275.9 | 0.00 | 0 | 0.0 | 42 |
| scratch | server | Alice (explicit own) | 273.1 | 0.00 | 0 | 0.0 | 42 |
| scratch | server | C# | 329.3 | 596.62 | 1680 | 76.7 | 40 |
| frames | server | Alice (inferred) | 207.7 | 0.00 | 0 | 0.0 | 44 |
| frames | server | Alice (explicit own) | 227.0 | 0.00 | 0 | 0.0 | 49 |
| frames | server | C# | 413.0 | 494.25 | 231 | 122.4 | 127 |
| trees | server | Alice (inferred) | 27.6 | 33.99 | 83 | 39.0 | 53 |
| trees | server | C# | 37.0 | 67.99 | 63 | 61.7 | 78 |
