# Dependency and license inventory (F00)

> Parent: [Development](README.md) · Root: [README](../../README.md)

Pinned versions live in `global.json`, `Directory.Packages.props`, each project's `packages.lock.json`, `native/dependencies.json` and `ui/pnpm-lock.yaml`. The machine-generated inventory is [`evidence/F00/license-inventory.json`](../evidence/F00/license-inventory.json), produced from the locked graph by:

```powershell
node tools/license-inventory.mjs docs/evidence/F00/license-inventory.json tools/Susu.Probes/packages.lock.json
```

Rerun it with the product host's lockfile once `src/Susu.Host` publishes (F03+).

## Redistributed components (F00 probe build)

| Component | Version | License | Where it ends up |
|---|---|---|---|
| .NET runtime (NativeAOT, via runtime.win-x64.Microsoft.DotNet.ILCompiler) | 10.0.12 | MIT (+ .NET ThirdPartyNotices) | compiled into the executable |
| QuickJS-NG | v0.17.0 `6d46d07` | MIT | statically linked into `susu_quickjs.dll` |
| Microsoft WebView2 SDK loader (static lib) | 1.0.4191.47 | Microsoft WebView2 SDK license (`LICENSE.txt`, `NOTICE.txt` in package) | statically linked into `susu_windows_probe.dll`; the WebView2 Runtime is installed separately |
| MSVC static CRT/UCRT | 14.44.35207 | Visual Studio license (redistributable code) | statically linked (`/MT`) since F00 — no VC++ redistributable needed for our DLLs |
| Microsoft.Data.Sqlite (+ .Core) | 10.0.12 | MIT | managed, compiled in |
| SQLitePCLRaw (bundle, core, provider, lib.e_sqlite3) | 2.1.12 | Apache-2.0; SQLite itself public domain | `e_sqlite3.dll` |
| YamlDotNet | 18.1.0 | MIT | compiled in |
| NSec.Cryptography | 26.4.0 | MIT | compiled in |
| libsodium | 1.0.22 | ISC | `libsodium.dll` — **imports `VCRUNTIME140.dll`** (see open item) |
| Vue 3 runtime (vue, @vue/runtime-dom/-core, reactivity, shared) | 3.5.43 | MIT | UI bundle; the inventory lists the full production closure (23 packages, MIT/ISC/BSD) as an upper bound |

Build-only tools (not shipped): .NET SDK, ILCompiler/ILLink tasks, MSVC/CMake/Windows SDK, Node 24.19.0, pnpm 11.19.0, Vite, TypeScript, vue-tsc, Jint/Acornima (comparison build only). Test targets downloaded for SEL01 (Firefox 156.0.1, Electron 22.3.27/44.4.5) are not redistributed; their SHA-256 matched the vendors' published sums.

The IAccessible2 read-only ABI boundary is hand-written from `api/AccessibleText.idl` / `AccessibleHypertext.idl` (https://github.com/LinuxA11y/IAccessible2, BSD-3-Clause-style terms); no IDL or generated code is copied.

## Open items

- `libsodium.dll` needs the VC++ runtime on the target machine. Before F18: redistribute `vcruntime140.dll` app-locally under the Visual Studio redistribution terms, or verify Ed25519 with a statically linked implementation.
- Exact license texts must be copied into the installer payload (F18); this file is the inventory, not the release notice bundle.
- The repository's own license has not been selected.
