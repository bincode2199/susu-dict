using System.Runtime.InteropServices;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using static Susu.Windows.Shell.Win32;

namespace Susu.Windows.Shell;

/// <summary>Where the production UI is loaded from and how each window kind maps to a trusted origin.</summary>
public sealed record UiHosting(string UiFolder, string WebViewUserData, bool DevTools)
{
    public const string AppHost = "app.susu.example", SettingsHost = "settings.susu.example";
    /// <summary>Settings (the only window allowed to write credentials) gets its own origin (PLAN 4.5.5).</summary>
    public static string HostFor(WindowKind kind) => kind == WindowKind.Settings ? SettingsHost : AppHost;
}

/// <summary>
/// Win32 + WebView2 implementation of <see cref="IWindowPlatform"/> on the UI thread. One environment for all
/// windows, one controller per window, lazily created; hidden windows stay warm (suspended) until the
/// coordinator asks for a full release, after which BrowserProcessExited is awaited and reported.
/// </summary>
public sealed class WindowPlatform : IWindowPlatform, ICaptureWindowHider, IDisposable
{
    private readonly UiDispatcher dispatcher;
    private readonly UiHosting hosting;
    private readonly IWindowStateStore positions;
    private readonly Func<string> uiLanguage;
    private readonly Dictionary<WindowKind, ShellWindow> windows = [];
    private readonly Dictionary<int, string> hotkeyIds = [];
    private readonly Dictionary<WindowKind, (PixelRect? Selection, int WidthDip)> anchors = [];
    private WebViewEnvironment? environment;
    private int nextHotkey = 1;

    public WindowPlatform(UiDispatcher dispatcher, UiHosting hosting, IWindowStateStore positions, Func<string> uiLanguage)
    {
        this.dispatcher = dispatcher;
        this.hosting = hosting;
        this.positions = positions;
        this.uiLanguage = uiLanguage;
        dispatcher.HotkeyPressed += id => { if (hotkeyIds.TryGetValue(id, out var action)) HotkeyPressed?.Invoke(action); };
    }

    public event Action<WindowKind, WindowRequest>? WindowRequested;
    public event Action<WindowKind, string>? PageMessage;
    public event Action<string>? Diagnostic;
    public event Action<string>? HotkeyPressed;
    public event Action? ExitRequested;
    public event Action? BrowserExited;
    /// <summary>Tick at which a window's native shell became visible and its page loaded (PER03 instrumentation).</summary>
    public event Action<WindowKind, string, long>? Timing;

    public bool EnvironmentAlive => environment is not null;

    internal void Request(WindowKind kind, WindowRequest request) => WindowRequested?.Invoke(kind, request);

    public string Show(WindowKind kind, bool activate)
    {
        long started = Environment.TickCount64;
        if (!windows.TryGetValue(kind, out var window)) windows[kind] = window = new ShellWindow(this, kind);
        if (!window.Visible) window.Place(Placement(window));
        // Native shell first (white, with the frame) so feedback does not wait for WebView2 (ARCHITECTURE 11).
        ShowWindow(window.Handle, activate ? SW_SHOW : SW_SHOWNOACTIVATE);
        if (kind == WindowKind.Tray) SetWindowPos(window.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        if (activate) SetForegroundWindow(window.Handle);
        Timing?.Invoke(kind, "NativeShellVisible", Environment.TickCount64 - started);
        EnsureView(window);
        window.View?.Visible(true);
        window.ResizeView();
        if (activate) window.View?.Focus(); // keyboard input goes to the page, not the empty host window
        return window.SessionId;
    }

    public void Hide(WindowKind kind)
    {
        if (!windows.TryGetValue(kind, out var window) || !window.Visible) return;
        if (window.Spec.RemembersPosition && !IsZoomed(window.Handle))
        {
            var rect = window.Rect();
            nint monitor = MonitorFromWindow(window.Handle, MONITOR_DEFAULTTONEAREST);
            GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _);
            try { positions.Save(new WindowPlacement(window.Spec.PlacementKey, monitor.ToString("x"), rect.X, rect.Y, (int)dpi)); }
            catch (Exception e) { Diagnostic?.Invoke($"window.position.save {e.GetType().Name}"); }
        }
        window.View?.Visible(false);
        ShowWindow(window.Handle, SW_HIDE);
        if (IsZoomed(window.Handle)) ShowWindow(window.Handle, SW_RESTORE); // size and maximize are never remembered
    }

