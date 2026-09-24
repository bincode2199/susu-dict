using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Susu.Windows.Shell.Win32;

namespace Susu.Windows.Shell;

/// <summary>Receives window messages for one HWND; return null to fall through to DefWindowProc.</summary>
internal interface IMessageTarget
{
    nint? OnMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

/// <summary>One window procedure for every shell class; routes by HWND and never lets an exception reach native code.</summary>
internal static unsafe class WindowClasses
{
    private static readonly Dictionary<nint, IMessageTarget> targets = [];
    [ThreadStatic] private static IMessageTarget? creating;
    private static readonly HashSet<string> registered = [];
    public static Action<Exception>? Unhandled { get; set; }
    public static nint AppIcon { get; set; }
    public static nint AppIconSmall { get; set; }

    public static nint Create(IMessageTarget target, string className, uint exStyle, uint style, int x, int y, int width, int height, nint parent = 0, string title = "Su-Su")
    {
        Register(className);
        creating = target;
        nint hwnd;
        try { hwnd = CreateWindowEx(exStyle, className, title, style, x, y, width, height, parent, 0, GetModuleHandleW(0), 0); }
        finally { creating = null; }
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        targets[hwnd] = target;
        return hwnd;
    }

    private static void Register(string className)
    {
        if (!registered.Add(className)) return;
        fixed (char* name = className)
        {
            var cls = new WNDCLASSEXW
            {
                Size = sizeof(WNDCLASSEXW), Style = 0x8 /* CS_DBLCLKS */, WndProc = &WndProc, Instance = GetModuleHandleW(0),
                Cursor = LoadCursorW(0, 32512 /* IDC_ARROW */), Background = 0, ClassName = name, Icon = AppIcon, IconSmall = AppIconSmall,
            };
            if (RegisterClassExW(cls) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (!targets.TryGetValue(hwnd, out var target) && creating is { } pending) targets[hwnd] = target = pending;
            if (message == 0x82 /* WM_NCDESTROY */) { targets.Remove(hwnd); return DefWindowProcW(hwnd, message, wParam, lParam); }
            if (target?.OnMessage(hwnd, message, wParam, lParam) is { } handled) return handled;
        }
        catch (Exception e)
        {
            Unhandled?.Invoke(e);
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}

/// <summary>
/// The main STA message thread (ARCHITECTURE 4): owns HWNDs, WebView controllers, tray and hotkeys. Other threads
/// hand work over with <see cref="Post"/>; timers run on this thread; awaits resume here via the installed context.
/// </summary>
public sealed class UiDispatcher : IMessageTarget, IDisposable
{
    public const string MessageClass = "SuSu.Host.Message";
    internal const uint WM_INVOKE = WM_APP + 1, WM_TRAY = WM_APP + 2;
    public const string ActivateToken = "Su-Su:activate";

    private readonly ConcurrentQueue<Action> queue = new();
    private readonly Dictionary<nuint, Action> timers = [];
    private readonly uint taskbarCreated;
    private nuint nextTimer = 1;
    private readonly int threadId = Environment.CurrentManagedThreadId;

    /// <param name="instanceName">Title of the message window, so a second launch wakes only the instance with the same name (data root).</param>
    public UiDispatcher(string instanceName = "Su-Su.Instance")
    {
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        Handle = WindowClasses.Create(this, MessageClass, 0, 0, 0, 0, 0, 0, HWND_MESSAGE, instanceName);
        SynchronizationContext.SetSynchronizationContext(new DispatcherContext(this));
    }

    public nint Handle { get; }
    public bool OnThread => Environment.CurrentManagedThreadId == threadId;

    public event Action<int>? HotkeyPressed;
    public event Action? ActivateRequested;
    public event Action<uint, int, int>? TrayEvent; // event, x, y
    public event Action? TaskbarRecreated;

    public void Post(Action action)
    {
        queue.Enqueue(action);
        PostMessageW(Handle, WM_INVOKE, 0, 0);
    }

    public void StartTimer(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero) { Post(action); return; }
        if (!OnThread) { Post(() => StartTimer(delay, action)); return; }
        nuint id = nextTimer++;
        timers[id] = action;
        SetTimer(Handle, id, (uint)Math.Clamp(delay.TotalMilliseconds, 1, int.MaxValue), 0);
    }

    public int Run()
    {
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(message);
            DispatchMessageW(message);
        }
        Drain();
        return 0;
    }

    public void Quit() => PostQuitMessage(0);

    nint? IMessageTarget.OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_INVOKE: Drain(); return 0;
            case WM_TIMER:
                KillTimer(hwnd, (nuint)wParam);
                if (timers.Remove((nuint)wParam, out var action)) action();
                return 0;
            case WM_HOTKEY: HotkeyPressed?.Invoke((int)wParam); return 0;
            case WM_TRAY: TrayEvent?.Invoke((uint)LowWord(lParam), LowWord(wParam), HighWord(wParam)); return 0;
            case WM_COPYDATA:
                unsafe
                {
                    var data = (COPYDATASTRUCT*)lParam;
                    if (data->Length == ActivateToken.Length * 2 && new string((char*)data->Pointer, 0, ActivateToken.Length) == ActivateToken) { Post(() => ActivateRequested?.Invoke()); return 1; }
                }
                return 0;
        }
        if (message == taskbarCreated && taskbarCreated != 0) { TaskbarRecreated?.Invoke(); return 0; }
        return null;
    }

