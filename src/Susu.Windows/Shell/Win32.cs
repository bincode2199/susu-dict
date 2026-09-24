using System.Runtime.InteropServices;

namespace Susu.Windows.Shell;

/// <summary>Win32 surface used by the shell (NativeAOT-safe: LibraryImport, blittable structs, function pointers).</summary>
internal static unsafe partial class Win32
{
    public const uint WS_OVERLAPPED = 0, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000,
        WS_CLIPCHILDREN = 0x02000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_MAXIMIZE = 3, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_RESTORE = 9;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
    public const uint WM_DESTROY = 0x2, WM_MOVE = 0x3, WM_SIZE = 0x5, WM_ACTIVATE = 0x6, WM_SETFOCUS = 0x7, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_ERASEBKGND = 0x14,
        WM_GETMINMAXINFO = 0x24, WM_COPYDATA = 0x4A, WM_NCCALCSIZE = 0x83, WM_TIMER = 0x113, WM_SYSCOMMAND = 0x112, WM_HOTKEY = 0x312, WM_DPICHANGED = 0x2E0,
        WM_SETICON = 0x80, WM_APP = 0x8000, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B, WM_MOUSEACTIVATE = 0x21;
    public const int SC_MINIMIZE = 0xF020, SC_CLOSE = 0xF060, MA_NOACTIVATE = 3;
    public static readonly nint HWND_MESSAGE = -3, HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
    public const uint MONITOR_DEFAULTTONEAREST = 2, MDT_EFFECTIVE_DPI = 0;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80, NOTIFYICON_VERSION_4 = 4, NIIF_WARNING = 2;
    public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
    public const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const uint MB_ICONWARNING = 0x30, MB_YESNO = 4, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000; public const int IDYES = 6;
    public const int DT_CENTER = 1, DT_VCENTER = 4, DT_SINGLELINE = 0x20, TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public POINT Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] public struct COPYDATASTRUCT { public nint Data; public int Length; public nint Pointer; }
    [StructLayout(LayoutKind.Sequential)] public struct PAINTSTRUCT { public nint Hdc; public int Erase; public RECT Paint; public int Restore, IncUpdate; public fixed byte Reserved[32]; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public int Size; public uint Style; public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public char* MenuName, ClassName; public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int Size; public nint Hwnd; public uint Id, Flags, CallbackMessage; public nint Icon;
        public fixed char Tip[128];
        public uint State, StateMask;
        public fixed char Info[256];
        public uint VersionOrTimeout;
        public fixed char InfoTitle[64];
        public uint InfoFlags; public Guid Item; public nint BalloonIcon;
    }

    [LibraryImport("user32.dll", SetLastError = true)] public static partial ushort RegisterClassExW(in WNDCLASSEXW cls);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(out MSG message, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(in MSG message);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(in MSG message);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsZoomed(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(POINT point, uint flags);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data);
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint monitor, uint type, out uint dpiX, out uint dpiY);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT point);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AllowSetForegroundWindow(uint processId);
    [LibraryImport("user32.dll")] public static partial nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowEx(nint parent, nint after, string className, string? title);
    [LibraryImport("user32.dll")] public static partial nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint hwnd, int id);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)] public static partial nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32.dll")] public static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)] public static partial int MessageBox(nint hwnd, string text, string caption, uint type);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] public static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] public static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EndPaint(nint hwnd, in PAINTSTRUCT paint);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawText(nint hdc, string text, int length, ref RECT rect, int format);
    [LibraryImport("user32.dll")] public static partial int FillRect(nint hdc, in RECT rect, nint brush);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessDpiAwarenessContext(nint context);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")] public static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32.dll")] public static partial nint GetModuleHandleW(nint name);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] public static partial nint CreateMutex(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(nint handle);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)] public static partial nint ShellExecute(nint hwnd, string verb, string file, string? parameters, string? directory, int show);
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public static int LowWord(nint value) => (short)(value & 0xFFFF);
    public static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public static void CopyTo(string text, char* destination, int capacity)
    {
        int length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }
}
