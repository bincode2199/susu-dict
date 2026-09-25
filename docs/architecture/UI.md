# Production UI

> Parent: [Architecture](README.md) · Root: [README](../../README.md)

`ui/` holds two separate builds:

| Folder | Build | Output | Purpose |
|---|---|---|---|
| `ui/src/` + `ui/index.html` | `pnpm run build` | `ui/dist/` (shipped as `ui/` next to `susu.exe`) | Production UI from F03 |
| `ui/probe/` | `pnpm run build:probe` | `ui/dist-probe/` | F00 synthetic eight-window fixture, used only by the F00 memory and latency probes |

The design HTML ([artboards](../design/ARTBOARDS.md)) is reference material only and is never loaded.

## Structure

- `src/main.ts` reads `?w=<kind>&s=<session>&l=<lang>` from its own address and connects to `window.chrome.webview`. It does nothing when opened outside Su-Su.
- `src/bridge/bridge.ts` is the typed channel. The page sends only `Ready` and whitelisted commands (`UI_COMMANDS`, generated from `Susu.Contracts.UiCommands`) with a correlation id. It accepts only envelopes with its UI version and session, applies them in sequence order, and sends `Ready` again after a gap to get a fresh snapshot.
- `src/bridge/store.ts` is the projection: snapshot, then card patches (by revision; patches from a newer generation wait for the `translation` event), settings/window events, and the `window.hidden` counter.
- `src/windows/` has one root per window kind (Main, Settings, Tray), each loaded as its own chunk. `src/components/` holds the shared controls (title bar, result/source card, toggle, stepper, setting row, one-way secret field, hotkey recorder) and 16 px line icons.
- `src/locales/i18n.ts` has zh-Hans and English resources; English is typed to cover every Chinese key. `src/styles/tokens.css` holds every colour as a token, with light values and reserved dark values.
- Types come from `protocol/generated/ui.ts` (alias `@protocol`). Never edit that file; run `tools/Susu.ContractsGen`.

## Rules the code enforces

- Service text is rendered only through text interpolation. `v-html`, `innerHTML`, `eval`, browser storage and network APIs are absent from `src/`; a UI test scans for them.
- The CSP in `index.html` allows only same-origin script, style and image, with no connections, frames, objects, forms or inline script. The native host additionally blocks foreign navigation, frames, new windows, downloads, permissions and foreign sub-resources, and forwards web messages only from the window's trusted origin (`app.susu.example`, or `settings.susu.example` for Settings).
- A saved secret is shown only as "Saved" with Replace/Delete. A new value stays in the component until it is saved, cancelled, the page changes or the window hides.
- Title bars are CSS drag regions (`app-region: drag`, WebView2 non-client region support); buttons opt out.

## Commands

```powershell
cd ui
pnpm install --frozen-lockfile --ignore-scripts
pnpm test            # vitest + happy-dom
pnpm run build       # vue-tsc type check + production bundle
pnpm run dev         # http://127.0.0.1:5173/?w=main&s=dev&l=zh-Hans (development stand-in host)
```

In the dev server, `src/bridge/devHost.ts` answers with fixture data so layouts can be reviewed in a browser. It is imported only under `import.meta.env.DEV` and is absent from `ui/dist`. The dev server also relaxes the CSP (inline styles and the reload websocket); production output keeps the strict policy.
