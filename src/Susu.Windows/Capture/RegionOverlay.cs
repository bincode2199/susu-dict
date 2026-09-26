using Susu.Abstractions;
using Susu.Domain;
using Susu.Windows.Shell;
using static Susu.Windows.Capture.CaptureNative;
using static Susu.Windows.Shell.Win32;

namespace Susu.Windows.Capture;

/// <summary>
/// The screenshot selection overlay (PLAN 6.2, DESIGN "OCR 的截图选区上没有工具条"): one borderless topmost window across the
/// whole virtual screen that paints the frozen frame under a mask, the selection unmasked with a 1px accent border, 9px corner
/// ticks and a size label at its top-right, and the one-line hint. Press the left button, drag, release: done. Esc cancels.
///
/// <para>Runs on the UI (message) thread. It is created only after the freeze-frame was taken, so it is never in the image;
/// it is additionally excluded from screen capture (WDA_EXCLUDEFROMCAPTURE) and destroyed at release, so the mask disappears
/// at once. Coordinates are physical pixels: the process is Per-Monitor-V2 and WM_DPICHANGED is ignored (no resize), so client
/// point + virtual-screen origin is the virtual-screen pixel on every monitor.</para>
/// </summary>
public sealed unsafe class RegionOverlay : IRegionSelector
{
    public const string ClassName = "SuSu.CaptureOverlay";
    private const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_KEYDOWN = 0x100, WM_SETCURSOR = 0x20, WM_CANCELMODE = 0x1F;
    private const int VK_ESCAPE = 0x1B;

    /// <summary>The overlay window while a selection is open (tests drive it with posted mouse/key messages).</summary>
    public nint CurrentWindow => session?.Hwnd ?? 0;
    /// <summary>Raised on the UI thread once the overlay is visible.</summary>
    public event Action<nint>? Shown;

    private Session? session;

    public Task<PixelRect?> SelectAsync(IScreenFrame frame, RegionSelectOptions options, CancellationToken cancellationToken)
    {
        if (session is not null) return Task.FromResult<PixelRect?>(null);
        var current = new Session(this, frame, options);
        session = current;
        try { current.Open(cancellationToken); }
        catch { current.Finish(null); throw; }
        Shown?.Invoke(current.Hwnd);
        return current.Result;
    }

    private sealed class Session : IMessageTarget
    {
        private readonly RegionOverlay owner;
        private readonly IScreenFrame frame;
        private readonly RegionSelectOptions options;
        private readonly PixelRect bounds;
        private readonly TaskCompletionSource<PixelRect?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint original, originalOwned, masked, font;
        private byte* maskedPixels;
        private bool dragging;
        private int anchorX, anchorY;
        private PixelRect selection;
        private CancellationTokenRegistration cancel;
        private readonly int scale; // percent, from the primary/cursor monitor, for ticks, label and hint

        public Session(RegionOverlay owner, IScreenFrame frame, RegionSelectOptions options)
        {
            this.owner = owner; this.frame = frame; this.options = options; bounds = frame.Bounds;
            GetCursorPos(out var cursor);
            scale = Math.Max(100, CaptureGeometry.MonitorAt(cursor.X, cursor.Y, frame.Monitors).Dpi * 100 / 96);
        }

        public nint Hwnd { get; private set; }
        public Task<PixelRect?> Result => done.Task;

