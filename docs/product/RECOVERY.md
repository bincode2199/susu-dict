# Recovering from backup, restore and settings problems · 备份与恢复故障说明

> Parent: [Product](README.md) · Root: [README](../../README.md)

What to do when exporting, restoring or starting with a backup goes wrong. In short: a restore only ever replaces your settings and keys at the next start, and it keeps the previous pair first, so a failure leaves the old configuration as it was and you can try again or undo.

| What happened | Old configuration | What to do |
|---|---|---|
| Wrong password (`decrypt-failed`) | Untouched; nothing was staged | Enter the password again, as often as you like. A file that was edited gives the same message on purpose. |
| Disk full or no permission while exporting (`disk-full`, `write-failed`) | Untouched; an existing backup file with the same name is kept; no half file is left | Free space or choose another folder, export again. |
| Disk full while preparing a restore (`disk-full`, `stage-failed`) | Untouched; the half-prepared files are removed | Free space, choose the backup again. |
| Disk full or lock while switching at start (`disk-full`, `io`) | Put back exactly (both files, or neither); the page says the restore failed | Free space, choose the backup again. |
| Power loss or crash during the switch | Entirely old or entirely new, never half; the next start finishes or recognises the switch | Just start Su-Su again. Check Settings, Backup, "last restore". |
| Backup from a newer Su-Su (`format-newer`, `schema-newer`) | Untouched; nothing was staged | Update Su-Su, then restore. |
| Backup from an older Su-Su | Replaced only after you confirm; the settings are upgraded on the way in | Nothing; the preview says "older version". |
| A restore went the wrong way | The pair from before the restore is kept (one generation) | Settings, Backup, "Undo last restore", then restart. |
| `settings.yaml` damaged at start | Previous saved version is used if it is valid; else defaults in memory; the damaged file is kept aside before the next save | Fix the file, or use "Undo last restore" if a restore point exists, or restore a backup. |
| Database newer than the app (after an app downgrade) | Not touched; the pre-upgrade copy `susu.db.v<N>.bak` is kept | Install the newer app, or quit and restore that copy as `susu.db`. |

Each section below says what happened, whether the old configuration is intact, and what to do. The texts in the Backup page (Chinese and English) carry the same three points per error code; the drills that prove them are listed in [F17](../evidence/F17/F17.md).

## Wrong password

- **What happened.** The file is encrypted and the password did not open it. A file that was changed after export fails the same check, with the same message and no hint which of the two it was.
- **Old configuration.** Intact. A failed password writes nothing: no staged files, no pending record. There is no lockout; each try costs one key derivation (bounded by the file's recorded limit of at most 2,000,000 rounds), so guessing is slow by design.
- **What to do.** Try again. If the password is lost the backup cannot be opened; export a new one from a machine that still has the settings.

## Disk full

- **While exporting.** The file is written next to its target under a temporary name and moved into place only when complete. If writing fails, the temporary file is deleted and an existing backup with the same name stays as it was. Message: `disk-full` (space) or `write-failed` (in use, no permission, bad path). Free space or pick another folder.
- **While preparing a restore.** Only the import staging folder is written at this step. A failure removes the staged files and the live `settings.yaml` and `secrets.dat` are not touched. Message: `disk-full` or `stage-failed`.
- **While switching at the next start.** The switch uses the configuration journal: both files change together or not at all. A write failure puts the first file back and drops the import; the Backup page shows "last restore failed" with `disk-full` or `io`. The restore point of the previous import is only replaced after the new one is complete, so it survives a failure there too.

## Power loss or crash

- **What happened.** Su-Su stopped while switching to a restored configuration (at any of 13 named steps, from "import begin" to "done").
- **Old configuration.** Both files are entirely old or entirely new after the journal recovery that runs first at every start. A copy of the old pair is in the restore point before anything is replaced.
- **What to do.** Start Su-Su. It finishes the switch (or notices it already finished). After three starts that crash inside the same import it is abandoned (`too-many-attempts`) and the old configuration stays. A database upgrade interrupted by a crash leaves the database at the last upgrade step that completed (each step is all or nothing), with its data and a complete copy from before the upgrade; the next start continues from there.

## Version incompatibility

- **Backup newer than the app** (`format-newer`: file layout, `schema-newer`: settings layout). Refused at preview. Nothing is staged and nothing changes. Update Su-Su, then restore.
- **Backup older than the app.** Accepted; the settings are upgraded to the current layout during preview and the page says so.
- **Database newer than the app** (you started a newer version, then went back to an older one). The older app refuses to open it and names the copy made before the upgrade (`susu.db.v<N>.bak`). Restoring that copy returns the database to the older version; anything saved after the upgrade is not in it.
- **A database upgrade that fails** (an error in a step, a full disk while copying) undoes that step; the database stays at the old version with its data. The copy from before the upgrade is written under a temporary name and moved into place only when complete, so a full disk never leaves a partial copy and an earlier good copy survives. The app works as before.
- **What an older app shows.** A message in the interface language: the database is newer, nothing was changed, install the newer Su-Su, or restore the named copy as `susu.db` after quitting.

## Settings file damaged at start

- **What happened.** `settings.yaml` cannot be read (hand edit, disk error).
- **Old configuration.** Su-Su loads the previous saved version (`settings.yaml.prev`) when it is valid, otherwise the defaults, in memory only. The damaged file is not overwritten until you save, and a copy is kept aside as `settings.invalid-<time>.yaml` before the first save.
- **What to do.** Open `settings.yaml` and fix the line the Settings page names, or restore a backup, or choose "Undo last restore" when one exists (it brings back the pair from before the last restore at the next start).

## Undo after a restore

The pair before the last restore stays as the restore point. "Undo last restore" schedules it for the next start and goes through the same safe switch. The undo keeps its own restore point, so you can go back and forth once. A second normal restore replaces the restore point.

## Where things are

| Item | Location |
|---|---|
| Staged import, pending record, last result, restore point | `%LOCALAPPDATA%\Su-Su\import\` |
| Configuration journal (only exists during a switch) | `%LOCALAPPDATA%\Su-Su\transactions\` |
| Previous saved settings | `settings.yaml.prev` beside `settings.yaml` |
| Database copy before an upgrade | `susu.db.v<N>.bak` beside `susu.db` |
