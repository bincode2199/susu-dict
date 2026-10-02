namespace Susu.Abstractions;

/// <summary>
/// The file dialogs for a <c>.susubak</c> backup (F17.1). Both answer null when the user cancelled. The page never sends a path: the host opens
/// the dialog and keeps the chosen path.
/// </summary>
public interface IBackupFilePicker
{
    /// <summary>Save dialog with the suggested file name (<c>su-su-backup-yyyyMMdd.susubak</c>).</summary>
    Task<string?> PickSaveAsync(string suggestedFileName, CancellationToken cancellationToken);

    /// <summary>Open dialog for a <c>.susubak</c> file.</summary>
    Task<string?> PickOpenAsync(CancellationToken cancellationToken);
}

/// <summary>A backup was refused. <see cref="Code"/> is a stable key (never a path, YAML text or secret) the page translates.</summary>
public sealed class BackupException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Local data a restore leaves alone (the backup holds no favorites, entries, outbox, history, caches or logs).</summary>
public sealed record BackupLocalData(int Favorites, int OutboxOpen);

public sealed record BackupExportOptions(bool IncludeSecrets, string? Password);

public sealed record BackupExportOutcome(bool Ok, string? Error, bool Encrypted, bool IncludedSecrets, int SecretCount);

/// <summary>Area: settings | accounts | instances | enabledServices | prompts. Differs: the restore would change this area.</summary>
public sealed record BackupDelta(string Area, int Current, int Backup, bool Differs);

/// <summary>Status: missing | builtin | same | older | newer (the backup's version against the installed one).</summary>
public sealed record BackupPluginStatus(string Id, string BackupVersion, string? InstalledVersion, string Status);

/// <summary>An account in the restored settings. Every account needs its grants confirmed again; MissingSecrets are the keys the backup did not bring.</summary>
public sealed record BackupAccountStatus(string Id, string Label, string[] MissingSecrets);

/// <summary>
/// What restoring would replace, before anything is applied (DATA09). Conflicts are stable keys: plugins-missing (disabled, not installed),
/// accounts-need-authorization, keys-missing, keys-removed (local keys the restore drops), services-disabled.
/// </summary>
public sealed record BackupPreview(string Token, DateTimeOffset? Created, string AppVersion, bool Encrypted, bool IncludesSecrets, bool SchemaOlder,
    IReadOnlyList<BackupDelta> Deltas, IReadOnlyList<BackupPluginStatus> Plugins, IReadOnlyList<string> MissingPackages, IReadOnlyList<string> DisabledInstances,
    IReadOnlyList<BackupAccountStatus> Accounts, int BackupSecrets, int KeysRemoved, BackupLocalData Kept, IReadOnlyList<string> Conflicts);

/// <summary>State: None | Previewed | Ready (applies at the next start). Result: how the last import at start ended.</summary>
public sealed record BackupStatus(string State, string? Source, bool CanUndo, BackupApplyResult? Result);

/// <summary>State: Applied | Failed; Error is a stable key.</summary>
public sealed record BackupApplyResult(string State, string? Error, string Source, int DisabledInstances, DateTimeOffset At);

/// <summary>
/// The backup and restore port the Settings page uses (F17.1). <c>Preview</c> verifies a whole file and stages it without touching the live config,
/// <c>Apply</c> confirms the staged import for the next start. Implemented over the config transaction in Susu.Storage.
/// </summary>
public interface IBackupService
{
    BackupExportOutcome Export(string path, BackupExportOptions options);
    bool NeedsPassword(string path);
    BackupPreview Preview(string path, string? password);
    bool Apply(string token);
    void Discard();
    bool ScheduleUndo();
    BackupStatus Status();
    void DismissResult();
}

/// <summary>Export password length bounds (characters), shared by the page, the service and the importer.</summary>
public static class BackupPasswordPolicy
{
    public const int Min = 8, Max = 256;
}
