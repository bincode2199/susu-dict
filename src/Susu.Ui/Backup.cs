using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Ui;

/// <summary>
/// F17.1 backup and restore on the Settings page. The page never sends a path: the host opens the file dialogs (<see cref="BackupPicker"/>) and keeps the
/// chosen path, an encrypted file waits for its password (<c>Backup.Unlock</c>), and only a token comes back to confirm. A confirmed import is switched
/// at the next start by the storage layer, never here. Passwords travel in the command, are used once and are not kept or logged.
/// Every Settings-window command answers with the fresh settings view.
/// </summary>
public sealed partial class ShellCoordinator
{
    private IBackupService? backup;
    private string? backupPath;
    private bool backupLocked;
    private BackupPreview? backupPreview;
    private BackupOutcomeView? lastBackup;
    private int backupBusy;

    /// <summary>The open/save dialogs. Null: the page offers neither export nor import.</summary>
    public IBackupFilePicker? BackupPicker { get; set; }

    /// <summary>The backup service. Null: the page has no backup section.</summary>
    public IBackupService? Backup
    {
        get => backup;
        set => backup = value;
    }

    private BackupView? ProjectBackup()
    {
        if (backup is not { } service) return null;
        var status = service.Status();
        string step = backupPreview is not null ? "preview" : backupLocked && backupPath is not null ? "password" : "idle";
        BackupPreviewView? preview = backupPreview is { } p ? new BackupPreviewView(p.Token, p.Created?.UtcDateTime.ToString("O"), p.AppVersion, p.Encrypted, p.IncludesSecrets, p.SchemaOlder,
            [.. p.Deltas.Select(d => new BackupDeltaView(d.Area, d.Current, d.Backup, d.Differs))],
            [.. p.Plugins.Select(x => new BackupPluginView(x.Id, x.BackupVersion, x.InstalledVersion, x.Status))],
            [.. p.MissingPackages], [.. p.DisabledInstances],
            [.. p.Accounts.Select(a => new BackupAccountView(a.Id, a.Label, a.MissingSecrets))],
            p.BackupSecrets, p.KeysRemoved, p.Kept.Favorites, p.Kept.OutboxOpen, [.. p.Conflicts]) : null;
        var applied = status.Result is { } r ? new BackupAppliedView(r.State, r.Error, r.Source, r.DisabledInstances, r.At.UtcDateTime.ToString("O")) : null;
        string? fileName = backupPath is null ? null : Path.GetFileName(backupPath);
        return new BackupView(BackupPicker is not null, BackupPicker is not null, step, fileName, preview, lastBackup,
            status.State == "Ready", status.State == "Ready" ? status.Source : null, applied, status.CanUndo);
    }

    private async Task<CommandResult> ExportBackupAsync(BackupExportRequest request)
    {
        if (backup is not { } service || BackupPicker is not { } picker) return new CommandResult(false, "unavailable");
        bool encrypted = !string.IsNullOrEmpty(request.Password);
        string? early = request.IncludeSecrets && !encrypted ? "password-required"
            : encrypted && (request.Password!.Length < BackupPasswordPolicy.Min || request.Password.Length > BackupPasswordPolicy.Max) ? "password-length" : null;
        if (early is not null)
        {
            lastBackup = new BackupOutcomeView("export", early, null, encrypted, false, 0);
            return Ok(SettingsElement());
        }
        if (Interlocked.Exchange(ref backupBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            string? path;
            try { path = await picker.PickSaveAsync($"su-su-backup-{DateTime.Now:yyyyMMdd}.susubak", CancellationToken.None); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("backup.picker-failed " + e.GetType().Name);
                lastBackup = new BackupOutcomeView("export", "picker", null, encrypted, false, 0);
                return Ok(SettingsElement());
            }
            if (path is null) return Ok(SettingsElement()); // cancelled: nothing written
            BackupExportOutcome outcome;
            try { outcome = await Task.Run(() => service.Export(path, new BackupExportOptions(request.IncludeSecrets, request.Password))); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("backup.export-failed " + e.GetType().Name);
                outcome = new BackupExportOutcome(false, "write-failed", encrypted, false, 0);
            }
            lastBackup = new BackupOutcomeView("export", outcome.Error, outcome.Ok ? Path.GetFileName(path) : null, outcome.Encrypted, outcome.IncludedSecrets, outcome.SecretCount);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref backupBusy, 0); }
    }

