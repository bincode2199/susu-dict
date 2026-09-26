# Progress

> Parent: [Evidence](README.md) · Root: [README](../../README.md)

Where the project stands, what to do next, and how it got here. This is the one place that records project status; specs and plans link here.

**Summary (2026-09-25):** F00–F04 are complete. F05 and F06 are pending integration acceptance: vendor accounts are missing, and so is an interactive run of the G1 demo. F06, the first product loop, is built and independently verified. F07 is next; the plan for F06–F19 is in [Execution plan](#execution-plan-goal-set-2026-09-25).

Ground rules: work started 2026-09-22 in the Windows development VM on branch `work/f00-f03-foundation`. Local commits only; **no remote push** is authorized. Dependencies may be fetched from official sources. No rate-limit reset credits are to be used.

## Current state (2026-09-24)

| Module | State | Record |
|---|---|---|
| F00 | Complete. Every measurement has run, including the interactive ones: C06 passes (17/17); GDI BitBlt was chosen for capture; PER04 passes. PER03 misses two budgets (hot content visible p95 57 ms against ≤50; cold interactive first frame p95 792 ms against ≤300). The product owner accepted both as a known gap (D-66); the targets were not changed. | [F00](F00/F00.md) |
| F01 | Complete: contracts, domain, scheduler and session with 112 deterministic tests. | [F01](F01/F01.md) |
| F02 | Complete: settings, secrets, SQLite, leases, logs; 76 tests; exercised inside the AOT `susu.exe`. | [F02](F02/F02.md) |
| F03 | Complete. NativeAOT `susu.exe` shell, WebView2 host, tray, hotkeys, production Vue UI and minimal settings are in place. PER02, PER04, S07, S08 and UI06 pass. UI01–UI05 pass as far as this single-display VM allows. Multi-monitor and mixed DPI, a real IME, screen reader, high contrast and PER03 on the real shell moved to the F19 final acceptance on a physical Windows machine (D-67). | [F03](F03/F03.md) |
| F04 | Complete. Plugin runtime (`Susu.Runtime`), host side (`Susu.Plugins`), production sandbox module `susu_plugin_sandbox.dll`, supervised sessions and `susu-plugin check/test`. X01–X07 re-ran 54/54 against the production module; S09, J04, J06 and J07 pass; 279 tests. Live stream-chunk limits wait for the first streaming capability. | [F04](F04/F04.md) |
| F05 | Pending integration acceptance. Network broker (origin, DNS and redirect policy; proxy; cancel; SSE), signers checked against official vectors, file handles and transforms, S10 credential-leak interception, 9 contract probes through the real sandbox, one real MyMemory call; 380 tests. Vendor accounts are missing, so A01–A04 and the other real calls are not executed. | [F05](F05/F05.md) |
| F06 | Pending integration acceptance (2026-09-25). Four built-in packages (MyMemory, Tencent, DeepL, OpenAI with streaming), services built from settings, key entry with origin-confirmed grants, the production input-translation UI. Independently verified: T01/T02, J01–J06, UI03/UI04, S07/S08, plugin contracts, PER02 with the lazy host (idle 4.72 MiB). 493 C# + 37 UI tests. Missing: real Tencent/DeepL/OpenAI calls (no accounts) and the interactive G1 walk-through. | [F06](F06/F06.md) |
| F07 | Pending integration acceptance (2026-09-26); only the interactive demo in the real window remains. Full service list with merged order, schema-generated config and dynamic options, SetPrompt, SetNetwork with proxy test, and speech selections. CFG01–CFG05, A02/A03 (selection) and UI04 pass; no production bugs found in verification. 597 C# + 70 UI tests. | [F07](F07/F07.md) |
| F08 | Pending integration acceptance (2026-09-26). Production selection helper (`Susu.Windows.Selection`, UIA/IA2, deadlines, failure cache), clipboard-borrow transaction, Selection window, failure bar and SetHotkeys. C01–C06, SEL02, SEL03 (except elevated), UI01 (math), UI03 and J01 pass. Missing: the SEL01 matrix on the production helper, SendInput, the Notepad end-to-end run and C07 (need a foreground window). 748 tests (2 skip) + 87 UI; the suite was hardened to 8/8 clean runs. | [F08](F08/F08.md) |
| F09 | Pending integration acceptance (2026-09-26); only the real Youdao query is missing (no account). Youdao package (translate + dictionary, broker digest signing), word classification, lookup only when a card is expanded with empty-entry fallback, safe card rendering, copy and vocab projections, the audio-authorization port for F10. DICT01–DICT03, S06 and S08 pass. Verification found and the team fixed: plugin redirects not limited to granted origins, and IPC payloads over 1 MiB hanging (now split into parts per PLAN 4.5). 986 tests (2 skip) + 104 UI. | [F09](F09/F09.md) |
| F10 | Pending integration acceptance (2026-09-26). Native SAPI and Microsoft/Google/Tencent TTS packages (audio reaches plugins only as file handles), a single player with stop and supersede, the pronounce hotkey and bar, dictionary audio from existing results, a session-scoped TTS cache, and device-loss handling. TTS01–TTS03, B03/B04/B07 and SEL03 pass, with real SAPI and WASAPI playback; no production bugs in verification. Missing: real vendor TTS calls and the hotkey demo. 1103 tests (2 skip) + 117 UI. | [F10](F10/F10.md) |
| F11 | In progress (2026-09-26). Plan: 11.1 overlay and capture → 11.2 OCR packages → 11.3 UI → testing. | [F11](F11/F11.md) |

Environment notes for building and running (PowerShell setup, non-interactive RDP sessions, data roots) are in [BUILD § Environment notes](../development/BUILD.md#environment-notes).

## Decisions and next steps

1. **Resolved (2026-09-24), D-66:** the product owner accepted the two PER03 misses as a known gap. See [F00 § G0 decision](F00/F00.md#g0-decision).
2. **Resolved (2026-09-24), D-67:** checks that need real hardware move to the F19 final acceptance: a second monitor with a different DPI, a real IME session, screen reader, high contrast, and PER03 on the real shell (`tools/measure-hotkey.ps1`). All F19 final checks run on a physical Windows 11 machine, not a VM.
3. **F04 complete; F05 pending integration acceptance (vendor accounts needed); F06 paused (2026-09-24)** at the product owner's request, to be resumed manually. A master agent coordinates, writes the docs and watches the usage limits; a fresh coding agent handles each sub-item and an independent testing agent verifies each module. Modules run one at a time.
4. **Usage rules (2026-09-24):** F05's coding pass used more than one 5-hour window because one sub-agent ran 370 turns in a single uncompacted context. The rules that prevent a repeat are in [CLAUDE.md](../../CLAUDE.md).
5. **Active (2026-09-26):** F06–F10 are pending integration acceptance. G2 (F07–F10) is reached below the window. **Next action:** start F11 (OCR). Read DEV-PLAN § F11 and TEST-PLAN OCR01–OCR03, B01, B05–B08, UI01 and DATA08. Write the plan in `docs/evidence/F11/F11.md`, then run a fresh coding agent for F11.1: multi-monitor capture overlay with screenshot coordinates, Esc to cancel, hide/restore windows, and an optional screenshot copy with retention cleanup. Capture uses GDI BitBlt, per the F00 decision.
6. **Input needed from the product owner (not blocking the next module):** an attended interactive desktop with a foreground window. It is needed to walk the F06 G1 and F07 demos in the real `susu.exe` window, and to run the F08 checks that need input focus (SEL01 matrix, SendInput, Notepad end to end, C07). The RDP session shows Active, but `GetForegroundWindow` returns 0, so agents can't activate windows. Unless you say otherwise, these are grouped with the F19 physical-machine checks (D-67).
   - Also noted: the CLAUDE.md temp-folder cleanup rule (a working-tree edit, not made by an agent) is followed. The leak's root cause is fixed in `2e6004b`.
7. **Original resume plan:** follow [F06 § Handoff](F06/F06.md#handoff): F06.2b (Tencent, DeepL), then F06.3 (UI and dynamic providers), then the F06 testing agent. After F06 (G1), per DEV-PLAN: F07, then F08–F18 (after F07, OCR, recording and vocab do not depend on each other), then F19 on a physical machine. Before starting a sub-agent, check the 5-hour and weekly usage windows. At about 90 %, have agents commit and stop, and pause until the reset.
8. **Open items carried forward:**
   - F05 real vendor calls and A01–A04 need accounts.
   - F04/F05 WebSocket is not built (no v1 adapter needs it).
   - F06 real Tencent/DeepL/OpenAI calls need accounts; the interactive G1 walk-through needs a drivable desktop.
   - The plugin sandbox lacks `crypto` and `URLSearchParams`, which PLAN 4.3 lists (found in F09.1). Assigned to F16.
   - Resolved 2026-09-25: the intermittent `NetworkBrokerTests` failure (timing assumptions in test code); PER02 re-measured with the lazy host (pass).

## Execution plan (goal set 2026-09-25)

Goal: finish every remaining DEV-PLAN module (F06–F19), each meeting its DEV-PLAN § 4 exit criteria with tests passing, recorded here. Coordinator: master agent. Per sub-item one fresh coding agent; per sub-item or module one testing agent (it may prepare tests in a separate worktree while coding runs, then verifies the integrated result). Coding agents run one at a time in the main tree (shared `bin/obj`, local commits); only worktree-isolated testing work runs alongside.

| Order | Module | Hard deps | Sub-items (DEV-PLAN § 4) | Exit criteria | Expected ceiling in this VM |
|---|---|---|---|---|---|
| 1 | F06 | F03, F05 | 2b Tencent/DeepL → 3 UI + dynamic providers → testing | T01/T02, J01–J06, UI03/UI04, S07/S08, 4 plugin contracts + real calls; G1 | Pending integration acceptance (no Tencent/DeepL/OpenAI accounts) |
| 2 | F07 | F06 | 07.1 service list → 07.2 schema controls/options → 07.3 prompts/network → 07.4 speech selection | CFG01–CFG05, A02/A03, UI04 | Complete except vendor-dependent A02/A03 |
| 3 | F08 | F06 | 08.1 helper → 08.2 3-level capture/clipboard → 08.3 UI | C01–C07, SEL01–SEL03, UI01/UI03/J01 | Program matrix in this VM; hardware parts → F19 |
| 4 | F09 | F06 | 09.1 Youdao → 09.2 lazy lookup → 09.3 rendering | DICT01–03, S06/S08, contracts | Pending acceptance (no Youdao account) |
| 5 | F10 | F07, F08 | 10.1 SAPI + 3 cloud TTS → 10.2 hotkey/bar → 10.3 cache/errors | TTS01–03, B03/B04/B07, SEL03 | SAPI real; cloud pending accounts; audio device may be absent in VM |
| 6 | F11 | F07 | 11.1 overlay/capture → 11.2 P-O01/O02 → 11.3 UI | OCR01–03, B01/B05–B08, UI01, DATA08 | Pending accounts |
| 7 | F12 | F07 | 12.1 WASAPI → 12.2 ASR pipeline P-R01/R02 → 12.3 UI | A01–A06/A08, REC01–03, B02/B07 | VM has no microphone: real recording → physical machine |
| 8 | F13 | F12 | 13.1 loopback → 13.2 reuse F12 | REC01–04, A08 | Same as F12 |
| 9 | F14 | F12 | 14.1 decode → 14.2 job → 14.3 export → 14.4 UI | T03–T07, A05–A08, VID01–04, PER05; G4 | 42-min real ASR needs account |
| 10 | F15 | F07 | 15.1 entries/outbox → 15.2 exporters → 15.3 P-V01/V02 → 15.4 UI | DATA05–08, CFG02, real sync | Anki/Eudic real sync needs install/account |
| 11 | F16 | F07 | 16.1 install → 16.2 update → 16.3 CLI | UPD01–04, X02/X05/S02 | Complete in VM |
| 12 | F17 | F07 | 17.1 backup → 17.2 about/diagnostics → 17.3 drills | DATA01–04/09, S07/S10, UI05 | Second Windows user may be needed |
| 13 | F18 | F16, F17 | 18.1 NSIS → 18.2 update/rollback → 18.3 failure drills | UPD05–08, X01/X06/DATA03, clean Win11 install | Clean Win11 user evidence may need another machine |
| 14 | F19 | F08–F18, all P items | 19.1–19.4 | All applicable TEST-PLAN items, M5 | **Blocked in this VM**: D-67 requires a physical Windows 11 machine |

Remaining P items not owned by a module (P-T04–T06, P-A02–A05) are picked up after F07 as separate adapter items (DEV-PLAN § 5), before F19.

Verification commands: affected tests `dotnet test --project tests/Susu.Tests.Unit -c Release --no-restore --filter-class "<filter>"`; full suite `./tools/dev.ps1 test` before each commit; real-sandbox classes need `dotnet publish src/Susu.Host -c Release -r win-x64` with `tools/env.ps1` dot-sourced.

Unresolved questions (assumptions recorded, not blocking): vendor accounts are not provided, so real calls stay "not executed" and affected modules end at "pending integration acceptance" per DEV-PLAN § 1.3; no physical machine is available to this session, so F19 cannot be completed here.

## History (condensed)

- 2026-09-22: toolchain installed (.NET 10.0.401, MSVC 14.44, Windows SDK 26100, Node 24.19.0, pnpm 11.19.0). Narrow AOT probes passed. Jint failed strict AOT, and the failure is preserved. The eight-window baseline measured 186.75 MiB warm (fail). With `TrySuspend` it measured 31.87 MiB. The `.local` → `.example` virtual host change cut first navigation to 750 ms.
- 2026-09-23: authenticated AppContainer plugin host passed X01–X07 (54/54). PER01 compared three engines, and QuickJS-NG was chosen. SEL01 passed in ten programs. The clipboard C01–C07 matrix passed 16/17. Detect → dispatch passed. PER02 passed on the full 21-plugin workload. Static CRT, license inventory and CI workflow were added. F01 was implemented.
- 2026-09-24: docs moved into the `docs/` tree (commit `e54b114`). F00 remainder and F01 committed (`23f36f5`). The local CI-equivalent run passed ([local-ci-run.txt](F00/local-ci-run.txt)). The G0 decision was recorded.
- 2026-09-24 (later): F02 committed (`7283302`). F03 implemented (`d0aba9a` and follow-up). On an interactive desktop, the F00 capture, C06, PER03 and PER04 measurements ran, and two probe-driver crashes were fixed along the way. PER02 was measured on the real `susu.exe`. Real-window checks found and fixed three bugs: focus on open, waking an unrelated instance, and a duplicate service label.
- 2026-09-24 (final): interactive measurements, F03 fixes and PER02 on the real `susu.exe` committed (`d2140b6`). Native guard test (S08, 10/10 with the CSP removed) and PER03 timing points on the real shell committed (`c7479fc`); `tools/measure-hotkey.ps1` is ready but not yet run, because the desktop went non-interactive. Tests at this point: 232 C# and 16 UI, all passing. Nothing has been pushed.
- 2026-09-24 (decisions): the product owner accepted the PER03 misses (D-66) and moved the hardware-dependent checks to F19 on a physical machine (D-67). F00 and F03 are marked complete.
- 2026-09-24: F04 complete. Plugin runtime, IPC, production sandbox module, supervisor and `susu-plugin` CLI; 279 tests; X-matrix re-run 54\/54 on the production module.
- 2026-09-24: F05 pending integration acceptance. Broker, signers, file transforms, SSE, proxy, cancel, S02 at the broker, S10; 380 tests; S05 test blind spot found and fixed by the testing agent. F06 started.
- 2026-09-24 (late): F06.1 and F06.2a done (411 tests, all local; nothing pushed). Paused at the product owner's request; the resume plan is in F06 § Handoff.
- 2026-09-25: goal set to finish F06–F19; execution plan recorded. F06.2b (Tencent, DeepL), 3a (services from settings) and 3b (UI) done. Testing found and the team fixed five production bugs:
  - the signer grant use;
  - a stream error read as a normal end;
  - cancel not reaching the vendor;
  - OpenAI without `[DONE]`;
  - a `susu-plugin test` crash.
  
  Testing also fixed the flaky timing tests. F06 is pending integration acceptance at 493 + 37 tests.
