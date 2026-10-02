using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;

namespace Susu.Ui;

/// <summary>
/// Validates a page → host message (ARCHITECTURE 6, PLAN 4.5.5): bounded size, strict schema (unknown fields
/// rejected), current UI version, the window's own session id, and for commands the per-window whitelist.
/// The native layer has already dropped messages whose source is not the window's trusted origin.
/// </summary>
public static class UiInbound
{
    public const int MaxMessageChars = 1 << 20;

    public static UiEnvelope? Decode(string json, WindowKind kind, string sessionId, out string reason)
    {
        reason = "";
        if (json.Length > MaxMessageChars) { reason = "too-large"; return null; }
        UiEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize(json, ContractsJson.Default.UiEnvelope); }
        catch (JsonException) { reason = "malformed"; return null; }
        if (envelope is null) { reason = "malformed"; return null; }
        if (envelope.UiVersion != ProtocolVersions.Ui) { reason = "version"; return null; }
        if (envelope.WindowSessionId != sessionId) { reason = "session"; return null; }
        switch (envelope.Kind)
        {
            case UiMessageKind.Ready:
                return envelope;
            case UiMessageKind.Command:
                if (envelope.CorrelationId is not { Length: > 0 and <= 64 }) { reason = "correlation"; return null; }
                if (!UiCommands.IsAllowed(kind, envelope.Name)) { reason = "not-allowed"; return null; }
                return envelope;
            default:
                reason = "kind";
                return null;
        }
    }
}

/// <summary>ReleaseAfter overrides the 10-minute keep-warm period (automated lifecycle runs only).</summary>
public sealed record ShellOptions(bool DevelopmentBuild, bool DevPreview, TimeSpan? ReleaseAfter = null);

/// <summary>
/// Host services behind the F06.3a settings commands. RuntimeAvailable: the plugin runtime was composed
/// (the wired built-in translation packages can run). ValidationProvider: a provider for one service built
/// from the given settings whether or not it is enabled, for Settings.ValidateProvider. MonthlyUsage: local
/// characters sent this month by a service id (DATA04), shown next to the service. Schemas (F07.2): each wired
/// instance's manifest config schema, from which the settings controls are generated. Options: the dynamic
/// option loader for fields with an optionsSource (Settings.LoadOptions). TestNetwork (F07.3): probes the given
/// paths through a network broker built from the given (unsaved) proxy settings (Settings.TestNetwork).
/// </summary>
public sealed record TranslationBackend(bool RuntimeAvailable, Func<AppSettings, string, ITranslationProvider?> ValidationProvider, Func<string, long>? MonthlyUsage = null,
    IReadOnlyDictionary<string, IReadOnlyList<ConfigField>>? Schemas = null, OptionsBroker? Options = null,
    Func<NetworkSettings, IReadOnlyList<NetworkProbeTarget>, CancellationToken, Task<IReadOnlyList<NetworkProbeResult>>>? TestNetwork = null,
    SpeechBackend? Speech = null);

/// <summary>
/// The UI brain on the message thread: window sessions, snapshot + patch sequencing, command dispatch,
/// close/minimize semantics, keep-warm/release, tray menu model and hotkey registration. It never touches
/// Win32 or COM directly (<see cref="IWindowPlatform"/>) and never hands a page a secret, grant or path.
/// </summary>
public sealed partial class ShellCoordinator
{
    public static readonly TimeSpan PatchInterval = TimeSpan.FromMilliseconds(33); // ≤ 30 streaming updates per second

    private static readonly Dictionary<string, string> hotkeyFeatures = new(StringComparer.Ordinal)
    {
        ["inputTranslate"] = FeatureRegistry.Ids.InputTranslation, ["selectionTranslate"] = FeatureRegistry.Ids.Selection,
        ["clipboardTranslate"] = FeatureRegistry.Ids.Clipboard, ["ocrTranslate"] = FeatureRegistry.Ids.Ocr,
        ["voiceTranslate"] = FeatureRegistry.Ids.Voice, ["audioTranslate"] = FeatureRegistry.Ids.SystemAudio,
        ["videoTranscribe"] = FeatureRegistry.Ids.Transcription, ["pronounce"] = FeatureRegistry.Ids.Pronunciation,
    };

    // PLAN 1.4: selection translation is hotkey-only; the menu offers clipboard translation instead.
    private static readonly (string Id, string? Hotkey, string? Feature, bool SeparatorBefore)[] trayLayout =
    [
        ("input-translation", "inputTranslate", FeatureRegistry.Ids.InputTranslation, false),
        ("clipboard", "clipboardTranslate", FeatureRegistry.Ids.Clipboard, false),
        ("ocr", "ocrTranslate", FeatureRegistry.Ids.Ocr, false),
        ("voice", "voiceTranslate", FeatureRegistry.Ids.Voice, false),
        ("system-audio", "audioTranslate", FeatureRegistry.Ids.SystemAudio, false),
        ("transcription", "videoTranscribe", FeatureRegistry.Ids.Transcription, false),
        ("settings", null, null, true),
        ("check-update", null, "update", false),
        ("exit", null, null, false),
    ];

    private sealed class WindowSession(string id)
    {
        public string Id { get; } = id;
        public long Sequence;
        public bool Ready;
        public bool Pinned;
        public readonly Dictionary<string, CardPatch> PendingCards = new(StringComparer.Ordinal);
        public bool FlushScheduled;
    }

    private readonly IWindowPlatform platform;
    private readonly IConfigService config;
    private readonly FeatureRegistry features;
    private readonly Func<Capability, bool> capabilityReady;
    private readonly ShellOptions options;
    private readonly Func<AppSettings, TranslationSession?> sessionFactory;
    private readonly ILanguageDetector? languageDetector;
    private readonly Dictionary<WindowKind, WindowSession> windows = [];
    private readonly WebViewLifecycle lifecycle = new();
    private Dictionary<string, bool> hotkeyResults = new(StringComparer.Ordinal);
    private readonly TranslationBackend? backend;
    private readonly CaptureCoordinator? capture;
    // One translation session per result window (Main, and the floating Selection window for hotkey/tray captures):
    // closing one window cancels only its own task. Stale: settings changed since the session was built, so the next
    // submit rebuilds it from the current services (enable/disable/reorder/key changes apply without a restart while
    // shown results stay until then).
    private sealed class TranslationSlot(TranslationSession session)
    {
        public TranslationSession Session { get; } = session;
        public bool Stale;
    }
    private readonly Dictionary<WindowKind, TranslationSlot> translations = [];
    private (string From, string To)? languageOverride;
    private CaptureView? captureView;
    private ErrorBarView? errorBar;
    private long captureViews, errorBars;
    /// <summary>The failure bar hides itself after 4 s (DESIGN 9).</summary>
    public static readonly TimeSpan ErrorBarLifetime = TimeSpan.FromSeconds(4);

    public ShellCoordinator(IWindowPlatform platform, IConfigService config, FeatureRegistry features, Func<Capability, bool> capabilityReady,
        ShellOptions options, Func<AppSettings, TranslationSession?>? sessionFactory = null, ILanguageDetector? languageDetector = null, TranslationBackend? backend = null,
        CaptureCoordinator? capture = null)
    {
        this.capture = capture;
        this.platform = platform;
        this.config = config;
        this.features = features;
        this.capabilityReady = capabilityReady;
        this.options = options;
        this.sessionFactory = sessionFactory ?? (_ => null);
        this.languageDetector = languageDetector;
        this.backend = backend;
    }

    public event Action<string>? Diagnostic;
    /// <summary>A page sent Ready and received its snapshot (instrumentation for UiReady timing).</summary>
    public event Action<WindowKind>? PageReady;
    /// <summary>PER03 timing points (ARCHITECTURE 11): (window, phase, milliseconds).</summary>
    public event Action<WindowKind, string, double>? Timing;
    private readonly Dictionary<WindowKind, long> openedAt = [];
    private long hotkeyAt;
    public IReadOnlyDictionary<string, bool> HotkeyResults => hotkeyResults;
    public bool ReleasePending => lifecycle.TimerPending;
    public IReadOnlyCollection<WindowKind> VisibleWindows => lifecycle.Visible;

    public void Start()
    {
        ApplyHotkeys(config.State.Effective);
        config.Changed += state => OnSettingsChanged(state);
        // The player reports from its worker threads; the shell projects its state on the message thread.
        if (Speech is { } speech) speech.Player.StateChanged += state => platform.StartTimer(TimeSpan.Zero, () => OnPlayerState(state));
    }

    // ---------- windows ----------

    public void Open(WindowKind kind) => _ = OpenAsync(kind, WindowSpec.For(kind).Activates);

    /// <summary>Shows a window; the task completes once a warm page has been resynchronized (so later events follow the snapshot).</summary>
    private Task OpenAsync(WindowKind kind, bool activate)
    {
        if (kind is WindowKind.Main && !translations.ContainsKey(kind)) AttachTranslation(kind);
        bool wasVisible = lifecycle.Visible.Contains(kind);
        if (!wasVisible) openedAt[kind] = System.Diagnostics.Stopwatch.GetTimestamp();
        string session = platform.Show(kind, activate);
        if (hotkeyAt != 0 && !wasVisible) Timing?.Invoke(kind, "NativeShellAfterHotkey", System.Diagnostics.Stopwatch.GetElapsedTime(hotkeyAt).TotalMilliseconds);
        if (!windows.TryGetValue(kind, out var existing) || existing.Id != session) windows[kind] = existing = new WindowSession(session);
        lifecycle.Shown(kind);
        return !wasVisible && existing.Ready ? SendSnapshotAsync(kind, existing) : Task.CompletedTask; // warm reopen: resynchronize the page
    }

    public bool IsOpen(WindowKind kind) => lifecycle.Visible.Contains(kind);

    public void OnWindowRequest(WindowKind kind, WindowRequest request)
    {
        switch (WindowSemantics.Decide(kind, request, config.State.Effective.General.CloseAction))
        {
            case WindowOutcome.ExitApp:
                platform.Exit();
                return;
            case WindowOutcome.HideCancelTask:
                if (translations.Remove(kind, out var slot)) _ = slot.Session.CloseAsync(TimeSpan.FromSeconds(3));
                if (kind == WindowKind.Ocr) Ocr?.Cancel(); // Esc/close stops the recognition too; its image lease goes as it unwinds
                if (kind == WindowKind.Voice) CancelVoice(); // Esc/close discards the recording, stops transcription and releases the microphone
                if (kind == WindowKind.Transcribe) CancelTranscribeWindow(); // Esc/close cancels the job; finished cues stay for reopen and export
                break;
        }
        HideWindow(kind);
    }

