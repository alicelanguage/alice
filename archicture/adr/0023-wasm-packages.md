# ADR 0023: WebAssembly packages (`kind = "wasm"`)

Status: Accepted (0.13.2)

Running Alice in a browser meant writing a `Microsoft.NET.Sdk.WebAssembly` project by hand (the playground's
`scripts/build.py` generates one). .NET already compiles to WebAssembly; what was missing was the Alice side.

Decision:

- `[package] kind = "wasm"` generates the hidden `.alproj` with the `Microsoft.NET.Sdk.WebAssembly` SDK. The package's
  `wwwroot/` is linked in as the site root. No GC properties are emitted (the browser runtime has one mode), and CA1416
  is off, because the JS interop attributes are browser-only by design.
- `alice new <name> --wasm` writes `src/main.al` with an `@[JSExport]` method, plus `wwwroot/index.html` and a
  `main.js` that boots the runtime, calls the export and runs the top level. `dist/` is gitignored.
- `alice build` on a wasm package publishes to `dist/` (`dist/wwwroot` is deployable as is); `--release` trims.
  `alice run` builds, then runs the bundle under Node, which hosts the same .NET WebAssembly runtime; a missing Node is a
  usage error that points at serving `dist/wwwroot`.
- JS interop needs no new language: Alice modules already emit `static partial` classes. `@[JSExport]` and
  `@[JSImport]` on `partial def` therefore satisfy the .NET source generator as written.
- No workload is required (the runtime pack comes from NuGet). Native relinking and AOT (wasm-tools) are out of scope.

Tests: `Integration/WasmTests` builds a new wasm package and calls its export from Node (skipped without Node).
