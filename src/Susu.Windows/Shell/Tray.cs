using static Susu.Windows.Shell.Win32;

namespace Susu.Windows.Shell;

/// <summary>
/// Notification-area icon (PLAN 1.4): left click does nothing, double click opens settings, right click opens the
/// menu window. The monochrome icon follows the taskbar theme, not the app theme (DESIGN 6). Re-added when
/// Explorer restarts. Background-path failures (e.g. hotkey registration) use system notifications (DESIGN 9).
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private readonly UiDispatcher dispatcher;
    private readonly string assets;
    private nint icon;
    private bool added;

    public TrayIcon(UiDispatcher dispatcher, string assetsFolder)
    {
        this.dispatcher = dispatcher;
        assets = assetsFolder;
        dispatcher.TaskbarRecreated += () => { added = false; Add(); };
        dispatcher.TrayEvent += OnEvent;
    }

    public event Action? DoubleClick;
    public event Action? MenuRequested;
    public bool Added => added;

    public bool Add()
    {
        LoadIcon();
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        CopyTo("Su-Su", data.Tip, 128);
        added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!added) return false;
        data.VersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        return true;
    }

    /// <summary>A Windows notification with a title and one line of body text (no content from user data).</summary>
    public void Notify(string title, string body)
    {
        if (!added) return;
        var data = Data(NIF_INFO);
        CopyTo(title, data.InfoTitle, 64);
        CopyTo(body, data.Info, 256);
        data.InfoFlags = NIIF_WARNING;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void OnEvent(uint message, int x, int y)
    {
        switch (message)
        {
            case WM_LBUTTONDBLCLK: DoubleClick?.Invoke(); break;
            case WM_CONTEXTMENU or WM_RBUTTONUP: MenuRequested?.Invoke(); break;
        }
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        Size = sizeof(NOTIFYICONDATAW), Hwnd = dispatcher.Handle, Id = IconId, Flags = flags, CallbackMessage = UiDispatcher.WM_TRAY, Icon = icon,
    };

    private void LoadIcon()
    {
        if (icon != 0) DestroyIcon(icon);
        uint dpi = GetDpiForWindow(dispatcher.Handle);
        if (dpi == 0) dpi = 96;
        int size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
        string file = Path.Combine(assets, OperatingSystem.IsWindows() && TaskbarIsLight() ? "tray-dark.ico" : "tray-light.ico");
        icon = File.Exists(file) ? LoadImage(0, file, IMAGE_ICON, size, size, LR_LOADFROMFILE) : 0;
    }

    /// <summary>Light taskbar → ink stroke; dark taskbar → paper stroke.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TaskbarIsLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            added = false;
        }
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
    }
}
