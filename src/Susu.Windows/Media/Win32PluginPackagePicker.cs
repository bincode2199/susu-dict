using Susu.Abstractions;

namespace Susu.Windows.Media;

/// <summary>
/// The open-file dialog for a plugin package (F16.1): .susuext or .zip, on its own STA thread. Reuses the classic common dialog helper of
/// the subtitle save picker in open mode. Compiles; needs an interactive desktop, so the tests do not run it. Dropping a file on the
/// Settings window is not wired to native code yet: the shell exposes <c>PreviewPluginPackageAsync(path)</c> for it.
/// </summary>
public sealed class Win32PluginPackagePicker(Func<nint>? owner = null) : IPluginPackagePicker
{
    public Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint parent = owner?.Invoke() ?? 0;
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Win32SubtitleSavePicker.ShowOpen(parent, "Plugin package (*.susuext;*.zip)\0*.susuext;*.zip\0\0")); }
            catch (Exception e) { done.TrySetException(e); }
        }) { IsBackground = true, Name = "susu-plugin-open" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }
}
