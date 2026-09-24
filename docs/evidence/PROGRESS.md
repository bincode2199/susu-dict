# F00–F03 implementation checkpoint

> Parent: [DEV-PLAN](../development/DEV-PLAN.md) · Catalog: [docs/README](../README.md)  
> Child documents: [F00](F00/F00.md) · [F01](F01/F01.md) · [F02](F02/F02.md) · [F03](F03/F03.md)

Started 2026-09-22 in the Windows development VM. Branch: `work/f00-f03-foundation`. Local commits only; **no remote push** is authorized. Dependencies may be fetched from official sources. No rate-limit reset credits are to be used.

## Current state (2026-09-24)

| Module | State | Record |
|---|---|---|
| F00 | Pending integration acceptance. Every measurement has run, including the interactive ones: C06 now passes (17/17); GDI BitBlt was chosen for capture; PER04 passes. **PER03 misses two budgets**: hot content visible p95 57 ms against ≤50, and cold interactive first frame p95 792 ms against ≤300. That needs a product decision, and the options are recorded; the budgets were not changed. | [F00](F00/F00.md) |
| F01 | Complete: contracts, domain, scheduler and session with 112 deterministic tests. | [F01](F01/F01.md) |
| F02 | Complete: settings, secrets, SQLite, leases, logs; 76 tests; exercised inside the AOT `susu.exe`. | [F02](F02/F02.md) |
| F03 | Pending integration acceptance. NativeAOT `susu.exe` shell, WebView2 host, tray, hotkeys, production Vue UI and minimal settings are in place. PER02, PER04, S07, S08 and UI06 pass. UI01–UI05 pass as far as this single-display VM allows; multi-monitor and mixed DPI, a real IME, screen reader and high contrast still need to be checked. | [F03](F03/F03.md) |

Environment notes for whoever resumes:

- Use Windows PowerShell 5.1 with `. ./tools/env.ps1`, which puts `.tools/dotnet` and `.tools/node` on PATH. There is no `pwsh`. In Git Bash use `.tools/node/node.exe`.
- The RDP session is sometimes non-interactive: `SetCursorPos` fails, there is no foreground window, and DXGI returns `E_ACCESSDENIED`. Input-driven probes need a visible desktop.
- Build `susu.exe` and run it with its own data root (`--data-root`), as described in [BUILD](../development/BUILD.md). Don't run the unit tests while a `--measure` run is in progress: the single-instance test signals message windows, and before the fix it woke an unrelated instance.
- `tools/Susu.SelectionFixtures` needs `DOTNET_ROOT` set to `.tools/dotnet`.
- Never commit machine names, account names or IPs; see [SENSITIVE.example](../SENSITIVE.example.md).

## Open decisions and next steps

1. **G0 PER03 decision (product owner)**: accept WebView2 cold start as a known gap, keep a warm browser longer (which costs idle memory), or re-measure on the clean VM and on physical hardware first. See [F00 § G0 decision](F00/F00.md#g0-decision).
2. F03 environment checks: a second monitor with a different DPI, a real IME session (Microsoft Pinyin is not installed), screen reader and high contrast. The native guard test passes (S08). When the desktop is interactive, run `tools/measure-hotkey.ps1` for PER03 on the real shell.
3. Next modules per DEV-PLAN: F04 (plugin runtime, IPC, sandbox; the `--plugin-host` mode is reserved) and F05 (network, signers, contract probes); then F06 (first usable input translation, which re-measures PER03 in the production shell).

## History (condensed)

- 2026-09-22: toolchain installed (.NET 10.0.401, MSVC 14.44, Windows SDK 26100, Node 24.19.0, pnpm 11.19.0). Narrow AOT probes passed. Jint failed strict AOT, and the failure is preserved. The eight-window baseline measured 186.75 MiB warm (fail). With `TrySuspend` it measured 31.87 MiB. The `.local` → `.example` virtual host change cut first navigation to 750 ms.
- 2026-09-23: authenticated AppContainer plugin host passed X01–X07 (54/54). PER01 compared three engines, and QuickJS-NG was chosen. SEL01 passed in ten programs. The clipboard C01–C07 matrix passed 16/17. Detect → dispatch passed. PER02 passed on the full 21-plugin workload. Static CRT, license inventory and CI workflow were added. F01 was implemented.
- 2026-09-24: docs moved into the `docs/` tree (commit `e54b114`). F00 remainder and F01 committed (`23f36f5`). The local CI-equivalent run passed ([local-ci-run.txt](F00/local-ci-run.txt)). The G0 decision was recorded.
- 2026-09-24 (later): F02 committed (`7283302`). F03 implemented (`d0aba9a` and follow-up). On an interactive desktop, the F00 capture, C06, PER03 and PER04 measurements ran, and two probe-driver crashes were fixed along the way. PER02 was measured on the real `susu.exe`. Real-window checks found and fixed three bugs: focus on open, waking an unrelated instance, and a duplicate service label.
- 2026-09-24 (final): interactive measurements, F03 fixes and PER02 on the real `susu.exe` committed (`d2140b6`). Native guard test (S08, 10/10 with the CSP removed) and PER03 timing points on the real shell committed (`c7479fc`); `tools/measure-hotkey.ps1` is ready but not yet run, because the desktop went non-interactive. Tests at this point: 232 C# and 16 UI, all passing. Nothing has been pushed.