    private void HideWindow(WindowKind kind)
    {
        if (!lifecycle.Visible.Contains(kind)) return;
        if (windows.TryGetValue(kind, out var session) && session.Ready)
            Send(kind, session, UiMessageKind.Event, "window.hidden", null, null); // pages drop unsaved secret input before keep-warm
        platform.Hide(kind);
        platform.Suspend(kind);
        if (lifecycle.Hidden(kind) == WebViewAction.StartReleaseTimer)
        {
            long generation = lifecycle.TimerGeneration;
            platform.StartTimer(options.ReleaseAfter ?? WebViewLifecycle.ReleaseAfter, () => OnReleaseTimer(generation));
        }
    }

    public void OnReleaseTimer(long generation)
    {
        if (lifecycle.TimerFired(generation) != WebViewAction.Release) return;
        platform.ReleaseAll();
        windows.Clear();
        Diagnostic?.Invoke("webview.released");
    }

    public void OnTrayDoubleClick() => Open(WindowKind.Settings);
    public void OnTrayMenu() => Open(WindowKind.Tray);

    /// <summary>A second launch only wakes this instance (PLAN 1.4).</summary>
    public void OnActivateRequest() => Open(Resolve(FeatureRegistry.Ids.InputTranslation).State == FeatureState.Available ? WindowKind.Main : WindowKind.Settings);

    public void OnHotkey(string action)
    {
        if (!hotkeyFeatures.TryGetValue(action, out var feature)) return;
        var (featureState, reasonKey) = Resolve(feature);
        if (featureState != FeatureState.Available)
        {
            // Unavailable: no action (PLAN 1.2). A registered OCR chord whose service became unusable says why (DESIGN 9).
            if (feature == FeatureRegistry.Ids.Ocr && featureState == FeatureState.Unavailable && Ocr is not null) ReportOcrUnavailable(reasonKey);
            if (feature is FeatureRegistry.Ids.Voice or FeatureRegistry.Ids.SystemAudio && featureState == FeatureState.Unavailable && Asr is not null) ReportVoiceUnavailable(reasonKey);
            return;
        }
        hotkeyAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (feature == FeatureRegistry.Ids.InputTranslation) Open(WindowKind.Main);
        else if (feature == FeatureRegistry.Ids.Voice) OpenVoice();
        else if (feature == FeatureRegistry.Ids.SystemAudio) OpenVoice(AudioSourceKind.SystemLoopback);
        else if (feature == FeatureRegistry.Ids.Transcription) OpenTranscribe();
        // F08.2: selection/clipboard capture. The foreground snapshot is taken synchronously here, before any Su-Su
        // window can take focus; no Su-Su window is shown or activated until the capture has finished (UI03, SEL02).
        else if (feature == FeatureRegistry.Ids.Ocr) StartScreenCapture();
        else if (capture is not null && CaptureCoordinator.TriggerFor(action, config.State.Effective.Hotkeys) is { } trigger)
            _ = CaptureAsync(capture, capture.CaptureAsync(trigger));
    }

    /// <summary>
    /// F11.1 screenshot port (ARCHITECTURE 7 <c>IScreenCapture.CaptureRegion</c>). The OCR hotkey and tray entry reach it only
    /// while the OCR feature resolves Available (F11.3), or in a development preview.
    /// </summary>
    public IScreenCapture? ScreenCapture { get; set; }

    /// <summary>F12.1 microphone port (ARCHITECTURE 7 <c>IAudioCapture</c>). No entry point reaches it until the Voice feature resolves Available (F12.3).</summary>
    public IAudioCapture? AudioCapture { get; set; }

    /// <summary>F13.2: the same recorder over the system output (WASAPI loopback). The system-audio hotkey and tray entry use it in the voice window.</summary>
    public IAudioCapture? SystemAudioCapture { get; set; }

    /// <summary>A screenshot capture finished (captured, cancelled or failed). A subscriber (F11.2/F11.3) owns and disposes
    /// <see cref="ScreenCaptureResult.Image"/>; with no subscriber the image lease is released at once.</summary>
    public event Action<ScreenCaptureResult>? ScreenCaptured;

    private void StartScreenCapture()
    {
        if (ScreenCapture is { } screen) _ = RunScreenCaptureAsync(screen);
    }

    private async Task RunScreenCaptureAsync(IScreenCapture screen)
    {
        ScreenCaptureResult result;
        try { result = await screen.CaptureRegionAsync(); }
        catch (Exception e) { Diagnostic?.Invoke($"capture.screen.exception {e.GetType().Name}"); return; }
        Diagnostic?.Invoke($"capture.screen {result.Status} {result.ErrorCode ?? result.CopyErrorCode ?? ""}".TrimEnd());
        if (Ocr is { } job)
        {
            // F11.3: the shell owns the image and presents it on the message thread (window, then recognition).
            ScreenCaptured?.Invoke(result);
            platform.StartTimer(TimeSpan.Zero, () => _ = PresentScreenCaptureAsync(job, result));
        }
        else if (ScreenCaptured is { } handler) handler(result);
        else result.Image?.Dispose();
    }

    /// <summary>
    /// F11.2: recognized OCR text enters the common translation pipeline (T02): the OCR result window's own session, with the
    /// same services, chunking and cards as typed input. Marshalled to the UI thread; F11.3 shows the window and its cards.
    /// </summary>
    public Task<CommandResult> SubmitRecognizedTextAsync(string text, WindowKind window = WindowKind.Ocr)
    {
        var done = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        platform.StartTimer(TimeSpan.Zero, async () =>
        {
            try { done.SetResult(await SubmitAsync(window, text)); }
            catch (Exception error) { done.SetException(error); }
        });
        return done.Task;
    }

    /// <summary>F11.2: the OCR result window's translation session (null before any OCR text was submitted); for F11.3 and tests.</summary>
    public TranslationSession? OcrTranslation => TranslationOf(WindowKind.Ocr);

    /// <summary>A capture (hotkey or tray) finished and is still the current one (J01); superseded ones are never raised.</summary>
    public event Action<CaptureOutcome>? Captured;

    /// <summary>The last capture's presentation finished (window opened and text submitted, or failure bar shown); for tests and diagnostics.</summary>
    public event Action<CaptureOutcome>? CapturePresented;

    private async Task CaptureAsync(CaptureCoordinator coordinator, Task<CaptureOutcome> pending)
    {
        var outcome = await pending;
        Diagnostic?.Invoke($"capture {outcome.Trigger} {outcome.Status} {outcome.Source} {outcome.FailureKey}");
        if (outcome.Status == CaptureStatus.Superseded || !coordinator.IsCurrent(outcome.Generation)) return;
        // Back on the UI thread; a newer capture that finished meanwhile wins (J01).
        platform.StartTimer(TimeSpan.Zero, () =>
        {
            if (!coordinator.IsCurrent(outcome.Generation)) return;
            Captured?.Invoke(outcome);
            _ = PresentAsync(coordinator, outcome);
        });
    }

    /// <summary>
    /// F08.3 (PLAN 3.1/6.1, DESIGN 9): text or an empty capture opens the floating Selection window at its remembered
    /// position (never at the pointer or the selection) and only now activates it, so Esc and typing reach it; a failure
    /// with a message shows the failure bar near the pointer instead of a window; a restore notice rides along as its own
    /// bar line. A focus change or a vanished target shows nothing (the user already moved on).
    /// </summary>
    private async Task PresentAsync(CaptureCoordinator coordinator, CaptureOutcome outcome)
    {
        var lines = new List<ErrorLineView>();
        if (outcome.Status == CaptureStatus.Failed && outcome.Message is { } message) lines.Add(LineFor(message));
        if (outcome.Notice is { } notice) lines.Add(LineFor(notice));
        if (lines.Count > 0) ShowErrorBar([.. lines]);
        if (outcome.Trigger == CaptureTrigger.Pronounce)
        {
            if (outcome.Status == CaptureStatus.Text) await PronounceAsync(coordinator, outcome); // F10.2: spoken, no window
        }
        else if (outcome.Status is CaptureStatus.Text or CaptureStatus.Empty)
        {
            string origin = outcome.Source == "clipboard" ? "clipboard" : "selection";
            bool empty = outcome.Status == CaptureStatus.Empty;
            // Every capture is a new task for the floating window: the previous one is cancelled, never merged (J01).
            ReattachTranslation(WindowKind.Selection);
            captureView = new CaptureView(++captureViews, origin, empty);
            await OpenAsync(WindowKind.Selection, activate: true);
            if (windows.TryGetValue(WindowKind.Selection, out var session) && session.Ready)
            {
                Send(WindowKind.Selection, session, UiMessageKind.Event, "capture", null, JsonSerializer.SerializeToElement(captureView, ContractsJson.Default.CaptureView));
                if (TranslationOf(WindowKind.Selection) is { } fresh)
                    Send(WindowKind.Selection, session, UiMessageKind.Event, "translation", null, JsonSerializer.SerializeToElement(await fresh.SnapshotAsync(), ContractsJson.Default.TranslationSnapshot));
            }
            if (!empty && coordinator.IsCurrent(outcome.Generation)) await SubmitAsync(WindowKind.Selection, outcome.Text);
        }
        CapturePresented?.Invoke(outcome);
    }

    /// <summary>Capture texts are host resources: the page shows them in the UI language (Error artboard 01).</summary>
    private static ErrorLineView LineFor(string message) => message switch
    {
        CaptureMessages.NotSupportedBorrowOff => new ErrorLineView("capture.notSupportedBorrowOff", "settings"),
        CaptureMessages.NotSupported => new ErrorLineView("capture.notSupported"),
        CaptureMessages.RestoreFailed => new ErrorLineView("capture.restoreFailed", "settings"),
        _ => new ErrorLineView("capture.failed"),
    };

    private void ShowErrorBar(ErrorLineView[] lines)
    {
        var bar = errorBar = new ErrorBarView(++errorBars, lines);
        bool wasVisible = lifecycle.Visible.Contains(WindowKind.Error);
        _ = OpenAsync(WindowKind.Error, activate: false);
        if (wasVisible && windows.TryGetValue(WindowKind.Error, out var session) && session.Ready)
            Send(WindowKind.Error, session, UiMessageKind.Event, "errorbar", null, JsonSerializer.SerializeToElement(bar, ContractsJson.Default.ErrorBarView));
        platform.StartTimer(ErrorBarLifetime, () => { if (ReferenceEquals(errorBar, bar)) HideWindow(WindowKind.Error); });
    }

