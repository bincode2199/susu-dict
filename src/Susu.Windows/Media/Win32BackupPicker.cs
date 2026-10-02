using Susu.Abstractions;

namespace Susu.Windows.Media;

/// <summary>
/// The open and save dialogs for a <c>.susubak</c> backup (F17.1): the classic common dialog helper of the subtitle picker on its own STA thread.
/// Compiles; needs an interactive desktop, so the tests do not run it.
/// </summary>
public sealed class Win32BackupPicker(Func<nint>? owner = null) : IBackupFilePicker
{
    private const string Filter = "Su-Su backup (*.susubak)\0*.susubak\0\0";

    public Task<string?> PickSaveAsync(string suggestedFileName, CancellationToken cancellationToken)
        => Run("susu-backup-save", parent => Win32SubtitleSavePicker.Show(parent, suggestedFileName, Filter, "susubak"), cancellationToken);

    public Task<string?> PickOpenAsync(CancellationToken cancellationToken)
        => Run("susu-backup-open", parent => Win32SubtitleSavePicker.ShowOpen(parent, Filter), cancellationToken);

    private Task<string?> Run(string name, Func<nint, string?> show, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint parent = owner?.Invoke() ?? 0;
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(show(parent)); }
            catch (Exception e) { done.TrySetException(e); }
        }) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }
}
