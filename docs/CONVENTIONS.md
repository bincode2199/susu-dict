# Documentation conventions

> Parent: [README](../README.md)  
> Children: [SENSITIVE.example](SENSITIVE.example.md)

How to add, place and link a document so the tree stays navigable. In short: one purpose per document, one parent per document, every parent indexes its children, and every document opens with its summary.

## The tree

- The repository-root [README](../README.md) is the root. It indexes every document.
- Each folder under `docs/` has a `README.md` that indexes the documents in that folder. That index is their parent.
- A document may have its own children only when they exist to support it alone (for example, [F00](evidence/F00/F00.md) and its memory experiment). It then lists them in a `Children:` / `子文档：` line.
- Code folders do not carry their own docs; their notes live under `docs/architecture/`. Only the root and the `docs/` folder indexes are named `README.md`.

## Adding or moving a document

1. Give it one purpose. If a new section would serve a different reader or a different question, write a separate document.
2. Put a breadcrumb under the title: `> Parent: [Folder](README.md) · Root: [README](../../README.md)` (Chinese docs: `> 上级：… · 根目录：…`).
3. List it in its parent index and in the root [README](../README.md#documentation-index).
4. Keep section numbers stable in the numbered specs (PLAN, ARCHITECTURE, DESIGN, DEV-PLAN, RECORD, WINDOWS-VM-*). Code comments cite them as `ARCHITECTURE 5.1` or `PLAN 4.5.1`. When content moves out, leave the heading with a one-line pointer.

## Writing a document

- **Progressive disclosure.** Open with one or two sentences on what the document is for and what it concludes, then a summary (a table or short list), then the detail. A reader should be able to stop after the summary.
- **Status lives in one place.** Current project and module status belongs in [PROGRESS](evidence/PROGRESS.md) and the module records. Specs and plans link there instead of stating their own status.
- **Evidence.** Raw evidence (JSON, logs) stays in `docs/evidence/Fxx/` because the tools write there. Each module's record is `docs/evidence/Fxx/Fxx.md`, created from [MODULE-TEMPLATE](development/MODULE-TEMPLATE.md).

## Sensitive values

Never commit IP addresses, account names, machine IDs, private links or credentials. Write a placeholder such as `` `<HOST_LAN_IP>` ``, list it in [SENSITIVE.example](SENSITIVE.example.md), and keep the real value in the local, gitignored `docs/SENSITIVE.md`. Passwords, tokens and keys do not belong in either file.
