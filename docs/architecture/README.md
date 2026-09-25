# Architecture

> Parent: [README](../../README.md)

How Su-Su is built: the technology choices, where each part of the code lives, and the documents that specify it. Start with the summary below; read [ARCHITECTURE](ARCHITECTURE.md) for the design itself.

## Technology choices

| Area | Choice |
|---|---|
| Host | .NET 10 / C#, bare Win32 message loop, NativeAOT, single `susu.exe` |
| UI | One WebView2 environment, one WebView per window, kept warm after close |
| Plugins | QuickJS-NG (chosen by PER01 in F00) in an AppContainer plugin-host child process, one runtime per plugin |
| Text capture | UIA → IA2/MSAA → optional clipboard borrowing, isolated in a bounded selection-host process |
| Distribution | Per-user NSIS installer; license MIT (tentative, confirmed before release) |

## Repository layout

| Path | Contents | Notes |
|---|---|---|
| `src/` | Product projects: `Susu.Host`, `Susu.Windows`, `Susu.Contracts`, `Susu.Domain`, `Susu.Jobs`, … | [ARCHITECTURE § 2](ARCHITECTURE.md) |
| `tests/` | Unit and integration test projects | [BUILD](../development/BUILD.md) |
| `tools/` | Build scripts, F00 probe runner (`Susu.Probes`), contract generator, measurement scripts | [BUILD](../development/BUILD.md) |
| `protocol/` | Generated TypeScript contracts and IPC compatibility samples | [PROTOCOL](PROTOCOL.md) |
| `native/` | Pinned native dependencies and the QuickJS bridge | [NATIVE](NATIVE.md) |
| `ui/` | Production Vue UI and the F00 synthetic fixture | [UI](UI.md) |
| `design/` | Design-canvas source files (`*.dc.html`, `canvas.json`) | [ARTBOARDS](../design/ARTBOARDS.md) |
| `scripts/` | Host-side helpers for the Windows VMs | [WINDOWS-VM-OPERATIONS](../vm/WINDOWS-VM-OPERATIONS.md) |
| `docs/` | All documentation and acceptance evidence | [README](../../README.md) |

## Documents

| Document | Purpose |
|---|---|
| [ARCHITECTURE](ARCHITECTURE.md) · 技术架构设计 (A1) | The implementation design: processes, dependencies, threading, job state, IPC, storage, windows, updates, performance |
| [PROTOCOL](PROTOCOL.md) | Rules for `protocol/`: contract versioning and IPC compatibility samples |
| [NATIVE](NATIVE.md) | Rules for `native/`: pinned native dependencies and the QuickJS bridge |
| [UI](UI.md) | The `ui/` builds, the rules the UI code enforces, and its commands |