    // ---------- page messages ----------

    public void OnPageMessage(WindowKind kind, string json)
    {
        if (!windows.TryGetValue(kind, out var session)) { Diagnostic?.Invoke($"ui.reject {kind} no-session"); return; }
        var envelope = UiInbound.Decode(json, kind, session.Id, out var reason);
        if (envelope is null) { Diagnostic?.Invoke($"ui.reject {kind} {reason}"); return; }
        if (envelope.Kind == UiMessageKind.Ready) { _ = SendSnapshotAsync(kind, session); return; }
        _ = HandleCommandAsync(kind, session, envelope);
    }

    private async Task SendSnapshotAsync(WindowKind kind, WindowSession session)
    {
        TranslationSnapshot? snapshot = TranslationOf(kind) is { } t ? await t.SnapshotAsync() : null;
        var payload = new UiSnapshot(WindowViewFor(kind, session), snapshot,
            kind == WindowKind.Settings ? ProjectSettings(config.State) : null,
            kind == WindowKind.Tray ? TrayModel() : null,
            kind == WindowKind.Selection ? captureView : null,
            kind == WindowKind.Error ? errorBar : null,
            kind == WindowKind.Speech || UiCommands.IsAllowed(kind, UiCommands.SpeakCard) ? speechState : null,
            kind == WindowKind.Speech ? speechBar : null,
            kind == WindowKind.Ocr ? ocrView : null,
            kind == WindowKind.Voice ? voiceView : null,
            kind == WindowKind.Transcribe ? TranscribeForSnapshot() : null);
        session.Ready = true;
        session.PendingCards.Clear();
        Send(kind, session, UiMessageKind.Snapshot, null, null, JsonSerializer.SerializeToElement(payload, ContractsJson.Default.UiSnapshot));
        PageReady?.Invoke(kind);
    }

    private async Task HandleCommandAsync(WindowKind kind, WindowSession session, UiEnvelope envelope)
    {
        CommandResult result;
        try { result = await ExecuteAsync(kind, session, envelope.Name!, envelope.Payload); }
        catch (JsonException) { result = new CommandResult(false, "bad-payload"); }
        catch (ArgumentException e) { result = new CommandResult(false, e.Message); }
        Send(kind, session, UiMessageKind.Result, envelope.Name, envelope.CorrelationId, JsonSerializer.SerializeToElement(result, ContractsJson.Default.CommandResult));
    }

    private async Task<CommandResult> ExecuteAsync(WindowKind kind, WindowSession session, string name, JsonElement? payload)
    {
        switch (name)
        {
            case UiCommands.Close: OnWindowRequest(kind, WindowRequest.Close); return Ok();
            case UiCommands.Minimize: OnWindowRequest(kind, WindowRequest.Minimize); return Ok();
            case UiCommands.Maximize: platform.ToggleMaximize(kind); return Ok();
            case UiCommands.Pin:
                session.Pinned = !session.Pinned;
                platform.SetPinned(kind, session.Pinned);
                return Ok(JsonSerializer.SerializeToElement(session.Pinned, ContractsJson.Default.Boolean));
            case UiCommands.OpenSettings: Open(WindowKind.Settings); return Ok();
            case UiCommands.Painted:
                if (openedAt.Remove(kind, out long opened))
                {
                    Timing?.Invoke(kind, "FirstFrameAfterOpen", System.Diagnostics.Stopwatch.GetElapsedTime(opened).TotalMilliseconds);
                    if (hotkeyAt != 0 && hotkeyAt <= opened) Timing?.Invoke(kind, "FirstFrameAfterHotkey", System.Diagnostics.Stopwatch.GetElapsedTime(hotkeyAt).TotalMilliseconds);
                    hotkeyAt = 0;
                }
                return Ok();
            case UiCommands.CopyText:
                platform.SetClipboardText(Read(payload, ContractsJson.Default.SubmitTextRequest).Text);
                return Ok();
            case UiCommands.SubmitText:
                return await SubmitAsync(kind, Read(payload, ContractsJson.Default.SubmitTextRequest).Text);
            case UiCommands.OpenInMain:
            {
                // The floating window hands its text over; its own task is cancelled, the main window translates it anew.
                var text = Read(payload, ContractsJson.Default.SubmitTextRequest).Text;
                OnWindowRequest(kind, WindowRequest.Close);
                await OpenAsync(WindowKind.Main, WindowSpec.For(WindowKind.Main).Activates);
                return string.IsNullOrWhiteSpace(text) ? Ok() : await SubmitAsync(WindowKind.Main, text);
            }
            case UiCommands.FitContent:
                platform.FitHeight(kind, Read(payload, ContractsJson.Default.FitContentRequest).HeightDip);
                return Ok();
            case UiCommands.ToggleCard:
                if (TranslationOf(kind) is not { } toggled) return new CommandResult(false, "unavailable");
                await toggled.ToggleAsync(Read(payload, ContractsJson.Default.ToggleCardRequest).ServiceId);
                return Ok();
            case UiCommands.RetryCard:
                if (TranslationOf(kind) is not { } retried) return new CommandResult(false, "unavailable");
                await retried.RetryAsync(Read(payload, ContractsJson.Default.ToggleCardRequest).ServiceId);
                return Ok();
            case UiCommands.SelectLanguage:
            {
                var request = Read(payload, ContractsJson.Default.SelectLanguageRequest);
                if (!Languages.IsCanonical(request.From) || !Languages.IsCanonical(request.To) || request.From == request.To) return new CommandResult(false, "language");
                languageOverride = (request.From, request.To);
                return Ok();
            }
            case UiCommands.SettingsRead: return Ok(SettingsElement());
            case UiCommands.SettingsSave: return Save(Read(payload, ContractsJson.Default.SettingsSaveRequest));
            case UiCommands.SecretWriteNew: return WriteSecret(Read(payload, ContractsJson.Default.SecretWriteRequest));
            case UiCommands.SecretDelete: return DeleteSecret(Read(payload, ContractsJson.Default.SecretDeleteRequest));
            case UiCommands.BindAccount: return BindAccount(Read(payload, ContractsJson.Default.BindAccountRequest));
            case UiCommands.ReorderService: return Reorder(Read(payload, ContractsJson.Default.ReorderServiceRequest));
            case UiCommands.ValidateProvider: return await ValidateAsync(Read(payload, ContractsJson.Default.ValidateProviderRequest));
            case UiCommands.SaveServiceConfig: return SaveServiceConfig(Read(payload, ContractsJson.Default.ServiceConfigRequest));
            case UiCommands.LoadOptions: return await LoadOptionsAsync(Read(payload, ContractsJson.Default.LoadOptionsRequest));
            case UiCommands.SavePrompt: return SavePrompt(Read(payload, ContractsJson.Default.PromptSaveRequest));
            case UiCommands.PreviewPrompt: return PreviewPrompt(Read(payload, ContractsJson.Default.PromptPreviewRequest));
            case UiCommands.TestNetwork: return await TestNetworkAsync(Read(payload, ContractsJson.Default.NetworkTestRequest));
            case UiCommands.SelectSpeech: return SelectSpeech(Read(payload, ContractsJson.Default.SpeechSelectRequest));
            case UiCommands.SaveOcr: return SaveOcr(Read(payload, ContractsJson.Default.OcrSaveRequest));
            case UiCommands.Collect: return await CollectAsync(kind, Read(payload, ContractsJson.Default.CollectRequest));
            case UiCommands.VocabExport: return await ExportVocabAsync(Read(payload, ContractsJson.Default.VocabExportCommand));
            case UiCommands.VocabResolve: return ResolveVocab(Read(payload, ContractsJson.Default.VocabResolveRequest));
            case UiCommands.VocabSync: return await SyncVocabAsync(Read(payload, ContractsJson.Default.VocabSyncCommand));
            case UiCommands.BackupExport: return await ExportBackupAsync(Read(payload, ContractsJson.Default.BackupExportRequest));
            case UiCommands.BackupPick: return await PickBackupAsync();
            case UiCommands.BackupUnlock: return await UnlockBackupAsync(Read(payload, ContractsJson.Default.BackupUnlockRequest));
            case UiCommands.BackupApply: return ApplyBackup(Read(payload, ContractsJson.Default.BackupTokenRequest));
            case UiCommands.BackupDiscard: return DiscardBackup();
            case UiCommands.BackupUndo: return UndoBackup();
            case UiCommands.BackupDismiss: return DismissBackupResult();
            case UiCommands.AboutOpenLogs: return OpenLogs();
            case UiCommands.AboutExportDiagnostics: return await ExportDiagnosticsAsync();
            case UiCommands.AboutDismiss: return DismissAbout();
            case UiCommands.DataClear: return await ClearDataAsync(Read(payload, ContractsJson.Default.DataCleanRequest));
            case UiCommands.UpdateCheck: return await CheckAppUpdateAsync();
            case UiCommands.UpdateDownload: return await DownloadAppUpdateAsync();
            case UiCommands.UpdateInstall: return InstallAppUpdate(Read(payload, ContractsJson.Default.UpdateInstallRequest));
            case UiCommands.UpdateDiscard: return DiscardAppUpdate();
            case UiCommands.UpdateAutoCheck: return SetAppUpdateAutoCheck(Read(payload, ContractsJson.Default.UpdateAutoCheckRequest));
            case UiCommands.PluginPick: return await PickPluginAsync();
            case UiCommands.PluginConfirm: return await ConfirmPluginAsync(Read(payload, ContractsJson.Default.PluginTokenRequest));
            case UiCommands.PluginDiscard: return DiscardPlugin(Read(payload, ContractsJson.Default.PluginTokenRequest));
            case UiCommands.PluginUninstall: return UninstallPlugin(Read(payload, ContractsJson.Default.PluginUninstallRequest));
            case UiCommands.PluginCheckUpdates: return await CheckPluginUpdatesAsync();
            case UiCommands.BeginCapture: return Recapture();
            case UiCommands.StartRecording: return await StartRecordingAsync();
            case UiCommands.PauseRecording: return await PauseRecordingAsync();
            case UiCommands.StopRecording: return await StopRecordingAsync();
            case UiCommands.CancelRecording: return CancelVoiceCommand();
            case UiCommands.TranscribeRecorded: return TranscribeRecorded();
            case UiCommands.PickMedia: return await PickMediaAsync();
            case UiCommands.StartTranscription: return StartTranscription();
            case UiCommands.PauseTranscription: return PauseTranscription();
            case UiCommands.ResumeTranscription: return ResumeTranscription();
            case UiCommands.CancelTranscription: return CancelTranscription();
            case UiCommands.ConfirmTranscription: return ConfirmTranscription(Read(payload, ContractsJson.Default.TranscribeConfirmRequest));
            case UiCommands.ChangeTranslator: return ChangeTranscribeService(Read(payload, ContractsJson.Default.TranscribeSwitchRequest));
            case UiCommands.Export: return await ExportTranscriptAsync(Read(payload, ContractsJson.Default.TranscribeExportRequest));
            case UiCommands.SpeakCard: return await SpeakCardAsync(kind, Read(payload, ContractsJson.Default.SpeakCardRequest));
            case UiCommands.SpeechPlay: return PlayBarService(Read(payload, ContractsJson.Default.SpeechPlayRequest).Instance);
            case UiCommands.SpeechStop: Speech?.Player.Stop(); return Ok();
            case UiCommands.TrayOpen: return TrayOpen(Read(payload, ContractsJson.Default.TrayOpenRequest).Id);
            case UiCommands.TrayExit: platform.Exit(); return Ok();
            default: return new CommandResult(false, "unavailable"); // whitelisted but its module is not built yet
        }
    }

