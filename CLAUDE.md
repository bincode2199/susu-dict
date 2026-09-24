# Su-Su agent working rules

Project docs start at [docs/README.md](docs/README.md); current state is in [docs/evidence/PROGRESS.md](docs/evidence/PROGRESS.md). These rules keep module work inside the usage budget. F05's coding agent ran 370 turns in one context that grew to 645k tokens without compaction; re-reading that context each turn cost about 4x what the same work needed.

## Master agent: keep sub-agent contexts short

- Start a **fresh coding agent per sub-item** (e.g. F05.1, F05.2, then one per gap batch), not one agent per module. Do the same for testing when a pass is long.
- Don't keep a finished agent alive with `SendMessage` for new work. Follow-ups on the same sub-item are fine; a new sub-item or a new round of gaps gets a new agent.
- Give each new agent a short handoff instead of history: the goal, the commits so far, the files touched, the open issues, and the exact test filter to run. Point it at the module record (`docs/evidence/Fxx/Fxx.md`) rather than pasting long text.
- If an agent reports it has been working for a long time or is re-reading large files, have it commit, write its handoff notes, and stop; continue in a fresh agent.
- Include the two rule sections below in every coding and testing agent prompt (or point the agent at this file).

## Build and test output

- While iterating, run only the affected tests:
  `dotnet test --project tests/Susu.Tests.Unit -c Release --no-restore --filter-class "*NetworkBrokerTests"`
  (xUnit v3 on Microsoft.Testing.Platform; `--filter-method` also works). Run the full suite (`./tools/dev.ps1 test`) once before each commit, not after every edit.
- Keep only the tail of build and test output in context. In PowerShell: `$out = <command> 2>&1; $LASTEXITCODE; $out | Select-Object -Last 15`. On failure, search the output for the error lines (`$out | Select-String 'error|failed' | Select-Object -First 20`) instead of printing the whole log.
- Don't publish NativeAOT (`dotnet publish src/Susu.Host ...`) unless the change affects the sandbox or host binary and a real-sandbox test needs it.

## Reading code and docs

- Search first (Grep, `Select-String`), then read only the lines you need (Read with `offset`/`limit`, `sed -n 'a,bp'`). Don't read large files end to end: `bridge.c`, `PluginHostIntegrationTests.cs`, `HostSession.cs`, `Broker.cs`, `ChildHost.cs` and the planning docs are each 10–25k characters.
- Don't re-read a file you already have in context unless it changed.
- For docs, read the section named in the task (`DEV-PLAN § F05`, specific TEST-PLAN rows), not the whole document.
- Prefer `Edit` over rewriting whole files with `Write`.
