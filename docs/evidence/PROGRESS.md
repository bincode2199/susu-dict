# F00–F03 implementation checkpoint

Started 2026-09-22 in the Windows development VM. Branch: `work/f00-f03-foundation`.

Scope follows the four explicit IDs F00, F01, F02, F03 in the request. The repository initially had only untracked planning/design documents and no commits or implementation.

## Current state

Latest handoff (2026-09-22): F00 is incomplete; G0 has not passed; F01–F03 remain pending their hard dependencies. Branch `work/f00-f03-foundation`; latest successful local commit `0f0f9cf`. No remote push. The latest WebView hostname fix and its evidence are saved on disk but **not committed**. See the final checkpoint below before resuming; earlier entries are historical snapshots.

- F00: in progress. .NET SDK 10.0.401 installed locally; VS 2022 Build Tools installed successfully, including MSVC 14.44.35207, Windows SDK 10.0.26100.0 and CMake. Node 24.19.0 and pnpm 11.19.0 available. Solution and initial Windows/library/QuickJS probes implemented; see [F00 evidence](F00/README.md).
- F01: pending F00/G0.
- F02: pending F01.
- F03: pending F02.
- G0: not passed. Solution compilation passed; strict Jint AOT compatibility failed and has an isolated reproducer. Native build environment issue diagnosed and fixed in a child-process wrapper. Full acceptance matrices remain unexecuted.

The branch was created successfully using the environment's required escalation for protected `.git` writes. Project edits are authorized by the user.

Initial usage snapshot: 47% of five-hour allocation consumed; 54% of weekly allocation consumed. Checkpoints must be updated before usage exhaustion; no reset credits are authorized for consumption.

## Checkpoint at 93% five-hour usage (2026-09-22)

Native bridge built successfully after environment normalization. Strict NativeAOT publication of the primary probe executable succeeded. Final published execution at 05:06 UTC passed all 11 narrow probes, including NativeAOT detection, HWND create/destroy, UIA/SAPI/WASAPI activation, MF, SQLite online backup, YAML parser, Ed25519 RFC 8032/tamper, DPAPI roundtrip/tamper, and QuickJS modules/promises/dynamic-compiler removal/infinite-loop interruption. DPAPI passed outside the restricted sandbox; its sandbox failure is preserved as evidence. The first Ed25519 fixture had a hex typo, corrected before the final passing run.

Build instructions: `BUILD.md`. Local output: `artifacts/probes/Susu.Probes.exe` plus native DLLs. CI workflow added but not run remotely. No production host/UI exists yet; the eleven src projects other than Windows are dependency scaffolds. Do not report F01/F02/F03 implemented.

Resume with the final probe report and F00 ledger, then WebView2/ELS/IA2 probes, AppContainer/IPC matrix, selection and performance experiments. Jint strict AOT failure needs investigation or a documented disqualification, not hidden warning suppression. No engine route is accepted and G0 remains open.

The work is saved as files on the requested branch. No git commit was made: the repository began without commits, and no Git author identity is configured. The initial planning/design files remain present. No rate-limit reset credit was consumed; the goal remains incomplete.

## Next steps

1. Finish and run the eight-window lifecycle measurement (`tools/measure-windows.ps1`).
2. Complete selection/UIA/IA2/clipboard probes and ten-program matrix, plus the remaining sandbox/IPC matrix.
3. Complete same-workload engine comparison, record G0 outcomes and route decisions without silently changing budgets.
4. Implement downstream modules once their gates are satisfied, with deterministic tests and per-module evidence.

## Continuation checkpoint (2026-09-22, 07:00 UTC vicinity)