    private static T Read<T>(JsonElement? payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        => payload is { ValueKind: JsonValueKind.Object } p ? p.Deserialize(type) ?? throw new JsonException("null payload") : throw new JsonException("payload required");

    private static CommandResult Ok(JsonElement? value = null) => new(true, null, value);

    private void Send(WindowKind kind, WindowSession session, UiMessageKind messageKind, string? name, string? correlationId, JsonElement? payload)
    {
        var envelope = new UiEnvelope(ProtocolVersions.Ui, messageKind, session.Id, ++session.Sequence, name, correlationId, payload);
        platform.Post(kind, JsonSerializer.Serialize(envelope, ContractsJson.Default.UiEnvelope));
    }

    private void Broadcast(UiMessageKind kind, string name, JsonElement payload, WindowKind? only = null)
    {
        foreach (var (windowKind, session) in windows)
            if (session.Ready && (only is null || only == windowKind)) Send(windowKind, session, kind, name, null, payload);
    }

    /// <summary>
    /// TextInput → detect/direction (ARCHITECTURE 7): pure Han/Latin text takes the fast Unicode path;
    /// mixed/ambiguous text falls to the native ELS detector, bounded so a slow/unavailable detector
    /// never blocks submission past a short budget. Detection failure or no confident candidate falls
    /// back to the configured default source language; the target is always the zh↔en opposite of it.
    /// </summary>
    private async Task<(string From, string To)> ResolveLanguageAsync(string text, GeneralSettings general)
    {
        string? detected = ScriptDetector.Detect(text);
        if (detected is null && languageDetector is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var candidates = await languageDetector.DetectAsync(text, timeout.Token);
                detected = candidates.Select(Languages.FromDetector).FirstOrDefault(c => c is not null);
            }
            catch (OperationCanceledException) { } // bounded: falls back below rather than stalling submit
            catch (Exception) { } // a native detector failure is not a translation failure
        }
        detected ??= general.SourceLanguage;
        return (detected, Languages.OppositeOf(detected));
    }

    // ---------- translation projection ----------

    private TranslationSession? TranslationOf(WindowKind kind) => translations.TryGetValue(kind, out var slot) ? slot.Session : null;

    /// <summary>F10.1: the pronunciation port (single player, TTS providers, dictionary audio) for the F10.2 commands.</summary>
    public SpeechBackend? Speech => backend?.Speech;

    /// <summary>
    /// F09.3/F10: resolves a dictionary audio id against the entries currently shown in any result window (message thread).
    /// Null when no shown entry has it, so a stale id is refused (S06).
    /// </summary>
    public async Task<DictionaryAudioLink?> ResolveAudioLinkAsync(string audioId)
    {
        foreach (var slot in translations.Values.ToList())
            if (await slot.Session.ResolveAudioLinkAsync(audioId) is { } link) return link;
        return null;
    }

    private void ReattachTranslation(WindowKind kind)
    {
        if (translations.Remove(kind, out var old)) _ = old.Session.CloseAsync(TimeSpan.FromSeconds(3));
        AttachTranslation(kind);
    }

    private void AttachTranslation(WindowKind kind)
    {
        if (sessionFactory(config.State.Effective) is not { } attached) return;
        var slot = translations[kind] = new TranslationSlot(attached);
        attached.CardChanged += patch => platform.StartTimer(TimeSpan.Zero, () =>
        {
            if (translations.TryGetValue(kind, out var current) && current == slot) OnCardPatch(kind, patch);
        });
    }

    /// <summary>Translates text in a result window's own session (a new generation) and hands the page the new snapshot.</summary>
    private async Task<CommandResult> SubmitAsync(WindowKind kind, string text)
    {
        if (!translations.TryGetValue(kind, out var slot) || slot.Stale) ReattachTranslation(kind);
        if (TranslationOf(kind) is not { } translation) return new CommandResult(false, "unavailable");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return new CommandResult(false, "text-length");
        var general = config.State.Effective.General;
        var (from, to) = languageOverride ?? await ResolveLanguageAsync(text, general);
        await translation.SubmitAsync(text, from, to);
        // A submit starts a new generation: the page adopts it from this snapshot and replays newer patches.
        var snapshot = await translation.SnapshotAsync();
        if (windows.TryGetValue(kind, out var session) && session.Ready)
            Send(kind, session, UiMessageKind.Event, "translation", null, JsonSerializer.SerializeToElement(snapshot, ContractsJson.Default.TranslationSnapshot));
        return Ok();
    }

    /// <summary>Main window card patch (kept for the F06 tests); see <see cref="OnCardPatch(WindowKind, CardPatch)"/>.</summary>
    public void OnCardPatch(CardPatch patch) => OnCardPatch(WindowKind.Main, patch);

    /// <summary>Streaming states are coalesced to at most 30 per second; every other state is sent at once.</summary>
    public void OnCardPatch(WindowKind kind, CardPatch patch)
    {
        if (!windows.TryGetValue(kind, out var session) || !session.Ready) return;
        if (patch.Card.State == CardState.Streaming)
        {
            session.PendingCards[patch.Card.ServiceId] = patch;
            if (session.FlushScheduled) return;
            session.FlushScheduled = true;
            platform.StartTimer(PatchInterval, () => FlushCards(kind, session));
            return;
        }
        session.PendingCards.Remove(patch.Card.ServiceId);
        SendCard(kind, session, patch);
    }

    private void FlushCards(WindowKind kind, WindowSession session)
    {
        session.FlushScheduled = false;
        if (!windows.TryGetValue(kind, out var current) || current != session) return;
        var pending = session.PendingCards.Values.OrderBy(p => p.Revision).ToList();
        session.PendingCards.Clear();
        foreach (var patch in pending) SendCard(kind, session, patch);
    }

    private void SendCard(WindowKind kind, WindowSession session, CardPatch patch)
        => Send(kind, session, UiMessageKind.Patch, "card", null, JsonSerializer.SerializeToElement(patch, ContractsJson.Default.CardPatch));

    // ---------- settings ----------

    private void OnSettingsChanged(SettingsState state)
    {
        Speech?.Cache?.Retain(state.Effective); // F10.3: a changed service config (voice, speed, account) drops its cached audio
        Vocab?.Kick(); // F15.4: a target that was just enabled or given its key takes its waiting rows at once
        platform.StartTimer(TimeSpan.Zero, () =>
        {
            foreach (var slot in translations.Values) slot.Stale = true;
            ApplyHotkeys(state.Effective);
            Broadcast(UiMessageKind.Event, "settings", JsonSerializer.SerializeToElement(ProjectSettings(state), ContractsJson.Default.SettingsView), WindowKind.Settings);
            foreach (var (kind, session) in windows)
                if (session.Ready) Send(kind, session, UiMessageKind.Event, "window", null, JsonSerializer.SerializeToElement(WindowViewFor(kind, session), ContractsJson.Default.WindowView));
        });
    }

    private JsonElement SettingsElement() => JsonSerializer.SerializeToElement(ProjectSettings(config.State), ContractsJson.Default.SettingsView);

    private CommandResult Save(SettingsSaveRequest request)
    {
        var state = config.State;
        var s = state.Effective;
        if (!Enum.TryParse<ProxyMode>(request.Network.ProxyMode, true, out var proxyMode)) throw new ArgumentException("proxy-mode");
        if (request.General.CloseAction is not ("hide" or "exit")) throw new ArgumentException("close-action");
        var serviceIds = s.Services.Select(x => x.ServiceId).ToHashSet(StringComparer.Ordinal);
        if (request.Services.Any(t => !serviceIds.Contains(t.ServiceId))) throw new ArgumentException("unknown-service");
        var toggles = request.Services.ToDictionary(t => t.ServiceId, t => t.Enabled, StringComparer.Ordinal);
        var chords = new Dictionary<string, string>(s.Hotkeys.Chords, StringComparer.Ordinal);
        foreach (var hotkey in request.Hotkeys)
        {
            if (!chords.ContainsKey(hotkey.Action)) throw new ArgumentException("unknown-hotkey");
            chords[hotkey.Action] = hotkey.Chord;
        }
        var proposed = s with
        {
            General = s.General with
            {
                UiLanguage = request.General.UiLanguage, SourceLanguage = request.General.SourceLanguage, TargetLanguage = request.General.TargetLanguage,
                DefaultExpandedCards = request.General.DefaultExpandedCards, AllowClipboardBorrowing = request.General.AllowClipboardBorrowing,
                CloseAction = request.General.CloseAction == "exit" ? CloseAction.Exit : CloseAction.Hide, LaunchAtStartup = request.General.LaunchAtStartup,
            },
            Hotkeys = new HotkeySettings(chords),
            Network = s.Network with { ProxyMode = proxyMode, ProxyHost = request.Network.ProxyHost.Trim(), ProxyPort = request.Network.ProxyPort, ProxyUsername = request.Network.ProxyUsername, AiTimeoutSeconds = request.Network.AiTimeoutSeconds },
            Services = [.. s.Services.Select(x => toggles.TryGetValue(x.ServiceId, out bool enabled) ? x with { Enabled = enabled } : x)],
        };
        return Outcome(config.Save(proposed, request.ExpectedRevision, request.ExpectedFileHash));
    }

