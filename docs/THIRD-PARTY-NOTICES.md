# Dependency inventory (F00, provisional)

Pinned versions are in `global.json`, `Directory.Packages.props`, per-project `packages.lock.json`, and `native/dependencies.json`.

| Dependency | License / source |
|---|---|
| .NET SDK/runtime | MIT; installed SDK includes LICENSE.txt and ThirdPartyNotices.txt |
| QuickJS-NG v0.17.0 | MIT; pinned source includes LICENSE |
| Microsoft.Data.Sqlite / SQLitePCLRaw | MIT / Apache-2.0; package license metadata and transitive lockfile |
| SQLite | Public domain; bundled via SQLitePCLRaw |
| YamlDotNet | MIT; package license metadata |
| NSec.Cryptography | MIT; native libsodium uses ISC |
| Jint / Acornima | BSD-2-Clause / BSD-3-Clause; package license metadata |
| Microsoft.Web.WebView2 | Microsoft WebView2 SDK license in package; runtime separate |
| MSVC / Windows SDK | Microsoft development tools licenses; installed separately |
| IAccessible2 ABI specification | BSD-3-Clause-style terms, Linux Foundation / IBM / Sun; hand-written read-only boundary references `api/AccessibleText.idl` from https://github.com/LinuxA11y/IAccessible2 |

This inventory is not a completed redistribution audit. Copy exact applicable license texts into release artifacts and verify all transitive/native components before F00.1/release acceptance. The repository's own license has not been selected by this task.
