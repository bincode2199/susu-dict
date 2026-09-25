# Su-Su (susu-dict)

Su-Su is a Windows 11 desktop translator. It lives in the system tray. Text comes in through a hotkey-driven capture mode (typed input, text selection, clipboard, OCR screen region, microphone, system audio or a video file), and each configured service returns its result in its own card in a list. The UI supports Simplified Chinese and English, and the translation pair is Simplified Chinese ↔ English.

| Area | Choice |
|---|---|
| Host | .NET 10 / C#, bare Win32 message loop, NativeAOT, single `susu.exe` |
| UI | One WebView2 environment, one WebView per window, kept warm after close |
| Plugins | Embedded JS engine (baseline QuickJS-NG, Jint for comparison) in an AppContainer plugin-host child process, one runtime per plugin |
| Text capture | UIA → IA2/MSAA → optional clipboard borrowing, isolated in a bounded selection-host process |
| Distribution | Per-user NSIS installer; license MIT (tentative, confirmed before release) |

**Status (2026-09-24):** F00–F04 are complete: feasibility probes, contracts and job framework, settings and data, the NativeAOT `susu.exe` shell with tray, hotkeys, production Vue UI and minimal settings, and the sandboxed plugin runtime with IPC and supervision. Two PER03 latency misses are accepted as a known gap (D-66). Hardware-dependent checks move to the F19 final acceptance on a physical Windows machine (D-67). F05 is pending integration acceptance (vendor accounts). F06 (input translation) is in progress and paused: the real translation loop with MyMemory works in `susu.exe`, and the UI and the remaining adapters are still to come. See [PROGRESS](evidence/PROGRESS.md) and [BUILD](development/BUILD.md).

## Repository layout

| Path | Contents |
|---|---|
| `docs/` | All project documentation (this tree) and acceptance evidence (`docs/evidence/`) |
| `src/` | Product projects: `Susu.Host`, `Susu.Windows`, `Susu.Contracts`, `Susu.Domain`, `Susu.Jobs`, … |
| `tests/` | Unit and integration test projects |
| `tools/` | Build scripts, F00 probe runner (`Susu.Probes`), contract generator, measurement scripts |
| `protocol/` | Generated TypeScript contracts and IPC compatibility samples. See [PROTOCOL](architecture/PROTOCOL.md) |
| `native/` | Pinned native dependencies and the QuickJS bridge. See [NATIVE](architecture/NATIVE.md) |
| `ui/` | F00 synthetic Vue fixture, not the production UI. See [UI](architecture/UI.md) |
| `design/` | Design-canvas source files (`*.dc.html`, `canvas.json`). See [ARTBOARDS](design/ARTBOARDS.md) |
| `scripts/` | Host-side helpers for the Windows VMs. See [WINDOWS-VM-OPERATIONS](vm/WINDOWS-VM-OPERATIONS.md) |

## Document catalog

Every document has exactly one parent. The breadcrumb at the top of each document links to its parent, and parent documents list their children, so the tree can be walked in either direction. Most planning documents are written in Chinese; build and evidence notes are written in English.

