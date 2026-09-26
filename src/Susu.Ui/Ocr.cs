using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;

namespace Susu.Ui;

/// <summary>
/// F11.3 OCR window and SetOcr (DESIGN Ocr/SetOcr, PLAN 6.2): a finished capture opens the OCR result window at its remembered
/// position and starts recognition; the window follows the <see cref="OcrJob"/> states (recognizing, recognized, no text, failed,
/// unavailable) and its cards come from the window's own translation session (T02). A capture failure has no window yet, so it
/// shows the failure bar (DESIGN 9 "OCR 热键按下即出错"); Esc cancels silently. The page never sees a file path: the image
/// preview is a small host-made PNG sent as a data URL.
/// </summary>
public sealed partial class ShellCoordinator
{
    private OcrJob? ocr;
    private OcrView? ocrView;
    private long ocrViews;
    private long ocrStartedAt;
    private string? ocrPreview;
    private (int Width, int Height) ocrSize;
    private string? ocrNotice;
    private long ocrGeneration = -1;

    /// <summary>
    /// F11.2/F11.3: the OCR job. Once set, the shell owns every captured image: it opens the OCR window and hands the image to
    /// the job (which releases the lease however recognition ends); <see cref="ScreenCaptured"/> is then informational only.
    /// </summary>
    public OcrJob? Ocr
    {
        get => ocr;
        set
        {
            if (ocr is not null) ocr.StateChanged -= OnOcrStateFromJob;
            ocr = value;
            if (ocr is not null) ocr.StateChanged += OnOcrStateFromJob;
        }
    }

    /// <summary>The OCR window's current view (null before the first capture); for tests and diagnostics.</summary>
    public OcrView? OcrWindowView => ocrView;

    /// <summary>The last screenshot presentation finished (window opened and recognition started, failure bar shown, or nothing on cancel).</summary>
    public event Action<ScreenCaptureResult>? ScreenCapturePresented;

    /// <summary>Capture error codes (F11.1) → host resource keys for the failure bar and the kept-copy notice.</summary>
    public static string CaptureErrorKey(string? code) => code switch
    {
        "capture.diskFull" => "ocr.capture.diskFull",
        "capture.writeFailed" => "ocr.capture.writeFailed",
        _ => "ocr.capture.failed",
    };

    private static string KeepErrorKey(string code) => code == "capture.diskFull" ? "ocr.keep.diskFull" : "ocr.keep.writeFailed";

    /// <summary>Makes the page-safe preview: a data URL of the host-made thumbnail PNG, or null (never a path or file URL).</summary>
    public static string? PreviewUrl(byte[]? png)
        => png is { Length: > 8 } && png.Length <= OcrView.PreviewMaxBytes && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47
            ? "data:image/png;base64," + Convert.ToBase64String(png) : null;

    /// <summary>Runs on the message thread once a capture finished (the image, if any, is owned here from now on).</summary>
    private async Task PresentScreenCaptureAsync(OcrJob job, ScreenCaptureResult result)
    {
        try
        {
            switch (result.Status)
            {
                case ScreenCaptureStatus.Failed:
                    ShowErrorBar([new ErrorLineView(CaptureErrorKey(result.ErrorCode))]);
                    return;
                case ScreenCaptureStatus.Cancelled:
                    return; // Esc, a click or a tiny box: the previous result (if any) stays as it was
            }
            if (result.Image is not { } image) return;
            // Every capture is a new task for the OCR window: the previous recognition and its cards are dropped (J01).
            job.Cancel();
            ReattachTranslation(WindowKind.Ocr);
            ocrPreview = PreviewUrl(image.Preview);
            ocrSize = (image.Width, image.Height);
            ocrNotice = result.CopyErrorCode is { } copyError ? KeepErrorKey(copyError) : null;
            ocrGeneration = -1;
            ocrStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            SetOcrView(new OcrView(++ocrViews, "recognizing", config.State.Effective.Ocr.Service, null, [], null, ocrPreview, ocrSize.Width, ocrSize.Height,
                false, config.State.Effective.Ocr.AutoTranslate, OcrHotkey(), ocrNotice));
            await OpenAsync(WindowKind.Ocr, activate: true);
            if (windows.TryGetValue(WindowKind.Ocr, out var session) && session.Ready && TranslationOf(WindowKind.Ocr) is { } fresh)
                Send(WindowKind.Ocr, session, UiMessageKind.Event, "translation", null, JsonSerializer.SerializeToElement(await fresh.SnapshotAsync(), ContractsJson.Default.TranslationSnapshot));
            _ = RecognizeAsync(job, image);
        }
        finally { ScreenCapturePresented?.Invoke(result); }
    }

    private async Task RecognizeAsync(OcrJob job, ScreenshotImage image)
    {
        try { await job.RecognizeAsync(image); }
        catch (Exception e) { Diagnostic?.Invoke($"ocr.exception {e.GetType().Name}"); }
    }

    private void OnOcrStateFromJob(OcrState state) => platform.StartTimer(TimeSpan.Zero, () => OnOcrState(state));

