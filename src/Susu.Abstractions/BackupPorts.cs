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
