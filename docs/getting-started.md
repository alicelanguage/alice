# Getting started

Requires .NET 10 SDK. Optional: clang (native kernels).

```bash
dotnet build Alice.sln
dotnet test
dotnet run --project src/Alice.Cli -- run hello.al
dotnet run --project src/Alice.Cli -- emit hello.al          # show C#
dotnet run --project src/Alice.Cli -- build hello.al --native # NativeAOT
dotnet run --project src/Alice.Cli -- bootstrap               # self-host check
```

`hello.al`:

```crystal
puts "Hello, #{"Alice"}!"
```

Native kernels (optional):

```bash
clang -O3 -march=native -shared -fPIC src/Alice.Runtime/Native/alice_native.c -o src/Alice.Runtime/libalice_native.so
```

Without the library, the runtime uses managed SIMD fallbacks.
