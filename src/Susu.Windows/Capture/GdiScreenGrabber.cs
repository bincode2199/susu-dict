using System.Runtime.InteropServices;
using Susu.Abstractions;
using Susu.Domain;
using static Susu.Windows.Shell.Win32;

namespace Susu.Windows.Capture;

/// <summary>GDI surface for the screenshot capture and its overlay (NativeAOT-safe LibraryImport, blittable structs).</summary>
internal static unsafe partial class CaptureNative
{
    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint hdc);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] public static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "InvalidateRect")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateArea(nint hwnd, RECT* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll", SetLastError = true)] public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GdiFlush();
    [LibraryImport("dwmapi.dll")] public static partial int DwmFlush();

    /// <summary>A top-down 32-bit DIB section of the given size; returns the HBITMAP (0 on failure) and its pixel pointer.</summary>
    public static nint CreateBitmap(int width, int height, out byte* pixels)
    {
        var header = new BITMAPINFOHEADER { Size = sizeof(BITMAPINFOHEADER), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        nint bitmap = CreateDIBSection(0, &header, 0, out nint bits, 0, 0);
        pixels = (byte*)bits;
        return bitmap;
    }

    [ThreadStatic] private static List<CaptureMonitor>? enumerated;

    /// <summary>Every monitor's full bounds (not the work area) and effective DPI, in physical pixels. Call under Per-Monitor-V2 awareness.</summary>
    public static IReadOnlyList<CaptureMonitor> Monitors()
    {
        enumerated = [];
        EnumDisplayMonitors(0, 0, &OnMonitor, 0);
        var result = enumerated;
        enumerated = null;
        return result;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var info = new MONITORINFO { Size = sizeof(MONITORINFO) };
        if (!GetMonitorInfo(monitor, ref info)) return 1;
        uint dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint x, out _) >= 0 ? x : 96;
        enumerated?.Add(new CaptureMonitor(monitor.ToString("x"),
            new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), (int)dpi, (info.Flags & 1) != 0));
        return 1;
    }
}

/// <summary>
/// The freeze-frame of the whole virtual screen with GDI BitBlt (F00 decision: same speed as DXGI duplication, no D3D device,
/// the same physical-pixel coordinates). The calling thread switches to Per-Monitor-V2 awareness for the duration so the
/// virtual-screen origin, monitor bounds and the copy are all in physical pixels on mixed-DPI setups.
/// </summary>
public sealed unsafe class GdiScreenGrabber : IScreenGrabber
{
    public IScreenFrame CaptureVirtualScreen()
    {
        nint previous = CaptureNative.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            var monitors = CaptureNative.Monitors();
            PixelRect bounds = monitors.Count > 0 ? CaptureGeometry.VirtualBounds(monitors)
                : new PixelRect(CaptureNative.GetSystemMetrics(CaptureNative.SM_XVIRTUALSCREEN), CaptureNative.GetSystemMetrics(CaptureNative.SM_YVIRTUALSCREEN),
                    CaptureNative.GetSystemMetrics(CaptureNative.SM_CXVIRTUALSCREEN), CaptureNative.GetSystemMetrics(CaptureNative.SM_CYVIRTUALSCREEN));
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("no display to capture");
            if (monitors.Count == 0) monitors = [new CaptureMonitor("0", bounds, 96, true)];
            nint bitmap = CaptureNative.CreateBitmap(bounds.Width, bounds.Height, out byte* pixels);
            if (bitmap == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            nint screen = CaptureNative.GetDC(0);
            nint memory = CaptureNative.CreateCompatibleDC(screen);
            nint old = SelectObject(memory, bitmap);
            bool ok = CaptureNative.BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, CaptureNative.SRCCOPY | CaptureNative.CAPTUREBLT);
            int error = Marshal.GetLastPInvokeError();
            SelectObject(memory, old);
            CaptureNative.DeleteDC(memory);
            CaptureNative.ReleaseDC(0, screen);
            CaptureNative.GdiFlush();
            if (!ok) { DeleteObject(bitmap); throw new System.ComponentModel.Win32Exception(error); }
            return new GdiScreenFrame(bitmap, pixels, bounds, monitors);
        }
        finally
        {
            if (previous != 0) CaptureNative.SetThreadDpiAwarenessContext(previous);
        }
    }
}

/// <summary>A captured virtual screen held in a DIB section (painted directly by the overlay, cropped for OCR).</summary>
public sealed unsafe class GdiScreenFrame : IScreenFrame
{
    private nint bitmap;
    private readonly byte* pixels;
    private readonly object gate = new();

    internal GdiScreenFrame(nint bitmap, byte* pixels, PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        this.bitmap = bitmap; this.pixels = pixels; Bounds = bounds; Monitors = monitors;
    }

    public PixelRect Bounds { get; }
    public IReadOnlyList<CaptureMonitor> Monitors { get; }
    internal nint Bitmap => bitmap;
    internal byte* Pixels => pixels;

    public byte[] CopyBgra(PixelRect region)
    {
        var inFrame = CaptureGeometry.ToFrame(region, Bounds);
        if (inFrame.X < 0 || inFrame.Y < 0 || inFrame.Right > Bounds.Width || inFrame.Bottom > Bounds.Height || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "region is outside the captured frame");
        var result = new byte[region.Width * region.Height * 4];
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(bitmap == 0, this);
            int stride = Bounds.Width * 4, rowBytes = region.Width * 4;
            for (int y = 0; y < region.Height; y++)
                new ReadOnlySpan<byte>(pixels + (long)(inFrame.Y + y) * stride + inFrame.X * 4, rowBytes).CopyTo(result.AsSpan(y * rowBytes, rowBytes));
        }
        return result;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (bitmap != 0) DeleteObject(bitmap);
            bitmap = 0;
        }
    }
}