    /// <summary>Projects a job state onto the OCR window (message thread). A state of an older job never replaces a newer one.</summary>
    private void OnOcrState(OcrState state)
    {
        if (ocrView is null || state.Generation < ocrGeneration) return;
        ocrGeneration = state.Generation;
        string phase = state.Phase switch
        {
            OcrPhase.Recognizing => "recognizing",
            OcrPhase.Recognized => "recognized",
            OcrPhase.NoText => "noText",
            OcrPhase.Failed => "failed",
            OcrPhase.Unavailable => "unavailable",
            OcrPhase.Cancelled => "cancelled",
            _ => "idle",
        };
        long? elapsed = state.Phase is OcrPhase.Recognizing or OcrPhase.Idle ? null : (long)System.Diagnostics.Stopwatch.GetElapsedTime(ocrStartedAt).TotalMilliseconds;
        OcrBlockView[] blocks = state.Blocks is { } list ? [.. list.Where(b => !string.IsNullOrWhiteSpace(b.Text)).Select(b => new OcrBlockView(b.Kind == "formula" ? b.Text.Trim() : b.Text.TrimEnd(), b.Kind == "formula" ? "formula" : "text"))] : [];
        SetOcrView(new OcrView(ocrView.Id, phase, state.ServiceId ?? ocrView.ServiceId, state.Text, blocks, state.Phase == OcrPhase.Failed ? state.Error?.Kind ?? ErrorKind.Unavailable : null,
            ocrPreview, ocrSize.Width, ocrSize.Height, state.Translated, config.State.Effective.Ocr.AutoTranslate, OcrHotkey(), ocrNotice, elapsed));
    }

    private void SetOcrView(OcrView view)
    {
        ocrView = view;
        if (windows.TryGetValue(WindowKind.Ocr, out var session) && session.Ready)
            Send(WindowKind.Ocr, session, UiMessageKind.Event, "ocr", null, JsonSerializer.SerializeToElement(view, ContractsJson.Default.OcrView));
    }

    private string OcrHotkey() => config.State.Effective.Hotkeys.Chords.TryGetValue("ocrTranslate", out var chord) ? chord : "";

    /// <summary>Capture.BeginCapture from the OCR window ("重新截图"): a new capture while the feature is available.</summary>
    private CommandResult Recapture()
    {
        if (Resolve(FeatureRegistry.Ids.Ocr).State != FeatureState.Available || ScreenCapture is null) return new CommandResult(false, "unavailable");
        StartScreenCapture();
        return Ok();
    }

    /// <summary>The OCR hotkey arrived while OCR is not usable (e.g. its service lost its key since registration): say why.</summary>
    private void ReportOcrUnavailable(string? reasonKey)
        => ShowErrorBar([new ErrorLineView(reasonKey ?? "feature.noService.ocr", "settings")]);

    // ---------- SetOcr ----------

    /// <summary>Saved and granted credentials of an OCR package instance (the service toggle is checked separately).</summary>
    private bool OcrCredentialsReady(AppSettings s, OcrPackage package)
        => s.Instances.FirstOrDefault(i => i.Id == package.InstanceId) is { } instance && instance.Package == package.PackageId
            && CredentialPackages.States(s, package, instance, config.Secrets.Has).All(t => t.Saved && t.Granted);

    private OcrSettingsView OcrSettingsOf(AppSettings s)
    {
        var choices = OcrCatalog.All.Select(p =>
        {
            var service = s.Services.FirstOrDefault(x => x.Capability == Capability.Ocr && x.Instance == p.InstanceId);
            bool enabled = service?.Enabled ?? false;
            bool credentials = OcrCredentialsReady(s, p);
            string availability = !enabled ? nameof(Availability.Disabled) : credentials ? nameof(Availability.Ready) : nameof(Availability.MissingCredential);
            return new OcrChoiceView(p.InstanceId, service?.ServiceId ?? p.InstanceId, enabled, availability, enabled && credentials);
        }).ToArray();
        var (state, reason) = Resolve(FeatureRegistry.Ids.Ocr);
        bool ready = state == FeatureState.Available;
        return new OcrSettingsView(s.Ocr.Service, s.Ocr.AutoTranslate, s.Ocr.KeepScreenshots, s.Ocr.RetentionDays, OcrSettings.MinRetentionDays, OcrSettings.MaxRetentionDays,
            choices, ready, OcrHotkey(), ready ? null : reason);
    }

    /// <summary>Settings.SaveOcr: only the SetOcr fields change; an unknown service or an out-of-range retention is refused.</summary>
    private CommandResult SaveOcr(OcrSaveRequest request)
    {
        if (OcrCatalog.Find(request.Service) is null) return new CommandResult(false, "unknown-service");
        if (request.RetentionDays is < OcrSettings.MinRetentionDays or > OcrSettings.MaxRetentionDays) return new CommandResult(false, "retention");
        var s = config.State.Effective;
        var next = new OcrSettings(request.Service, request.AutoTranslate, request.KeepScreenshots, request.RetentionDays);
        return Outcome(config.Save(s with { Ocr = next }, request.ExpectedRevision, request.ExpectedFileHash));
    }
}
