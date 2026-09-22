# Development build

Windows 11 x64; .NET SDK **10.0.401** (`global.json`); VS 2022 Build Tools C++ workload, MSVC **14.44.35207**, Windows SDK **10.0.26100.0**. Node **24.19.0** is used by the child-environment wrapper. The production UI toolchain is not yet installed/locked.

The current deliverable is an F00 feasibility harness, not a usable translator. Follow [progress](docs/evidence/PROGRESS.md) and [F00 evidence](docs/evidence/F00/README.md).

```powershell
# SDK may be installed globally or under .tools/dotnet.
./tools/dev.ps1 restore
./tools/fetch-f00-dependencies.ps1
node tools/run-clean-env.mjs pwsh -NoProfile -File tools/build-native.ps1
node tools/run-clean-env.mjs pwsh -NoProfile -File tools/dev.ps1 publish
./artifacts/probes/Susu.Probes.exe
```

Restore/fetch require network access. Run Windows probes as the actual dev user: a restricted token cannot access the user's DPAPI key store. Native libraries must remain next to the published executable. No user settings directory is accessed by the harness; all input data is synthetic. DPAPI uses the current Windows user's OS key store.

`tools/Susu.JintProbe` is a separate strict NativeAOT comparison, currently failing AOT/trim analysis. It is intentionally not part of the passing solution build. Do not suppress those diagnostics to label the engine compatible.

Use the published probe runner for current checks. The domain/unit test harness will be added with F01.
