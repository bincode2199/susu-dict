using Susu.Contracts;
using Susu.Domain;
using static Susu.Windows.Shell.Win32;

namespace Susu.Windows.Shell;

/// <summary>
/// A top-level window hosting one WebView (ARCHITECTURE 9). Framed kinds (main, results, settings) keep a
/// caption style for DWM shadow, snapping and maximize but draw their own 40 px title bar in the page
/// (CSS <c>app-region: drag</c>); floating kinds are tool popups that can be shown without activation.
/// Sizes and positions are physical pixels computed from 96-DPI design sizes.
/// </summary>
internal sealed class ShellWindow : IMessageTarget
{
    public const string ClassName = "SuSu.Window";
    private readonly WindowPlatform owner;

    public ShellWindow(WindowPlatform owner, WindowKind kind)
    {
        this.owner = owner;
        Kind = kind;
        Spec = WindowSpec.For(kind);
        Framed = kind is WindowKind.Main or WindowKind.Settings or WindowKind.Ocr or WindowKind.Transcribe;
        uint style = Framed ? WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_CLIPCHILDREN : WS_POPUP | WS_CLIPCHILDREN;
        uint exStyle = Framed ? WS_EX_APPWINDOW : WS_EX_TOOLWINDOW | (kind == WindowKind.Tray ? WS_EX_TOPMOST : 0);
        Handle = WindowClasses.Create(this, ClassName, exStyle, style, 0, 0, 400, 300);
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, corner, sizeof(int));
        int border = (int)Rgb(0xD9, 0xDC, 0xE0);
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, border, sizeof(int));
        SetWindowPos(Handle, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (WindowClasses.AppIcon != 0) { SendMessageW(Handle, WM_SETICON, 1, WindowClasses.AppIcon); SendMessageW(Handle, WM_SETICON, 0, WindowClasses.AppIconSmall); }
    }

    public WindowKind Kind { get; }
    public WindowSpec Spec { get; }
    public bool Framed { get; }
    public nint Handle { get; private set; }
    public WebViewControl? View { get; set; }
    public string SessionId { get; set; } = "";
    public bool Visible => Handle != 0 && IsWindowVisible(Handle);

    public void Place(PixelRect rect) => SetWindowPos(Handle, 0, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    public PixelRect Rect()
    {
        GetWindowRect(Handle, out var r);
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public void ResizeView()
    {
        if (View is null) return;
        GetClientRect(Handle, out var r);
        View.Bounds(r.Right - r.Left, r.Bottom - r.Top);
    }

    public void Destroy()
    {
        View?.Close();
        View = null;
        if (Handle != 0) DestroyWindow(Handle);
        Handle = 0;
    }

    public unsafe nint? OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_NCCALCSIZE when Framed && wParam != 0:
                return 0; // client area covers the whole window; the page draws the title bar
            case WM_ERASEBKGND:
                return 1; // the WebView paints (white default background); avoids flashes
            case WM_SIZE:
                ResizeView();
                return 0;
            case WM_MOVE:
                View?.ParentMoved();
                return 0;
            case WM_DPICHANGED:
            {
                var suggested = (RECT*)lParam;
                SetWindowPos(hwnd, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top, SWP_NOZORDER | SWP_NOACTIVATE);
                return 0;
            }
            case WM_GETMINMAXINFO:
            {
                // Maximize fills the monitor work area (taskbar stays visible) for the frameless caption window.
                var info = (MINMAXINFO*)lParam;
                var monitor = new MONITORINFO { Size = sizeof(MONITORINFO) };
                if (GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref monitor))
                {
                    info->MaxPosition = new POINT { X = monitor.Work.Left - monitor.Monitor.Left, Y = monitor.Work.Top - monitor.Monitor.Top };
                    info->MaxSize = new POINT { X = monitor.Work.Right - monitor.Work.Left, Y = monitor.Work.Bottom - monitor.Work.Top };
                }
                return 0;
            }
            case WM_SYSCOMMAND when ((int)wParam & 0xFFF0) == SC_MINIMIZE:
                owner.Request(Kind, WindowRequest.Minimize); // minimized windows go to the tray, not the taskbar
                return 0;
            case WM_CLOSE:
                owner.Request(Kind, WindowRequest.Close);
                return 0;
            case WM_ACTIVATE:
                if (LowWord(wParam) != 0) View?.Focus();
                else if (Kind == WindowKind.Tray && Visible) owner.Request(Kind, WindowRequest.Escape); // menu closes on deactivate
                return 0;
        }
        return null;
    }
}

/// <summary>
/// Native "working…" shell shown without activation while a slower step runs (ARCHITECTURE 4.1, 11): drawn
/// with GDI so it is visible before any WebView exists and never takes focus from the target application.
/// </summary>
public sealed class WaitingShell : IMessageTarget
{
    private const string ClassName = "SuSu.Waiting";
    private string text = "";
    private readonly nint font;
    private readonly nint background = CreateSolidBrush(Rgb(0xFF, 0xFF, 0xFF));

    public WaitingShell()
    {
        Handle = WindowClasses.Create(this, ClassName, WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, WS_POPUP, 0, 0, 10, 10);
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUNDSMALL, sizeof(int));
        int border = (int)Rgb(0xD9, 0xDC, 0xE0);
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, border, sizeof(int));
        font = CreateFont(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
    }

    public nint Handle { get; }

    /// <summary>Shows near a physical point without activating; returns the tick when it was made visible.</summary>
    public long Show(string message, int x, int y, int dpi)
    {
        text = message;
        int width = Dip.ToPixels(160, dpi), height = Dip.ToPixels(34, dpi);
        SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(Handle, 0, true);
        return Environment.TickCount64;
    }

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    public nint? OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
            case WM_PAINT:
            {
                nint hdc = BeginPaint(hwnd, out var paint);
                GetClientRect(hwnd, out var rect);
                FillRect(hdc, rect, background);
                nint old = SelectObject(hdc, font);
                SetBkMode(hdc, TRANSPARENT);
                SetTextColor(hdc, Rgb(0x16, 0x18, 0x1D));
                DrawText(hdc, text, text.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                SelectObject(hdc, old);
                EndPaint(hwnd, paint);
                return 0;
            }
        }
        return null;
    }
}