    /// <summary>
    /// F11.1: hides every visible Su-Su window before the screenshot freeze-frame (no position save, no page event: this is not
    /// a close) and waits for DWM to compose without them; the token shows the same windows again without activating them.
    /// </summary>
    public Task<IDisposable> HideAllAsync(CancellationToken cancellationToken)
    {
        var hidden = windows.Values.Where(w => w.Visible).Select(w => w.Handle).ToList();
        foreach (var hwnd in hidden) ShowWindow(hwnd, SW_HIDE);
        if (hidden.Count > 0) { Capture.CaptureNative.DwmFlush(); Capture.CaptureNative.DwmFlush(); }
        return Task.FromResult<IDisposable>(new CaptureRestore(this, hidden));
    }

    private sealed class CaptureRestore(WindowPlatform platform, List<nint> hidden) : IDisposable
    {
        private int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            foreach (var hwnd in hidden)
                if (platform.windows.Values.Any(w => w.Handle == hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE); // skip windows released meanwhile
        }
    }

    public void ToggleMaximize(WindowKind kind)
    {
        if (windows.TryGetValue(kind, out var window) && window.Framed) ShowWindow(window.Handle, IsZoomed(window.Handle) ? SW_RESTORE : SW_MAXIMIZE);
    }

    public void FitHeight(WindowKind kind, int contentHeightDip)
    {
        if (!windows.TryGetValue(kind, out var window) || !window.Spec.AutoHeight || IsZoomed(window.Handle)) return;
        var current = window.Rect();
        string hint = MonitorFromWindow(window.Handle, MONITOR_DEFAULTTONEAREST).ToString("x");
        var monitors = Monitors();
        var monitor = monitors.FirstOrDefault(m => m.Hint == hint) ?? monitors[0];
        var fitted = PlacementPolicy.FitHeight(window.Spec, current, monitor, contentHeightDip);
        if (fitted != current) window.Place(fitted);
    }

