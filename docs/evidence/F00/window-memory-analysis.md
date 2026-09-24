# Eight-window UI memory experiment

> Parent: [F00](F00.md) · Catalog: [docs/README](../../README.md)

This is a partial F00 experiment, not PER02 or G0 acceptance. It uses eight representative Vue pages with one WebView environment. It does not yet include the default/full 21-package plugin workloads, real services, production state, or production lifecycle implementation.

| Run | Five-minute tree PWS | Fifteen-minute tree PWS | Warm private bytes | Fixture memory targets |
|---|---:|---:|---:|---|
| Baseline hidden controllers | 186.75 MiB | 2.95 MiB | 322.51 MiB | Warm fails, idle passes |
| Hidden + TrySuspend | 31.87 MiB | 2.80 MiB | 316.30 MiB | Both pass |

The warm tree contains 15 processes, including eight renderers. After controller close and BrowserProcessExited, only the probe and its console host remain. The runner includes both in the final total. Raw per-process values, private bytes and CPU counters are retained, not just rounded totals.

Suspension changes the working-set result much more than private allocation. These measurements do not establish a comparable reduction in commit. No explicit process working-set trimming or target-budget change is used.

The suspended run's final approximately one-minute warm/idle intervals consumed 0.015625 CPU seconds each: 0.0261% / 0.0272% of one core, or 0.00326% / 0.00340% of this eight-logical-processor VM. Sample timestamps determine elapsed CPU interval duration because collecting process counters takes time. CPU comparisons against baseline are confounded by a harness improvement: the baseline used a 20 ms polling message loop, while the final suspension run waits for messages or its actual deadline. A repeat baseline with the corrected pump is needed before attributing the CPU change specifically to suspension.

The completed suspension lifecycle hid all windows at tick 138138234, finished suspending at 138138250, closed controllers at 138738703, and observed browser exit at 138739750. The ten-minute interval starts when all windows become hidden. The last sample is at 899.125 seconds; it is an approximately fifteen-minute sample just before the bounded test exits, not an exact 900-second reading.

The earlier suspension smoke verified resume and nonempty Vue DOM in all eight views. It did not measure resume-to-interactive latency or validate all application state. The initial full suspension attempt exposed a pump predicate/deadline bug and was interrupted; `memory-suspend-invalid-timing/` is diagnostic evidence only. `windows-suspend-smoke.jsonl` predates that timing fix and is not latency evidence.

The corrected [smoke](windows-suspend-corrected-smoke.jsonl) also passes resume/DOM/browser exit. A later [readiness diagnostic](windows-readiness-diagnostic.jsonl) reports environment readiness in the initial tick and controller/page navigation readiness taking 2.766 seconds for the first window, then approximately 2.2–2.4 seconds per later window. These are sequential fresh-controller fixture measurements; their cause remains unresolved and they do not establish the cold/hot interactive-frame targets.

Follow-up: replacing the virtual `.local` origin with `.example` reduced the same [smoke](windows-example-host-smoke.jsonl) to first-page readiness at 750 ms and later pages at 234–344 ms, with all-visible at 3.704 seconds including the intentional one-second wait. Microsoft explicitly warns about `.local` navigation delay in its [mapping documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping). This addresses the large delay; the latency acceptance matrix remains unexecuted. The full memory measurements above predate the hostname change and have not been rerun.

Reproduce from a built fixture/executable:

```powershell
pwsh -NoProfile -File tools/measure-windows.ps1 -MemoryMode suspend
pwsh -NoProfile -File tools/summarize-window-memory.ps1 -InputDirectory docs/evidence/F00/memory-suspend
```

Hardware and runtime: [environment.json](environment.json). Raw suspension [samples](memory-suspend/windows-memory-samples.json), [events](memory-suspend/windows-lifecycle.jsonl), and [derived summary](memory-suspend/summary.json). Baseline [samples](windows-memory-samples.json) and [derived summary](windows-memory-summary.json).

The supported alternatives are separate experiment modes: TrySuspend/Resume and MemoryUsageTargetLevel Low/Normal. Microsoft documents that applications should choose one approach rather than combine them: [MemoryUsageTargetLevel](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2_19). The low-memory mode has not been measured here. Suspension is a promising candidate, not a final architecture decision; resume latency and the complete host workload remain required.
