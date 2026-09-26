using System.Runtime.InteropServices;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Domain;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Susu.Windows.Capture;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F11 verification (testing agent): B01 on the plugin side (what each OCR package's code actually sees from
/// <c>$http</c>, probed inside the real sandbox), B07 for two calls sharing one screenshot lease with one cancelled, and the
/// OCR01/OCR02 demo on the real desktop: GDI freeze frame → real overlay driven by posted mouse messages → PNG lease → the real
/// Tencent package in the sandbox against a loopback vendor → the translation callback; plus a real-overlay Esc that leaves no
/// file. Sandbox tests need susu.exe published and skip themselves otherwise; desktop tests skip when BitBlt is denied.
/// </summary>
public class F11VerificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Range(0, 3000).Select(i => (byte)(i * 11 % 251))];

    private static string TencentOk(string text)
        => JsonSerializer.Serialize(new { Response = new { TextDetections = new[] { new { DetectedText = text, Confidence = 95, ItemPolygon = new { X = 1, Y = 1, Width = 10, Height = 5 } } }, RequestId = "r" } });

    private const string SimpleOk = """{"status":true,"res":{"type":"formula","info":"x^2","conf":0.9},"request_id":"r"}""";

    /// <summary>A copy of a shipped OCR package whose code, right after its <c>$http</c> call, throws what it saw (req and r).</summary>
    private static string ProbeCopy(string packageDir, string marker)
    {
        string source = Path.Combine(OcrPluginTests.StagedDir!, packageDir);
        string relative = packageDir + "-probe-" + Guid.NewGuid().ToString("N")[..8];
        string target = Path.Combine(OcrPluginTests.StagedDir!, relative);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        string main = File.ReadAllText(Path.Combine(target, "main.js"));
        int at = main.IndexOf("if (r.status < 200 || r.status >= 300)", StringComparison.Ordinal);
        Assert.True(at > 0, "probe anchor not found");
        string probe = "{ const seen = JSON.stringify({ req: req, r: r }); throw new PluginError('bad_response', 'B64=' + (seen.indexOf('" + marker +
            "') >= 0) + ' LEN=' + seen.length + ' PROBE ' + seen.slice(0, 1500)); }";
        File.WriteAllText(Path.Combine(target, "main.js"), main[..at] + probe + main[at..]);
        return relative;
    }

    [Theory] // B01 (plugin side): the OCR plugin's input and its $http result hold the handle and metadata only, never the image or its Base64
    [InlineData(OcrCatalog.TencentOcr)]
    [InlineData(OcrCatalog.SimpleLatex)]
    public async Task Plugin_sees_only_the_handle_never_the_image(string instanceId)
    {
        string base64 = Convert.ToBase64String(Png);
        LoopbackHttpRequest? sent = null;
        using var server = new LoopbackHttpServer(req => { sent = req; return LoopbackHttpResponse.Json(200, instanceId == OcrCatalog.TencentOcr ? TencentOk("hi") : SimpleOk); });
        if (OcrPluginTests.StagedDir is null) return;
        var package = PluginTranslationProviders.WiredPackages.Single(p => p.InstanceId == instanceId);
        using var rig = OcrPluginTests.Build(instanceId, server, directory: ProbeCopy(package.Directory, base64[20..44]));
        if (rig is null) return;
        using var image = rig.Image(Png);
        var outcome = await rig.RecognizeAsync(image);
        string detail = Assert.IsType<OcrOutcome.Failure>(outcome).Error.Detail ?? "";
        TestContext.Current.TestOutputHelper?.WriteLine(detail);
        Assert.Contains("PROBE", detail); // the probe ran after the real $http call
        Assert.Contains("B64=false", detail);
        int len = int.Parse(System.Text.RegularExpressions.Regex.Match(detail, @"LEN=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(len < base64.Length, $"plugin-visible JSON is {len} chars; the image's Base64 alone is {base64.Length}");
        Assert.Contains(image.File.LeaseId, detail); // the handle
        Assert.Contains("image/png", detail);
        Assert.NotNull(sent); // and the vendor still got the real image
        if (instanceId == OcrCatalog.TencentOcr) Assert.Equal(base64, JsonDocument.Parse(sent.Body).RootElement.GetProperty("ImageBase64").GetString());
        else Assert.Contains(System.Text.Encoding.Latin1.GetString(Png), System.Text.Encoding.Latin1.GetString(sent.Body));
        image.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    [Fact] // B07: two legal calls share one screenshot lease; cancelling one leaves the other and the owner's lease intact
    public async Task Two_calls_share_one_image_and_cancelling_one_keeps_the_other()
    {
        var gate = new ManualResetEventSlim(false);
        int hits = 0;
        var bodies = new List<string>();
        using var server = new LoopbackHttpServer(req =>
        {
            lock (bodies) bodies.Add(System.Text.Encoding.UTF8.GetString(req.Body));
            if (Interlocked.Increment(ref hits) == 1) gate.Wait(TimeSpan.FromSeconds(10));
            return LoopbackHttpResponse.Json(200, TencentOk("second"));
        });
        using var rig = OcrPluginTests.Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;
        using var image = rig.Image(Png);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = rig.RecognizeAsync(image, token: cts.Token);
        for (var deadline = DateTime.UtcNow.AddSeconds(15); Volatile.Read(ref hits) < 1 && DateTime.UtcNow < deadline;) await Task.Delay(10, Ct);
        Assert.Equal(1, Volatile.Read(ref hits));

        var second = Assert.IsType<OcrOutcome.Recognized>(await rig.RecognizeAsync(image));
        Assert.Equal("second", Assert.Single(second.Blocks).Text);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        gate.Set();

        // Both requests carried the same real bytes; the owner's lease and file survive the cancelled call.
        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(Png, Convert.FromBase64String(JsonDocument.Parse(b).RootElement.GetProperty("ImageBase64").GetString()!)));
        var deadline2 = DateTime.UtcNow.AddSeconds(5);
        while (rig.Leases.ActiveCount > 1 && DateTime.UtcNow < deadline2) await Task.Delay(20, Ct);
        Assert.Equal(1, rig.Leases.ActiveCount);
        Assert.Equal(Png, File.ReadAllBytes(image.File.FilePath));
        Assert.IsType<OcrOutcome.Recognized>(await rig.RecognizeAsync(image)); // still usable
        image.Dispose();
        await rig.AssertNoFilesLeftAsync();
    }

    // ---------------- real desktop ----------------

    private sealed class NoHider : ICaptureWindowHider
    {
        public Task<IDisposable> HideAllAsync(CancellationToken cancellationToken) => Task.FromResult<IDisposable>(new Nothing());
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    [Fact] // OCR01/OCR02 demo on the real desktop: freeze frame → real overlay → PNG lease → Tencent package in the sandbox → translation
    public async Task Real_capture_through_the_overlay_is_recognized_and_translated()
    {
        LoopbackHttpRequest? sent = null;
        using var server = new LoopbackHttpServer(req => { sent = req; return LoopbackHttpResponse.Json(200, TencentOk("屏幕文字")); });
        using var rig = OcrPluginTests.Build(OcrCatalog.TencentOcr, server);
        if (rig is null) return;

        var outcome = OnMessageThread(async () =>
        {
            nint previous = Native.SetThreadDpiAwarenessContext(-4);
            nint marker = Native.CreateWindowExW(0x8 | 0x80 | 0x08000000, "STATIC", "", 0x80000000 | 0x10000000, 40, 40, 64, 48, 0, 0, 0, 0);
            try
            {
                Native.GetWindowRect(marker, out var rect);
                for (int i = 0; i < 20; i++) { Pump(); Paint(marker); Native.DwmFlush(); }
                var overlay = new RegionOverlay();
                var capture = new ScreenCaptureCoordinator(new GdiScreenGrabber(), overlay, new NoHider(), new LeasedFiles(rig.Leases), SystemClock.Instance);
                var vx = Native.GetSystemMetrics(76);
                var vy = Native.GetSystemMetrics(77);
                var task = capture.CaptureRegionAsync(Ct);
                if (task.IsCompleted) return (await task, new PixelRect(0, 0, 0, 0));
                nint hwnd = overlay.CurrentWindow;
                Assert.NotEqual(0, hwnd);
                int x1 = rect.Left + 4 - vx, y1 = rect.Top + 4 - vy, x2 = rect.Left + 60 - vx, y2 = rect.Top + 44 - vy;
                Native.PostMessageW(hwnd, 0x201, 1, Native.Point(x1, y1));
                Native.PostMessageW(hwnd, 0x200, 1, Native.Point(x2, y2));
                Native.PostMessageW(hwnd, 0x202, 0, Native.Point(x2, y2));
                return (await task, new PixelRect(rect.Left + 4, rect.Top + 4, 56, 40));
            }
            finally
            {
                Native.DestroyWindow(marker);
                if (previous != 0) Native.SetThreadDpiAwarenessContext(previous);
            }
        });
        var (result, expected) = outcome;
        if (result.Status == ScreenCaptureStatus.Failed && result.ErrorCode == "capture.failed") { Assert.Skip("BitBlt of the screen was denied: not the input desktop"); return; }
        Assert.Equal(ScreenCaptureStatus.Captured, result.Status);
        var image = result.Image!;
        Assert.Equal(expected, image.Region);
        byte[] png = File.ReadAllBytes(image.File.FilePath);
        Assert.Equal((56, 40), Susu.Jobs.Png.SizeOf(png));
        var rgb = ScreenCaptureTests.DecodeRgb(png);
        int magenta = 0;
        for (int i = 0; i < rgb.Length; i += 3) if (rgb[i] > 200 && rgb[i + 1] < 60 && rgb[i + 2] > 200) magenta++;
        if (magenta == 0 && rgb.All(b => b == 0)) { image.Dispose(); Assert.Skip("the session renders nothing to GDI"); return; }
        Assert.True(magenta >= 56 * 40 * 9 / 10, $"marker pixels {magenta}/{56 * 40}: the overlay's mask or hint is in the image");

        string? translated = null;
        var job = new OcrJob(() => rig.Provider, t => { translated = t; return Task.CompletedTask; }, () => true);
        var final = await job.RecognizeAsync(image);
        Assert.Equal(OcrPhase.Recognized, final.Phase);
        Assert.Equal("屏幕文字", translated);
        Assert.NotNull(sent);
        Assert.Equal(png, Convert.FromBase64String(JsonDocument.Parse(sent.Body).RootElement.GetProperty("ImageBase64").GetString()!)); // the exact captured bytes
        await rig.AssertNoFilesLeftAsync(); // the job released the screenshot
    }

    [Fact] // OCR01 on the real desktop: Esc on the real overlay after a real grab submits nothing and leaves no file
    public void Real_overlay_escape_after_a_real_grab_leaves_no_file()
    {
        string dir = TestTemp.NewDir("susu-f11-esc");
        using var leases = new FileLeases(dir);
        var result = OnMessageThread(async () =>
        {
            nint previous = Native.SetThreadDpiAwarenessContext(-4);
            try
            {
                var overlay = new RegionOverlay();
                var capture = new ScreenCaptureCoordinator(new GdiScreenGrabber(), overlay, new NoHider(), new LeasedFiles(leases), SystemClock.Instance);
                var task = capture.CaptureRegionAsync(Ct);
                if (task.IsCompleted) return await task;
                nint hwnd = overlay.CurrentWindow;
                Native.PostMessageW(hwnd, 0x201, 1, Native.Point(10, 10));
                Native.PostMessageW(hwnd, 0x200, 1, Native.Point(200, 200));
                Native.PostMessageW(hwnd, 0x100, 0x1B, 0);
                return await task;
            }
            finally { if (previous != 0) Native.SetThreadDpiAwarenessContext(previous); }
        });
        if (result.ErrorCode == "capture.failed") { Assert.Skip("BitBlt of the screen was denied: not the input desktop"); return; }
        Assert.Equal((ScreenCaptureStatus.Cancelled, "escape"), (result.Status, result.ErrorCode));
        Assert.Null(result.Image);
        Assert.Equal(0, leases.ActiveCount);
        Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
    }

    // ---------------- helpers ----------------

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

    private static T OnMessageThread<T>(Func<Task<T>> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var task = body();
                var deadline = Environment.TickCount64 + 20000;
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