- User clarified all four modules, explicitly authorized dependencies from official sources, and prohibited remote pushes. No push performed.
- Extended native bridge adds real ELS language detection and WebView controller/script/BrowserProcessExited tests. 14 narrow probes passed in `F00/extended-probes.json`.
- Disposable AppContainer profile, scoped read/execute binary ACL, 128 MiB process Job, one-process limit, explicit stdout inheritance. Synthetic secret read and resource write are denied. A bounded loopback test uses successful parent TCP/UDP controls; container TCP times out without receiver connection, UDP produces no received packet, child creation is denied. See `F00/sandbox-network-probes.json` and `F00/sandbox-result.txt`. These do not close the full X01–X07 matrix.
- Mixed sandbox-account ownership prevented changing the build directory ACL. Fixed by staging disposable copies owned by the actual test user, not taking ownership or granting broad access.
- QuickJS probe now also tests the memory cap and infinite microtask-chain interruption.
- Vue 3.5.43 / Vite 8.3.0 / TypeScript 5.9.3 / vue-tsc 3.3.11 / plugin-vue 6.0.9 pinned with pnpm lockfile. TypeScript 7.0.2 was incompatible with vue-tsc exports; fixed by pinning compatible 5.9.3. `pnpm run build:probe` passes.
- `ui/` contains only clearly labeled F00 synthetic Vue fixtures for eight window types, shared tokens/cards, and Chinese/English resources. No production host or fake production provider. Browser inspection confirmed settings/network controls, English main at 520×700, card collapse and no console warnings/errors. It is not F03 acceptance.
- Native eight-window memory harness is being validated. Initial call on the default .NET thread failed RPC_E_CHANGED_MODE; moved to a dedicated native STA thread. Full 5/15-minute measurement not yet completed at this checkpoint.
- Current preview process: exec session 25083, loopback `http://127.0.0.1:4173/`. Revalidate before reuse. Browser handles in Node session: agent/browser/tab.
- Latest usage check: 73% five-hour used, 74% weekly used. Progress saved before exhaustion. G0 remains unpassed and F01–F03 pending.

## Latest checkpoint — 07:05 UTC, 97% five-hour / 78% weekly used

- UIA selection helper implemented with native MTA, UIA connection/transaction limits, password rejection, bounded multi-range text and no empty-selection whole-field fallback. AOT subprocess tests of selected text/empty/password and a stalled-helper kill passed (`F00/selection-probes.json`). This is a controlled Win32 Edit fixture, not the required ten-app matrix. IA2, focus-change races and clipboard borrowing are still missing.
- The selection runner includes a 500 ms helper deadline and bounded output. Latest executable: `artifacts/selection-probes/Susu.Probes.exe`; its DLLs are alongside it. The older `artifacts/probes` binary is intentionally untouched while the measurement runs. To rebuild into a separate output, `tools/build-native.ps1 -OutputDirectory artifacts/selection-probes` and dotnet publish with that output directory.
- Eight-window native smoke passed with actual Vue assets, one environment, eight controllers, hide/close and BrowserProcessExited (`F00/windows-smoke.jsonl`). An STA worker fixes the earlier COM mode failure.
- **Full memory measurement is live**: exec session **35929**, parent probe PID **9104**, run directory `artifacts/window-measure-332859317e2a46378829bb0adbf49606`. Revalidate these handles/processes before deciding it stopped; do not launch a duplicate. It runs for about 15 minutes after all-hidden and autonomously saves `F00/windows-memory-samples.json`, lifecycle and error files. Expected finish around 07:16 UTC. At the checkpoint, samples at 0/60/240 seconds were 185.64/198.77/183.48 MiB PWS, each 15 processes. The 300-second and post-release samples are still pending. This fixture already exceeds the 60 MiB target; do not relabel the target or claim G0 passed.
- Next actions: finish analyzing that measurement, record the architecture gate result, investigate a policy-compatible memory improvement, complete remaining F00 matrices and then proceed through F01–F03. No remote push or rate-limit reset-credit redemption occurred.

Do not mark modules complete based on a source skeleton or JIT builds. Preserve the original design files.

## Continuation — 13:15 UTC onward

