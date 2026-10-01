using System.Runtime.InteropServices;
using Susu.Abstractions;

namespace Susu.Windows.Media;

/// <summary>
/// The save dialog for subtitle files (F14.3): classic common dialog on its own STA thread, overwrite prompt on, the extension of the
/// chosen format appended when the user types none. Compiles; needs an interactive desktop, so it is not run by the tests.
/// </summary>
public sealed unsafe partial class Win32SubtitleSavePicker(Func<nint>? owner = null) : ISubtitleSavePicker
{
    private const uint OverwritePrompt = 0x2, PathMustExist = 0x800, Explorer = 0x80000, NoChangeDir = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public uint StructSize; public nint Owner, Instance, Filter, CustomFilter;
        public uint MaxCustomFilter, FilterIndex;
        public nint File; public uint MaxFile;
        public nint FileTitle; public uint MaxFileTitle;
        public nint InitialDir, Title;
        public uint Flags; public ushort FileOffset, FileExtension;
        public nint DefaultExtension, CustomData, Hook, TemplateName, Reserved;
        public uint Reserved2, FlagsEx;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSaveFileName(ref OpenFileName dialog);

    public Task<string?> PickAsync(string suggestedFileName, SubtitleFormat format, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint parent = owner?.Invoke() ?? 0;
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Show(parent, suggestedFileName, format)); }
            catch (Exception e) { done.TrySetException(e); }
        }) { IsBackground = true, Name = "susu-subtitle-save" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }

    private static string? Show(nint parent, string suggested, SubtitleFormat format)
    {
        const int maxFile = 32768;
        string ext = format.ToString().ToLowerInvariant();
        string filter = format switch
        {
            SubtitleFormat.Srt => "SubRip (*.srt)\0*.srt\0",
            SubtitleFormat.Vtt => "WebVTT (*.vtt)\0*.vtt\0",
            _ => "Text (*.txt)\0*.txt\0",
        } + "\0";
        nint file = Marshal.AllocHGlobal(maxFile * 2), filterPtr = Marshal.StringToHGlobalUni(filter), title = Marshal.StringToHGlobalUni("Su-Su"), defExt = Marshal.StringToHGlobalUni(ext);
        try
        {
            new Span<byte>((void*)file, maxFile * 2).Clear();
            string initial = suggested.Length >= maxFile ? suggested[..(maxFile - 1)] : suggested;
            var chars = new Span<char>((void*)file, maxFile);
            initial.AsSpan().CopyTo(chars);
            var dialog = new OpenFileName
            {
                StructSize = (uint)sizeof(OpenFileName), Owner = parent, Filter = filterPtr, FilterIndex = 1, File = file, MaxFile = maxFile,
                Title = title, DefaultExtension = defExt, Flags = OverwritePrompt | PathMustExist | Explorer | NoChangeDir,
            };
            return GetSaveFileName(ref dialog) ? Marshal.PtrToStringUni(file) : null;
        }
        finally { Marshal.FreeHGlobal(file); Marshal.FreeHGlobal(filterPtr); Marshal.FreeHGlobal(title); Marshal.FreeHGlobal(defExt); }
    }
}
