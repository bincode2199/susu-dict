# Native dependencies

> Parent: [Architecture](README.md) · Root: [README](../../README.md)

Covers the repository folder `native/` (`native/dependencies.json`, `native/quickjs-bridge/`) and the native code under `src/Susu.Windows/native/`.

QuickJS-NG (chosen by PER01 in [F00](../evidence/F00/F00.md)) is built from pinned source through a thin C ABI bridge. No precompiled plugin bytecode is accepted.

Windows API/COM implementations belong in Susu.Windows. Native dependencies must be pinned and their licenses recorded (see [THIRD-PARTY-NOTICES](../development/THIRD-PARTY-NOTICES.md)) before acceptance.