    private CommandResult WriteSecret(SecretWriteRequest request)
    {
        if (request.Value.Length is 0 or > 8192 || request.Value.Any(char.IsControl)) throw new ArgumentException("secret-format");
        if (request.InstanceId == ProxySecretInstance)
        {
            if (request.SecretName != "password") throw new ArgumentException("unknown-secret");
            config.Secrets.Write(NetworkSettings.ProxyAccountId, "password", request.Value);
            return Ok(SettingsElement());
        }
        var (proposed, accountId) = EnsureOwnAccount(request.InstanceId, request.SecretName);
        if (TranslationPackages.Find(request.InstanceId) is { } package)
        {
            var instance = proposed.Instances.First(i => i.Id == request.InstanceId);
            var nextConfig = package.ConfigAfterSecret(instance.Config, request.SecretName, request.Value);
            if (!ReferenceEquals(nextConfig, instance.Config))
            {
                // A plan change moves the origin (DeepL): the old origin's grants go with the old key.
                string oldOrigin = package.Origin(instance.Config);
                if (package.Origin(nextConfig) != oldOrigin)
                    proposed = RevokeGrants(proposed, instance, g => g.Package == package.PackageId && g.Origin == oldOrigin);
                instance = instance with { Config = nextConfig, Revision = instance.Revision + 1 };
                proposed = WithInstance(proposed, instance);
            }
            if (request.ConfirmGrants) proposed = ConfirmGrants(proposed, package, instance, request.SecretName);
        }
        else if (request.ConfirmGrants && CredentialPackages.Find(request.InstanceId) is { } other)
            proposed = ConfirmGrants(proposed, other, proposed.Instances.First(i => i.Id == request.InstanceId), request.SecretName);
        var state = config.State;
        var saved = config.SaveWithSecrets(proposed, state.Revision, state.FileHash, [(accountId, request.SecretName, request.Value)]);
        InvalidateOptions(accountId, request.InstanceId); // after the write: a load that starts now already uses the new key
        return Outcome(saved);
    }

    /// <summary>
    /// Settings.BindAccount (F06.3a): optionally re-binds the instance's secrets to an existing account that
    /// holds the same secret names, then grants the targets the instance currently needs (PLAN 4.5.4: a
    /// changed plan or address is confirmed here; nothing is widened without this call).
    /// </summary>
    private CommandResult BindAccount(BindAccountRequest request)
    {
        var s = config.State.Effective;
        var package = CredentialPackages.Find(request.InstanceId) ?? throw new ArgumentException("unknown-instance");
        var instance = s.Instances.FirstOrDefault(i => i.Id == request.InstanceId) ?? throw new ArgumentException("unknown-instance");
        var proposed = s;
        if (request.AccountId is { } accountId)
        {
            var account = s.Accounts.FirstOrDefault(a => a.Id == accountId) ?? throw new ArgumentException("unknown-account");
            if (package.SecretNames.Any(name => !account.Secrets.Contains(name))) throw new ArgumentException("account-secrets");
            var bindings = new Dictionary<string, string>(instance.AccountBindings, StringComparer.Ordinal);
            foreach (var name in package.SecretNames) bindings[name] = accountId;
            instance = instance with { AccountBindings = bindings, Revision = instance.Revision + 1 };
            proposed = WithInstance(proposed, instance);
        }
        if (request.ConfirmGrants)
        {
            if (package.SecretNames.Any(name => !instance.AccountBindings.ContainsKey(name))) throw new ArgumentException("not-bound");
            proposed = ConfirmGrants(proposed, package, instance, null);
        }
        if (ReferenceEquals(proposed, s)) return Ok(SettingsElement());
        var state = config.State;
        return Outcome(config.Save(proposed, state.Revision, state.FileHash));
    }

    /// <summary>Grants the package's current targets (optionally for one secret) on the bound accounts.</summary>
    private static AppSettings ConfirmGrants(AppSettings s, ICredentialPackage package, InstanceSettings instance, string? onlySecret)
    {
        var accounts = s.Accounts.ToList();
        foreach (var grant in package.RequiredGrants(instance.Config).Where(g => onlySecret is null || g.Secret == onlySecret))
        {
            if (!instance.AccountBindings.TryGetValue(grant.Secret, out var accountId)) continue;
            int index = accounts.FindIndex(a => a.Id == accountId);
            if (index < 0) continue;
            var account = accounts[index];
            if (!account.Secrets.Contains(grant.Secret)) account = account with { Secrets = [.. account.Secrets, grant.Secret] };
            accounts[index] = CredentialAuthorizer.Confirm(account, [grant]);
        }
        return s with { Accounts = accounts };
    }

    private static AppSettings WithInstance(AppSettings s, InstanceSettings instance)
        => s with { Instances = [.. s.Instances.Select(i => i.Id == instance.Id ? instance : i)] };

    /// <summary>
    /// Settings.ReorderService: page-local move within the shared translation order, or a move in the merged
    /// list of the General page (CFG03). Result cards and the default expanded count follow this one order.
    /// </summary>
    private CommandResult Reorder(ReorderServiceRequest request)
    {
        var s = config.State.Effective;
        if (!s.Services.Any(x => x.ServiceId == request.ServiceId && x.Capability == Capability.Translate)) throw new ArgumentException("unknown-service");
        static string PageOf(string serviceId) => BuiltInCatalog.Find(serviceId.Split('/')[0])?.Page ?? "general";
        var order = s.TranslationOrder.ToList();
        if (!order.Contains(request.ServiceId)) order.Add(request.ServiceId);
        string page = PageOf(request.ServiceId);
        var next = CapabilityResolver.ReorderWithinPage(order, id => request.Merged || PageOf(id) == page, request.ServiceId, request.Index);
        var state = config.State;
        return Outcome(config.Save(s with { TranslationOrder = next }, state.Revision, state.FileHash));
    }

