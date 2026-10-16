# Tooling

## CLI

Run from source: `dotnet run --project src/Alice.Cli -- <command>`.

| Command | Action |
|---|---|
| `alice new <name> [--lib\|--game]` | create project (exe, library, or MonoGame game) |
| `alice build [proj\|files] [--release] [--native] [-o out] [-r ref.dll]` | compile to IL; `--native` = NativeAOT |
| `alice run [proj\|file.al] [-- args]` | build and run |
| `alice check` | type-check, no output |
| `alice test` | run every `def test_*` in `test/*.al` |
| `alice fmt [--check] [paths]` | format `.al` files in place; `--check` lists unformatted files, exit 1 (CI) |
| `alice add <package> [--version v]` | add NuGet package |
| `alice clean` | remove build outputs |
| `alice emit file.al` | print lowered C# |
| `alice sln add <sln> <proj>` | add `.alproj` to a solution |
| `alice version` (`-V`) | version, runtime, SIMD capabilities |

Global flags: `--lang en|pt|es` (or `ALICE_LANG`), `-q`/`--quiet`, `--no-color`.

Diagnostics are rustc-style with AL codes and a `help:` hint. See [errors.md](errors.md).

## fmt

Re-indents by block structure (2 spaces), aligns `when`/`rescue` with their opener, strips trailing whitespace, collapses blank-line runs, ends with one newline. Strings and comments are untouched. Idempotent. Skips `bin/` and `obj/`. Tests: `FormatterTests`.

## Tests

```crystal
# test/greet_test.al
def test_greet
  assert_equal "Hello, Bob!", greet("Bob")
  assert greet("x").size > 0
end
```

`alice test` compiles `src/` + each test file and prints `test result: ok. N passed; 0 failed`.

## MSBuild / .alproj

| File | Role |
|---|---|
| `src/Alice.Sdk/Alice.props` | defaults: net10.0, nullable errors, TieredPGO, server + concurrent GC, `.alproj` -> C# targets |
| `src/Alice.Sdk/Alice.targets` | compiles `**/*.al` (excl. `bin/`, `obj/`) in any SDK project (.alproj or .csproj) |
| `*.alproj` | ordinary SDK-style project (`Microsoft.NET.Sdk`) |

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../CSharpLib/CSharpLib.csproj" />
    <ProjectReference Include="../AliceLib/AliceLib.alproj" />
  </ItemGroup>
</Project>
```

`dotnet build` / `dotnet run` work on `.alproj`. In-repo projects import the SDK via `samples/Directory.Build.props|targets`; `alice new` projects import it next to `alice.dll`.

## Interop (samples/)

| Sample | Shows |
|---|---|
| `AliceLib` | Alice library: `Shape` sum type, `Point` record, `Geometry` module |
| `CSharpApp` | C# consumes `AliceLib`, pattern-matches the sum type |
| `CSharpLib` | C# library (`Stats`, `Counter`) |
| `AliceApp` | Alice consumes `CSharpLib` + `AliceLib` |
| `BounceGame` | MonoGame (`PackageReference`) game in Alice |

Alice -> C#: `using CSharpLib`, call by declared names (`Stats.Median(xs)`). C# -> Alice: Alice types are plain CLR types. Test: `SampleTests.Interop_samples_build_and_run` (BounceGame not test-covered).