    private async Task<CommandResult> PickBackupAsync()
    {
        if (backup is not { } service || BackupPicker is not { } picker) return new CommandResult(false, "unavailable");
        if (Interlocked.Exchange(ref backupBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            string? path;
            try { path = await picker.PickOpenAsync(CancellationToken.None); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnostic?.Invoke("backup.picker-failed " + e.GetType().Name);
                lastBackup = new BackupOutcomeView("preview", "picker", null, false, false, 0);
                return Ok(SettingsElement());
            }
            if (path is null) return Ok(SettingsElement()); // cancelled
            backupPath = path;
            backupPreview = null;
            lastBackup = null;
            backupLocked = service.NeedsPassword(path);
            if (!backupLocked) await PreviewBackupAsync(service, null);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref backupBusy, 0); }
    }

    private async Task<CommandResult> UnlockBackupAsync(BackupUnlockRequest request)
    {
        if (backup is not { } service) return new CommandResult(false, "unavailable");
        if (backupPath is null || !backupLocked) return new CommandResult(false, "not-found");
        if (Interlocked.Exchange(ref backupBusy, 1) == 1) return new CommandResult(false, "busy");
        try
        {
            await PreviewBackupAsync(service, request.Password);
            return Ok(SettingsElement());
        }
        finally { Interlocked.Exchange(ref backupBusy, 0); }
    }

    /// <summary>Verifies and stages. A wrong password keeps the page on the password step; any other refusal ends the import with its reason.</summary>
    private async Task PreviewBackupAsync(IBackupService service, string? password)
    {
        string path = backupPath!;
        string name = Path.GetFileName(path);
        try
        {
            backupPreview = await Task.Run(() => service.Preview(path, password));
            backupLocked = false;
            lastBackup = null;
        }
        catch (BackupException e)
        {
            backupPreview = null;
            bool retry = e.Code is "decrypt-failed" or "password-required";
            lastBackup = new BackupOutcomeView("preview", e.Code, name, false, false, 0);
            if (!retry) { backupPath = null; backupLocked = false; }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Diagnostic?.Invoke("backup.preview-failed " + e.GetType().Name);
            backupPreview = null; backupPath = null; backupLocked = false;
            lastBackup = new BackupOutcomeView("preview", "unreadable", name, false, false, 0);
        }
    }

    private CommandResult ApplyBackup(BackupTokenRequest request)
    {
        if (backup is not { } service) return new CommandResult(false, "unavailable");
        if (backupPreview?.Token != request.Token || !service.Apply(request.Token)) return new CommandResult(false, "not-found");
        lastBackup = new BackupOutcomeView("apply", null, backupPath is null ? null : Path.GetFileName(backupPath), backupPreview.Encrypted, backupPreview.IncludesSecrets, backupPreview.BackupSecrets);
        backupPreview = null; backupPath = null; backupLocked = false;
        return Ok(SettingsElement());
    }

    private CommandResult DiscardBackup()
    {
        if (backup is not { } service) return new CommandResult(false, "unavailable");
        service.Discard();
        backupPreview = null; backupPath = null; backupLocked = false; lastBackup = null;
        return Ok(SettingsElement());
    }

    private CommandResult UndoBackup()
    {
        if (backup is not { } service) return new CommandResult(false, "unavailable");
        if (!service.ScheduleUndo()) return new CommandResult(false, "unavailable");
        backupPreview = null; backupPath = null; backupLocked = false;
        lastBackup = new BackupOutcomeView("undo", null, null, false, false, 0);
        return Ok(SettingsElement());
    }

    private CommandResult DismissBackupResult()
    {
        if (backup is not { } service) return new CommandResult(false, "unavailable");
        service.DismissResult();
        lastBackup = null;
        return Ok(SettingsElement());
    }
}