        public void Open(CancellationToken cancellationToken)
        {
            if (frame is GdiScreenFrame gdi) { original = gdi.Bitmap; sourcePixels = gdi.Pixels; }
            else original = originalOwned = CopyToBitmap(frame);
            masked = CreateBitmap(bounds.Width, bounds.Height, out maskedPixels);
            if (original == 0 || masked == 0) throw new InvalidOperationException("overlay bitmaps could not be created");
            BuildMask();
            font = CreateFont(-Scaled(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI"); // 10.5pt ≈ 14 px at 100 %
            Hwnd = WindowClasses.Create(this, ClassName, WS_EX_TOPMOST | WS_EX_TOOLWINDOW, WS_POPUP, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, "Su-Su");
            SetWindowDisplayAffinity(Hwnd, WDA_EXCLUDEFROMCAPTURE); // best effort (Windows 10 2004+)
            SetWindowPos(Hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_SHOWWINDOW);
            SetForegroundWindow(Hwnd);
            SetFocus(Hwnd);
            if (cancellationToken.CanBeCanceled)
            {
                nint hwnd = Hwnd;
                cancel = cancellationToken.Register(() => PostMessageW(hwnd, WM_CLOSE, 0, 0));
            }
        }

        public nint? OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case WM_SETCURSOR: SetCursor(LoadCursorW(0, 32515 /* IDC_CROSS */)); return 1;
                case WM_ERASEBKGND: return 1;
                case WM_DPICHANGED: return 0; // keep covering the virtual screen 1:1 in physical pixels
                case WM_PAINT: Paint(hwnd); return 0;
                case WM_LBUTTONDOWN:
                    (anchorX, anchorY) = CaptureGeometry.ClientToVirtual(LowWord(lParam), HighWord(lParam), bounds);
                    dragging = true;
                    Update(new PixelRect(anchorX, anchorY, 0, 0));
                    SetCapture(hwnd);
                    return 0;
                case WM_MOUSEMOVE:
                    if (dragging) Update(DragTo(lParam));
                    return 0;
                case WM_LBUTTONUP:
                    if (!dragging) return 0;
                    dragging = false;
                    Finish(DragTo(lParam)); // release = done; the caller treats < 8×8 as cancel (PLAN 6.2)
                    return 0;
                case WM_KEYDOWN:
                    if ((int)wParam == VK_ESCAPE) Finish(null);
                    return 0;
                case WM_CANCELMODE:
                    dragging = false;
                    ReleaseCapture();
                    return null;
                case WM_CLOSE: Finish(null); return 0;
            }
            return null;
        }

        private PixelRect DragTo(nint lParam)
        {
            var (x, y) = CaptureGeometry.ClientToVirtual(LowWord(lParam), HighWord(lParam), bounds);
            return CaptureGeometry.FromDrag(anchorX, anchorY, x, y, bounds);
        }

        private void Update(PixelRect next)
        {
            var old = selection;
            selection = next;
            Invalidate(old);
            Invalidate(next);
        }

        /// <summary>Repaints a selection's area plus room for the border, ticks and the size label above it.</summary>
        private void Invalidate(PixelRect rect)
        {
            int margin = Scaled(12), label = Scaled(28);
            var r = CaptureGeometry.ToFrame(rect, bounds);
            var area = new RECT { Left = r.X - margin, Top = r.Y - margin - label, Right = r.Right + margin + Scaled(80), Bottom = r.Bottom + margin };
            InvalidateArea(Hwnd, &area, false);
        }

        public void Finish(PixelRect? result)
        {
            if (done.Task.IsCompleted) return;
            cancel.Dispose();
            if (Hwnd != 0) { ReleaseCapture(); DestroyWindow(Hwnd); }
            if (masked != 0) DeleteObject(masked);
            if (originalOwned != 0) DeleteObject(originalOwned);
            if (font != 0) DeleteObject(font);
            masked = originalOwned = font = 0;
            if (ReferenceEquals(owner.session, this)) owner.session = null;
            done.TrySetResult(result);
        }

        private int Scaled(int px) => px * scale / 100;

        private void Paint(nint hwnd)
        {
            nint dc = BeginPaint(hwnd, out var ps);
            try
            {
                var p = ps.Paint;
                nint memory = CreateCompatibleDC(dc);
                nint old = SelectObject(memory, masked);
                BitBlt(dc, p.Left, p.Top, p.Right - p.Left, p.Bottom - p.Top, memory, p.Left, p.Top, SRCCOPY);
                var sel = CaptureGeometry.ToFrame(selection, bounds);
                if (selection.Width > 0 && selection.Height > 0)
                {
                    SelectObject(memory, original);
                    BitBlt(dc, sel.X, sel.Y, sel.Width, sel.Height, memory, sel.X, sel.Y, SRCCOPY);
                }
                SelectObject(memory, old);
                DeleteDC(memory);
                if (dragging || selection.Width > 0) DrawSelection(dc, sel);
                DrawHint(dc);
            }
            finally { EndPaint(hwnd, ps); }
        }

        private (uint Accent, uint Ink, uint Paper) Colors => options.Dark
            ? (Rgb(0x6C, 0x9B, 0xFF), Rgb(0xE8, 0xE9, 0xEC), Rgb(0x1B, 0x1D, 0x22))
            : (Rgb(0x2D, 0x64, 0xE0), Rgb(0x16, 0x18, 0x1D), Rgb(0xFF, 0xFF, 0xFF));

