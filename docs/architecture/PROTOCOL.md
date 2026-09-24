# Protocol sources

> Parent: [ARCHITECTURE](ARCHITECTURE.md) (section 6, IPC and UI protocol) · Catalog: [docs/README](../README.md)

Covers the repository folder `protocol/`: `protocol/generated/` (generated TypeScript contracts) and `protocol/ipc/samples/` (IPC compatibility samples).

API v1 is not frozen. Contracts and generated TypeScript must be verified together before F01 acceptance. Plugin protocol, UI protocol and manifest schema have separate version fields; UI never receives invocation grants or stored credentials.

## IPC compatibility samples

`protocol/ipc/samples/valid-*.json` must decode; `protocol/ipc/samples/invalid-<case>.<IpcDecodeError>.json` must be rejected with that error by
`IpcCodec.TryDecode` (tests/Susu.Tests.Unit `Compatibility_samples_decode_as_declared`). Add a sample whenever the
envelope changes; never edit an existing valid sample to make a breaking change pass.