    /// <summary>
    /// Settings.ValidateProvider (F06.2a/F06.3a): one minimal real call through the service's provider, built
    /// from the current settings even while the service is disabled. Only the classification comes back;
    /// vendor detail text is not sent to the page.
    /// </summary>
    private async Task<CommandResult> ValidateAsync(ValidateProviderRequest request)
    {
        var s = config.State.Effective;
        var service = s.Services.FirstOrDefault(x => x.ServiceId == request.ServiceId) ?? throw new ArgumentException("unknown-service");
        if (backend is null || !backend.RuntimeAvailable) return new CommandResult(false, "unavailable");
        var package = TranslationPackages.Find(service.Instance);
        var instance = s.Instances.FirstOrDefault(i => i.Id == service.Instance);
        if (package is null || instance is null) return new CommandResult(false, "unavailable");
        if (TranslationPackages.CredentialStates(s, package, instance, config.Secrets.Has).Any(c => !c.Saved || !c.Granted)) return new CommandResult(false, "missing-credential");
        var provider = backend.ValidationProvider(s, service.ServiceId);
        if (provider is null) return new CommandResult(false, "unavailable");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(s.Network.AiTimeoutSeconds, 5, 60));
        var result = await ServiceValidator.ValidateAsync(provider, Languages.English.Code, Languages.ChineseSimplified.Code, timeout, CancellationToken.None);
        var view = new ServiceValidationView(service.ServiceId, result.Credential.ToString().ToLowerInvariant(), result.ServiceAvailable, result.Error);
        return Ok(JsonSerializer.SerializeToElement(view, ContractsJson.Default.ServiceValidationView));
    }

    private IReadOnlyList<ConfigField>? SchemaOf(string instanceId)
        => backend?.Schemas is { } schemas && schemas.TryGetValue(instanceId, out var fields) ? fields : null;

    /// <summary>
    /// Settings.SaveServiceConfig (F07.2): values for the generated controls, checked against the package's
    /// manifest schema here (a page cannot skip it); secret fields and undeclared names are refused, and keys
    /// the host manages itself (DeepL's plan) are kept. A changed address drops the grants of the old origin:
    /// the new one is confirmed afresh (PLAN 4.6). ExpectedInstanceRevision guards against a page that
    /// edited an older copy of the instance.
    /// </summary>
    private CommandResult SaveServiceConfig(ServiceConfigRequest request)
    {
        var state = config.State;
        var s = state.Effective;
        var instance = s.Instances.FirstOrDefault(i => i.Id == request.InstanceId) ?? throw new ArgumentException("unknown-instance");
        var schema = SchemaOf(instance.Id) ?? throw new ArgumentException("unknown-instance");
        if (request.ExpectedInstanceRevision != instance.Revision) return new CommandResult(false, "conflict", SettingsElement());
        var next = new Dictionary<string, string>(instance.Config, StringComparer.Ordinal);
        var issues = new List<SettingsIssueView>();
        foreach (var value in request.Values)
        {
            var field = schema.FirstOrDefault(f => f.Name == value.Name && !f.Secret) ?? throw new ArgumentException("unknown-field");
            string text = field.Type == ConfigFieldType.String ? value.Value.Trim() : value.Value;
            if (text.Length == 0) { next.Remove(field.Name); continue; }
            if (ConfigSchema.Check(field, text) is { } problem) { issues.Add(new SettingsIssueView($"instances.{instance.Id}.config.{field.Name}", problem, "", 0)); continue; }
            next[field.Name] = text;
        }
        if (issues.Count > 0) return new CommandResult(false, "invalid", JsonSerializer.SerializeToElement(issues.ToArray(), ContractsJson.Default.SettingsIssueViewArray));
        if (next.Count == instance.Config.Count && next.All(kv => instance.Config.TryGetValue(kv.Key, out var old) && old == kv.Value)) return Ok(SettingsElement());
        var proposed = s;
        if (TranslationPackages.Find(instance.Id) is { } package)
        {
            string oldOrigin = package.Origin(instance.Config);
            if (package.Origin(next) != oldOrigin) proposed = RevokeGrants(proposed, instance, g => g.Package == package.PackageId && g.Origin == oldOrigin);
        }
        proposed = WithInstance(proposed, instance with { Config = next, Revision = instance.Revision + 1 });
        return Outcome(config.Save(proposed, state.Revision, state.FileHash));
    }

    /// <summary>
    /// The dependency fingerprint of a dynamic field: the values of the fields it depends on, the accounts its
    /// secrets are bound to and the address the package calls. Any change bumps the field's revision.
    /// </summary>
    private static string OptionsFingerprint(InstanceSettings instance, ConfigField field)
    {
        var b = new System.Text.StringBuilder();
        foreach (var name in field.Options?.DependsOn ?? [])
            b.Append(name).Append('=').Append(instance.Config.TryGetValue(name, out var v) ? v : "").Append('\u0001');
        foreach (var kv in instance.AccountBindings.OrderBy(k => k.Key, StringComparer.Ordinal)) b.Append(kv.Key).Append("->").Append(kv.Value).Append('\u0001');
        if (TranslationPackages.Find(instance.Id) is { } package) b.Append("@").Append(package.Origin(instance.Config));
        return b.ToString();
    }

    /// <summary>
    /// A secret was written or removed: every dynamic field of every instance using that account is stale, and so is the
    /// cached TTS audio of those instances (F10.3).
    /// </summary>
    private void InvalidateOptions(string accountId, string? instanceId = null)
    {
        if (Speech?.Cache is { } cache)
        {
            if (instanceId is not null) cache.Invalidate(instanceId);
            foreach (var i in config.State.Effective.Instances.Where(i => i.AccountBindings.Values.Contains(accountId))) cache.Invalidate(i.Id);
        }
        if (backend?.Options is not { } broker) return;
        if (instanceId is not null) broker.Invalidate(instanceId);
        foreach (var i in config.State.Effective.Instances.Where(i => i.AccountBindings.Values.Contains(accountId))) broker.Invalidate(i.Id);
    }

    /// <summary>
    /// Settings.LoadOptions (F07.2, CFG02): one page of a dynamic field through <see cref="OptionsBroker"/>. A
    /// request for a revision that is no longer current, or a result that arrives after the dependencies
    /// changed, comes back Stale and is never shown. Failures carry the kind only.
    /// </summary>
    private async Task<CommandResult> LoadOptionsAsync(LoadOptionsRequest request)
    {
        var s = config.State.Effective;
        var instance = s.Instances.FirstOrDefault(i => i.Id == request.InstanceId) ?? throw new ArgumentException("unknown-instance");
        var field = SchemaOf(instance.Id)?.FirstOrDefault(f => f.Name == request.Field && f.Options is not null) ?? throw new ArgumentException("unknown-field");
        if (request.Cursor is { Length: > OptionsBroker.MaxCursorLength }) throw new ArgumentException("cursor");
        if (backend is not { RuntimeAvailable: true, Options: { } broker }) return new CommandResult(false, "unavailable");
        long current = broker.Track(instance.Id, field.Name, OptionsFingerprint(instance, field));
        OptionsView View(IReadOnlyList<OptionItem> items, string? next = null, bool cached = false, ErrorKind? error = null)
            => new(instance.Id, field.Name, request.DependsOnRevision, [.. items], next, cached, false, error);
        // Stale carries the current revision so the page knows which revision to ask for next.
        OptionsView Stale(long revision) => new(instance.Id, field.Name, revision, [], Stale: true);
        if (request.DependsOnRevision != current) return Ok(JsonSerializer.SerializeToElement(Stale(current), ContractsJson.Default.OptionsView));
        if (TranslationPackages.Find(instance.Id) is { } package && TranslationPackages.CredentialStates(s, package, instance, config.Secrets.Has).Any(c => !c.Saved || !c.Granted))
            return new CommandResult(false, "missing-credential");
        // F15.4: a vocabulary package needs its key for the lists (Eudic always; AnkiConnect only with "use an API key") and AnkiConnect only a local address.
        if (VocabCatalog.Find(instance.Id) is { } vocabPackage && instance.Package == vocabPackage.PackageId && (!vocabPackage.Local || instance.Config.GetValueOrDefault("useApiKey") is "true")
            && CredentialPackages.States(s, vocabPackage, instance, config.Secrets.Has).Any(c => !c.Saved || !c.Granted))
            return new CommandResult(false, "missing-credential");
        var outcome = await broker.LoadAsync(new OptionsQuery(instance.Id, field.Name, field.Options!.Method, current, request.Cursor), request.Refresh);
        var view = outcome switch
        {
            OptionsOutcome.Loaded loaded => View(loaded.Items, loaded.NextCursor, loaded.Cached),
            OptionsOutcome.Failed failed => View([], error: failed.Kind),
            OptionsOutcome.Stale stale => Stale(stale.CurrentRevision),
            _ => View([], error: ErrorKind.Unavailable),
        };
        return Ok(JsonSerializer.SerializeToElement(view, ContractsJson.Default.OptionsView));
    }

    // ---------- SetPrompt and SetNetwork (F07.3) ----------

    /// <summary>
    /// Settings.SavePrompt (CFG04): level, template choice, scope and the custom templates, checked here. Saving
    /// changes only later tasks: a running task's providers hold the snapshot they were built with.
    /// </summary>
    private CommandResult SavePrompt(PromptSaveRequest request)
    {
        if (request.Level.Length > 0 && PromptCatalog.FindLevel(request.Level) is null) throw new ArgumentException("level");
        if (request.Scope.Any(id => !PromptCatalog.AiInstances.Contains(id)) || request.Scope.Distinct(StringComparer.Ordinal).Count() != request.Scope.Length) throw new ArgumentException("scope");
        if (request.Profiles.Length > PromptCatalog.MaxProfiles) throw new ArgumentException("profiles");
        var issues = new List<SettingsIssueView>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in request.Profiles)
        {
            if (!PromptIdFormat().IsMatch(profile.Id) || !ids.Add(profile.Id)) throw new ArgumentException("profile-id");
            string name = profile.Name.Trim();
            if (name.Length == 0 || name.Length > PromptCatalog.MaxNameLength) issues.Add(new SettingsIssueView($"prompts.{profile.Id}.name", "length", "", 0));
            if (PromptCatalog.CheckTemplate(profile.Template) is { } problem) issues.Add(new SettingsIssueView($"prompts.{profile.Id}.template", problem, "", 0));
        }
        if (request.Profile.Length > 0 && !ids.Contains(request.Profile)) throw new ArgumentException("profile");
        if (issues.Count > 0) return new CommandResult(false, "invalid", JsonSerializer.SerializeToElement(issues.ToArray(), ContractsJson.Default.SettingsIssueViewArray));
        var s = config.State.Effective;
        var proposed = s with
        {
            Prompts = [.. request.Profiles.Select(p => new PromptProfile(p.Id, p.Name.Trim(), p.Template))],
            Prompt = new PromptSettings(request.Level, request.Profile, [.. PromptCatalog.AiInstances.Where(request.Scope.Contains)]),
        };
        return Outcome(config.Save(proposed, request.ExpectedRevision, request.ExpectedFileHash));
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial System.Text.RegularExpressions.Regex PromptIdFormat();

    /// <summary>Settings.PreviewPrompt: the same single-pass renderer a task uses (<see cref="PromptSnapshot.Render"/>).</summary>
    private static CommandResult PreviewPrompt(PromptPreviewRequest request)
    {
        if (request.Text.Length > 2000 || request.Template.Length > PromptCatalog.MaxTemplateLength * 2) throw new ArgumentException("length");
        string template = request.Template.Length > 0 ? request.Template : PromptCatalog.DefaultTemplate;
        string level = PromptCatalog.FindLevel(request.Level)?.Value ?? PromptCatalog.NoLevel;
        string rendered = new PromptSnapshot(0, template, level).Render(request.Text, request.From, request.To);
        var unknown = PromptTemplate.Names(template).Where(n => !PromptTemplate.Variables.Contains(n)).ToArray();
        var view = new PromptPreviewView(rendered, unknown, PromptCatalog.CheckTemplate(request.Template));
        return Ok(JsonSerializer.SerializeToElement(view, ContractsJson.Default.PromptPreviewView));
    }

    /// <summary>
    /// Settings.TestNetwork (CFG05): the proxy settings on the page (saved password) are tried against the origins
    /// of the enabled services, one result per path. Nothing is saved and no service is disabled by a failure.
    /// </summary>
    private async Task<CommandResult> TestNetworkAsync(NetworkTestRequest request)
    {
        if (backend?.TestNetwork is not { } test) return new CommandResult(false, "unavailable");
        if (!Enum.TryParse<ProxyMode>(request.Network.ProxyMode, true, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("proxy-mode");
        var s = config.State.Effective;
        var network = s.Network with { ProxyMode = mode, ProxyHost = request.Network.ProxyHost.Trim(), ProxyPort = request.Network.ProxyPort, ProxyUsername = request.Network.ProxyUsername };
        if (mode is ProxyMode.Http or ProxyMode.Socks5 && (network.ProxyHost.Length == 0 || network.ProxyPort is <= 0 or > 65535 || Uri.CheckHostName(network.ProxyHost) == UriHostNameType.Unknown))
            return new CommandResult(false, "proxy-address");
        var results = await test(network, NetworkPaths.For(s), CancellationToken.None);
        var view = new NetworkTestView([.. results.Select(r => new NetworkPathView(r.Origin, [.. r.Services], r.Route, r.Ok, r.Error, r.Status, r.ElapsedMs))],
            DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));
        return Ok(JsonSerializer.SerializeToElement(view, ContractsJson.Default.NetworkTestView));
    }

    private CommandResult DeleteSecret(SecretDeleteRequest request)
    {
        if (request.InstanceId == ProxySecretInstance)
        {
            config.Secrets.Delete(NetworkSettings.ProxyAccountId, "password");
            return Ok(SettingsElement());
        }
        var instance = config.State.Effective.Instances.FirstOrDefault(i => i.Id == request.InstanceId) ?? throw new ArgumentException("unknown-instance");
        if (!instance.AccountBindings.TryGetValue(request.SecretName, out var accountId)) return Ok(SettingsElement());
        var state = config.State;
        // The value is gone, so are its grants: a later key for this slot is confirmed afresh (PLAN 4.5.4).
        var accounts = state.Effective.Accounts.Select(a => a.Id == accountId ? CredentialAuthorizer.Revoke(a, g => g.Secret == request.SecretName) : a).ToList();
        var saved = config.SaveWithSecrets(state.Effective with { Accounts = accounts }, state.Revision, state.FileHash, [(accountId, request.SecretName, null)]);
        InvalidateOptions(accountId, instance.Id);
        return Outcome(saved);
    }

    /// <summary>Withdraws matching grants on every account <paramref name="instance"/> is bound to.</summary>
    private static AppSettings RevokeGrants(AppSettings s, InstanceSettings instance, Func<CredentialGrant, bool> revoked)
    {
        var bound = instance.AccountBindings.Values.ToHashSet(StringComparer.Ordinal);
        return s with { Accounts = [.. s.Accounts.Select(a => bound.Contains(a.Id) ? CredentialAuthorizer.Revoke(a, revoked) : a)] };
    }

    public const string ProxySecretInstance = "network.proxy";

    /// <summary>
    /// Minimal F03 binding: an instance's secrets go to its own account (created on first write). Sharing an
    /// account between services is an explicit user choice added in F07; grants are confirmed per origin in F05.
    /// </summary>
    private (AppSettings Proposed, string AccountId) EnsureOwnAccount(string instanceId, string secretName)
    {
        var s = config.State.Effective;
        var package = BuiltInCatalog.Find(instanceId) ?? throw new ArgumentException("unknown-instance");
        if (!package.Secrets.Contains(secretName)) throw new ArgumentException("unknown-secret");
        var instance = s.Instances.FirstOrDefault(i => i.Id == instanceId) ?? throw new ArgumentException("unknown-instance");
        string accountId = instance.AccountBindings.TryGetValue(secretName, out var bound) ? bound : instanceId;
        var accounts = s.Accounts.ToList();
        if (!accounts.Any(a => a.Id == accountId)) accounts.Add(new AccountSettings(accountId, instanceId, package.Secrets, []));
        var bindings = new Dictionary<string, string>(instance.AccountBindings, StringComparer.Ordinal);
        foreach (var name in package.Secrets) bindings.TryAdd(name, accountId);
        var instances = s.Instances.Select(i => i.Id == instanceId ? i with { AccountBindings = bindings, Revision = i.Revision + (bindings.Count != i.AccountBindings.Count ? 1 : 0) } : i).ToList();
        return (s with { Accounts = accounts, Instances = instances }, accountId);
    }

    private CommandResult Outcome(SaveResult result)
    {
        if (result.Status == SaveStatus.Saved) return Ok(SettingsElement());
        if (result.Status == SaveStatus.Invalid)
            return new CommandResult(false, "invalid", JsonSerializer.SerializeToElement(result.Issues.Select(i => new SettingsIssueView(i.Path, i.Code, i.Message, i.Line)).ToArray(), ContractsJson.Default.SettingsIssueViewArray));
        return new CommandResult(false, result.Status == SaveStatus.Conflict ? "conflict" : "failed", SettingsElement());
    }

    /// <summary>The generated controls of one instance (F07.2): its schema minus secret fields, with the saved values.</summary>
    private ConfigFieldView[]? ConfigFields(InstanceSettings instance)
    {
        if (SchemaOf(instance.Id) is not { Count: > 0 } schema) return null;
        var broker = backend?.Options;
        return [.. schema.Where(f => !f.Secret).Select(f =>
        {
            bool dynamic = f.Options is not null && broker is not null;
            long revision = dynamic ? broker!.Track(instance.Id, f.Name, OptionsFingerprint(instance, f)) : 0;
            return new ConfigFieldView(f.Name, f.Type.ToString().ToLowerInvariant(), instance.Config.TryGetValue(f.Name, out var v) ? v : null, f.Default,
                f.Enum is { } e ? [.. e] : null, f.Title, f.Format, f.Minimum, f.Maximum, f.Group, f.Placeholder, f.Help, f.ShowWhenField, f.ShowWhenEquals, dynamic, revision);
        })];
    }

    public SettingsView ProjectSettings(SettingsState state)
    {
        var s = state.Effective;
        var conflicts = s.Hotkeys.Conflicts().SelectMany(c => c.Actions).ToHashSet(StringComparer.Ordinal);
        var hotkeys = HotkeySettings.Actions.Select(action =>
        {
            string chord = s.Hotkeys.Chords.TryGetValue(action, out var c) ? c : "";
            var (featureState, reason) = Resolve(hotkeyFeatures[action]);
            string status = chord.Length == 0 ? "unassigned"
                : conflicts.Contains(action) ? "conflict"
                : featureState != FeatureState.Available ? "unavailable"
                : RegistrationOf(action, chord, s.Hotkeys) == false ? "failed" : "ok";
            return new HotkeyView(action, chord, status, status == "unavailable" ? reason : null);
        }).ToArray();
        var translationOrder = s.TranslationOrder.ToList();
        var services = s.Services.Select(x =>
        {
            var package = BuiltInCatalog.Find(x.Instance);
            var instance = s.Instances.First(i => i.Id == x.Instance);
            string[] secrets = package?.Secrets ?? [];
            bool missing = secrets.Any(name => !instance.AccountBindings.TryGetValue(name, out var account) || !config.Secrets.Has(account, name));
            string availability = !x.Enabled ? nameof(Availability.Disabled) : missing ? nameof(Availability.MissingCredential) : nameof(Availability.Ready);
            bool implemented = capabilityReady(x.Capability) && x.Enabled;
            CredentialTargetView[]? targets = null;
            string? plan = null;
            // A wired built-in translation package (F06.3a): availability also needs the grants, and the
            // page gets the exact targets to show with the key field.
            // F09.2: a wired package's dictionary service (Youdao) shares the package's credentials and grants.
            if ((x.Capability == Capability.Translate || (x.Capability == Capability.Dictionary && TranslationPackages.SupportsDictionary(x.Instance)))
                && TranslationPackages.Find(x.Instance) is { } wired && instance.Package == wired.PackageId)
            {
                availability = TranslationPackages.AvailabilityOf(s, x, config.Secrets.Has).ToString();
                targets = [.. TranslationPackages.CredentialStates(s, wired, instance, config.Secrets.Has).Select(t => new CredentialTargetView(t.Secret, t.Origin, t.Use, t.Saved, t.Granted))];
                if (wired.InstanceId == TranslationPackages.DeepL) plan = instance.Config.TryGetValue("plan", out var p) && p == "pro" ? "pro" : "free";
                if (backend is not null) implemented = backend.RuntimeAvailable;
            }
            else if (SpeechCatalog.Find(x.Instance) is { Credentials.Count: > 0 } speech && instance.Package == speech.PackageId && x.Capability == speech.Capability)
            {
                // A planned speech package (F07.4): its grants are confirmed like a translation package's, so a shared
                // account (OpenAI, Tencent Cloud) is usable only for the package, origin and use the user confirmed.
                var states = CredentialPackages.States(s, speech, instance, config.Secrets.Has);
                targets = [.. states.Select(t => new CredentialTargetView(t.Secret, t.Origin, t.Use, t.Saved, t.Granted))];
                if (x.Enabled) availability = (states.All(t => t.Saved && t.Granted) ? Availability.Ready : Availability.MissingCredential).ToString();
                implemented = false;
            }
            else if (x.Capability == Capability.Ocr && OcrCatalog.Find(x.Instance) is { } ocrPackage && instance.Package == ocrPackage.PackageId)
            {
                // F11.3 SetOcr: an OCR package's grants are confirmed per package, origin and use, like a translation package's, so
                // the shared Tencent Cloud account needs its own grant for ocr.tencentcloudapi.com (PLAN 1.3/4.5.3).
                var states = CredentialPackages.States(s, ocrPackage, instance, config.Secrets.Has);
                targets = [.. states.Select(t => new CredentialTargetView(t.Secret, t.Origin, t.Use, t.Saved, t.Granted))];
                if (x.Enabled) availability = (states.All(t => t.Saved && t.Granted) ? Availability.Ready : Availability.MissingCredential).ToString();
                if (backend is not null) implemented = backend.RuntimeAvailable && x.Enabled;
            }
            else if (x.Capability == Capability.Vocab && VocabCatalog.Find(x.Instance) is { } vocabPackage && instance.Package == vocabPackage.PackageId)
            {
                // F15.4 SetVocab: a vocabulary package's key (Eudic always; AnkiConnect only with "use an API key") is granted per package, origin and use like any
                // wired package's; the AnkiConnect address must be a loopback address (VocabTargets).
                targets = [.. CredentialPackages.States(s, vocabPackage, instance, config.Secrets.Has).Select(t => new CredentialTargetView(t.Secret, t.Origin, t.Use, t.Saved, t.Granted))];
                if (VocabTargets.Evaluate(s, x.Instance, config.Secrets.Has) is { Enabled: true } vocabState) availability = vocabState.Availability.ToString();
                if (backend is not null) implemented = backend.RuntimeAvailable && x.Enabled;
            }
            else if (backend is not null) implemented = false;
            int order = x.Capability == Capability.Translate ? translationOrder.IndexOf(x.ServiceId) : -1;
            // Local usage counts translated characters; OCR calls are not counted in characters (no OCR usage line yet).
            long? usage = targets is not null && x.Capability != Capability.Ocr && backend?.MonthlyUsage is { } monthly ? monthly(x.ServiceId) : null;
            return new ServiceView(x.ServiceId, x.Instance, x.Capability.ToString().ToLowerInvariant(), package?.Page ?? "general", x.Enabled, availability,
                implemented, secrets, secrets.Length > 0 && instance.AccountBindings.TryGetValue(secrets[0], out var a) ? a : null, targets, plan, order, usage,
                ConfigFields(instance), instance.Revision);
        }).ToArray();
        var accounts = s.Accounts.Select(a => new AccountView(a.Id, a.Label,
            [.. a.Secrets.Select(name => new SecretSlotView(name, config.Secrets.Has(a.Id, name)))],
            [.. s.Instances.Where(i => i.AccountBindings.Values.Contains(a.Id)).Select(i => i.Id)])).ToArray();
        var g = s.General;
        return new SettingsView(state.Revision, state.FileHash,
            [.. state.Issues.Select(i => new SettingsIssueView(i.Path, i.Code, i.Message, i.Line))],
            new GeneralView(g.UiLanguage, g.SourceLanguage, g.TargetLanguage, g.DefaultExpandedCards, g.AllowClipboardBorrowing, g.CloseAction == CloseAction.Exit ? "exit" : "hide", g.LaunchAtStartup),
            hotkeys,
            new NetworkView(s.Network.ProxyMode.ToString().ToLowerInvariant(), s.Network.ProxyHost, s.Network.ProxyPort, s.Network.ProxyUsername, config.Secrets.Has(NetworkSettings.ProxyAccountId, "password"), s.Network.AiTimeoutSeconds),
            services, accounts,
            new PromptView(s.Prompt.Level, s.Prompt.Profile, [.. s.Prompt.Scope], [.. PromptCatalog.Levels.Select(l => l.Id)], [.. PromptCatalog.AiInstances],
                [.. s.Prompts.Select(p => new PromptProfileView(p.Id, p.Name, p.Template))], PromptCatalog.DefaultTemplate, [.. PromptTemplate.Variables]),
            new SpeechView(SpeechSlotOf(s, SpeechSlot.Tts), SpeechSlotOf(s, SpeechSlot.Asr), SpeechSlotOf(s, SpeechSlot.VideoAsr), s.Speech.VideoTranslator, VideoTranslatorChoices(s)),
            OcrSettingsOf(s), ProjectVocab(s), ProjectPlugins(), ProjectBackup(), ProjectAbout(), ProjectUpdate());
    }

    private static readonly Dictionary<SpeechSlot, string> speechSlotNames = new() { [SpeechSlot.Tts] = "tts", [SpeechSlot.Asr] = "asr", [SpeechSlot.VideoAsr] = "videoAsr" };

    /// <summary>Credential state of a speech choice, independent of the service-list toggle: the selection is what uses it.</summary>
    private bool SpeechCredentialsReady(AppSettings s, SpeechPackage package)
        => s.Instances.FirstOrDefault(i => i.Id == package.InstanceId) is { } instance && instance.Package == package.PackageId
            && CredentialPackages.States(s, package, instance, config.Secrets.Has).All(t => t.Saved && t.Granted);

    /// <summary>
    /// One SetSpeech/SetSpeechB selection with its choices (F07.4). The pronunciation selection is ready once its package
    /// is installed and its credentials are saved and granted (F10); recording, audio and video stay unavailable until F12.
    /// </summary>
    private SpeechSlotView SpeechSlotOf(AppSettings s, SpeechSlot slot)
    {
        var selection = s.Speech[slot];
        var choices = SpeechCatalog.Choices(slot).Select(p =>
        {
            string? reason = SpeechCatalog.CheckPackage(slot, p);
            string availability = p.Credentials.Count == 0 || SpeechCredentialsReady(s, p) ? nameof(Availability.Ready) : nameof(Availability.MissingCredential);
            return new SpeechChoiceView(p.InstanceId, p.Native, p.Installed, p.Plan, p.Timecodes, reason is null, availability,
                [.. p.Models.Select(m => new SpeechModelView(m.Id, m.Timecodes, SpeechCatalog.Encodable(m) && (slot != SpeechSlot.VideoAsr || m.Timecodes)))], reason);
        }).ToArray();
        // Pronunciation plays since F10.2 and voice/audio recording transcribes since F12.3; video transcription comes in F14.
        string? why = SpeechProblem(s, slot);
        return new SpeechSlotView(speechSlotNames[slot], selection.Instance, selection.Model, choices, why is null, why);
    }

    /// <summary>Why the selection of a slot cannot be used (not selected, no timecodes for video, not installed, key missing); null when it can.</summary>
    private string? SpeechProblem(AppSettings s, SpeechSlot slot)
    {
        var selection = s.Speech[slot];
        if (SpeechCatalog.Find(selection.Instance) is not { } package) return slot == SpeechSlot.VideoAsr ? SpeechCatalog.NeedsTimecodes : "none-selected";
        if (SpeechCatalog.Check(slot, selection) is { } problem) return problem;
        if (!package.Installed) return "not-installed";
        if (package.Credentials.Count > 0 && !SpeechCredentialsReady(s, package)) return "missing-credential";
        return null;
    }

    /// <summary>
    /// Settings.SelectSpeech (F07.4, A02/A03): sets one slot and nothing else. The package must declare the slot's
    /// capability; video transcription accepts only a model that returns timecodes (a text-only ASR is refused, never
    /// given invented start/end). Credentials are not touched: a shared account is bound and granted in the service's
    /// details (Settings.BindAccount), per package, origin and use.
    /// </summary>
    private CommandResult SelectSpeech(SpeechSelectRequest request)
    {
        if (request.Slot == "videoTranslator")
        {
            // F14.4: the translation service of video transcription, independent of the main window (empty: the first enabled service).
            var current = config.State.Effective;
            if (request.Instance != "" && !VideoTranslatorChoices(current).Contains(request.Instance)) return new CommandResult(false, "range");
            return Outcome(config.Save(current with { Speech = current.Speech with { VideoTranslator = request.Instance } }, request.ExpectedRevision, request.ExpectedFileHash));
        }
        var slot = speechSlotNames.Where(kv => kv.Value == request.Slot).Select(kv => (SpeechSlot?)kv.Key).FirstOrDefault() ?? throw new ArgumentException("slot");
        var selection = new SpeechSelection(request.Instance, request.Model);
        if (SpeechCatalog.Check(slot, selection) is { } problem) return new CommandResult(false, problem);
        var s = config.State.Effective;
        return Outcome(config.Save(s with { Speech = s.Speech.With(slot, selection) }, request.ExpectedRevision, request.ExpectedFileHash));
    }

    // ---------- tray and hotkeys ----------

    public TrayView TrayModel()
    {
        var chords = config.State.Effective.Hotkeys.Chords;
        return new TrayView([.. trayLayout.Select(item =>
        {
            if (item.Feature is null) return new TrayItemView(item.Id, "", true, null, item.SeparatorBefore);
            var (state, reason) = Resolve(item.Feature);
            string chord = item.Hotkey is not null && chords.TryGetValue(item.Hotkey, out var c) ? c : "";
            // A recording in progress keeps its entry usable (and marked) so the window can be brought back from the tray.
            if ((item.Id == "voice" && recording is { Source: AudioSourceKind.Microphone } || item.Id == "system-audio" && recording is { Source: AudioSourceKind.SystemLoopback }) && recording is { } active)
                return new TrayItemView(item.Id, chord, true, null, item.SeparatorBefore, active.Phase == RecordingPhase.Paused ? "paused" : "recording");
            return new TrayItemView(item.Id, chord, state == FeatureState.Available, state == FeatureState.Available ? null : reason, item.SeparatorBefore);
        })]);
    }

    private CommandResult TrayOpen(string id)
    {
        var item = trayLayout.FirstOrDefault(t => t.Id == id);
        if (item.Id is null) throw new ArgumentException("unknown-item");
        if (item.Feature is not null && Resolve(item.Feature).State != FeatureState.Available && !(id == "voice" && recording is { Source: AudioSourceKind.Microphone } || id == "system-audio" && recording is { Source: AudioSourceKind.SystemLoopback })) return new CommandResult(false, "unavailable");
        HideWindow(WindowKind.Tray);
        switch (id)
        {
            case "settings": Open(WindowKind.Settings); break;
            case "exit": platform.Exit(); break;
            case "input-translation": Open(WindowKind.Main); break;
            // Reads the text already on the clipboard (never Ctrl+C). There is no selection entry: opening this menu has
            // already taken focus from the program holding the selection (PLAN 1.4, D-51).
            case "clipboard":
                if (capture is null) return new CommandResult(false, "unavailable");
                _ = CaptureAsync(capture, capture.CaptureAsync(CaptureTrigger.Clipboard));
                break;
            case "system-audio":
                OpenVoice(AudioSourceKind.SystemLoopback);
                break;
            case "transcription":
                OpenTranscribe();
                break;
            case "voice":
                OpenVoice();
                break;
            case "ocr":
                if (ScreenCapture is null) return new CommandResult(false, "unavailable");
                StartScreenCapture();
                break;
        }
        return Ok();
    }

    private (FeatureState State, string? ReasonKey) Resolve(string feature)
    {
        var (state, reason) = features.Resolve(feature, capabilityReady);
        if (feature == FeatureRegistry.Ids.Transcription && videoJobs is not null)
        {
            // F14.4 (A03): the Transcribe entry needs the video ASR selection to work and return timecodes; text-only ASR greys it with the prerequisite.
            if (SpeechProblem(config.State.Effective, SpeechSlot.VideoAsr) is not null) return (FeatureState.Unavailable, "feature.noService.videoAsr");
            return (FeatureState.Available, null);
        }
        if (state == FeatureState.InDevelopment && options.DevPreview) return (FeatureState.Available, null);
        // A03: video transcription needs a service that returns timecodes. Voice and audio entries do not (text-only is fine), so
        // with only a text-only service the video entry stays greyed with that prerequisite, never an invented start/end.
        if (feature == FeatureRegistry.Ids.Transcription && state != FeatureState.Available && SpeechProblem(config.State.Effective, SpeechSlot.VideoAsr) is not null)
            return (FeatureState.Unavailable, "feature.noService.videoAsr");
        return (state, reason);
    }

    /// <summary>
    /// The RegisterHotKey result behind an action (CFG05): clipboard translation sharing selection's chord has no
    /// registration of its own, so it reports selection's. Null: not registered by this run (e.g. unavailable).
    /// </summary>
    private bool? RegistrationOf(string action, string chord, HotkeySettings hotkeys)
    {
        if (hotkeyResults.TryGetValue(action, out bool registered)) return registered;
        if (action == "clipboardTranslate" && hotkeys.Chords.TryGetValue("selectionTranslate", out var shared) && shared == chord
            && hotkeyResults.TryGetValue("selectionTranslate", out bool viaSelection)) return viaSelection;
        return null;
    }

    /// <summary>Registers chords only for available features; selection and clipboard share one registration.</summary>
    private void ApplyHotkeys(AppSettings settings)
    {
        var conflicts = settings.Hotkeys.Conflicts().SelectMany(c => c.Actions).ToHashSet(StringComparer.Ordinal);
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (action, chord) in settings.Hotkeys.Chords)
        {
            if (chord.Length == 0 || conflicts.Contains(action) || Resolve(hotkeyFeatures[action]).State != FeatureState.Available) continue;
            if (action == "clipboardTranslate" && settings.Hotkeys.Chords.TryGetValue("selectionTranslate", out var shared) && shared == chord && wanted.ContainsKey("selectionTranslate")) continue;
            wanted[action] = chord;
        }
        hotkeyResults = new Dictionary<string, bool>(platform.RegisterHotkeys(wanted), StringComparer.Ordinal);
    }

    private WindowView WindowViewFor(WindowKind kind, WindowSession session)
    {
        var s = config.State.Effective;
        var available = features.All.Select(f => f.Id).Where(id => Resolve(id).State == FeatureState.Available).ToArray();
        return new WindowView(kind, s.General.UiLanguage, s.General.Theme, false, session.Pinned, options.DevPreview, available);
    }
}
