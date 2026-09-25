using System.Runtime.InteropServices;

namespace Susu.Windows.Selection;

/// <summary>Win32 and susu_selection.dll imports of the selection helper (F08.1). Read-only queries only.</summary>
internal static unsafe partial class SelectionNative
{
    internal const string Module = "susu_selection";

    // susu_selection.dll (native/selection.cpp, native/ia2.cpp). reason: 0 selected, 1 password, 2 unsupported, 3 empty, 4 focus changed.
    [LibraryImport(Module, EntryPoint = "susu_selection_uia")]
    internal static partial int ReadUia(nint window, uint timeoutMs, char* text, uint capacity, out int reason, double* rect, out int ranges);
    [LibraryImport(Module, EntryPoint = "susu_selection_ia2")]
    internal static partial int ReadIa2(nint window, char* text, uint capacity, out int reason);
    [LibraryImport(Module, EntryPoint = "susu_selection_self_test")]
    internal static partial int SelfTest();

    [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool IsWindow(nint window);
    [LibraryImport("user32.dll")] internal static partial uint GetWindowThreadProcessId(nint window, out int processId);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] internal static partial int GetClassName(nint window, char* name, int capacity);
    [LibraryImport("user32.dll")] internal static partial nint MonitorFromRect(in NativeRect rect, uint flags);
    [LibraryImport("user32.dll")] internal static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll")] internal static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("shcore.dll")] internal static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentProcess();
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool OpenProcessToken(nint process, uint access, out nint token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetTokenInformation(nint token, int infoClass, void* buffer, int length, out int returned);
    [LibraryImport("advapi32.dll")] internal static partial byte* GetSidSubAuthorityCount(nint sid);
    [LibraryImport("advapi32.dll")] internal static partial uint* GetSidSubAuthority(nint sid, uint index);

    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenIntegrityLevel = 25;
    internal const uint MonitorDefaultToNearest = 2;
    internal static readonly nint DpiAwarenessPerMonitorV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }

    internal static string ClassOf(nint window)
    {
        char* buffer = stackalloc char[257];
        int length = GetClassName(window, buffer, 257);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>Mandatory integrity RID of a process token (0x2000 medium, 0x3000 high); null when it cannot be read.</summary>
    internal static int? IntegrityOf(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out nint token)) return null;
        try
        {
            byte* buffer = stackalloc byte[128];
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, 128, out _)) return null;
            nint sid = *(nint*)buffer; // TOKEN_MANDATORY_LABEL.Label.Sid
            byte count = *GetSidSubAuthorityCount(sid);
            return count == 0 ? null : (int)*GetSidSubAuthority(sid, (uint)(count - 1));
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Effective DPI of the monitor holding the rectangle (or the window when there is none); 96 on failure.</summary>
    internal static int DpiAt(nint window, double[]? rect)
    {
        nint monitor = rect is [var l, var t, var r, var b] && r > l && b > t
            ? MonitorFromRect(new NativeRect { Left = (int)l, Top = (int)t, Right = (int)r, Bottom = (int)b }, MonitorDefaultToNearest)
            : MonitorFromWindow(window, MonitorDefaultToNearest);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? (int)x : 96;
    }
}
