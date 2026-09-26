using System.Buffers.Binary;
using System.IO.Compression;
using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Jobs;

/// <summary>
/// F11.1 screenshot capture (ARCHITECTURE 7 <c>IScreenCapture.CaptureRegion</c>, PLAN 6.2, TEST-PLAN OCR01/OCR03/UI01):
/// hide Su-Su windows → freeze-frame all monitors (GDI BitBlt, F00) → native overlay over the frozen frame (press, drag,
/// release; Esc cancels) → show the windows again → crop in physical pixels → PNG into a host file lease for OCR (F11.2).
///
/// <para>The overlay is shown only after the frame was taken, so it can never be in the image. A cancel (Esc, a click, a
/// selection under 8×8) writes nothing. A write failure releases the lease (the partial file goes) and reports
/// <c>capture.diskFull</c> / <c>capture.writeFailed</c>: never a false success. The kept copy (Pictures/Su-Su) is written
/// only when <paramref name="keepScreenshots"/> returns true, which it does not by default (OCR03); its failure is reported
/// alongside the capture in <see cref="ScreenCaptureResult.CopyErrorCode"/>.</para>
/// </summary>
public sealed class ScreenCaptureCoordinator(
    IScreenGrabber grabber,
    IRegionSelector selector,
    ICaptureWindowHider hider,
    ILeasedFileFactory files,
    IClock clock,
    IScreenshotArchive? archive = null,
    Func<bool>? keepScreenshots = null,
    Func<RegionSelectOptions>? overlay = null,
    Action<string, ReadOnlyMemory<byte>>? writeFile = null) : IScreenCapture
{
    public const string HintZh = "按住左键拖动框选 · 松开即完成 · Esc 取消", HintEn = "Drag to select · release to finish · Esc to cancel";
    private readonly Func<bool> keepScreenshots = keepScreenshots ?? (() => false);
    private readonly Func<RegionSelectOptions> overlay = overlay ?? (() => new RegionSelectOptions(HintZh));
    private readonly Action<string, ReadOnlyMemory<byte>> writeFile = writeFile ?? WriteFlushed;
    private int busy;

    /// <summary>Phase timings of the last capture (hide, grab, select, encode), milliseconds; for diagnostics.</summary>
    public event Action<string, double>? Timing;

    public async Task<ScreenCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0) return ScreenCaptureResult.Cancelled("busy");
        try { return await CaptureCoreAsync(cancellationToken); }
        finally { Volatile.Write(ref busy, 0); }
    }

    private async Task<ScreenCaptureResult> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        IScreenFrame frame;
        PixelRect? picked;
        long t0 = clock.NowMilliseconds;
        using (var hidden = await hider.HideAllAsync(cancellationToken))
        {
            long t1 = clock.NowMilliseconds;
            Timing?.Invoke("hide", t1 - t0);
            try { frame = grabber.CaptureVirtualScreen(); }
            catch (Exception e) when (e is not OperationCanceledException) { return ScreenCaptureResult.Failed("capture.failed"); }
            long t2 = clock.NowMilliseconds;
            Timing?.Invoke("grab", t2 - t1);
            try { picked = await selector.SelectAsync(frame, overlay(), cancellationToken); }
            catch (OperationCanceledException) { frame.Dispose(); return ScreenCaptureResult.Cancelled("cancelled"); }
            catch { frame.Dispose(); throw; }
            Timing?.Invoke("select", clock.NowMilliseconds - t2);
        } // windows come back before any encoding work

        using (frame)
        {
            if (picked is not { } drawn) return ScreenCaptureResult.Cancelled("escape");
            var region = CaptureGeometry.Intersect(drawn, frame.Bounds);
            if (!CaptureGeometry.IsSelection(region)) return ScreenCaptureResult.Cancelled("tooSmall");
            long t3 = clock.NowMilliseconds;
            var result = await Task.Run(() => Store(frame, region), cancellationToken).ConfigureAwait(false);
            Timing?.Invoke("encode", clock.NowMilliseconds - t3);
            return result;
        }
    }

    private ScreenCaptureResult Store(IScreenFrame frame, PixelRect region)
    {
        byte[] png;
        byte[]? preview;
        try
        {
            var pixels = frame.CopyBgra(region);
            png = Png.EncodeBgra(pixels, region.Width, region.Height);
            preview = Png.Thumbnail(pixels, region.Width, region.Height, png);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return ScreenCaptureResult.Failed("capture.failed"); }
        ILeasedFile file = files.Create("ocr", "image/png", "png");
        try { writeFile(file.FilePath, png); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            file.Dispose(); // the lease goes, and with it any partial file
            return ScreenCaptureResult.Failed(IsDiskFull(e) ? "capture.diskFull" : "capture.writeFailed");
        }
        var monitor = CaptureGeometry.DominantMonitor(region, frame.Monitors);
        var image = new ScreenshotImage(file, region, region.Width, region.Height, monitor?.Dpi ?? 96, CaptureGeometry.MonitorsUnder(region, frame.Monitors).Count) { Preview = preview };
        if (!keepScreenshots() || archive is null) return new ScreenCaptureResult(ScreenCaptureStatus.Captured, image);
        KeepResult kept;
        try { kept = archive.Keep(png, clock.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { kept = new KeepResult(false, null, IsDiskFull(e) ? "capture.diskFull" : "capture.writeFailed"); }
        return new ScreenCaptureResult(ScreenCaptureStatus.Captured, image, null, kept.Kept ? null : kept.ErrorCode ?? "capture.writeFailed", kept.FileName);
    }

    /// <summary>ERROR_HANDLE_DISK_FULL (39) or ERROR_DISK_FULL (112), as HRESULT_FROM_WIN32 in <see cref="Exception.HResult"/>.</summary>
    public static bool IsDiskFull(Exception e) => e is IOException && (e.HResult & 0xFFFF) is 39 or 112 && (e.HResult >> 16 & 0x7FFF) is 7 or 0;

    private static void WriteFlushed(string path, ReadOnlyMemory<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes.Span);
        stream.Flush(flushToDisk: true);
    }
}

