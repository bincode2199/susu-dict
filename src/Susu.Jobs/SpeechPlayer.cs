using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Jobs;

/// <summary>How one playback request ended.</summary>
public enum PlaybackStatus
{
    /// <summary>The clip played to the end.</summary>
    Completed,
    /// <summary>The user stopped it (<see cref="SpeechPlayer.Stop"/>) or the caller's token was cancelled.</summary>
    Stopped,
    /// <summary>A newer playback request replaced it (ARCHITECTURE 7: a new playback stops the old one).</summary>
    Superseded,
    /// <summary>Synthesis/download failed (<see cref="PlaybackResult.Error"/>) or the output failed (<see cref="PlaybackResult.Device"/>).</summary>
    Failed,
}

/// <summary>Error: the service failure (auth, rate_limited, network, ...). Device: the audio output failure.</summary>
public sealed record PlaybackResult(PlaybackStatus Status, ProviderError? Error = null, AudioFailure? Device = null);

/// <summary>What the player is doing now: Idle, Loading (synthesis or download) or Playing; Generation counts requests.</summary>
public enum PlayerPhase { Idle, Loading, Playing }

public sealed record PlayerState(PlayerPhase Phase, long Generation);

/// <summary>
/// The one TTS player of the app (F10.1, ARCHITECTURE 7 "TTS 一次仅有一个播放会话，新播放停止旧播放并释放旧 lease").
/// Each request gets its own generation: starting one cancels the previous request's synthesis/download and playback,
/// waits until the previous clip has left the audio output, and releases its lease. A clip that arrives for a request
/// that is no longer current is released without being played, so old audio never plays late (TTS02). The clip lease
/// is released once playback ends, is stopped or fails.
/// </summary>
public sealed class SpeechPlayer(IAudioSink sink)
{
    private sealed class Session(long generation, CancellationTokenSource cts)
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cts { get; } = cts;
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Superseded;
    }

    private readonly object gate = new();
    private Session? current;
    private long generation;
    private Task lastFinished = Task.CompletedTask;
    /// <summary>Completes when the clip now in the audio output has left it; null when the output is free.</summary>
    private TaskCompletionSource? sinkBusy;

    /// <summary>Raised on every phase change, from the thread doing the work.</summary>
    public event Action<PlayerState>? StateChanged;

    public PlayerState State { get; private set; } = new(PlayerPhase.Idle, 0);

    public AudioDeviceStatus Device => sink.Probe();

    /// <summary>Synthesizes with <paramref name="provider"/> and plays the result, replacing whatever was playing.</summary>
    public Task<PlaybackResult> SpeakAsync(ITtsProvider provider, SpeakRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        => PlayAsync(token => provider.SynthesizeAsync(new SpeakCall(request, $"tts-{Guid.NewGuid():N}", timeout), token), cancellationToken);

    /// <summary>
    /// Plays the audio <paramref name="acquire"/> produces (a TTS synthesis, or F09 dictionary audio), replacing whatever
    /// was playing. The token passed to <paramref name="acquire"/> is cancelled when this request is stopped or superseded.
    /// </summary>
    public async Task<PlaybackResult> PlayAsync(Func<CancellationToken, Task<AudioOutcome>> acquire, CancellationToken cancellationToken = default)
    {
        Session session;
        Session? previous;
        lock (gate)
        {
            previous = current;
            session = new Session(++generation, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            current = session;
            lastFinished = session.Finished.Task;
        }
        if (previous is not null) { previous.Superseded = true; Cancel(previous); }
        Publish(session, PlayerPhase.Loading);

        IAudioClip? clip = null;
        try
        {
            AudioOutcome outcome;
            try { outcome = await acquire(session.Cts.Token); }
            catch (OperationCanceledException) when (session.Cts.IsCancellationRequested) { return Ended(session); }
            switch (outcome)
            {
                case AudioOutcome.Failure failure:
                    if (session.Cts.IsCancellationRequested) return Ended(session);
                    return new PlaybackResult(PlaybackStatus.Failed, failure.Error);
                case AudioOutcome.Ready ready:
                    clip = ready.Clip;
                    break;
                default:
                    return new PlaybackResult(PlaybackStatus.Failed, new ProviderError(ErrorKind.BadResponse, "no audio"));
            }
            // Only one clip is ever in the output: wait until the replaced clip (cancelled above) has left it. The
            // cancellation check and taking the output happen under one lock, so stale audio is released, never played late.
            TaskCompletionSource owned;
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (session.Cts.IsCancellationRequested) return Ended(session);
                    if (sinkBusy is null) { owned = sinkBusy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); break; }
                    wait = sinkBusy.Task;
                }
                await wait.ConfigureAwait(false);
            }
            try
            {
                Publish(session, PlayerPhase.Playing);
                try { await sink.PlayAsync(clip, session.Cts.Token); }
                catch (OperationCanceledException) when (session.Cts.IsCancellationRequested) { return Ended(session); }
                catch (AudioPlaybackException error) { return new PlaybackResult(PlaybackStatus.Failed, new ProviderError(ErrorKind.Unavailable, error.Message), error.Failure); }
                return session.Cts.IsCancellationRequested ? Ended(session) : new PlaybackResult(PlaybackStatus.Completed);
            }
            finally
            {
                lock (gate) sinkBusy = null;
                owned.TrySetResult();
            }
        }
        finally
        {
            clip?.Dispose();
            bool wasCurrent;
            lock (gate) { wasCurrent = ReferenceEquals(current, session); if (wasCurrent) current = null; }
            session.Cts.Dispose();
            session.Finished.TrySetResult();
            if (wasCurrent) Publish(session, PlayerPhase.Idle);
        }
    }

    /// <summary>Stops the current request (synthesis, download or playback). No-op when idle.</summary>
    public void Stop()
    {
        Session? session;
        lock (gate) session = current;
        if (session is not null) Cancel(session);
    }

    /// <summary>Completes when the current request (if any) has fully ended and released its clip.</summary>
    public Task IdleAsync() { lock (gate) return lastFinished; }

    private static void Cancel(Session session)
    {
        try { session.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static PlaybackResult Ended(Session session) => new(session.Superseded ? PlaybackStatus.Superseded : PlaybackStatus.Stopped);

    private void Publish(Session session, PlayerPhase phase)
    {
        lock (gate)
        {
            if (phase != PlayerPhase.Idle && !ReferenceEquals(current, session)) return;
            State = new PlayerState(phase, session.Generation);
        }
        StateChanged?.Invoke(State);
    }
}
