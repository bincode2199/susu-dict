# Su-Su (susu-dict)

Su-Su is a Windows 11 desktop translator that lives in the system tray. A hotkey starts a capture mode (typed input, text selection, clipboard, OCR screen region, microphone, system audio or a video file), and each configured service returns its result in its own card. The UI supports Simplified Chinese and English, and the translation pair is Simplified Chinese ↔ English.

This page is the root of the documentation. It says where to start, then indexes every document.

## Start here

| If you want to… | Read |
|---|---|
| Know where the project stands and where to resume | [Progress](docs/evidence/PROGRESS.md) |
| Build and run the code | [Development build](docs/development/BUILD.md) |
| Understand what the product does | [Product](docs/product/README.md) |
| Understand how it is built and where the code lives | [Architecture](docs/architecture/README.md) |
| Pick up the next module | [Development](docs/development/README.md) |
| Write a plugin | [Plugin author guide](docs/development/PLUGIN-AUTHOR-GUIDE.md) |
| Write or move a document | [Documentation conventions](docs/CONVENTIONS.md) |

Agent working rules are in [CLAUDE.md](CLAUDE.md). Most planning documents are written in Chinese; build notes and evidence records are in English.

## Documentation index

Each folder has a `README.md` that indexes its documents. Every document names its parent in a breadcrumb line under its title.

- [README](README.md): this page.
  - [Product](docs/product/README.md): what Su-Su does and why.
    - [PLAN](docs/product/PLAN.md) · 项目方案: the authoritative product spec: features, services, plugin API, data, flows, milestones.
    - [RECORD](docs/product/RECORD.md) · 方案记录: decision log (D-xx), rejected options, research and measurements.
    - [REVIEW](docs/product/REVIEW.md) · 技术方案评审: findings R01–R13 of the third-version review and how each was resolved.
  - [Design](docs/design/README.md): how Su-Su looks and behaves.
    - [DESIGN](docs/design/DESIGN.md) · 设计基础: visual and interaction spec: colors, type, controls, cards, windows, accessibility, artboard index.
    - [ARTBOARDS](docs/design/ARTBOARDS.md) · 画板源文件: the `design/` artboard sources, how to view and restore them, editing rules.
    - [ARTBOARD-REVISIONS](docs/design/ARTBOARD-REVISIONS.md) · 画板修订记录: the third- and fourth-version artboard change lists (both done).
  - [Architecture](docs/architecture/README.md): how Su-Su is built; technology choices and repository layout.
    - [ARCHITECTURE](docs/architecture/ARCHITECTURE.md) · 技术架构设计 (A1): processes, dependencies, threading, job state, IPC, storage, windows, updates, performance.
    - [PROTOCOL](docs/architecture/PROTOCOL.md): `protocol/`: contract versioning and IPC compatibility samples.
    - [NATIVE](docs/architecture/NATIVE.md): `native/`: native dependency and QuickJS bridge rules.
    - [UI](docs/architecture/UI.md): `ui/`: the production UI and the F00 fixture.
  - [Development](docs/development/README.md): how the work is planned, built and accepted.
    - [DEV-PLAN](docs/development/DEV-PLAN.md) · 模块开发计划: modules F00–F19, quality gates M0–M5, dependencies and exit criteria.
    - [TEST-PLAN](docs/development/TEST-PLAN.md) · 开发与发布验收计划: acceptance cases (C, B, T, A, S, X, J, PER, SEL, CFG, UI, DATA, UPD, …).
    - [BUILD](docs/development/BUILD.md): toolchain, environment, build, run and test commands.
    - [PLUGIN-AUTHOR-GUIDE](docs/development/PLUGIN-AUTHOR-GUIDE.md): `susu-plugin` init/check/test/pack, capability templates, cases file format, permission model, signing.
    - [MODULE-TEMPLATE](docs/development/MODULE-TEMPLATE.md): template for a module delivery record.
    - [THIRD-PARTY-NOTICES](docs/development/THIRD-PARTY-NOTICES.md): dependency and license inventory.
  - [Evidence](docs/evidence/README.md): what has been delivered and verified.
    - [PROGRESS](docs/evidence/PROGRESS.md): current state, next steps and condensed history.
    - [F00](docs/evidence/F00/F00.md): foundation and feasibility probes.
      - [window-memory-analysis](docs/evidence/F00/window-memory-analysis.md): eight-window UI memory experiment.
    - [F01](docs/evidence/F01/F01.md): protocol, domain and job framework.
    - [F02](docs/evidence/F02/F02.md): configuration, credentials and data foundation.
    - [F03](docs/evidence/F03/F03.md): native shell and production UI.
    - [F04](docs/evidence/F04/F04.md): plugin runtime, IPC and sandbox.
    - [F05](docs/evidence/F05/F05.md): network broker and executable plugin contracts.
    - [F06](docs/evidence/F06/F06.md): input translation (first product loop).
    - [F07](docs/evidence/F07/F07.md): service settings and prompts.
    - [F08](docs/evidence/F08/F08.md): selection and clipboard translation.
    - [F09](docs/evidence/F09/F09.md): dictionary card.
    - [F10](docs/evidence/F10/F10.md): pronunciation.
    - [F11](docs/evidence/F11/F11.md): screenshot OCR.
    - [F12](docs/evidence/F12/F12.md): microphone recording and ASR.
    - [F13](docs/evidence/F13/F13.md): system audio translation.
    - [F14](docs/evidence/F14/F14.md): video transcription and subtitles.
    - [F15](docs/evidence/F15/F15.md): favorites, export and sync.
    - [F16](docs/evidence/F16/F16.md): plugin management and developer tools.
    - [F17](docs/evidence/F17/F17.md): backup, restore and diagnostics.
  - [Windows VMs](docs/vm/README.md): the development and clean-test virtual machines.
    - [WINDOWS-VM-OPERATIONS](docs/vm/WINDOWS-VM-OPERATIONS.md) · 运行与维护手册: current spec, daily operation, maintenance, troubleshooting, rebuild.
    - [WINDOWS-VM-DEPLOYMENT](docs/vm/WINDOWS-VM-DEPLOYMENT.md) · 部署记录: what was deployed and verified.
    - [WINDOWS-VM-PLAN](docs/vm/WINDOWS-VM-PLAN.md) · 安装部署计划 (V5): decisions, layout, install phases and acceptance criteria.
    - [WINDOWS-VM-REVIEW](docs/vm/WINDOWS-VM-REVIEW.md): multi-round review of the VM plan and its implementation.
  - [CONVENTIONS](docs/CONVENTIONS.md): where documents go, how they link, and how sensitive values are handled.
    - [SENSITIVE.example](docs/SENSITIVE.example.md): the placeholders that stand in for sensitive values.
