# F00–F03 implementation checkpoint

> Parent: [DEV-PLAN](../development/DEV-PLAN.md) · Catalog: [docs/README](../README.md)  
> Child documents: [F00](F00/F00.md) · [F01](F01/F01.md) · [F02](F02/F02.md)

Started 2026-09-22 in the Windows development VM. Branch: `work/f00-f03-foundation`. Local commits only; **no remote push** is authorized. Dependencies may be fetched from official sources. No rate-limit reset credits are to be used.

## Current state (2026-09-24)

| Module | State | Record |
|---|---|---|
| F00 | Pending integration acceptance. G0 route decisions are made: QuickJS-NG, three-level selection with IA2, one WebView2 environment with suspend keep-warm. Four measurements are open, all blocked by a non-rendering desktop: PER03 hotkey latency, PER04, capture backend, C06 recheck. | [F00](F00/F00.md) |
| F01 | Complete: contracts, domain, scheduler and session with 112 deterministic tests. | [F01](F01/F01.md) |
| F02 | Complete: settings, secrets, SQLite, leases, logs; 76 tests | [F02](F02/F02.md) |
| F03 | In progress | not yet |

Environment notes for whoever resumes:

- Use Windows PowerShell 5.1 with `. ./tools/env.ps1`, which puts `.tools/dotnet` and `.tools/node` on PATH. There is no `pwsh`. In Git Bash use `.tools/node/node.exe`.
- The RDP session is often non-interactive: `SetCursorPos` fails, there is no foreground window, and DXGI returns `E_ACCESSDENIED`. Probes that need input check `Desktop.InputAvailable()`. Run the three commands in [F00 § G0 decision](F00/F00.md#g0-decision) when the desktop is visible.
- `tools/Susu.SelectionFixtures` needs `DOTNET_ROOT` set to `.tools/dotnet`.
- Never commit machine names, account names or IPs; see [SENSITIVE.example](../SENSITIVE.example.md).

## Next steps

1. F02: settings YAML schema, revision/hash, atomic save and journal recovery, file watching; DPAPI `SecretStore` with purpose/origin grants; SQLite single writer (WAL, foreign keys, migrations, online backup); file leases; redacted structured logs. Exit cases DATA01–DATA04 and S01/S02.
2. F03: Win32 shell (startup modes, single instance, tray, hotkeys, WebView2-missing prompt), production Vue UI (tokens, zh/en resources, typed bridge from `protocol/generated/ui.ts`, CSP, navigation limits, whitelist, snapshot + patch), window placement, DPI and keep-warm, minimal settings. Exit cases UI01–UI06, S07/S08, PER02/PER04.
3. When the desktop is interactive: PER03/PER04, capture and C06 (F00), then the F03 latency acceptance.

## History (condensed)

- 2026-09-22: toolchain installed (.NET 10.0.401, MSVC 14.44, Windows SDK 26100, Node 24.19.0, pnpm 11.19.0). Narrow AOT probes passed. Jint failed strict AOT, and the failure is preserved. The eight-window baseline measured 186.75 MiB warm (fail). With `TrySuspend` it measured 31.87 MiB. The `.local` → `.example` virtual host change cut first navigation to 750 ms.
- 2026-09-23: authenticated AppContainer plugin host passed X01–X07 (54/54). PER01 compared three engines, and QuickJS-NG was chosen. SEL01 passed in ten programs. The clipboard C01–C07 matrix passed 16/17. Detect → dispatch passed. PER02 passed on the full 21-plugin workload. Static CRT, license inventory and CI workflow were added. F01 was implemented.
- 2026-09-24: docs moved into the `docs/` tree (commit `e54b114`). F00 remainder and F01 committed (`23f36f5`). The local CI-equivalent run passed ([local-ci-run.txt](F00/local-ci-run.txt)). The G0 decision was recorded.
