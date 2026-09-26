using Susu.Domain;

namespace Susu.Abstractions;

/// <summary>
/// A frozen image of the whole virtual screen (F11.1, F00 decision: GDI BitBlt). Pixels are 32-bit BGRA, top-down;
/// pixel (0,0) is virtual-screen point (<see cref="Bounds"/>.X, <see cref="Bounds"/>.Y), which may be negative.
/// </summary>
public interface IScreenFrame : IDisposable
{
    PixelRect Bounds { get; }
    IReadOnlyList<CaptureMonitor> Monitors { get; }
    /// <summary>Copies a virtual-screen rectangle (inside <see cref="Bounds"/>) as tightly packed BGRA rows.</summary>
    byte[] CopyBgra(PixelRect region);
}

/// <summary>Takes the freeze-frame of all monitors.</summary>
public interface IScreenGrabber
{
    IScreenFrame CaptureVirtualScreen();
}

/// <summary>What the selection overlay shows: the one-line hint (DESIGN "按住左键拖动框选 · 松开即完成 · Esc 取消") and the theme.</summary>
public sealed record RegionSelectOptions(string Hint, bool Dark = false);

/// <summary>
/// The native overlay over the frozen frame: press, drag, release. Returns the virtual-screen rectangle at release, or null
/// when the user pressed Esc (or the overlay was closed). Size filtering (8×8) is the caller's job.
/// </summary>
public interface IRegionSelector
{
    Task<PixelRect?> SelectAsync(IScreenFrame frame, RegionSelectOptions options, CancellationToken cancellationToken);
}

/// <summary>Hides every visible Su-Su window before the freeze-frame; disposing the token shows them again (without activating).</summary>
public interface ICaptureWindowHider
{
    Task<IDisposable> HideAllAsync(CancellationToken cancellationToken);
}

/// <summary>A host-held temporary file lease (F05 file handles). The path never leaves the host; plugins get <see cref="LeaseId"/> through a grant.</summary>
public interface ILeasedFile : IDisposable
{
    string LeaseId { get; }
    string Mime { get; }
    string FilePath { get; }
    long Bytes { get; }
    bool Released { get; }
}

public interface ILeasedFileFactory
{
    /// <summary>A new, empty leased file in the session cache (deleted when the last owner releases it, ARCHITECTURE 8.4).</summary>
    ILeasedFile Create(string purpose, string mime, string extension);
}

public sealed record KeepResult(bool Kept, string? FileName, string? ErrorCode);

/// <summary>
/// The optional "keep screenshots" copies (ARCHITECTURE 8.1/8.4, TEST-PLAN OCR03/DATA08): written only when the user enabled it,
/// indexed so that retention cleanup deletes only files this app wrote and indexed.
/// </summary>
public interface IScreenshotArchive
{
    KeepResult Keep(ReadOnlySpan<byte> png, DateTimeOffset now);
}

/// <summary>A captured region as a PNG file lease, ready for an OCR plugin (F11.2). Disposing releases the lease (the file goes).</summary>
public sealed class ScreenshotImage(ILeasedFile file, PixelRect region, int width, int height, int dpi, int monitorCount) : IDisposable
{
    public ILeasedFile File { get; } = file;
    /// <summary>The selection in physical virtual-screen pixels.</summary>
    public PixelRect Region { get; } = region;
    public int Width { get; } = width;
    public int Height { get; } = height;
    /// <summary>DPI of the monitor holding most of the selection (96 = 100 %); OCR boxes are normalized with it.</summary>
    public int Dpi { get; } = dpi;
    public int MonitorCount { get; } = monitorCount;
    /// <summary>
    /// F11.3: a small PNG thumbnail of the selection for the OCR window (made from the pixels at capture time, at most
    /// <see cref="Susu.Contracts.OcrView.PreviewMaxBytes"/>), or null. It stays usable after the lease is released, so the page
    /// never needs the file path.
    /// </summary>
    public byte[]? Preview { get; init; }
    public void Dispose() => File.Dispose();
}

public enum ScreenCaptureStatus { Captured, Cancelled, Failed }

/// <summary>
/// The outcome of one capture. Cancelled: Esc, a click without a drag or a selection under 8×8 (no temp image is left).
/// Failed: <see cref="ErrorCode"/> is <c>capture.failed</c>, <c>capture.diskFull</c> or <c>capture.writeFailed</c> — never a
/// false success. <see cref="CopyErrorCode"/>: the image was captured but the optional kept copy could not be written.
/// </summary>
public sealed record ScreenCaptureResult(ScreenCaptureStatus Status, ScreenshotImage? Image, string? ErrorCode = null, string? CopyErrorCode = null, string? KeptFileName = null)
{
    public static ScreenCaptureResult Cancelled(string reason) => new(ScreenCaptureStatus.Cancelled, null, reason);
    public static ScreenCaptureResult Failed(string code) => new(ScreenCaptureStatus.Failed, null, code);
}

/// <summary>
/// ARCHITECTURE 7 <c>IScreenCapture.CaptureRegion</c>: monitor snapshots + drag → image lease or cancel. The port F11.2/F11.3
/// call for "screenshot translate" and "recapture". Only one capture runs at a time; a second request while one is open
/// returns Cancelled("busy").
/// </summary>
public interface IScreenCapture
{
    Task<ScreenCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default);
}
