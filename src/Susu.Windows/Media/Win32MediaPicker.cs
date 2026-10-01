using System.Runtime.InteropServices;
using Susu.Abstractions;

namespace Susu.Windows.Media;

/// <summary>
/// The file open dialog for video/audio (F14.1, ARCHITECTURE 6: the page asks the host to pick, the host answers with a token).
/// The classic common dialog runs on its own STA thread so the caller is never blocked; the chosen path goes straight into the
/// token table and is never returned. The extension filter is only a convenience: "All files" is offered and support is decided
/// by the decoder, not by the extension.
/// </summary>
public sealed unsafe partial class Win32MediaPicker(IMediaTokens tokens, Func<nint>? owner = null) : IMediaPicker
{
    private const uint FileMustExist = 0x1000, PathMustExist = 0x800, Explorer = 0x80000, NoChangeDir = 0x8;
    private const string Filter = "Video and audio\0*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus\0All files\0*.*\0";

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

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileName(ref OpenFileName dialog);

    public Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint parent = owner?.Invoke() ?? 0;
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Show(parent)); }
            catch (Exception e) { done.TrySetException(e); }
        }) { IsBackground = true, Name = "susu-media-picker" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(cancellationToken);
    }

    private string? Show(nint parent)
    {
        const int maxFile = 32768;
        nint file = Marshal.AllocHGlobal(maxFile * 2), filter = Marshal.StringToHGlobalUni(Filter + "\0"), title = Marshal.StringToHGlobalUni("Su-Su");
        try
        {
            new Span<byte>((void*)file, maxFile * 2).Clear();
            var dialog = new OpenFileName
            {
                StructSize = (uint)sizeof(OpenFileName), Owner = parent, Filter = filter, FilterIndex = 1,
                File = file, MaxFile = maxFile, Title = title, Flags = FileMustExist | PathMustExist | Explorer | NoChangeDir,
            };
            if (!GetOpenFileName(ref dialog)) return null; // cancelled (or the dialog failed: nothing was chosen either way)
            return tokens.Issue(Marshal.PtrToStringUni(file)!);
        }
        finally { Marshal.FreeHGlobal(file); Marshal.FreeHGlobal(filter); Marshal.FreeHGlobal(title); }
    }
}
