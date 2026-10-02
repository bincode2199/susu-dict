# Plugin author guide

> Parent: [Development](README.md) · Root: [README](../../README.md)

How to write, test, package and sign a Su-Su plugin with the `susu-plugin` command line. You start from a template for the capability you want (`init`), validate it without running anything (`check`), replay your own request/response cases through the same sandbox, IPC channel and broker the product uses (`test`), and build the installable `.susuext` (`pack`). Nothing in `test` is a Node or in-process stand-in: if a plugin only works outside the sandbox, `test` fails.

| Step | Command | What it proves |
|---|---|---|
| 1 | `susu-plugin init <dir> --capability <name>` | A working starter package and a passing cases file |
| 2 | `susu-plugin check [<dir>]` | Manifest, file list, entry file, cases file and signature are valid (no sandbox) |
| 3 | `susu-plugin test [<dir>] [--host <susu.exe>]` | Each case gives the expected result or error class in the real sandbox |
| 4 | `susu-plugin keygen <file>`, `susu-plugin pack [<dir>] [--key <file>]` | An installable, optionally signed package |

Specifications behind this page: [PLAN 4.4-4.8](../product/PLAN.md) (interfaces, host API, manifest, package format), [PROTOCOL 10](../architecture/PROTOCOL.md) (signature). Status of the tool is in the [F16 record](../evidence/F16/F16.md).

## Get the tools

`susu-plugin.exe` and `susu.exe` are built from this repository (they are not in the installer):

```
dotnet publish src/Susu.Host -c Release -r win-x64          # susu.exe (the plugin host)
dotnet publish tools/Susu.PluginCli -c Release -r win-x64   # susu-plugin.exe
```

`test` needs a `susu.exe`: it takes the one next to `susu-plugin.exe`, or `--host <path>`. Both run on Windows only (the sandbox is an AppContainer). Exit codes of every command: `0` success, `1` the package or a case failed, `2` usage or an unreadable input (bad arguments, malformed cases file, no host, no cases file).

## Package layout

A package is a folder (zipped into a `.susuext`) with `manifest.yaml` at its root:

```
manifest.yaml            required: identity, capabilities, hosts, credential names, config schema
main.js                  the entry (a different name goes in `entry:`); `export default { <capability>() {...} }`
lib/                     optional extra files
signature                optional, written by `pack --key`
susu-plugin.test.json    author only: the cases file; never staged for `test` as plugin code, never packed
```

Manifest fields (all validated by `check`): `id` (reverse-domain), `name`, `version` (`major.minor.patch`, digits only), `apiVersion: 1`, `minHost: 1`, `entry`, `capabilities`, `hosts`, `credentialUse`, `config`. Limits: 256 files, 4 MiB per file, 16 MiB in total, no symlinks, no reserved device names, no nested archives.

## Capability templates

`susu-plugin init <dir> --capability <name> [--id com.you.name] [--name "Display name"]` writes `manifest.yaml`, `main.js` and `susu-plugin.test.json` into a new or empty folder (it never overwrites). Without `--id` the package id is `com.example.<folder>`; change it before you publish. All templates call a fictional "Example API" through `ctx.$http`, read the vendor address from `ctx.config.baseUrl` and map vendor failures to the host's error classes.

| Capability | Method in `main.js` | Request (from the host) | Result | Template shows |
|---|---|---|---|---|
| `translate` | `translate(req, ctx)` | `{ text, from?, to?, prompt? }` | `{ text, detectedFrom? }` | JSON POST, key in a header, status to error class |
| `dictionary` | `dictionary(req, ctx)` | `{ word }` | `{ word, phonetics[], parts[], forms?, examples? }` | Keyless GET, unknown word is an empty entry, not an error |
| `detect` | `detect(req, ctx)` | `{ text }` | `[{ lang, confidence }]` | Array result |
| `ocr` | `ocr(req, ctx)` | `{ image: FileHandle, lang? }` | `{ blocks: [{ text, box?, kind?, confidence? }] }` | `bodyFiles`: the host fills a JSON field with the image, the plugin never holds the bytes |
| `tts` | `tts(req, ctx)` | `{ text, voice?, rate?, lang? }` | `{ audio: FileHandle }` | `responseFiles`: the host decodes a Base64 field into a file handle |
| `asr` | `asr(req, ctx)` | `{ audio: FileHandle, model, output: "text"\|"segments", lang? }` | `{ kind: "text", text }` or `{ kind: "segments", segments: [{ start, end, text }] }` | Multipart upload built from a file handle |
| `vocab` | `vocab(req, ctx)` | `{ operationId, action: "upsert"\|"lookup", entryRevision, word, lang, content?, entryId? }` | `{ status: "applied"\|"found"\|"absent"\|"unknown", remoteId? }` | Idempotency key, and "unknown, never applied" for an unreadable answer |

