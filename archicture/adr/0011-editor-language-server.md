# ADR 0011: Shared CLI language server and Rust editor adapters

Status: Accepted

Alice 0.11.1 ships `alice lsp --stdio` in the existing C# CLI. The server reuses
Alice's lexer, parser, binder, formatter, CLR type resolver, lowering, and Roslyn
backend. Compiler/runtime stay pure C# (ADRs 0001 and 0007).

Editor adapters live in the independent `extensions/` repository: a shared Rust
launch/version contract, a napi-rs addon for VS Code/VSCodium with a small JS
LanguageClient adapter, and a native Rust/WASI Zed extension. All launch the
installed CLI; none maintains a second compiler, auto-installs software, or runs
a C# language server alongside Alice. Existing toolchain selection still applies.

Documents use incremental UTF-16 sync. Edits are debounced; compiler access is
serialized. Fast checks parse/bind; deep checks on open/save inspect generated
C# without emitting IL or creating a Roslyn Workspace. Restored `obj/**/refs.txt`
provides NuGet and C# project references. Unsaved sibling sources overlay disk.
Type-name indexes use PE metadata and are lazy; project and XML-doc caches are
bounded. Project reference assemblies use collectible contexts loaded from streams,
so C# builds can replace DLLs while the editor runs. Clients select workstation GC
for the editor process.

NuGet ID/version suggestions are opt-out, on-demand HTTPS requests with a two
second timeout and bounded ten-minute cache. They never restore packages or
execute builds. Manifest/XML formatting preserves values and comments.

Zed uses a pinned Ruby tree-sitter grammar for the Ruby-compatible subset;
Alice-specific declarations use additional highlight queries. Compiler diagnostics
and completions always use Alice's parser and types.

Verification: compiler regression tests, framed LSP lifecycle/feature tests,
Rust tests/clippy, TextMate tokenization, native addon integration, platform VSIX
packages, WASI build and grammar-query checks. Marketplace publication and Zed
registry submission are separate distribution operations.