        private void DrawSelection(nint dc, PixelRect s)
        {
            nint accent = CreateSolidBrush(Colors.Accent);
            // 1px border just outside the selected pixels, so the border itself is never part of the image
            Fill(dc, accent, s.X - 1, s.Y - 1, s.Width + 2, 1);
            Fill(dc, accent, s.X - 1, s.Bottom, s.Width + 2, 1);
            Fill(dc, accent, s.X - 1, s.Y, 1, s.Height);
            Fill(dc, accent, s.Right, s.Y, 1, s.Height);
            int tick = Scaled(9), t = Math.Max(2, Scaled(2));
            foreach (var (cx, cy, dx, dy) in new[] { (s.X - 1, s.Y - 1, 1, 1), (s.Right + 1, s.Y - 1, -1, 1), (s.X - 1, s.Bottom + 1, 1, -1), (s.Right + 1, s.Bottom + 1, -1, -1) })
            {
                Fill(dc, accent, dx > 0 ? cx - t + 1 : cx - tick, dy > 0 ? cy - t + 1 : cy - 1, tick + t - 1, t);
                Fill(dc, accent, dx > 0 ? cx - t + 1 : cx - 1, dy > 0 ? cy - t + 1 : cy - tick, t, tick + t - 1);
            }
            DeleteObject(accent);
            string label = CaptureGeometry.SizeLabel(selection);
            var box = new RECT { Left = s.Right - Scaled(90), Top = s.Y - Scaled(24), Right = s.Right, Bottom = s.Y - Scaled(4) };
            if (box.Top < 0) { box.Top = s.Y + Scaled(4); box.Bottom = s.Y + Scaled(24); }
            DrawLabel(dc, label, box, right: true);
        }

        private void DrawHint(nint dc)
        {
            GetCursorPos(out var cursor);
            var monitor = CaptureGeometry.MonitorAt(cursor.X, cursor.Y, frame.Monitors);
            var m = CaptureGeometry.ToFrame(monitor.Bounds, bounds);
            var box = new RECT { Left = m.X, Top = m.Y + Scaled(24), Right = m.Right, Bottom = m.Y + Scaled(48) };
            DrawLabel(dc, options.Hint, box, right: false);
        }

        private void DrawLabel(nint dc, string text, RECT box, bool right)
        {
            nint oldFont = SelectObject(dc, font);
            SetBkMode(dc, TRANSPARENT);
            var measure = box;
            DrawText(dc, text, text.Length, ref measure, DT_SINGLELINE | 0x400 /* DT_CALCRECT */);
            int width = measure.Right - measure.Left + Scaled(12), height = box.Bottom - box.Top;
            int left = right ? box.Right - width : box.Left + (box.Right - box.Left - width) / 2;
            var pill = new RECT { Left = left, Top = box.Top, Right = left + width, Bottom = box.Top + height };
            nint paper = CreateSolidBrush(Colors.Paper);
            FillRect(dc, pill, paper);
            DeleteObject(paper);
            SetTextColor(dc, Colors.Ink);
            DrawText(dc, text, text.Length, ref pill, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            SelectObject(dc, oldFont);
        }

        private static void Fill(nint dc, nint brush, int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            FillRect(dc, new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height }, brush);
        }

        /// <summary>DESIGN mask: #F1F2F4 (light) / #0F1013 (dark) over the frozen screen ("屏幕被压暗").</summary>
        private void BuildMask()
        {
            var (mr, mg, mb) = options.Dark ? (0x0F, 0x10, 0x13) : (0xF1, 0xF2, 0xF4);
            const int alpha = 140; // of 256
            byte* src = sourcePixels, dst = maskedPixels;
            long count = (long)bounds.Width * bounds.Height;
            if (src == null) { new Span<byte>(dst, checked((int)(count * 4))).Clear(); return; }
            for (long i = 0; i < count; i++, src += 4, dst += 4)
            {
                dst[0] = (byte)((src[0] * (256 - alpha) + mb * alpha) >> 8);
                dst[1] = (byte)((src[1] * (256 - alpha) + mg * alpha) >> 8);
                dst[2] = (byte)((src[2] * (256 - alpha) + mr * alpha) >> 8);
                dst[3] = 255;
            }
        }

        private byte* sourcePixels;

        private nint CopyToBitmap(IScreenFrame source)
        {
            nint bitmap = CreateBitmap(bounds.Width, bounds.Height, out sourcePixels);
            if (bitmap != 0) source.CopyBgra(bounds).CopyTo(new Span<byte>(sourcePixels, bounds.Width * bounds.Height * 4));
            return bitmap;
        }
    }
}
