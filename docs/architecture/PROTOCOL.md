# Protocol sources

> Parent: [Architecture](README.md) · Root: [README](../../README.md)

Covers the repository folder `protocol/`: `protocol/generated/` (generated TypeScript contracts) and `protocol/ipc/samples/` (IPC compatibility samples).

API v1 is not frozen until G1. Contracts and generated TypeScript are verified together (`dotnet run --project tools/Susu.ContractsGen -c Release -- . --check`). Plugin protocol, UI protocol and manifest schema have separate version fields; UI never receives invocation grants or stored credentials.

## IPC compatibility samples

`protocol/ipc/samples/valid-*.json` must decode; `protocol/ipc/samples/invalid-<case>.<IpcDecodeError>.json` must be rejected with that error by
`IpcCodec.TryDecode` (tests/Susu.Tests.Unit `Compatibility_samples_decode_as_declared`). Add a sample whenever the
envelope changes; never edit an existing valid sample to make a breaking change pass.

Split payloads (PLAN 4.5 item 4, added in F09): a payload larger than one 1 MiB frame travels as consecutive envelopes. Each envelope carries a Base64 slice of at most 512 KiB in `payload` and an optional `part {transferId, index, count}` (2 ≤ count; the total reassembles to at most 4 MiB). Parts must arrive in order with the same type and request identity. A part that is out of order, has a different identity, or pushes the total past 4 MiB is a protocol error; an incomplete transfer is dropped after 30 s. A payload over 4 MiB is refused before sending (`bad_response`). Samples: `valid-apiresult-part.json`, `invalid-part-index-out-of-range.MalformedJson.json`.
