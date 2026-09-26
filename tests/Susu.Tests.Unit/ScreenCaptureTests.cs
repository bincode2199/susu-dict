using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Susu.Ui;
using Susu.Windows.Capture;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F11.1 screenshot capture (TEST-PLAN OCR01, OCR03, UI01, DATA08; ARCHITECTURE 7/8.4/9; PLAN 6.2): physical-pixel coordinate
/// math with negative monitors and 100/150/200 % DPI, the 8×8 rule, cancel leaves no file, window hide/restore order, PNG
/// lease output, disk-full as a real error, keep-screenshots off by default, indexed retention (7 days, held files kept,
/// user files never touched), and the overlay and GDI grabber on the real desktop where the session allows.
/// </summary>
public class ScreenCaptureTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly FileLeases leases;
    private readonly ManualClock clock = new();

    public ScreenCaptureTests() { leases = new FileLeases(root.Paths.Cache); }

    public void Dispose()
    {
        leases.Dispose();
        root.Dispose();
    }

    // Three monitors: a 150 % one left of and above the primary (negative coordinates), the 100 % primary, a 200 % one to the right.
    private static readonly CaptureMonitor Left150 = new("a", new PixelRect(-2880, -400, 2880, 1620), 144, false);
    private static readonly CaptureMonitor Primary100 = new("b", new PixelRect(0, 0, 1920, 1080), 96, true);
    private static readonly CaptureMonitor Right200 = new("c", new PixelRect(1920, 0, 3840, 2160), 192, false);
    private static readonly CaptureMonitor[] Mixed = [Left150, Primary100, Right200];

    // ---------- coordinate math ----------

    [Fact]
    public void Virtual_bounds_cover_negative_and_mixed_dpi_monitors_in_physical_pixels()
    {
        Assert.Equal(new PixelRect(-2880, -400, 2880 + 1920 + 3840, 2560), CaptureGeometry.VirtualBounds(Mixed));
        Assert.Equal((-2880 + 10, -400 + 20), CaptureGeometry.ClientToVirtual(10, 20, CaptureGeometry.VirtualBounds(Mixed)));
        Assert.Equal(new PixelRect(5, 5, 100, 100), CaptureGeometry.ToFrame(new PixelRect(-2875, -395, 100, 100), CaptureGeometry.VirtualBounds(Mixed)));
    }

    [Fact]
    public void Drag_rectangles_normalize_any_direction_and_clamp_to_the_virtual_screen()
    {
        var bounds = CaptureGeometry.VirtualBounds(Mixed);
        Assert.Equal(new PixelRect(-100, -50, 200, 150), CaptureGeometry.FromDrag(100, 100, -100, -50, bounds)); // dragged up-left
        Assert.Equal(new PixelRect(0, 0, 8, 8), CaptureGeometry.FromDrag(0, 0, 8, 8, bounds));
        Assert.Equal(new PixelRect(-2880, -400, 80, 410), CaptureGeometry.FromDrag(-2800, 10, -5000, -900, bounds)); // off the left/top edge
        Assert.Equal(new PixelRect(5700, 2000, 60, 160), CaptureGeometry.FromDrag(5700, 2000, 9999, 9999, bounds)); // off the right/bottom edge
    }

    [Theory] // PLAN 6.2 / OCR01 "8×8 边界": smaller than 8×8 (or a click) cancels
    [InlineData(8, 8, true)]
    [InlineData(7, 8, false)]
    [InlineData(8, 7, false)]
    [InlineData(0, 0, false)]
    [InlineData(400, 9, true)]
    public void The_8x8_minimum(int width, int height, bool selection)
        => Assert.Equal(selection, CaptureGeometry.IsSelection(new PixelRect(-3, -3, width, height)));

    [Fact] // UI01: a crop across the 150 %/100 %/200 % boundaries keeps physical pixels; OCR normalizes with the dominant monitor's DPI
    public void Crop_across_monitor_boundaries_reports_the_monitors_and_the_dominant_dpi()
    {
        var across = new PixelRect(-100, 100, 2120, 300); // 100 px on the 150 % monitor, all of the primary's width, 100 px on the 200 % one
        Assert.Equal(["a", "b", "c"], CaptureGeometry.MonitorsUnder(across, Mixed).Select(m => m.Hint));
        Assert.Equal(96, CaptureGeometry.DominantMonitor(across, Mixed)!.Dpi);
        Assert.Equal(192, CaptureGeometry.DominantMonitor(new PixelRect(1800, 0, 400, 50), Mixed)!.Dpi);
        Assert.Equal(144, CaptureGeometry.DominantMonitor(new PixelRect(-2000, -300, 50, 50), Mixed)!.Dpi);
        Assert.Null(CaptureGeometry.DominantMonitor(new PixelRect(0, 1500, 100, 100), Mixed)); // the gap under the primary
        Assert.Equal("2120 × 300", CaptureGeometry.SizeLabel(across));
        Assert.Same(Right200, CaptureGeometry.MonitorAt(3000, 1500, Mixed));
        Assert.Same(Primary100, CaptureGeometry.MonitorAt(0, 1500, Mixed)); // in a gap: the primary
    }

    [Fact]
    public async Task Captured_crop_across_mixed_dpi_monitors_has_the_exact_physical_pixels()
    {
        var frame = new FakeFrame(Mixed);
        var hider = new FakeHider();
        var region = new PixelRect(-50, -10, 120, 40); // crosses from the negative 150 % monitor into the primary
        var capture = Coordinator(frame, new FakeSelector(region, hider), hider);
        var result = await capture.CaptureRegionAsync(Ct);
        Assert.Equal(ScreenCaptureStatus.Captured, result.Status);
        using var image = result.Image!;
        Assert.Equal(region, image.Region);
        Assert.Equal((120, 40), (image.Width, image.Height));
        Assert.Equal(2, image.MonitorCount);
        Assert.Equal("image/png", image.File.Mime);
        byte[] png = File.ReadAllBytes(image.File.FilePath);
        Assert.Equal((120, 40), Png.SizeOf(png));
        var rgb = DecodeRgb(png);
        for (int y = 0; y < 40; y += 13)
            for (int x = 0; x < 120; x += 17)
                Assert.Equal(FakeFrame.ColorAt(region.X + x, region.Y + y), (rgb[(y * 120 + x) * 3], rgb[(y * 120 + x) * 3 + 1], rgb[(y * 120 + x) * 3 + 2]));
        Assert.True(frame.Disposed);
        Assert.Equal(["hide", "grab", "select", "restore"], hider.Log); // hidden during grab and selection, restored before encoding
    }

    // ---------- OCR01: cancel leaves nothing ----------

    [Theory]
    [InlineData("escape")]
    [InlineData("tooSmall")]
    [InlineData("click")]
    public async Task Cancel_leaves_no_temp_image_and_restores_windows(string how)
    {
        var frame = new FakeFrame(Mixed);
        var hider = new FakeHider();
        PixelRect? picked = how switch { "escape" => null, "tooSmall" => new PixelRect(10, 10, 7, 30), _ => new PixelRect(10, 10, 0, 0) };
        var result = await Coordinator(frame, new FakeSelector(picked, hider), hider).CaptureRegionAsync(Ct);
        Assert.Equal(ScreenCaptureStatus.Cancelled, result.Status);
        Assert.Null(result.Image);
        Assert.Equal(how == "escape" ? "escape" : "tooSmall", result.ErrorCode);
        Assert.Equal(0, leases.ActiveCount);
        Assert.Empty(CacheFiles());
        Assert.True(frame.Disposed);
        Assert.Equal("restore", hider.Log[^1]);
        Assert.False(Directory.Exists(root.Paths.KeptScreenshots));
    }

    [Fact]
    public async Task A_second_capture_while_one_is_open_is_refused_and_cancellation_cancels()
    {
        var hider = new FakeHider();
        var gate = new TaskCompletionSource<PixelRect?>();
        var selector = new FakeSelector(null, hider) { Pending = gate.Task };
        var capture = Coordinator(new FakeFrame(Mixed), selector, hider);
        var first = capture.CaptureRegionAsync(Ct);
        var second = await capture.CaptureRegionAsync(Ct);
        Assert.Equal("busy", second.ErrorCode);
        gate.SetCanceled(Ct);
        Assert.Equal("cancelled", (await first).ErrorCode);
        Assert.Equal("restore", hider.Log[^1]);
    }

    [Fact]
    public async Task A_failed_grab_is_an_error_and_windows_come_back()
    {
        var hider = new FakeHider();
        var capture = new ScreenCaptureCoordinator(new ThrowingGrabber(), new FakeSelector(new PixelRect(0, 0, 50, 50), hider), hider, new LeasedFiles(leases), clock);
        var result = await capture.CaptureRegionAsync(Ct);
        Assert.Equal((ScreenCaptureStatus.Failed, "capture.failed"), (result.Status, result.ErrorCode));
        Assert.Equal(["hide", "restore"], hider.Log);
    }

    // ---------- OCR03: disk full, keep-screenshots ----------

    [Fact]
    public async Task Disk_full_on_the_temp_image_is_a_real_error_and_leaves_no_file()
    {
        var hider = new FakeHider();
        var capture = new ScreenCaptureCoordinator(new FakeGrabber(new FakeFrame(Mixed)), new FakeSelector(new PixelRect(0, 0, 64, 64), hider), hider, new LeasedFiles(leases), clock,
            writeFile: (path, bytes) => { File.WriteAllBytes(path, bytes.Span[..10].ToArray()); throw DiskFull(); });
        var result = await capture.CaptureRegionAsync(Ct);
        Assert.Equal((ScreenCaptureStatus.Failed, "capture.diskFull"), (result.Status, result.ErrorCode));
        Assert.Null(result.Image);
        Assert.Equal(0, leases.ActiveCount);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task Keep_screenshots_is_off_by_default_and_writes_nothing_outside_the_cache()
    {
        var archive = new CountingArchive();
        var hider = new FakeHider();
        var result = await new ScreenCaptureCoordinator(new FakeGrabber(new FakeFrame(Mixed)), new FakeSelector(new PixelRect(0, 0, 32, 32), hider), hider, new LeasedFiles(leases), clock, archive)
            .CaptureRegionAsync(Ct);
        using var image = result.Image!;
        Assert.Equal(0, archive.Calls);
        Assert.Null(result.KeptFileName);
        Assert.False(Directory.Exists(root.Paths.KeptScreenshots));
        image.Dispose();
        Assert.Empty(CacheFiles()); // releasing the lease deletes the temp image (ARCHITECTURE 8.4)
    }

    [Fact]
    public async Task Keep_screenshots_on_writes_an_indexed_copy_and_a_disk_full_copy_is_reported_not_kept()
    {
        var kept = KeptScreenshots.For(root.Paths);
        var hider = new FakeHider();
        var result = await new ScreenCaptureCoordinator(new FakeGrabber(new FakeFrame(Mixed)), new FakeSelector(new PixelRect(0, 0, 32, 32), hider), hider, new LeasedFiles(leases), clock, kept, () => true)
            .CaptureRegionAsync(Ct);
        using (result.Image)
        {
            Assert.Null(result.CopyErrorCode);
            Assert.NotNull(result.KeptFileName);
            string copy = Path.Combine(root.Paths.KeptScreenshots, result.KeptFileName!);
            Assert.Equal(File.ReadAllBytes(result.Image!.File.FilePath), File.ReadAllBytes(copy));
            Assert.Single(kept.Store.Entries);
        }

        var full = KeptScreenshots.For(root.Paths, writeFile: (path, bytes) => { File.WriteAllBytes(path, [1, 2, 3]); throw DiskFull(); });
        var second = await new ScreenCaptureCoordinator(new FakeGrabber(new FakeFrame(Mixed)), new FakeSelector(new PixelRect(0, 0, 32, 32), hider), hider, new LeasedFiles(leases), clock, full, () => true)
            .CaptureRegionAsync(Ct);
        using (second.Image)
        {
            Assert.Equal(ScreenCaptureStatus.Captured, second.Status); // the OCR can still run on the temp image
            Assert.Equal("capture.diskFull", second.CopyErrorCode);
            Assert.Null(second.KeptFileName);
            Assert.Single(Directory.GetFiles(root.Paths.KeptScreenshots)); // no partial second file
            Assert.Single(full.Store.Entries);
        }
    }

    // ---------- DATA08 / OCR03: retention ----------

    [Fact]
    public void Retention_deletes_only_expired_indexed_files_and_never_user_files()
    {
        var store = new IndexedFileStore(Path.Combine(root.Root, "Pics"), Path.Combine(root.Paths.Local, "index.json"));
        var t0 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        string old = store.Add("a.png", [1, 2, 3], t0);
        string fresh = store.Add("b.png", [4, 5, 6], t0.AddDays(3));
        string held = store.Add("c.png", [7, 8], t0);
        string replaced = store.Add("d.png", [9], t0);
        string gone = store.Add("e.png", [10], t0);
        string userFile = Path.Combine(store.Directory, "holiday.png");
        File.WriteAllBytes(userFile, [42]);
        File.SetLastWriteTimeUtc(userFile, t0.AddYears(-1).UtcDateTime);
        File.WriteAllBytes(Path.Combine(store.Directory, replaced), [99, 99]); // the user saved over our copy
        File.Delete(Path.Combine(store.Directory, gone));
        var hold = store.Hold(held);

        var report = store.Cleanup(t0.AddDays(7).AddMinutes(1), RetentionRules.KeptScreenshots());
        Assert.Equal(new CleanupReport(Deleted: 1, Forgotten: 1, InUse: 1, Changed: 1), report);
        Assert.False(File.Exists(Path.Combine(store.Directory, old)));
        Assert.True(File.Exists(Path.Combine(store.Directory, fresh)));
        Assert.True(File.Exists(Path.Combine(store.Directory, held)));
        Assert.True(File.Exists(Path.Combine(store.Directory, replaced)));
        Assert.True(File.Exists(userFile));
        Assert.Equal([fresh, held], store.Entries.Select(e => e.Name).Order());

        hold.Dispose();
        Assert.Equal(1, store.Cleanup(t0.AddDays(8), RetentionRules.KeptScreenshots()).Deleted);
        Assert.Equal(1, store.Cleanup(t0.AddDays(11), RetentionRules.KeptScreenshots()).Deleted); // the 3-day-later file after its own 7 days
        Assert.Empty(store.Entries);
        Assert.Equal(new[] { "holiday.png", replaced }.Order(), Directory.GetFiles(store.Directory).Select(Path.GetFileName).Order());

        // a fresh store instance reads the index from disk
        Assert.Empty(new IndexedFileStore(store.Directory, Path.Combine(root.Paths.Local, "index.json")).Entries);
    }

    [Fact]
    public void Retention_honours_a_configured_day_count_and_an_optional_byte_bound()
    {
        var store = new IndexedFileStore(Path.Combine(root.Root, "Pics"), Path.Combine(root.Paths.Local, "index.json"));
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        store.Add("1.png", new byte[100], t0);
        store.Add("2.png", new byte[100], t0.AddHours(1));
        store.Add("3.png", new byte[100], t0.AddHours(2));
        Assert.Equal(1, store.Cleanup(t0.AddHours(3), new RetentionRules(TimeSpan.FromDays(7), MaxBytes: 250)).Deleted); // oldest first until ≤ 250
        Assert.Equal(["2.png", "3.png"], store.Entries.Select(e => e.Name));
        Assert.Equal(0, store.Cleanup(t0.AddDays(1.5), RetentionRules.KeptScreenshots(2)).Deleted);
        Assert.Equal(2, store.Cleanup(t0.AddDays(2.2), RetentionRules.KeptScreenshots(2)).Deleted);
    }

    [Fact]
    public void A_damaged_or_hostile_index_deletes_nothing()
    {
        string dir = Path.Combine(root.Root, "Pics");
        Directory.CreateDirectory(dir);
        string outside = Path.Combine(root.Root, "outside.png");
        File.WriteAllBytes(outside, [1]);
        File.WriteAllBytes(Path.Combine(dir, "x.png"), [1]);
        string index = Path.Combine(root.Paths.Local, "index.json");
        string hash = AtomicFile.Hash([1]);
        File.WriteAllText(index, $$"""{"version":1,"files":[{"name":"..\\outside.png","bytes":1,"sha256":"{{hash}}","createdUtc":"2000-01-01T00:00:00Z"}]}""");
        var hostile = new IndexedFileStore(dir, index);
        Assert.Empty(hostile.Entries);
        Assert.Equal(new CleanupReport(0, 0, 0, 0), hostile.Cleanup(DateTimeOffset.UtcNow, RetentionRules.KeptScreenshots()));
        File.WriteAllText(index, "{not json");
        Assert.Equal(new CleanupReport(0, 0, 0, 0), new IndexedFileStore(dir, index).Cleanup(DateTimeOffset.UtcNow, RetentionRules.KeptScreenshots()));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(Path.Combine(dir, "x.png")));
    }

    [Fact]
    public void Kept_names_never_overwrite_and_the_folder_stays_under_an_explicit_data_root()
    {
        Assert.Equal(Path.Combine(root.Root, "Pictures", "Su-Su"), root.Paths.KeptScreenshots);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "x"), AppPaths.Resolve(Path.Combine(Path.GetTempPath(), "x"), development: false).KeptScreenshots, StringComparison.OrdinalIgnoreCase);
        var kept = KeptScreenshots.For(root.Paths);
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var a = kept.Keep([1], now);
        var b = kept.Keep([2], now);
        Assert.True(a.Kept && b.Kept);
        Assert.NotEqual(a.FileName, b.FileName);
        Assert.Equal(2, kept.Cleanup(now.AddDays(7.1)).Deleted);
    }

    // ---------- shell gate ----------

    [Fact] // the OCR hotkey reaches the port only while the feature resolves Available (dev preview here); F11.3 flips it
    public async Task The_ocr_hotkey_is_gated_by_the_feature_state()
    {
        foreach (bool preview in new[] { false, true })
        {
            using var temp = new TempRoot();
            var settings = new SettingsStore(temp.Paths, new ManualClock());
            var config = new ConfigService(settings, new SecretStore(temp.Paths.Secrets, new XorProtector()));
            var features = new FeatureRegistry();
            features.Register(new FeatureDescriptor(FeatureRegistry.Ids.Ocr, FeatureState.InDevelopment, "feature.inDevelopment", []));
            var shell = new ShellCoordinator(new FakePlatform(), config, features, _ => false, new ShellOptions(false, preview));
            var port = new CountingCapture();
            shell.ScreenCapture = port;
            var got = new TaskCompletionSource<ScreenCaptureResult>();
            shell.ScreenCaptured += r => got.TrySetResult(r);
            shell.Start();
            shell.OnHotkey("ocrTranslate");
            Assert.Equal(preview ? 1 : 0, port.Calls);
            if (preview) Assert.Equal("escape", (await got.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ErrorCode);
        }
    }

    // ---------- real desktop ----------

    [Fact] // OCR01 on a real window: the overlay is driven with posted mouse/key messages over a known frame with a negative origin
    public void Real_overlay_returns_the_dragged_rectangle_and_escape_cancels()
    {
        var bounds = new PixelRect(-300, -120, 640, 360);
        var frame = new FakeFrame([new CaptureMonitor("n", bounds, 144, true)]);
        var overlay = new RegionOverlay();
        var picked = OnMessageThread(async () =>
        {
            var task = overlay.SelectAsync(frame, new RegionSelectOptions(ScreenCaptureCoordinator.HintZh), Ct);
            nint hwnd = overlay.CurrentWindow;
            Assert.NotEqual(0, hwnd);
            Native.PostMessageW(hwnd, 0x201, 1, Native.Point(200, 100)); // down at client (200,100) = virtual (-100,-20)
            Native.PostMessageW(hwnd, 0x200, 1, Native.Point(150, 60));
            Native.PostMessageW(hwnd, 0x200, 1, Native.Point(260, 180));
            Native.PostMessageW(hwnd, 0x202, 0, Native.Point(260, 180)); // up at client (260,180) = virtual (-40,60)
            return await task;
        });
        Assert.Equal(new PixelRect(-100, -20, 60, 80), picked);
        Assert.Equal(0, overlay.CurrentWindow); // destroyed at release: the mask is gone

        var cancelled = OnMessageThread(async () =>
        {
            var task = overlay.SelectAsync(frame, new RegionSelectOptions("hint", Dark: true), Ct);
            Native.PostMessageW(overlay.CurrentWindow, 0x201, 1, Native.Point(10, 10));
            Native.PostMessageW(overlay.CurrentWindow, 0x200, 1, Native.Point(90, 90));
            Native.PostMessageW(overlay.CurrentWindow, 0x100, 0x1B, 0); // Esc while dragging
            return await task;
        });
        Assert.Null(cancelled);
    }

    [Fact] // GDI BitBlt of the real desktop: bounds match the PMv2 virtual screen, and a known topmost window region is found
    public void Real_gdi_capture_finds_a_known_window_region()
    {
        var outcome = OnMessageThread(() =>
        {
            nint previous = Native.SetThreadDpiAwarenessContext(-4);
            nint hwnd = Native.CreateWindowExW(0x8 | 0x80 | 0x08000000, "STATIC", "", 0x80000000 | 0x10000000, 40, 40, 64, 48, 0, 0, 0, 0);
            try
            {
                Native.GetWindowRect(hwnd, out var rect);
                for (int i = 0; i < 20; i++) { Pump(); Paint(hwnd); Native.DwmFlush(); }
                IScreenFrame captured;
                try { captured = new GdiScreenGrabber().CaptureVirtualScreen(); }
                catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 5)
                {
                    return Task.FromResult<(PixelRect, PixelRect, int, int, int, bool)?>(null); // not the input desktop (locked/disconnected session)
                }
                using var frame = captured;
                var expected = new PixelRect(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
                var inner = new PixelRect(rect.Left + 4, rect.Top + 4, 56, 40);
                var pixels = frame.CopyBgra(inner);
                int magenta = 0;
                for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 200 && pixels[i + 1] < 60 && pixels[i + 2] > 200) magenta++;
                var whole = frame.CopyBgra(new PixelRect(frame.Bounds.X, frame.Bounds.Y, Math.Min(200, frame.Bounds.Width), Math.Min(200, frame.Bounds.Height)));
                return Task.FromResult<(PixelRect, PixelRect, int, int, int, bool)?>((frame.Bounds, expected, frame.Monitors.Count, magenta, pixels.Length / 4, whole.Any(b => b != 0)));
            }
            finally
            {
                Native.DestroyWindow(hwnd);
                if (previous != 0) Native.SetThreadDpiAwarenessContext(previous);
            }
        });
        if (outcome is not var (bounds, expected, monitors, magenta, total, anyPixel)) { Assert.Skip("BitBlt of the screen was denied: this session is not on the input desktop (locked or disconnected)"); return; }
        Assert.Equal(expected, bounds);
        Assert.True(monitors >= 1);
        if (magenta == 0 && !anyPixel) Assert.Skip("the desktop session renders nothing to GDI (locked or disconnected session)");
        Assert.True(magenta >= total * 9 / 10, $"marker pixels {magenta}/{total}");
    }

    // ---------- helpers ----------

    private ScreenCaptureCoordinator Coordinator(FakeFrame frame, FakeSelector selector, FakeHider hider)
        => new(new FakeGrabber(frame, hider), selector, hider, new LeasedFiles(leases), clock);

    private string[] CacheFiles() => Directory.GetFiles(root.Paths.Cache, "*", SearchOption.AllDirectories);

    private static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));

    private static void Paint(nint hwnd)
    {
        nint dc = Native.GetDC(hwnd);
        nint brush = Native.CreateSolidBrush(0xFF00FF);
        var r = new Native.RECT { Right = 64, Bottom = 48 };
        Native.FillRect(dc, ref r, brush);
        Native.DeleteObject(brush);
        Native.ReleaseDC(hwnd, dc);
    }

    private static void Pump()
    {
        while (Native.PeekMessageW(out var msg, 0, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessageW(ref msg); }
    }

    /// <summary>Runs <paramref name="body"/> on a fresh STA thread with a message pump until its task completes.</summary>
    private static T OnMessageThread<T>(Func<Task<T>> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var task = body();
                var deadline = Environment.TickCount64 + 15000;
                while (!task.IsCompleted && Environment.TickCount64 < deadline) { Pump(); Thread.Sleep(1); }
                result = task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("message-thread body did not finish");
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    internal static byte[] DecodeRgb(byte[] png)
    {
        var (width, height) = Png.SizeOf(png);
        using var idat = new MemoryStream();
        for (int p = 8; p < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IDAT") idat.Write(png, p + 8, length);
            p += 12 + length;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var data = raw.ToArray();
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            int row = y * (1 + width * 3);
            Assert.Equal(1, data[row]); // Sub filter
            for (int i = 0; i < width * 3; i++)
                rgb[y * width * 3 + i] = (byte)(data[row + 1 + i] + (i >= 3 ? rgb[y * width * 3 + i - 3] : 0));
        }
        return rgb;
    }

    private sealed class FakeFrame(IReadOnlyList<CaptureMonitor> monitors) : IScreenFrame
    {
        public PixelRect Bounds { get; } = CaptureGeometry.VirtualBounds(monitors);
        public IReadOnlyList<CaptureMonitor> Monitors { get; } = monitors;
        public bool Disposed { get; private set; }

        /// <summary>R, G, B of virtual pixel (x, y): a pattern that differs across neighbouring pixels and signs.</summary>
        public static (byte R, byte G, byte B) ColorAt(int x, int y) => ((byte)(x * 7 + 3), (byte)(y * 13 + 5), (byte)((x ^ y) + 11));

        public byte[] CopyBgra(PixelRect region)
        {
            Assert.False(Disposed);
            Assert.Equal(region, CaptureGeometry.Intersect(region, Bounds));
            var bytes = new byte[region.Width * region.Height * 4];
            for (int y = 0; y < region.Height; y++)
                for (int x = 0; x < region.Width; x++)
                {
                    var (r, g, b) = ColorAt(region.X + x, region.Y + y);
                    int o = (y * region.Width + x) * 4;
                    bytes[o] = b; bytes[o + 1] = g; bytes[o + 2] = r; bytes[o + 3] = 255;
                }
            return bytes;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeGrabber(IScreenFrame frame, FakeHider? hider = null) : IScreenGrabber
    {
        public IScreenFrame CaptureVirtualScreen()
        {
            if (hider is not null) { Assert.True(hider.Hidden); hider.Log.Add("grab"); }
            return frame;
        }
    }

    private sealed class ThrowingGrabber : IScreenGrabber
    {
        public IScreenFrame CaptureVirtualScreen() => throw new System.ComponentModel.Win32Exception(5);
    }

    private sealed class FakeSelector(PixelRect? result, FakeHider hider) : IRegionSelector
    {
        public Task<PixelRect?>? Pending { get; init; }
        public Task<PixelRect?> SelectAsync(IScreenFrame frame, RegionSelectOptions options, CancellationToken cancellationToken)
        {
            Assert.True(hider.Hidden);
            Assert.Equal(ScreenCaptureCoordinator.HintZh, options.Hint);
            hider.Log.Add("select");
            return Pending ?? Task.FromResult(result);
        }
    }

    private sealed class FakeHider : ICaptureWindowHider
    {
        public List<string> Log { get; } = [];
        public bool Hidden { get; private set; }
        public Task<IDisposable> HideAllAsync(CancellationToken cancellationToken)
        {
            Hidden = true;
            Log.Add("hide");
            return Task.FromResult<IDisposable>(new Restore(this));
        }

        private sealed class Restore(FakeHider hider) : IDisposable
        {
            public void Dispose() { hider.Hidden = false; hider.Log.Add("restore"); }
        }
    }

    private sealed class CountingArchive : IScreenshotArchive
    {
        public int Calls;
        public KeepResult Keep(ReadOnlySpan<byte> png, DateTimeOffset now) { Calls++; return new KeepResult(true, "x.png", null); }
    }

    private sealed class CountingCapture : IScreenCapture
    {
        public int Calls;
        public Task<ScreenCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(ScreenCaptureResult.Cancelled("escape"));
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private; }
        public static nint Point(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));
        [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] public static extern int FillRect(nint dc, ref RECT rect, nint brush);
        [DllImport("gdi32.dll")] public static extern nint CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    }
}
