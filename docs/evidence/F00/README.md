# F00 · Foundation and feasibility probes

Status: **in progress; G0 not passed**. Started 2026-09-22, Windows dev VM, branch `work/f00-f03-foundation`.

## Implemented so far

- Eleven projects with the documented dependency direction, .NET SDK 10.0.401 pinned, centralized package versions and NuGet lockfiles.
- Local SDK installation and Microsoft-signed VS 2022 C++ Build Tools installation (installer exit 0). MSVC directory 14.44.35207, compiler 19.44.35229.0, Windows SDK 10.0.26100.0.
- AOT-compatible explicit Win32/IUnknown boundaries and current-user DPAPI wrapper.
- Probe runner for HWND, UIA/SAPI/WASAPI activation, MF startup, DPAPI tamper/roundtrip, SQLite parameterized SQL/online backup, low-level YAML parser, RFC 8032 Ed25519 vector.
- QuickJS-NG v0.17.0 at `6d46d07d04041b40f4f49eaa7fdebe44c314c699`; source C ABI for bounded evaluation and a shared interruption budget across promise jobs.
- Separate Jint comparison project, preserving strict AOT diagnostics rather than globally suppressing them.

## Observations

The initial solution build passed with zero warnings/errors. This is a compilation check, **not** NativeAOT acceptance.

The final primary probe NativeAOT publish succeeded and its actual x64 executable passed **11/11 narrow probes** on 2026-09-22 at 05:06 UTC. Raw results: [real-user execution](platform-probes-unsandboxed.json). [Earlier sandbox execution](platform-probes.json) preserves the DPAPI sandbox limitation and the initial signature-fixture typo. The typo was corrected before the final passing run. These results do not close G0's broader exit cases.

QuickJS source compiled with upstream MSVC warnings (including C4701/C4703 potential uninitialized variables); these still require upstream review before engine acceptance. The first successful managed solution build was warning-free; that statement does not cover the subsequent third-party C build.

Jint 4.16.3 caused strict NativeAOT publish failure with IL2026 (NamespaceReference dynamic loading, DefaultTypeConverter expression property reflection), IL2104 and IL3053. Reproduce through `tools/Susu.JintProbe`. The initial failure occurred when the same probe was temporarily in the combined executable; it has since been isolated. No Jint performance numbers are claimed.

The VM inherited duplicate case variants `Path`/`PATH`, causing MSBuild MSB6001 during compiler detection. `tools/run-clean-env.mjs` normalizes the child environment; CMake then identified MSVC successfully. This does not change machine environment settings.

## Reproduction

From the repository root, with installed tools:

```powershell
node tools/run-clean-env.mjs pwsh -NoProfile -File tools/build-native.ps1
node tools/run-clean-env.mjs pwsh -NoProfile -File tools/dev.ps1 publish
./artifacts/probes/Susu.Probes.exe
```

The executable rejects JIT execution for its NativeAOT result. Individual activation probes do not validate feature behavior (for example, WASAPI activation is not recording).

## Acceptance ledger

| Cases | State | Remaining evidence |
|---|---|---|
| F00.1 | In progress | UI lockfile, remotely executed CI and complete redistribution-license audit |
| F00.2 | In progress | WebView, ELS, IA2 and deeper COM behavior; 11 narrow AOT probes passed |
| F00.3 / X01–X07 | Not executed | AppContainer/IPC access matrix; complete three-route comparison |
| F00.4 / C01–C07 / SEL01–SEL03 | Not executed | Selection helper, clipboard transactions, ten actual applications |
| F00.5 / PER01–PER05 | Not executed | Representative windows, thirty samples, idle/warm tree memory, capture backend |

F01–F03 remain pending the documented hard dependencies. No product entry point is registered and no fake service is shipped.

## Extended checkpoint

`extended-probes.json` records ELS detection, WebView lifecycle and disposable AppContainer resource boundaries passing. `sandbox-network-probes.json` and `sandbox-result.txt` add bounded direct TCP/UDP loopback attempts with successful parent controls and receiver checks, plus denial of a child process. TCP is specifically observed as a deadline without connection, not reported as an OS access-denied error. This is still a subset of X01–X07; authenticated IPC, external-network matrix, memory termination and crash cleanup remain.

The Vue fixture toolchain is now locked and builds successfully. Browser inspection covered Chinese settings/network controls and English main-window layout/card collapse, with no console errors. TypeScript 7 was incompatible with vue-tsc; the tested pin is 5.9.3. Fixtures are explicitly marked and the Vite configuration rejects production-mode packaging.

`selection-probes.json` records published AOT UIA helper tests against a synthetic Win32 Edit control: selected text, empty selection, password field and stalled-helper termination all passed. It does not establish real-app coverage or IA2/clipboard readiness.

`windows-smoke.jsonl` proves the eight-window fixture opens, hides, closes controllers and observes browser process exit. `tools/measure-windows.ps1` is collecting the full process tree at warm/idle timepoints. Early raw samples exceed the 60 MiB target; review the final sample file and keep G0 unpassed unless the actual required budgets and other gates are met.