- Initial local checkpoint commit `353ba77` now preserves the supplied design baseline and F00 source/evidence on `work/f00-f03-foundation`. Command-local author is Codex; no user Git identity configuration changed. No remote push.
- Earlier baseline measurement completed successfully: full tree private working set at 300.047 seconds was **186.75 MiB** (60 MiB target failed), and at 899.125 seconds **2.95 MiB** after browser exit (25 MiB idle target passed for this fixture). Raw results remain at `F00/windows-memory-samples.json`. Hardware/runtime details captured in `F00/environment.json`.
- Isolated strict Jint NativeAOT publication reproduced IL2026/IL2104/IL3053 failure; diagnostics retained in `F00/jint-aot-build.txt`. No warning suppression or Jint performance claim.
- Added separate WebView TrySuspend/Resume and MemoryUsageTargetLevel Low/Normal experiments. They are not combined. Suspension smoke restored DOM in all eight views and observed browser exit. The full suspension attempt exposed a message-pump timing bug: it slept after its completion predicate had already become true, and started the ten-minute timer after suspending. Fixed both before restarting. Interrupted evidence is retained under `F00/memory-suspend-invalid-timing`; it is not acceptance evidence.
- Five-hour usage had reset naturally (2% used at this continuation's start), weekly usage 79%. No reset credits consumed. G0 is still open; F01–F03 remain pending.

## Checkpoint — 14:31 UTC

- Corrected full suspension measurement completed successfully; no measurement process remains running. Warm (300.016 s) tree PWS 31.87 MiB; idle (899.125 s) 2.80 MiB. Warm private bytes remain 316.30 MiB. Sampled warm/idle CPU approximately 0.0261%/0.0272% of one core, below 0.1%. See `F00/window-memory-analysis.md` for scope, raw links and comparison caveats. This excludes the plugin host/full package workloads and is not PER02 acceptance.
- Added explicit native IA2 text vtable calls, MSAA focus/protected-state traversal, UIA focused-element matching, bounded external helper CLI and embedded-NUL rejection. Synthetic vtable checks cover multi/empty selection, malformed offsets/counts, overflow and native failure. Standard Win32 Edit out-of-process MSAA checks cover password/unsupported results.
- A new MSAA fixture initially hit the 500 ms deadline because an unsupported QueryService HRESULT became an unhandled helper exception. Isolated diagnostics found `E_INVALIDARG` at IA2 QueryService. That exact unsupported-service case now returns unsupported; other failures remain errors. Helper exceptions now produce bounded content-free diagnostics. Earlier failure and diagnostic reports are preserved. Final strict AOT report `F00/ia2-selection-probes.json` passes 15 probes, including MSAA under the original 500 ms deadline. Real IA2 provider text and full focus-race tests remain unverified.
- Computer-use app discovery worked, but Notepad launch returned `Computer Use app approval timed out`. A read-only window check confirmed no Notepad window. No app input occurred; ten-app coverage remains unexecuted.
- Latest usage checkpoint before these last fixes: five-hour 47%, weekly 86%. No reset credit consumed and no remote push. F01/F02/F03 have not begun because G0 is still open.

## Checkpoint — 14:35 UTC, five-hour 71% / weekly 90% used

- Local commit `0031ec7` preserves the memory/selection work. Subsequent AppContainer lifecycle checks now pass in strict NativeAOT: a 256 MiB commit is refused under a 128 MiB Job cap; a ready live container child terminates within two seconds of closing the sole Job handle; scoped ACL restoration and profile deletion have checked success results. Raw evidence: `F00/sandbox-lifecycle-probes.json` and `F00/sandbox-lifecycle-result.txt`.
- The first kill-on-close fixture incorrectly required nonzero exit; actual Windows Job teardown returned exit 0 while correctly terminating the live child. Fixed the test to assert readiness, live-before-close and bounded process termination, preserving the earlier failure in `F00/sandbox-job-exitcode-fixture-failure.json`. It does not yet simulate an externally crashed parent.
- Current updated executable/native DLLs are in `artifacts/selection-probes`; `artifacts/probes` still contains the earlier memory-run build. No live memory measurements remain. Authenticated IPC, hostile broker requests, full network matrix and complete sandbox lifecycle coverage remain open.

## Checkpoint — 14:39 UTC, five-hour 82% / weekly 91% used

- Container-owned LocalState file and private registry round trips passed and were cleaned up. The exact disposable folder is recorded in `F00/sandbox-storage-result.txt`; no real credentials/business data used. Combined published suite passes **17/17** narrow probes (`F00/combined-probes.json`), including unchanged 500 ms helper tests, WebView lifecycle and expanded sandbox lifecycle/storage.
- Corrected suspension smoke restored all eight Vue DOMs and observed BrowserProcessExited (`F00/windows-suspend-corrected-smoke.jsonl`). Startup to all-visible is approximately twenty seconds for the sequential eight-page harness; this is not a measured hotkey/interactive-first-frame latency and does not pass PER03. Per-stage readiness events are being added to diagnose that harness timing next.
- Remaining G0 work remains substantial: authenticated IPC/host authorization and complete access matrix; 10 actual applications including Firefox/old Electron; clipboard transaction cases; engine comparison and 30-sample latency tests; full plugin memory workload; capture/multi-monitor checks and license/CI completion. Do not begin downstream production modules or mark the goal complete based on the narrow passing suite.
- Per-stage diagnostic completed: environment callback returned in the same clock tick; first window navigation-ready took 2.766 s, subsequent windows approximately 2.2–2.4 s each. The sequential readiness delay is therefore in controller/page readiness, not a fifteen-second environment timeout. Root cause and actual first-interactive-frame metrics remain unresolved. Raw timestamps: `F00/windows-readiness-diagnostic.jsonl`. No measurement/test process from this continuation remains running.

## Continuation checkpoint — virtual hostname delay fixed

- Worktree was clean at continuation start; previous turn classified as progress. Usage was already 96% five-hour / 94% weekly. No reset credit redeemed.
- Microsoft documents navigation delays for `.local` virtual hosts. Changed the fixture mapping and navigation origin together to reserved `susu-probe.example`, retaining the existing cross-origin restriction. Source: https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping
- Native rebuild and identical eight-window suspension/resume smoke passed. Raw `F00/windows-example-host-smoke.jsonl`: first navigation-ready 750 ms; subsequent windows 234–344 ms; all-visible 3.704 s including the intentional one-second display wait, versus approximately 20 s before. All eight DOMs resumed and browser exit was observed. These are one-run navigation timings, not 30-sample interactive-frame acceptance. Cold latency remains above target; G0 and F01–F03 remain incomplete.
- Full memory results still belong to the earlier `.local` build; do not relabel those measurements as results from this change. Current native DLL in `artifacts/selection-probes` has changed since `latest-probe-artifact-hashes.json`; that earlier hash manifest still identifies its original report's build. No test process remains live.

## Saved handoff — requested progress documentation

The latest source and evidence were rechecked on disk. The attempted Git staging/commit command did **not execute**: automatic approval review could not complete because the usage limit had been reached. This was a review-service failure, not a finding that the operation was unsafe. The preceding document edits succeeded. No approval bypass, remote push, or reset-credit redemption occurred.

Uncommitted files at this checkpoint:

- `src/Susu.Windows/native/windows_measure.cpp`: `.local` → `.example` mapping/navigation change.
- `docs/evidence/F00/windows-example-host-smoke.jsonl`: passing smoke and raw readiness timestamps.
- `docs/evidence/F00/window-memory-analysis.md`: comparison and limitations.
- `docs/evidence/PROGRESS.md`: latest progress and handoff.

Resume sequence:

1. Preserve/review these changes and create the deferred local checkpoint when approval review is available. Do not push.
2. Complete F00's authenticated IPC and broker authorization, remaining sandbox/network cases, real ten-application selection matrix, and clipboard transaction cases.
3. Complete the same-workload engine comparison, thirty-sample cold/hot latency tests, full plugin-host memory workload, capture/multi-monitor checks, and license/CI evidence. The latest hostname change improves navigation but does not prove the latency gate.
4. Record the G0 route decision against the original budgets. Only after G0 passes, implement and verify F01, then F02, then F03 against `DEV-PLAN.md` and `TEST-PLAN.md`.

The full four-module goal remains active and unfinished. This document is a recovery checkpoint, not a completion report.
