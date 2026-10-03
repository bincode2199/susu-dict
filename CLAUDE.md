# Su-Su agent working rules

Project docs start at [README.md](README.md); current state is in [docs/evidence/PROGRESS.md](docs/evidence/PROGRESS.md). These rules keep module work inside the usage budget. F05's coding agent ran 370 turns in one context that grew to 645k tokens without compaction; re-reading that context each turn cost about 4x what the same work needed.

## Physical machine: stay inside the project folder

On the physical Windows machine, every operation stays inside the project folder. Anything that must use another folder, or that installs, changes or updates the system or other software, needs the user's manual confirmation first. This applies to every agent; include it in every sub-agent prompt.

## Master agent: keep sub-agent contexts short

- Start a **fresh coding agent per sub-item** (e.g. F05.1, F05.2, then one per gap batch), not one agent per module. Do the same for testing when a pass is long.
- Don't keep a finished agent alive with `SendMessage` for new work. Follow-ups on the same sub-item are fine; a new sub-item or a new round of gaps gets a new agent.
- Give each new agent a short handoff instead of history: the goal, the commits so far, the files touched, the open issues, and the exact test filter to run. Point it at the module record (`docs/evidence/Fxx/Fxx.md`) rather than pasting long text.
- If an agent reports it has been working for a long time or is re-reading large files, have it commit, write its handoff notes, and stop; continue in a fresh agent.
- Clean the test output after the test work for each feature is done: delete the leftover `%TEMP%\susu-*` folders (skip any still in use). Integration tests copy the host binaries (~12 MB) into a new temp folder per run and don't remove it; 4,665 of them had filled 59 GB by F08.
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

## Writing docs

Full rules are in [docs/CONVENTIONS.md](docs/CONVENTIONS.md). The principles:

- **Hierarchy.** The root [README.md](README.md) indexes every document. Each `docs/` folder has a `README.md` that indexes its documents and is their parent. A new or moved document is listed in its parent index and in the root index, and carries a breadcrumb under its title (`> Parent: [Folder](README.md) · Root: [README](../../README.md)`, or `> 上级：… · 根目录：…`).
- **Progressive disclosure.** Every document opens with its purpose and conclusion in one or two sentences, then a summary (table or short list), then the detail. A reader should be able to stop after the summary.
- **One purpose per document.** If new content answers a different question or serves a different reader, put it in its own document or in the document that already owns that purpose.
- **Status lives in one place.** Project and module status goes in [PROGRESS](docs/evidence/PROGRESS.md) and the module records (`docs/evidence/Fxx/Fxx.md`). Specs and plans link there instead of stating their own status.
- **Stable section numbers.** Code comments cite `ARCHITECTURE 5.1`, `PLAN 4.5.1` and similar. Don't renumber the numbered specs; when a section moves out, leave its heading with a one-line pointer.
- **No sensitive values.** Use a `<PLACEHOLDER>` listed in [SENSITIVE.example](docs/SENSITIVE.example.md); never commit IPs, account names, machine IDs, private links or credentials.
