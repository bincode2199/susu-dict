using Susu.Abstractions;
using Susu.Contracts;
using Susu.Jobs;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F10.1 single player (ARCHITECTURE 7, TTS01/TTS02 player parts, B07 lease parts) against a fake audio sink: one clip
/// in the output at a time, a new playback stops the old one, stop, stale audio never plays late, and every clip lease
/// is released after playback, stop, supersede, failure or cancel.
/// </summary>
public class SpeechPlayerTests
{
    private sealed class FakeClip(string name) : IAudioClip
    {
        public string Name { get; } = name;
        public int Disposals;
        public string Mime => "audio/wav";
        public string FilePath => $"fake://{Name}";
        public long Bytes => 10;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    /// <summary>Plays until released by <see cref="Finish"/> or cancelled; records order and the peak number of clips playing at once.</summary>
    private sealed class FakeSink : IAudioSink
    {
        private readonly object gate = new();
        private readonly Dictionary<string, TaskCompletionSource> running = [];
        private int playing;
        public int Peak;
        public readonly List<string> Played = [];
        public AudioFailure? FailWith;
        public bool Instant;
        public readonly SemaphoreSlim Started = new(0);

        public AudioDeviceStatus Probe() => FailWith is { } f ? new(false, f) : new(true);

        public async Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken)
        {
            if (FailWith is { } failure) throw new AudioPlaybackException(failure, "fake device");
            var name = ((FakeClip)clip).Name;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { Played.Add(name); running[name] = done; Peak = Math.Max(Peak, ++playing); }
            Started.Release();
            try
            {
                if (!Instant) await done.Task.WaitAsync(cancellationToken);
                // A real device takes a moment to stop; the next clip must still not overlap.
                else await Task.Yield();
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested) await Task.Delay(30, CancellationToken.None);
                lock (gate) { playing--; running.Remove(name); }
            }
        }

        public void Finish(string name) { lock (gate) if (running.TryGetValue(name, out var t)) t.TrySetResult(); }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<CancellationToken, Task<AudioOutcome>> Ready(FakeClip clip) => _ => Task.FromResult<AudioOutcome>(new AudioOutcome.Ready(clip));

    [Fact]
    public async Task Plays_to_the_end_and_releases_the_lease()
    {
        var sink = new FakeSink { Instant = true };
        var player = new SpeechPlayer(sink);
        var clip = new FakeClip("a");
        var states = new List<PlayerPhase>();
        player.StateChanged += s => { lock (states) states.Add(s.Phase); };
        var result = await player.PlayAsync(Ready(clip), Ct);
        Assert.Equal(PlaybackStatus.Completed, result.Status);
        Assert.Equal(1, clip.Disposals);
        Assert.Equal(["a"], sink.Played);
        Assert.Equal([PlayerPhase.Loading, PlayerPhase.Playing, PlayerPhase.Idle], states);
        Assert.Equal(PlayerPhase.Idle, player.State.Phase);
    }

