using Susu.Abstractions;

namespace Susu.Windows.Media;

/// <summary>
/// The save dialog for the vocabulary export (F15.4): the same classic common dialog as the subtitle export, on its own STA thread, with
/// the filter of the chosen format (Eudic/plain txt, CSV, Anki apkg). Compiles; needs an interactive desktop, so it is not run by the tests.
/// </summary>
public sealed class Win32VocabSavePicker(Func<nint>? owner = null) : IVocabSavePicker
{
    public Task<string?> PickAsync(string suggestedFileName, string format, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint parent = owner?.Invoke() ?? 0;
        string filter = format switch
        {
            "csv" => "CSV (*.csv)\0*.csv\0",
            "apkg" => "Anki deck package (*.apkg)\0*.apkg\0",
            _ => "Text (*.txt)\0*.txt\0",
        } + "\0";
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Win32SubtitleSavePicker.Show(parent, suggestedFileName, filter, format)); }
            catch (Exception e) { done.TrySetException(e); }
        }) { IsBackground = true, Name = "susu-vocab-save" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }
}
