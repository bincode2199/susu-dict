using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;

namespace Susu.Ui;

/// <summary>
/// F10.2 pronunciation in the shell (PLAN 6.5, DESIGN 9 "发音浮条", ARCHITECTURE 7): the pronunciation hotkey speaks the
/// captured selection with the default service and shows the bar next to the selection; the bar replays with any of
/// its services; card read-aloud keys speak a shown result, and a dictionary phonetic key plays the entry's own audio.
/// Everything goes through the one <see cref="SpeechPlayer"/>, so a new playback always stops the previous one.
/// </summary>
public sealed partial class ShellCoordinator
{
    /// <summary>Upper bound for one synthesis or download (the first SAPI synthesis on a cold VM took ~20 s in F10.1).</summary>
    public static readonly TimeSpan SpeakTimeout = TimeSpan.FromSeconds(45);
    /// <summary>The bar stays this long after its playback ended, so another service can still be tried.</summary>
    public static readonly TimeSpan SpeechBarLinger = TimeSpan.FromSeconds(5);
    public const string BarTarget = "bar";

    private SpeechStateView speechState = new(0, "idle");
    private SpeechBarView? speechBar;
    private (string Text, string? Lang)? barText;
    private long speechBars, speechRequests, barHideToken, playerGeneration = -1;

    /// <summary>A playback the shell started has ended (target, result); for tests and diagnostics.</summary>
    public event Action<string, PlaybackResult>? SpeechEnded;

    public SpeechStateView SpeechState => speechState;
    public SpeechBarView? SpeechBar => speechBar;

    /// <summary>
    /// The pronunciation hotkey's capture has text (PLAN 6.5): no window takes focus; the bar opens next to the selection
    /// (UIA bounds, else the pointer) without activation, and the default service speaks the text.
    /// </summary>
    private async Task PronounceAsync(CaptureCoordinator coordinator, CaptureOutcome outcome)
    {
        if (Speech is not { } speech) return;
        var settings = config.State.Effective;
        var (lang, _) = await ResolveLanguageAsync(outcome.Text, settings.General);
        if (!coordinator.IsCurrent(outcome.Generation)) return; // a newer capture won (J01)
        barText = (outcome.Text, lang);
        var services = BarServices(settings, speech);
        var bar = speechBar = new SpeechBarView(++speechBars, services, services[0].Instance);
        platform.Anchor(WindowKind.Speech, PixelsOf(outcome.Rect), WindowSpec.SpeechBarWidthDip(services.Length));
        bool wasVisible = lifecycle.Visible.Contains(WindowKind.Speech);
        await OpenAsync(WindowKind.Speech, activate: false);
        if (wasVisible && windows.TryGetValue(WindowKind.Speech, out var session) && session.Ready)
            Send(WindowKind.Speech, session, UiMessageKind.Event, "speechbar", null, JsonSerializer.SerializeToElement(bar, ContractsJson.Default.SpeechBarView));
        if (speech.Tts(settings, bar.Active) is null)
        {
            Fail(BarTarget, ErrorKind.Unavailable);
            return;
        }
        StartPlayback(speech, BarTarget, () => speech.SpeakSelectedAsync(settings, outcome.Text, lang, SpeakTimeout));
    }

    /// <summary>Speech.Play: per-service playback of the bar's text; the square becomes the bar's active one.</summary>
    private CommandResult PlayBarService(string instance)
    {
        if (Speech is not { } speech || speechBar is not { } bar || barText is not { } said) return new CommandResult(false, "unavailable");
        if (!bar.Services.Any(s => s.Instance == instance)) return new CommandResult(false, "unknown-service");
        var settings = config.State.Effective;
        if (speech.Tts(settings, instance) is not { } provider) return new CommandResult(false, "unavailable");
        speechBar = bar = bar with { Active = instance };
        if (windows.TryGetValue(WindowKind.Speech, out var session) && session.Ready)
            Send(WindowKind.Speech, session, UiMessageKind.Event, "speechbar", null, JsonSerializer.SerializeToElement(bar, ContractsJson.Default.SpeechBarView));
        StartPlayback(speech, BarTarget, () => speech.SpeakAsync(settings, provider, SpeechBackend.RequestFor(settings, instance, said.Text, said.Lang), SpeakTimeout));
        return Ok();
    }

    /// <summary>
    /// Speech.SpeakCard (DESIGN 8 card keys, TTS03): only a card that is shown expanded with a finished result is read;
    /// a collapsed or never-queried card is refused and nothing is requested for it, so no invisible dictionary lookup
    /// ever runs to get audio. A phonetic key plays the entry's own audio (F09 result, fetched by the host) and falls
    /// back to the default service speaking the word; the card key speaks the translation with the default service.
    /// </summary>
    private async Task<CommandResult> SpeakCardAsync(WindowKind kind, SpeakCardRequest request)
    {
        if (Speech is not { } speech || TranslationOf(kind) is not { } session) return new CommandResult(false, "unavailable");
        var snapshot = await session.SnapshotAsync();
        var card = snapshot.Cards.FirstOrDefault(c => c.ServiceId == request.ServiceId);
        if (card is null || card.Collapsed || card.State != CardState.Ready) return new CommandResult(false, "not-ready");
        var settings = config.State.Effective;
        string text;
        string? lang;
        string target = $"card:{card.ServiceId}";
        if (request.Phonetic is int index)
        {
            if (card.Entry is not { } entry || index < 0 || index >= entry.Phonetics.Length) return new CommandResult(false, "not-ready");
            var phonetic = entry.Phonetics[index];
            target += $":{index}";
            if (phonetic.AudioId is { } audioId && speech.DictionaryAudio is { } fetch)
            {
                StartPlayback(speech, target, () => speech.Player.PlayAsync(token => fetch(audioId, token)));
                return Ok();
            }
            text = entry.Word;
            lang = phonetic.Accent switch { "uk" => "en-GB", "us" => "en-US", _ => snapshot.From };
        }
        else
        {
            text = card.Entry is { } entry ? string.Join("；", entry.Parts.SelectMany(p => p.Means)) : card.Text;
            lang = snapshot.To;
        }
        if (string.IsNullOrWhiteSpace(text)) return new CommandResult(false, "empty");
        if (Resolve(FeatureRegistry.Ids.Pronunciation).State != FeatureState.Available
            || speech.Tts(settings, settings.Speech.Tts.Instance) is null) return new CommandResult(false, "unavailable");
        StartPlayback(speech, target, () => speech.SpeakSelectedAsync(settings, text, lang, SpeakTimeout));
        return Ok();
    }