/// <summary>Minimal PNG encoder for screenshots (8-bit RGB, Sub filter, zlib); NativeAOT-safe and without System.Drawing.</summary>
public static class Png
{
    private static readonly uint[] crcTable = BuildCrcTable();

    /// <summary>Encodes tightly packed top-down BGRA pixels; alpha is dropped (screen alpha carries no meaning).</summary>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4) throw new ArgumentException("pixel buffer does not match the size");
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; header[9] = 2; header[10] = 0; header[11] = 0; header[12] = 0;
        Chunk(output, "IHDR"u8, header);
        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + width * 3];
                row[0] = 1; // Sub
                for (int y = 0; y < height; y++)
                {
                    var src = bgra.Slice(y * width * 4, width * 4);
                    byte pr = 0, pg = 0, pb = 0;
                    for (int x = 0, o = 1; x < width; x++, o += 3)
                    {
                        byte b = src[x * 4], g = src[x * 4 + 1], r = src[x * 4 + 2];
                        row[o] = (byte)(r - pr); row[o + 1] = (byte)(g - pg); row[o + 2] = (byte)(b - pb);
                        pr = r; pg = g; pb = b;
                    }
                    z.Write(row);
                }
            }
            Chunk(output, "IDAT"u8, data.GetBuffer().AsSpan(0, (int)data.Length));
        }
        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>Largest thumbnail edge, in pixels (the OCR window is 420 DIP wide; 2× for high-DPI screens).</summary>
    public const int ThumbnailMaxWidth = 760, ThumbnailMaxHeight = 360;

    /// <summary>
    /// F11.3: the OCR window's preview. The full PNG when it already fits <see cref="ThumbnailMaxWidth"/>×<see cref="ThumbnailMaxHeight"/>
    /// and <see cref="Susu.Contracts.OcrView.PreviewMaxBytes"/>; otherwise a box-filtered copy shrunk by a whole factor until it
    /// fits both limits; null when even a small copy is too large.
    /// </summary>
    public static byte[]? Thumbnail(ReadOnlySpan<byte> bgra, int width, int height, byte[]? full = null)
    {
        int factor = Math.Max(1, Math.Max((width + ThumbnailMaxWidth - 1) / ThumbnailMaxWidth, (height + ThumbnailMaxHeight - 1) / ThumbnailMaxHeight));
        for (int attempt = 0; attempt < 4; attempt++, factor *= 2)
        {
            byte[] png = factor == 1 && full is not null ? full : factor == 1 ? EncodeBgra(bgra, width, height) : EncodeBgra(Shrink(bgra, width, height, factor, out int w, out int h), w, h);
            if (png.Length <= Susu.Contracts.OcrView.PreviewMaxBytes) return png;
        }
        return null;
    }

    /// <summary>Averages each <paramref name="factor"/>×<paramref name="factor"/> block (edge blocks may be smaller).</summary>
    public static byte[] Shrink(ReadOnlySpan<byte> bgra, int width, int height, int factor, out int targetWidth, out int targetHeight)
    {
        targetWidth = Math.Max(1, (width + factor - 1) / factor);
        targetHeight = Math.Max(1, (height + factor - 1) / factor);
        var output = new byte[targetWidth * targetHeight * 4];
        for (int ty = 0; ty < targetHeight; ty++)
        {
            int y0 = ty * factor, y1 = Math.Min(height, y0 + factor);
            for (int tx = 0; tx < targetWidth; tx++)
            {
                int x0 = tx * factor, x1 = Math.Min(width, x0 + factor);
                int b = 0, g = 0, r = 0, n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int o = (y * width + x) * 4;
                        b += bgra[o]; g += bgra[o + 1]; r += bgra[o + 2]; n++;
                    }
                int t = (ty * targetWidth + tx) * 4;
                output[t] = (byte)(b / n); output[t + 1] = (byte)(g / n); output[t + 2] = (byte)(r / n); output[t + 3] = 255;
            }
        }
        return output;
    }

    /// <summary>Reads width/height from a PNG header (tests and diagnostics).</summary>
    public static (int Width, int Height) SizeOf(ReadOnlySpan<byte> png)
        => (BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..]));

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        uint crc = Update(Update(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
