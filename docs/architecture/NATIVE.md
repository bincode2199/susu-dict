# Native dependencies

> Parent: [ARCHITECTURE](ARCHITECTURE.md) · Catalog: [docs/README](../README.md)

Covers the repository folder `native/` (`native/dependencies.json`, `native/quickjs-bridge/`) and the native code under `src/Susu.Windows/native/`.

QuickJS-NG will be built from pinned source through a thin C ABI bridge. No precompiled plugin bytecode is accepted. The engine decision remains pending G0 measurements; do not treat the planned baseline as a measured selection.

Windows API/COM implementations belong in Susu.Windows. Native dependencies must be pinned and their licenses recorded (see [THIRD-PARTY-NOTICES](../development/THIRD-PARTY-NOTICES.md)) before acceptance.