Shapes are generated from `src/Susu.Contracts/PluginApi.cs` into `protocol/generated/susu-plugin.d.ts`. The shipped packages under `src/Susu.Host/plugins` are the reference for real vendors (streaming translate: `openai`; signed requests: `tencent-translate`; loopback HTTP: `ankiconnect`; options lists and voices: `openai`, `google-tts`). A package may also export `options` (a config field's dynamic list) and `voices`; a case can call them by name.

Inside a method you have the host API of PLAN 4.5: `ctx.$http(req)` and `ctx.$http.stream(req)`, `ctx.$emit(chunk)` for streamed text, `ctx.$store` (private key-value storage, kept across updates), `ctx.config` (the user's settings for this instance), and the global `PluginError(kind, detail, retryAfter?)`. Throw `PluginError` with one of `auth`, `quota`, `rate_limited`, `network`, `timeout`, `unsupported_language`, `bad_response`; anything else the host reports as `bad_response`. Make one vendor request per call: the host owns retries.

## Permission model

A plugin has no ambient authority. What it may do is what its manifest declares and the user confirmed at install.

| Permission | Where declared | What the host enforces |
|---|---|---|
| Network | `hosts:` (a bare name means `https://name:443`; a full origin such as `http://127.0.0.1:8765` is for loopback services) | Calls go through the host's network layer: only the granted origins, HTTPS by default, no redirects with credentials, DNS answers pointing to private or loopback addresses refused for cloud origins, request and response size limits |
| Credentials | `credentialUse:` lists the secret names (for example `apiKey`) | The plugin names a secret in a `credentials` field (header, query or JSON target); the host writes the value when the request leaves. The plugin never sees it and the response is scanned for it. Plain text is never interpolated |
| Files | Handles in the request (`image`, `audio`) | A handle is an opaque id; the plugin cannot open a path or read bytes. The host puts the file into a body, multipart field or JSON field |
| Storage | `ctx.$store` | A private key-value store per package id, with quotas |
| Signing | `sign: { scheme: ... }` on a request | Named signers in the host (for example `tencent-tc3`) sign the final bytes; the plugin never holds the key |

At install the user sees the package identity, its signer (if any) and the permissions it asks for; an update that adds hosts, credentials or capabilities, changes the signer or drops the signature is held until the user confirms (F16.1/F16.2). The sandbox itself is an AppContainer process with no file, registry or network access of its own, running QuickJS.

## Check

`susu-plugin check [<dir>]` reads the package without starting anything: manifest schema, safe file list, that the entry file exists and has `export default`, a warning for a declared capability that has no method of that name, that `susu-plugin.test.json` parses (and uses only declared capabilities), and the signature if there is one (an invalid signature is an error; an unsigned package is allowed). A warning is printed on stdout and does not fail the check.

## Test

`susu-plugin test [<dir>] [--cases <file>] [--filter <text>] [--host <susu.exe>]` stages `susu.exe` and your package (without author-only files) into a temporary folder, starts the real plugin host in its AppContainer, and for each case:

1. starts a loopback "vendor" server that answers with the case's canned responses (in order; the last one repeats; with none it answers 404);
2. calls the capability with your request, a loopback-only origin grant and the case's test secrets, and with `ctx.config.baseUrl` set to the loopback address (unless the case sets its own);
3. compares the vendor requests the plugin sent, then the result or the error class.

A case that times out, or whose plugin never returns, ends with the error class `timeout`, and the host is restarted for the next case. The plugin only reaches the loopback vendor: nothing is sent to the real vendor, so the case file is safe to run offline and without a key. The ad-hoc form `susu-plugin test <dir> --host <susu.exe> --capability <name> --request <json>` makes one call against the manifest's real hosts, with no secrets.

Output: `PASS`/`FAIL` per case with a reason on stderr, then `N passed, M failed`. Exit `0` only if every case passed.

### The cases file

`susu-plugin.test.json` in the package folder (or any path with `--cases`). Comments and trailing commas are allowed. The file is strict: an unknown key, a wrong type or a missing field exits `2` and names the JSON path (for example `$.cases[0].bogus: unknown key`).

```json
{
  "version": 1,
  "cases": [
    {
      "name": "translates a sentence",
      "capability": "translate",
      "request": { "text": "hello", "from": "en", "to": "zh-Hans" },
      "config": { "model": "fast" },
      "secrets": { "apiKey": "test-key" },
      "vendor": [ { "status": 200, "json": { "translation": "你好" } } ],
      "expectRequests": [ { "method": "POST", "path": "/v1/translate", "bodyJson": { "q": "hello" }, "headerContains": { "Authorization": "Bearer test-key" } } ],
      "expect": { "result": { "text": "你好" } },
      "timeoutMs": 10000
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `name` | Required, unique. `--filter` selects cases whose name contains the text |
| `capability` | Defaults to the first declared capability. Must be declared (`voices` and `options` are allowed) |
| `request` | Required. The JSON the host would send. A string containing `$vendor` gets the loopback origin; `{ "$file": "<name>" }` becomes the file handle of an entry in `inputs` |
| `config` | The instance settings (`ctx.config`). `baseUrl` defaults to the loopback origin |
| `secrets` | Test values for the secret names in `credentialUse`; only these names are granted for the case |
| `inputs` | `{ name: { text \| base64, mime?, extension?, durationMs?, width?, height? } }`: real files the host leases; the plugin gets only the handle |
| `vendor` | Canned answers: `status` (default 200), one of `json` / `text` / `base64` (default empty), `contentType`, `headers`, `delayMs` |
| `expectRequests` | Optional list; if present its length must equal the number of vendor requests. Each entry: `method`, `path` (exact, URL-decoded, with query), `pathContains`, `bodyContains`, `bodyJson` (subset match), `headerContains` (substring per header) |
| `expect` | Required, exactly one of `result` (subset match) or `error` (one of `auth`, `quota`, `rate_limited`, `network`, `timeout`, `unsupported_language`, `bad_response`, `cancelled`, `busy`, `unavailable`), optionally with `detailContains` |
| `timeoutMs` | 100 to 120000, default 10000 |

Matching rules for `result` and `bodyJson`: objects match when every expected key is present and matches (extra keys are fine); arrays match item by item and by length; numbers compare as numbers; the string `"$any"` matches any present value (use it for file handle ids).

Write at least: one success per capability you declare, one case per vendor failure you map (`auth`, `rate_limited`, `quota`), a vendor answer in the wrong shape (expect `bad_response`), and for `vocab` an unreadable answer after a write (expect `unknown`, never `applied`). The shipped packages' cases are in `tests/plugin-cases/<package>.test.json` and show the same on real vendors.

### Web APIs in the sandbox

The plugin runtime has no network or file primitives of its own; besides `ctx.$http` and friends it offers `crypto.getRandomValues` (integer typed arrays, at most 65536 bytes per call, else `QuotaExceededError`), `crypto.randomUUID`, `crypto.subtle.digest` (`SHA-1`, `SHA-256`, `SHA-384`, `SHA-512`; resolves an `ArrayBuffer`) and `URLSearchParams` (form-urlencoded: `+` is a space, spaces are written back as `+`). Nothing else of Web Crypto exists (no `sign`, `importKey`, HMAC): request signing is not done in plugin code (PLAN 4.3), and `digest` is for content hashes and de-duplication. The random bytes come from the operating system and a plugin never sees host keys. A `translate` case that exercises all of them is in `PluginAuthorCliTests`.

## Package and sign

`susu-plugin pack [<dir>] [--out <file.susuext>] [--key <seed file>]` runs the same checks as `check`, zips the package without the author-only files (`susu-plugin.test.json`, `*.test.json` at the top level, a `tests/` folder, an old `signature`) and writes `<id>-<version>.susuext` (or `--out`). Install it from Settings > Plugins (pick the file or drop it on the window): the host unzips it with the safe unzip rules, shows the permission list and signer, and activates it in a transaction that rolls back if the new version cannot load.

Signing is optional. `susu-plugin keygen <file>` writes a new private seed (64 hex characters; it refuses to overwrite) and prints the key id, which is the 64-hex-character public key. Keep the seed secret and out of the repository. `pack --key <file>` adds a `signature` entry: an Ed25519 signature over the manifest bytes and the sorted path and SHA-256 list of every other file, tagged `susu-plugin-v1`. After signing, any change to any file invalidates it, and `check` verifies it on an unpacked package. A third-party key is shown to the user at install as the identity they accept; it is not an endorsement by Su-Su. Once users have installed a signed version, later versions must keep the same key (a changed signer or a dropped signature is held for the user to confirm). Only keys in the host's keyring count as host-signed, which is how a package may replace a built-in one.

Updating an installed package needs a higher `version`; its `$store` data is kept. There is no plugin marketplace and no update server yet (F16.2, F18), so distribute the `.susuext` yourself.
