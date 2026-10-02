using Susu.Abstractions;

namespace Susu.Windows.Media;

/// <summary>
/// The save dialog for the diagnostics zip (F17.2), on its own STA thread like <see cref="Win32BackupPicker"/>. Compiles; needs an interactive
/// desktop, so the tests do not run it.
/// </summary>
public sealed class Win32DiagnosticsPicker(Func<nint>? owner = null) : IDiagnosticsFilePicker
{
    private const string Filter = "Zip archive (*.zip)\0*.zip\0\0";

    public Task<string?> PickSaveAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint parent = owner?.Invoke() ?? 0;
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Win32SubtitleSavePicker.Show(parent, suggestedFileName, Filter, "zip")); }
            catch (Exception e) { done.TrySetException(e); }
        }) { IsBackground = true, Name = "susu-diagnostics-save" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>Opens one of the app's own folders in Explorer (F17.2). The folder comes from the host, never from the page. Compiles; shell launch is not run in tests.</summary>
public static class Win32FolderOpener
{
    public static bool Open(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(folder) }, UseShellExecute = false });
        return process is not null;
    }
}