    [Fact] // a new playback stops the old one; only one clip is ever in the output; both leases released
    public async Task New_playback_supersedes_the_old_one_without_overlap()
    {
        var sink = new FakeSink();
        var player = new SpeechPlayer(sink);
        var first = new FakeClip("first");
        var second = new FakeClip("second");
        var one = player.PlayAsync(Ready(first), Ct);
        Assert.True(await sink.Started.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var two = player.PlayAsync(Ready(second), Ct);
        Assert.Equal(PlaybackStatus.Superseded, (await one).Status);
        Assert.True(await sink.Started.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        sink.Finish("second");
        Assert.Equal(PlaybackStatus.Completed, (await two).Status);
        Assert.Equal(1, sink.Peak);
        Assert.Equal(["first", "second"], sink.Played);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public async Task Stop_ends_playback_and_releases_the_lease()
    {
        var sink = new FakeSink();
        var player = new SpeechPlayer(sink);
        var clip = new FakeClip("a");
        var task = player.PlayAsync(Ready(clip), Ct);
        Assert.True(await sink.Started.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(PlayerPhase.Playing, player.State.Phase);
        player.Stop();
        Assert.Equal(PlaybackStatus.Stopped, (await task).Status);
        Assert.Equal(1, clip.Disposals);
        Assert.Equal(PlayerPhase.Idle, player.State.Phase);
        player.Stop(); // idle: no-op
    }

    [Fact] // B07: stop during synthesis/download cancels the acquisition; a clip that still arrives is released, never played
    public async Task Stop_during_loading_cancels_it_and_late_audio_never_plays()
    {
        var sink = new FakeSink();
        var player = new SpeechPlayer(sink);
        var late = new FakeClip("late");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sawCancel = false;
        var task = player.PlayAsync(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { sawCancel = true; }
            return new AudioOutcome.Ready(late); // a provider that ignores the cancel and still hands over a clip
        }, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(PlayerPhase.Loading, player.State.Phase);
        player.Stop();
        Assert.Equal(PlaybackStatus.Stopped, (await task).Status);
        Assert.True(sawCancel);
        Assert.Empty(sink.Played);
        Assert.Equal(1, late.Disposals);
    }

    [Fact] // TTS02: an older, slower synthesis that finishes after a newer request never plays
    public async Task Slow_old_synthesis_finishing_late_is_dropped()
    {
        var sink = new FakeSink { Instant = true };
        var player = new SpeechPlayer(sink);
        var old = new FakeClip("old");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = player.PlayAsync(async _ => { await release.Task; return new AudioOutcome.Ready(old); }, Ct); // ignores its token
        var fresh = new FakeClip("new");
        var second = await player.PlayAsync(Ready(fresh), Ct);
        release.SetResult();
        Assert.Equal(PlaybackStatus.Superseded, (await first).Status);
        Assert.Equal(PlaybackStatus.Completed, second.Status);
        Assert.Equal(["new"], sink.Played);
        Assert.Equal(1, old.Disposals);
        Assert.Equal(1, fresh.Disposals);
    }

    [Fact]
    public async Task Service_failure_is_reported_with_its_class()
    {
        var player = new SpeechPlayer(new FakeSink());
        var result = await player.PlayAsync(_ => Task.FromResult<AudioOutcome>(new AudioOutcome.Failure(new ProviderError(ErrorKind.RateLimited, "429", TimeSpan.FromSeconds(3)))), Ct);
        Assert.Equal(PlaybackStatus.Failed, result.Status);
        Assert.Equal(ErrorKind.RateLimited, result.Error!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Error.RetryAfter);
        Assert.Null(result.Device);
    }

    [Theory] // no output device (this VM may have none) or device lost: classified, lease still released
    [InlineData(AudioFailure.NoDevice)]
    [InlineData(AudioFailure.DeviceLost)]
    [InlineData(AudioFailure.Unsupported)]
    public async Task Device_failure_is_classified_and_the_lease_released(AudioFailure failure)
    {
        var sink = new FakeSink { FailWith = failure };
        var player = new SpeechPlayer(sink);
        var clip = new FakeClip("a");
        var result = await player.PlayAsync(Ready(clip), Ct);
        Assert.Equal(PlaybackStatus.Failed, result.Status);
        Assert.Equal(failure, result.Device);
        Assert.Equal(1, clip.Disposals);
        Assert.False(player.Device.Available);
    }

    [Fact] // SpeakAsync: the provider gets the request, and the caller's cancel stops everything
    public async Task Speak_passes_the_request_and_caller_cancel_stops_it()
    {
        var sink = new FakeSink();
        var player = new SpeechPlayer(sink);
        var provider = new RecordingProvider(new FakeClip("tts"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var task = player.SpeakAsync(provider, new SpeakRequest("hello", "en", "voice-1", 1.5), TimeSpan.FromSeconds(10), cts.Token);
        Assert.True(await sink.Started.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("hello", provider.Last!.Request.Text);
        Assert.Equal("voice-1", provider.Last.Request.Voice);
        Assert.Equal(TimeSpan.FromSeconds(10), provider.Last.Timeout);
        cts.Cancel();
        Assert.Equal(PlaybackStatus.Stopped, (await task).Status);
        Assert.Equal(1, provider.Clip.Disposals);
    }

    [Fact] // many rapid requests: at most one clip in the output, every lease released, only the last completes
    public async Task Rapid_requests_keep_one_player_and_release_every_lease()
    {
        var sink = new FakeSink { Instant = true };
        var player = new SpeechPlayer(sink);
        var clips = Enumerable.Range(0, 20).Select(i => new FakeClip($"c{i}")).ToList();
        var tasks = clips.Select(c => player.PlayAsync(async token => { await Task.Delay(5, CancellationToken.None); return new AudioOutcome.Ready(c); }, Ct)).ToList();
        var results = await Task.WhenAll(tasks);
        await player.IdleAsync();
        Assert.Equal(1, sink.Peak);
        Assert.All(clips, c => Assert.Equal(1, c.Disposals));
        Assert.Equal(PlaybackStatus.Completed, results[^1].Status);
        Assert.Equal("c19", sink.Played[^1]);
    }

    [Fact] // the real lease store: the file is deleted when the player lets go
    public async Task Leased_clip_file_is_deleted_after_playback()
    {
        using var leases = new FileLeases(TestTemp.NewDir("susu-player-leases"));
        var files = new LeasedAudioFiles(leases);
        var clip = (LeasedAudioClip)files.Create("audio/wav", "wav");
        string path = clip.FilePath;
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);
        Assert.Equal(3, clip.Bytes);
        var player = new SpeechPlayer(new FakeSinkAcceptingAny());
        var result = await player.PlayAsync(_ => Task.FromResult<AudioOutcome>(new AudioOutcome.Ready(clip)), Ct);
        Assert.Equal(PlaybackStatus.Completed, result.Status);
        Assert.True(clip.Released);
        Assert.False(File.Exists(path));
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact] // the request carries the instance's configured voice and speed; the selected service is used; a missing one is unavailable
    public async Task Backend_uses_the_selected_service_with_its_configured_voice_and_rate()
    {
        var defaults = Susu.Domain.BuiltInCatalog.Defaults();
        var settings = defaults with
        {
            Instances = [.. defaults.Instances.Select(i => i.Id == "native-sapi" ? i with { Config = new Dictionary<string, string> { ["voice"] = "TTS_X", ["rate"] = "1.5" } } : i)],
        };
        var request = SpeechBackend.RequestFor(settings, "native-sapi", "hello", "en");
        Assert.Equal(new SpeakRequest("hello", "en", "TTS_X", 1.5), request);
        Assert.Equal(new SpeakRequest("hi", null, null, 1.0), SpeechBackend.RequestFor(settings, "microsoft-tts", "hi", null));
        Assert.Equal(2.0, new SpeakRequest("x", Rate: 9).ClampedRate);

        var provider = new RecordingProvider(new FakeClip("sapi"));
        var backend = new SpeechBackend(new SpeechPlayer(new FakeSink { Instant = true }), (_, id) => id == "native-sapi" ? provider : null);
        Assert.Equal(PlaybackStatus.Completed, (await backend.SpeakSelectedAsync(settings, "hello", "en", TimeSpan.FromSeconds(5), Ct)).Status);
        Assert.Equal("TTS_X", provider.Last!.Request.Voice);
        var none = settings with { Speech = settings.Speech with { Tts = new Susu.Domain.SpeechSelection("google-tts", "") } };
        var result = await backend.SpeakSelectedAsync(none, "hello", "en", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(PlaybackStatus.Failed, result.Status);
        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
    }

    [Fact] // TTS02 device lost mid-playback: classified, lease released, player idle, and the next clip plays normally
    public async Task Device_lost_mid_playback_is_classified_and_the_player_recovers()
    {
        var sink = new LosingSink();
        var player = new SpeechPlayer(sink);
        var first = new FakeClip("first");
        var result = await player.PlayAsync(Ready(first), Ct);
        Assert.Equal(PlaybackStatus.Failed, result.Status);
        Assert.Equal(AudioFailure.DeviceLost, result.Device);
        Assert.Equal(ErrorKind.Unavailable, result.Error!.Kind);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(PlayerPhase.Idle, player.State.Phase);
        var second = new FakeClip("second");
        Assert.Equal(PlaybackStatus.Completed, (await player.PlayAsync(Ready(second), Ct)).Status);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(2, sink.Calls);
    }

    [Fact] // TTS01: a hung cloud synthesis (it even blocks its thread and ignores cancel) never blocks the caller or native SAPI
    public async Task Hung_cloud_tts_never_blocks_the_caller_or_native_speech()
    {
        var sink = new FakeSink { Instant = true };
        var cloud = new HungCloudProvider();
        var sapi = new RecordingProvider(new FakeClip("sapi"));
        var backend = new SpeechBackend(new SpeechPlayer(sink), (_, id) => id == "native-sapi" ? sapi : cloud);
        var settings = Susu.Domain.BuiltInCatalog.Defaults();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hung = backend.SpeakWithAsync(settings, "microsoft-tts", "hello", "en", TimeSpan.FromSeconds(30), Ct);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the cloud request held the caller for {watch.Elapsed}");
        Assert.True(cloud.Entered.Wait(TimeSpan.FromSeconds(10), Ct));
        var local = await backend.SpeakWithAsync(settings, "native-sapi", "hello", "en", TimeSpan.FromSeconds(30), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(PlaybackStatus.Completed, local.Status);
        Assert.Equal(["sapi"], sink.Played);
        cloud.Release.Set(); // the hung call finally returns a clip: it is superseded and released, never played
        Assert.Equal(PlaybackStatus.Superseded, (await hung.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Status);
        Assert.Equal(["sapi"], sink.Played);
        Assert.Equal(1, cloud.Clip.Disposals);

        // A cloud failure (plugin host crashed or stopped) is reported with its class and SAPI still plays afterwards.
        var failing = new SpeechBackend(new SpeechPlayer(sink), (_, id) => id == "native-sapi" ? sapi : new FailingProvider());
        var failed = await failing.SpeakWithAsync(settings, "microsoft-tts", "hello", "en", TimeSpan.FromSeconds(30), Ct);
        Assert.Equal((PlaybackStatus.Failed, ErrorKind.Unavailable), (failed.Status, failed.Error!.Kind));
        Assert.Equal(PlaybackStatus.Completed, (await failing.SpeakWithAsync(settings, "native-sapi", "hello", "en", TimeSpan.FromSeconds(30), Ct)).Status);
    }

    private sealed class LosingSink : IAudioSink
    {
        public int Calls;
        public AudioDeviceStatus Probe() => new(true);
        public async Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) > 1) return;
            await Task.Delay(20, cancellationToken); // playing ...
            throw new AudioPlaybackException(AudioFailure.DeviceLost, "IAudioClient.GetCurrentPadding 0x88890004");
        }
    }

    private sealed class HungCloudProvider : ITtsProvider
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public readonly FakeClip Clip = new("cloud");
        public string InstanceId => "microsoft-tts";
        public bool Native => false;
        public Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30)); // synchronous: like a blocked pipe write to a hung plugin host
            return Task.FromResult<AudioOutcome>(new AudioOutcome.Ready(Clip));
        }
    }

    private sealed class FailingProvider : ITtsProvider
    {
        public string InstanceId => "microsoft-tts";
        public bool Native => false;
        public Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken)
            => Task.FromResult<AudioOutcome>(new AudioOutcome.Failure(new ProviderError(ErrorKind.Unavailable, "plugin host stopped after repeated crashes")));
    }

    private sealed class FakeSinkAcceptingAny : IAudioSink
    {
        public AudioDeviceStatus Probe() => new(true);
        public Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingProvider(FakeClip clip) : ITtsProvider
    {
        public FakeClip Clip { get; } = clip;
        public SpeakCall? Last;
        public string InstanceId => "fake";
        public bool Native => true;
        public Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken) { Last = call; return Task.FromResult<AudioOutcome>(new AudioOutcome.Ready(Clip)); }
    }
}
