# alice-grep

A `grep` for the command line in Alice: flags, globs (`*`, `?`, `[ab]`, `**`), files searched in parallel, .NET
regular expressions, grep's exit codes, and a NativeAOT build that starts in a few milliseconds.

| File | Role |
|---|---|
| [src/options.al](src/options.al) | Argument parsing into `Options`; `Parsed` is `Run`, `Help` or `Usage(message)` |
| [src/glob.al](src/glob.al) | Glob to regex, and expanding an argument (file, directory or glob) to files |
| [src/search.al](src/search.al) | The parallel scan (`Par.For`) and the output format |
| [src/main.al](src/main.al) | Wires them together; exit 0 = matched, 1 = no match, 2 = usage or pattern error |
| [test/grep_test.al](test/grep_test.al) | Globs, parsing, scanning a temporary tree (hidden directories and binary files skipped) |

## Run

```bash
cd samples/alice-grep
alice run -- -n 'def self\.\w+' 'src/**/*.al'     # quote globs so the shell leaves them alone
alice run -- -c -i regex src
alice run -- --help
```

```text
usage: alice-grep [-i] [-n] [-c] [-l] [-F] PATTERN [FILE | DIR | GLOB ...]
  -i  ignore case     -n  line numbers     -c  count matches per file
  -l  file names only -F  PATTERN is plain text, not a regex
GLOB: * and ? within a directory, ** across directories, e.g. 'src/**/*.al' (quote it)
```

A pattern is compiled once, with a 2-second match timeout per line, so a pathological pattern stops with exit
code 2 instead of running forever.

## Parallel scan

`Search.run` gives every file its own iteration of `Par.For`, and each iteration writes only `results[i]`, so the
output order is the order of the files. The compiler checks the loop for races (AL0202): a write to a shared
variable inside it does not compile.

It is `Par.For`, not `par for`: `par for` is for loops the compiler proves independent, and that proof accepts
only pure calls. Reading a file is I/O, and `Regex` and `String` methods are not marked pure, so `par for` rejects
this loop with AL0209.

## NativeAOT

```bash
alice build --release --native      # .alice/bin/native/alice-grep, no trimming warnings
```

Measured on the build box (Linux x64, 32 cores, shared, load average about 7; hyperfine, `-N`, warm cache),
2026-11-03:

| | NativeAOT | `dotnet alice-grep.dll` (JIT) |
|---|---|---|
| binary | 2,744,936 bytes, one file | needs the .NET runtime |
| `--help` (startup) | 3.3 ms ± 0.2 | 33.4 ms ± 1.2 |
| `-c class` over the repository's `src/` (394 files) | 25.3 ms ± 3.9 | 71.4 ms ± 4.1 |
| peak memory, `--help` | 13.2 MiB | 39.6 MiB |

Reproduce:

```bash
hyperfine -N --warmup 3 '.alice/bin/native/alice-grep --help' 'dotnet bin/Release/net10.0/alice-grep.dll --help'
```

## Test

```bash
alice test     # 6 examples, 0 failures
```

Arrays in the specs are compared joined into a string: `assert_equal` compares two arrays by reference.