    public void SetPinned(WindowKind kind, bool pinned)
    {
        if (windows.TryGetValue(kind, out var window)) SetWindowPos(window.Handle, pinned ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Anchor(WindowKind kind, PixelRect? selection, int widthDip)
    {
        anchors[kind] = (selection, widthDip);
        if (windows.TryGetValue(kind, out var window) && window.Visible) window.Place(Placement(window));
    }

    public void Post(WindowKind kind, string json)
    {
        if (windows.TryGetValue(kind, out var window)) window.View?.Post(json);
    }

    public void Suspend(WindowKind kind)
    {
        if (windows.TryGetValue(kind, out var window)) window.View?.Suspend();
    }

    public void ReleaseAll()
    {
        foreach (var window in windows.Values) window.Destroy();
        windows.Clear();
        environment?.Release();
        environment = null;
    }

    public IReadOnlyDictionary<string, bool> RegisterHotkeys(IReadOnlyDictionary<string, string> chordsByAction)
    {
        foreach (var id in hotkeyIds.Keys) UnregisterHotKey(dispatcher.Handle, id);
        hotkeyIds.Clear();
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (action, chord) in chordsByAction)
        {
            if (!Chords.TryParse(chord, out var modifiers, out uint key)) { results[action] = false; continue; }
            int id = nextHotkey++;
            bool ok = RegisterHotKey(dispatcher.Handle, id, (uint)modifiers | MOD_NOREPEAT, key);
            results[action] = ok;
            if (ok) hotkeyIds[id] = action;
            else Diagnostic?.Invoke($"hotkey.failed {action} {Marshal.GetLastPInvokeError()}");
        }
        return results;
    }

    public void SetClipboardText(string text)
    {
        if (!OpenClipboard(dispatcher.Handle)) return;
        try
        {
            EmptyClipboard();
            nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * 2));
            if (memory == 0) return;
            unsafe
            {
                char* target = (char*)GlobalLock(memory);
                text.AsSpan().CopyTo(new Span<char>(target, text.Length));
                target[text.Length] = '\0';
            }
            GlobalUnlock(memory);
            if (SetClipboardData(CF_UNICODETEXT, memory) == 0) GlobalFree(memory);
        }
        finally { CloseClipboard(); }
    }

    public void StartTimer(TimeSpan delay, Action elapsed) => dispatcher.StartTimer(delay, elapsed);

    public void Exit() => ExitRequested?.Invoke();

    /// <summary>Start with Windows (HKCU Run). The autostart launch shows no window.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void SetLaunchAtStartup(bool enabled, string executable)
    {
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) run.SetValue("Su-Su", $"\"{executable}\" --autostart");
        else if (run.GetValue("Su-Su") is not null) run.DeleteValue("Su-Su");
    }

    // ---------- WebView ----------

    private void EnsureView(ShellWindow window)
    {
        if (window.View is not null) return;
        environment ??= CreateEnvironment();
        window.SessionId = Guid.NewGuid().ToString("N");
        string path = $"index.html?w={window.Kind.ToString().ToLowerInvariant()}&s={window.SessionId}&l={uiLanguage()}";
        var env = environment;
        long started = Environment.TickCount64;
        env.WhenReady(() =>
        {
            if (window.Handle == 0 || window.View is not null || environment != env) return;
            if (!env.Ready) { Diagnostic?.Invoke($"webview.environment.failed 0x{env.Status:x8}"); return; }
            var view = new WebViewControl(env, window.Handle, UiHosting.HostFor(window.Kind), hosting.UiFolder, path, hosting.DevTools);
            window.View = view;
            view.ControllerReady += () =>
            {
                window.ResizeView();
                view.Visible(window.Visible);
                if (window.Visible && GetForegroundWindow() == window.Handle) view.Focus();
            };
            view.Navigated += () => Timing?.Invoke(window.Kind, "PageLoaded", Environment.TickCount64 - started);
            view.Message += json => PageMessage?.Invoke(window.Kind, json);
            view.Failed += hr => Diagnostic?.Invoke($"webview.failed {window.Kind} 0x{hr:x8}");
            view.Blocked += uri => Diagnostic?.Invoke($"webview.blocked {window.Kind} {OriginOf(uri)}");
        });
    }

    private WebViewEnvironment CreateEnvironment()
    {
        var env = new WebViewEnvironment(hosting.WebViewUserData, uiLanguage() == "en" ? "en-US" : "zh-CN");
        env.BrowserExited += () => { Diagnostic?.Invoke("webview.browser-exited"); BrowserExited?.Invoke(); };
        return env;
    }

    /// <summary>Scheme and host only: blocked-request diagnostics never log paths or queries.</summary>
    private static string OriginOf(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var u) ? (u.IsFile ? "file:" : $"{u.Scheme}://{u.Host}") : "(unparsed)";

    // ---------- placement ----------

    private PixelRect Placement(ShellWindow window)
    {
        var monitors = Monitors();
        GetCursorPos(out var cursor);
        var cursorMonitor = MonitorAt(monitors, cursor);
        if (window.Kind == WindowKind.Tray)
        {
            // Menu opens beside the pointer, inside the work area (the one window that follows the pointer by design).
            int width = Dip.ToPixels(window.Spec.WidthDip, cursorMonitor.Dpi), height = Dip.ToPixels(window.Spec.HeightDip, cursorMonitor.Dpi);
            var work = cursorMonitor.WorkArea;
            return new PixelRect(Math.Clamp(cursor.X - width, work.X, work.Right - width), Math.Clamp(cursor.Y - height, work.Y, work.Bottom - height), width, height);
        }
        if (window.Kind == WindowKind.Error) return PlacementPolicy.NearPointer(window.Spec, monitors, cursorMonitor, (cursor.X, cursor.Y));
        if (window.Kind == WindowKind.Speech)
        {
            var anchor = anchors.TryGetValue(WindowKind.Speech, out var a) ? a : (null, window.Spec.WidthDip);
            return PlacementPolicy.NearSelection(window.Spec, monitors, cursorMonitor, anchor.Selection, (cursor.X, cursor.Y), anchor.WidthDip);
        }
        (int, int)? remembered = null;
        if (window.Spec.RemembersPosition)
        {
            try { if (positions.Get(window.Spec.PlacementKey) is { } saved) remembered = (saved.X, saved.Y); }
            catch (Exception e) { Diagnostic?.Invoke($"window.position.read {e.GetType().Name}"); }
        }
        return PlacementPolicy.Compute(window.Spec, monitors, cursorMonitor, remembered);
    }

    private static MonitorInfo MonitorAt(IReadOnlyList<MonitorInfo> monitors, POINT point)
    {
        nint handle = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        return monitors.FirstOrDefault(m => m.Hint == handle.ToString("x")) ?? monitors[0];
    }

    [ThreadStatic] private static List<MonitorInfo>? enumerated;

    public static unsafe IReadOnlyList<MonitorInfo> Monitors()
    {
        enumerated = [];
        EnumDisplayMonitors(0, 0, &OnMonitor, 0);
        var result = enumerated;
        enumerated = null;
        return result;
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var info = new MONITORINFO { Size = sizeof(MONITORINFO) };
        if (!GetMonitorInfo(monitor, ref info)) return 1;
        uint dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint x, out _) >= 0 ? x : 96;
        enumerated?.Add(new MonitorInfo(monitor.ToString("x"), new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), (int)dpi));
        return 1;
    }

    public void Dispose()
    {
        foreach (var id in hotkeyIds.Keys) UnregisterHotKey(dispatcher.Handle, id);
        hotkeyIds.Clear();
        ReleaseAll();
    }
}
