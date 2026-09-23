using System.Runtime.InteropServices;
using System.Text;

namespace Susu.Probes.Selection;

/// <summary>Test-driver input and window helpers (harness side only, never the product helper).</summary>
internal static partial class Desktop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx; public int Dy; public uint Data; public uint Flags; public uint Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Key; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion U; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(nint hwnd, nint lparam);

    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint count, [In] Input[] inputs, int size);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BringWindowToTop(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumWindows(nint callback, nint lparam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumChildWindows(nint parent, nint callback, nint lparam);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] private static unsafe partial int GetWindowText(nint hwnd, char* text, int max);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] private static unsafe partial int GetClassName(nint hwnd, char* text, int max);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hwnd, out Rect rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint SendMessageTimeout(nint hwnd, uint message, nint wparam, string lparam, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [LibraryImport("user32.dll")] private static partial nint SetFocus(nint hwnd);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();

    public const ushort VkControl = 0x11, VkShift = 0x10, VkMenu = 0x12, VkA = 0x41, VkC = 0x43, VkEnd = 0x23, VkTab = 0x09, VkEscape = 0x1B, VkRight = 0x27;

    public static unsafe string Title(nint hwnd) { char* b = stackalloc char[512]; int n = GetWindowText(hwnd, b, 512); return new string(b, 0, n); }
    public static unsafe string ClassOf(nint hwnd) { char* b = stackalloc char[256]; int n = GetClassName(hwnd, b, 256); return new string(b, 0, n); }

    public static List<nint> TopLevel()
    {
        var list = new List<nint>();
        var handle = GCHandle.Alloc(list);
        try { unsafe { EnumWindows((nint)(delegate* unmanaged<nint, nint, int>)&Collect, GCHandle.ToIntPtr(handle)); } }
        finally { handle.Free(); }
        return list;
    }

    public static List<nint> Children(nint parent)
    {
        var list = new List<nint>();
        var handle = GCHandle.Alloc(list);
        try { unsafe { EnumChildWindows(parent, (nint)(delegate* unmanaged<nint, nint, int>)&Collect, GCHandle.ToIntPtr(handle)); } }
        finally { handle.Free(); }
        return list;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lparam) { ((List<nint>)GCHandle.FromIntPtr(lparam).Target!).Add(hwnd); return 1; }

    public static nint WaitForWindow(Func<string, bool> title, int timeoutMs, Func<nint, bool>? extra = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (nint hwnd in TopLevel())
                if (IsWindowVisible(hwnd) && title(Title(hwnd)) && (extra is null || extra(hwnd))) return hwnd;
            Thread.Sleep(100);
        }
        return 0;
    }

    /// <summary>Foreground-lock workaround for a test driver: a synthetic Alt tap then SetForegroundWindow.</summary>
    public static bool Activate(nint hwnd)
    {
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != hwnd; attempt++)
        {
            ShowWindow(hwnd, 9); // SW_RESTORE
            Keys([(VkMenu, true), (VkMenu, false)]);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            Thread.Sleep(150);
        }
        return GetForegroundWindow() == hwnd;
    }

    public static void Keys((ushort Vk, bool Down)[] keys)
    {
        var inputs = keys.Select(k => new Input { Type = 1, U = new InputUnion { Key = new KeyboardInput { Vk = k.Vk, Flags = k.Down ? 0u : 2u } } }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    public static void Chord(params ushort[] vks)
    {
        var keys = vks.Select(v => (v, true)).Concat(vks.Reverse().Select(v => (v, false))).ToArray();
        Keys(keys);
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        var inputs = new[]
        {
            new Input { Type = 0, U = new InputUnion { Mouse = new MouseInput { Flags = 0x0002 } } },
            new Input { Type = 0, U = new InputUnion { Mouse = new MouseInput { Flags = 0x0004 } } },
        };
        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

    /// <summary>Puts text into a child edit control and focuses it (test preparation only).</summary>
    public static bool FillAndFocus(nint topLevel, Func<string, bool> className, string text)
    {
        nint edit = Children(topLevel).FirstOrDefault(c => className(ClassOf(c)) && IsWindowVisible(c));
        if (edit == 0) return false;
        SendMessageTimeout(edit, 0x000C, 0, text, 0x0002, 2000, out _); // WM_SETTEXT, SMTO_ABORTIFHUNG
        uint target = GetWindowThreadProcessId(edit, out _), self = GetCurrentThreadId();
        AttachThreadInput(self, target, true);
        SetFocus(edit);
        AttachThreadInput(self, target, false);
        return true;
    }

    public static void Close(nint hwnd) => PostMessage(hwnd, 0x0010, 0, 0); // WM_CLOSE

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Point point);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    /// <summary>
    /// True when the session accepts synthetic input (e.g. not a minimized RDP client): moving the
    /// cursor to its current position succeeds only on an interactive input desktop.
    /// </summary>
    public static bool InputAvailable() => GetCursorPos(out var p) && SetCursorPos(p.X, p.Y);
}
