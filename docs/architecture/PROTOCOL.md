# Protocol sources

> Parent: [Architecture](README.md) · Root: [README](../../README.md)

Covers the repository folder `protocol/`: `protocol/generated/` (generated TypeScript contracts) and `protocol/ipc/samples/` (IPC compatibility samples).

API v1 is not frozen until G1. Contracts and generated TypeScript are verified together (`dotnet run --project tools/Susu.ContractsGen -c Release -- . --check`). Plugin protocol, UI protocol and manifest schema have separate version fields; UI never receives invocation grants or stored credentials.

## IPC compatibility samples

`protocol/ipc/samples/valid-*.json` must decode; `protocol/ipc/samples/invalid-<case>.<IpcDecodeError>.json` must be rejected with that error by
`IpcCodec.TryDecode` (tests/Susu.Tests.Unit `Compatibility_samples_decode_as_declared`). Add a sample whenever the
envelope changes; never edit an existing valid sample to make a breaking change pass.