- **[README](README.md)**: this page. Project introduction and catalog.
  - **[product/PLAN](product/PLAN.md)**: 项目方案. Product definition, features, services, plugin API, data, flows and milestones. This is the authoritative spec.
    - [product/RECORD](product/RECORD.md): 方案记录. Decision log (D-xx), rejected options, research and measurements.
    - [product/REVIEW](product/REVIEW.md): 技术方案评审. Findings R01–R13 of the third-version review and how each was resolved.
  - **[design/DESIGN](design/DESIGN.md)**: 设计基础. Visual and interaction spec: colors, type, controls, cards, windows, accessibility, artboard index.
    - [design/ARTBOARDS](design/ARTBOARDS.md): the `design/` artboard source files, how to view and restore them, and editing rules.
      - [design/ARTBOARD-REVISIONS](design/ARTBOARD-REVISIONS.md): checklist for syncing the fourth-version artboards (completed).
  - **[architecture/ARCHITECTURE](architecture/ARCHITECTURE.md)**: 技术架构设计 (A1). Processes, dependencies, threading, job state, IPC, storage, windows, updates, performance.
    - [architecture/PROTOCOL](architecture/PROTOCOL.md): `protocol/`. Contract versioning and IPC compatibility samples.
    - [architecture/NATIVE](architecture/NATIVE.md): `native/`. Native dependency and QuickJS bridge rules.
    - [architecture/UI](architecture/UI.md): `ui/`. The F00 Vue fixture and how to preview it.
  - **[development/DEV-PLAN](development/DEV-PLAN.md)**: 模块开发计划. Modules F00–F19, quality gates M0–M5, dependencies and exit criteria.
    - [development/BUILD](development/BUILD.md): toolchain versions and build/run commands for the F00 harness.
    - [development/TEST-PLAN](development/TEST-PLAN.md): 开发与发布验收计划. Acceptance cases (C, B, T, A, S, X, J, PER, SEL, CFG, UI, DATA, UPD, …).
    - [development/MODULE-TEMPLATE](development/MODULE-TEMPLATE.md): template for a module delivery record (`docs/evidence/Fxx/Fxx.md`).
    - [development/THIRD-PARTY-NOTICES](development/THIRD-PARTY-NOTICES.md): dependency and license inventory.
    - [evidence/PROGRESS](evidence/PROGRESS.md): F00–F03 implementation checkpoints and handoff notes.
      - [evidence/F00/F00](evidence/F00/F00.md): F00 delivery record: probes implemented, observations, reproduction, acceptance ledger. Raw evidence files sit beside it.
        - [evidence/F00/window-memory-analysis](evidence/F00/window-memory-analysis.md): eight-window UI memory experiment.
      - [evidence/F01/F01](evidence/F01/F01.md): F01 delivery record: contracts, domain and job framework, J/T acceptance mapping.
      - [evidence/F02/F02](evidence/F02/F02.md): F02 delivery record: settings, secrets, database, leases and logs.
      - [evidence/F03/F03](evidence/F03/F03.md): F03 delivery record: native shell, WebView2 host, production UI, settings.
  - **[vm/WINDOWS-VM-OPERATIONS](vm/WINDOWS-VM-OPERATIONS.md)**: Windows 虚拟机运行与维护手册. Current spec, daily operation, maintenance and troubleshooting of the dev and clean-test VMs.
    - [vm/WINDOWS-VM-DEPLOYMENT](vm/WINDOWS-VM-DEPLOYMENT.md): 部署记录. What was deployed and verified, plus rebuild steps.
    - [vm/WINDOWS-VM-PLAN](vm/WINDOWS-VM-PLAN.md): 安装部署计划 (V5). Decisions, layout, install phases and acceptance criteria.
      - [vm/WINDOWS-VM-REVIEW](vm/WINDOWS-VM-REVIEW.md): multi-round review of the VM plan and its implementation.
  - **[SENSITIVE.example](SENSITIVE.example.md)**: the list of placeholders that stand in for sensitive values. The real values are in `SENSITIVE.md`, which is local-only and gitignored.

## Documentation rules

- Every document goes under `docs/`. This file is the only `README.md`. Code folders do not carry their own docs; their notes live under `docs/architecture/`.
- A new document gets one parent. Add a breadcrumb line (`> Parent: …` / `> 上级：…`) under its title, list it in the parent's child line, and add it to the catalog above.
- Raw evidence (JSON, logs) stays in `docs/evidence/Fxx/` because the tools write there. Each module's record is `docs/evidence/Fxx/Fxx.md`.
- Never commit IP addresses, account names, machine IDs, private links or credentials. Write a placeholder such as `` `<HOST_LAN_IP>` ``, list it in [SENSITIVE.example](SENSITIVE.example.md), and keep the real value in the local `docs/SENSITIVE.md`. Passwords, tokens and keys do not belong in either file.