    /// <summary>
    /// The bar's squares: the default service first (DESIGN 9), then every other installed pronunciation service that can
    /// run now (a speech package only with its credentials saved and granted, which <see cref="SpeechBackend.Tts"/> checks).
    /// </summary>
    private SpeechServiceView[] BarServices(AppSettings s, SpeechBackend speech)
    {
        string chosen = s.Speech.Tts.Instance;
        var list = new List<SpeechServiceView> { new(chosen, true) };
        foreach (var package in SpeechCatalog.Choices(SpeechSlot.Tts))
            if (package.InstanceId != chosen && package.Installed && speech.Tts(s, package.InstanceId) is not null)
                list.Add(new SpeechServiceView(package.InstanceId, false));
        return [.. list];
    }

    private void StartPlayback(SpeechBackend speech, string target, Func<Task<PlaybackResult>> start)
    {
        long request = ++speechRequests;
        Task<PlaybackResult> task;
        try { task = start(); }
        catch (Exception e) { task = Task.FromResult(new PlaybackResult(PlaybackStatus.Failed, new ProviderError(ErrorKind.Unavailable, e.GetType().Name))); }
        playerGeneration = speech.Player.State.Generation; // PlayAsync publishes its generation before its first await
        if (task.IsCompleted) { _ = FinishPlaybackAsync(task, request, target); return; }
        bool playing = speech.Player.State is { Phase: PlayerPhase.Playing } now && now.Generation == playerGeneration; // a cached clip may already play
        SetSpeechState(new SpeechStateView(request, playing ? "playing" : "loading", target));
        _ = FinishPlaybackAsync(task, request, target);
    }

    private async Task FinishPlaybackAsync(Task<PlaybackResult> task, long request, string target)
    {
        PlaybackResult result;
        try { result = await task; }
        catch (Exception e) { result = new PlaybackResult(PlaybackStatus.Failed, new ProviderError(ErrorKind.Unavailable, e.GetType().Name)); }
        Diagnostic?.Invoke($"speech {target.Split(':')[0]} {result.Status} {result.Error?.Kind} {result.Device}");
        SpeechEnded?.Invoke(target, result);
        if (request != speechRequests) return; // a newer request owns the state
        SetSpeechState(result.Status == PlaybackStatus.Failed
            ? new SpeechStateView(request, "error", target, result.Error?.Kind, result.Device is { } device ? DeviceKey(device) : null)
            : new SpeechStateView(request, "stopped", target));
    }

    private void Fail(string target, ErrorKind kind) => SetSpeechState(new SpeechStateView(++speechRequests, "error", target, kind));

    /// <summary>Player phase changes (marshalled to the message thread): only "playing" of the current request is shown.</summary>
    private void OnPlayerState(PlayerState state)
    {
        if (state.Phase != PlayerPhase.Playing || state.Generation != playerGeneration) return;
        if (speechState.Generation != speechRequests || speechState.Phase != "loading") return;
        SetSpeechState(speechState with { Phase = "playing" });
    }

    private void SetSpeechState(SpeechStateView view)
    {
        speechState = view;
        var payload = JsonSerializer.SerializeToElement(view, ContractsJson.Default.SpeechStateView);
        foreach (var (kind, session) in windows.ToList())
            if (session.Ready && (kind == WindowKind.Speech || UiCommands.IsAllowed(kind, UiCommands.SpeakCard)))
                Send(kind, session, UiMessageKind.Event, "speech", null, payload);
        // The bar stays while its own playback runs and hides a moment after it ended or was replaced.
        long token = ++barHideToken;
        if (view.Target == BarTarget && view.Phase is "loading" or "playing") return;
        if (!lifecycle.Visible.Contains(WindowKind.Speech)) return;
        platform.StartTimer(SpeechBarLinger, () => { if (token == barHideToken) HideWindow(WindowKind.Speech); });
    }

    private static string DeviceKey(AudioFailure failure) => failure switch
    {
        AudioFailure.NoDevice => "no-device",
        AudioFailure.DeviceLost => "device-lost",
        AudioFailure.Unsupported => "unsupported",
        _ => "failed",
    };

    /// <summary>UIA selection bounds (physical pixels, possibly fractional) as the bar's anchor; null without a rect.</summary>
    public static PixelRect? PixelsOf(ScreenRect? rect)
    {
        if (rect is null || !(rect.Right > rect.Left) || !(rect.Bottom > rect.Top)) return null;
        int left = (int)Math.Floor(rect.Left), top = (int)Math.Floor(rect.Top);
        return new PixelRect(left, top, (int)Math.Ceiling(rect.Right) - left, (int)Math.Ceiling(rect.Bottom) - top);
    }
}
