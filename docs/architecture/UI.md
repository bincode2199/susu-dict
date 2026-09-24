# Production UI

> Parent: [ARCHITECTURE](ARCHITECTURE.md) (section 9, windows and production UI) · Catalog: [docs/README](../README.md)

The repository folder `ui/` contains the **F00 synthetic Vue SFC fixture**, not the production F03 application. Design HTML ([artboards](../design/ARTBOARDS.md)) is reference material only and is never loaded. F03 depends on the F00 feasibility gate and F02 storage services.

In `ui/`, run `pnpm install --frozen-lockfile --ignore-scripts`, `pnpm run build:probe`, and `pnpm run preview`. Preview is loopback-only on port 4173. Query `kind` selects main/settings/selection/ocr/voice/transcribe/tray/error; `lang=en` selects English. No action invokes a real provider or persists settings. Production packaging is deliberately rejected until the real bridge and services exist.
