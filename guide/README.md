# guide/

`GUIDE.md` is the Alice guide for people and AI agents. It is always written in English, describes the current
version only, and is what `alice guide` prints (the CLI embeds it at build time, so an installed Alice prints the
guide for itself).

Rules for keeping it right:

- Every `alice` code block must compile: `tests/Alice.Tests/Tooling/GuideTests.cs` transpiles each one.
- When a release changes the language, the CLI or an error code, update `GUIDE.md` in the same change and bump
  the version on its first line.
- On release, copy the guide to `guide/versions/GUIDE-<version>.md`, so older guides stay readable. Do not edit
  those copies afterwards.
- Keep it short and imperative: what to write, what the compiler enforces, which error code says so. Details
  belong in `docs/`.