    private void Drain()
    {
        while (queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { WindowClasses.Unhandled?.Invoke(e); }
        }
    }

    public void Dispose()
    {
        foreach (var id in timers.Keys) KillTimer(Handle, id);
        timers.Clear();
        DestroyWindow(Handle);
    }

    private sealed class DispatcherContext(UiDispatcher dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => dispatcher.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (dispatcher.OnThread) d(state);
            else throw new InvalidOperationException("synchronous Send to the UI thread is not allowed");
        }
        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>Single instance per user session (PLAN 1.4): a second launch wakes the first and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly nint mutex;

    private SingleInstance(nint mutex) => this.mutex = mutex;

    /// <summary>Returns null when another instance already runs (after asking it to activate).</summary>
    public static SingleInstance? TryAcquire(string name)
    {
        nint handle = CreateMutex(0, false, $"Local\\{name}");
        int error = Marshal.GetLastPInvokeError();
        if (handle != 0 && error != ERROR_ALREADY_EXISTS) return new SingleInstance(handle);
        if (handle != 0) CloseHandle(handle);
        SignalExisting(name);
        return null;
    }

    /// <summary>Asks the instance with this name (never any other Su-Su instance) to wake up.</summary>
    public static unsafe bool SignalExisting(string name)
    {
        nint target = FindWindowEx(HWND_MESSAGE, 0, UiDispatcher.MessageClass, name);
        if (target == 0) return false;
        // This (just launched, foreground) process lets the running instance take the foreground for its window.
        if (GetWindowThreadProcessId(target, out uint pid) != 0) AllowSetForegroundWindow(pid);
        fixed (char* token = UiDispatcher.ActivateToken)
        {
            var data = new COPYDATASTRUCT { Data = 1, Length = UiDispatcher.ActivateToken.Length * 2, Pointer = (nint)token };
            return SendMessageTimeoutW(target, WM_COPYDATA, 0, (nint)(&data), 0x2 /* SMTO_ABORTIFHUNG */, 2000, out _) != 0;
        }
    }

    public void Dispose() => CloseHandle(mutex);
}

/// <summary>Process-wide shell setup; call once on the UI thread before creating windows.</summary>
public static class ShellEnvironment
{
    /// <summary>Per-Monitor V2 DPI awareness (ARCHITECTURE 9), app icons from the assets folder, and a sink for exceptions thrown inside window procedures.</summary>
    public static void Initialize(string assetsFolder, Action<Exception> unhandled)
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); // also declared in the manifest
        WindowClasses.Unhandled = unhandled;
        string icon = Path.Combine(assetsFolder, "susu.ico");
        if (!File.Exists(icon)) return;
        WindowClasses.AppIcon = Win32.LoadImage(0, icon, Win32.IMAGE_ICON, 32, 32, Win32.LR_LOADFROMFILE);
        WindowClasses.AppIconSmall = Win32.LoadImage(0, icon, Win32.IMAGE_ICON, 16, 16, Win32.LR_LOADFROMFILE);
    }
}
