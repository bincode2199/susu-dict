using System.Runtime.InteropServices;

namespace Susu.Windows.Clipboard;

/// <summary>Clipboard, input and process calls of the F08.2 borrow (NativeAOT-safe LibraryImport).</summary>
internal static unsafe partial class ClipboardNative
{
    public const uint CF_TEXT = 1, CF_BITMAP = 2, CF_OEMTEXT = 7, CF_DIB = 8, CF_PALETTE = 9, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_LOCALE = 16, CF_DIBV5 = 17;
    public const uint WM_CLIPBOARDUPDATE = 0x031D, WM_APP = 0x8000, WM_CLOSE = 0x10, WM_DESTROY = 0x2;
    public const uint GMEM_MOVEABLE = 2;
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_C = 0x43;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;

    /// <summary>INPUT (x64: 4-byte type, padding, 32-byte union); only the keyboard member is used.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] public static partial uint EnumClipboardFormats(uint format);
    [LibraryImport("user32.dll")] public static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll")] public static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32.dll")] public static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] public static partial nint GetClipboardOwner();
    [LibraryImport("user32.dll")] public static partial int GetClipboardFormatNameW(uint format, char* name, int capacity);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AddClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RemoveClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] public static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial uint SendInput(uint count, INPUT* inputs, int size);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")] public static partial nuint GlobalSize(nint memory);
    [LibraryImport("kernel32.dll")] public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetExitCodeProcess(nint process, out uint code);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(nint handle);

    public static string FormatName(uint format)
    {
        if (format < 0xC000) return "";
        char* buffer = stackalloc char[256];
        int length = GetClipboardFormatNameW(format, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>Opens the clipboard, retrying every 5 ms until <paramref name="milliseconds"/> pass (another process may hold it briefly).</summary>
    public static bool OpenWithRetry(nint owner, int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        do
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(5);
        } while (Environment.TickCount64 < deadline);
        return false;
    }

    public static int OwnerProcessId()
    {
        nint owner = GetClipboardOwner();
        if (owner == 0) return 0;
        GetWindowThreadProcessId(owner, out int pid);
        return pid;
    }
}
