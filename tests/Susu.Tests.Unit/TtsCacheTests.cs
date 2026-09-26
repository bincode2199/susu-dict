using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F10.3 TTS audio cache (ARCHITECTURE 8.4, TTS02 "缓存按配置区分") with the real lease store: a replay reuses the leased
/// clip; any change of instance, voice, language, speed, text, config revision/value, account binding or credential misses
/// and drops the stale entries; 32 MiB / 7 day / entry bounds; the key carries no text; every file goes when the last owner
/// (player or cache) lets go.
/// </summary>
public class TtsCacheTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FileLeases leases = new(TestTemp.NewDir("susu-ttscache"));
    private readonly ManualClock clock = new();

    public void Dispose() => leases.Dispose();

    /// <summary>Writes a leased WAV-ish file of <paramref name="size"/> bytes per call and counts calls.</summary>
    private sealed class LeaseProvider(FileLeases leases, string instanceId, int size = 100) : ITtsProvider
    {
        public int Calls;
        public bool FailNext;
        public string InstanceId => instanceId;
        public bool Native => instanceId == BuiltInCatalog.NativeTts;
        public async Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (FailNext) { FailNext = false; return new AudioOutcome.Failure(new ProviderError(ErrorKind.Network, "boom")); }
            var clip = new LeasedAudioFiles(leases).Create("audio/wav", "wav");
            await File.WriteAllBytesAsync(clip.FilePath, new byte[size], cancellationToken);
            return new AudioOutcome.Ready(clip);
        }
    }

    private sealed class RecordingSink : IAudioSink
    {
        public readonly List<string> Paths = [];
        public AudioDeviceStatus Probe() => new(true);
        public Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(clip.FilePath));
            lock (Paths) Paths.Add(clip.FilePath);
            return Task.CompletedTask;
        }
    }

    private static AppSettings Settings(string voice = "", string rate = "1", long revision = 1, string account = "a1")
    {
        var d = BuiltInCatalog.Defaults();
        return d with
        {
            Instances = [.. d.Instances.Select(i => i.Id switch
            {
                "native-sapi" => i with { Revision = revision, Config = new Dictionary<string, string> { ["voice"] = voice, ["rate"] = rate } },
                "microsoft-tts" => i with { AccountBindings = new Dictionary<string, string> { ["apiKey"] = account } },
                _ => i,
            })],
        };
    }

    private (SpeechBackend Backend, LeaseProvider Sapi, LeaseProvider Cloud, TtsCache Cache, RecordingSink Sink) Rig(TtsCacheLimits? limits = null, int size = 100)
    {
        var cache = new TtsCache(clock, limits);
        var sapi = new LeaseProvider(leases, BuiltInCatalog.NativeTts, size);
        var cloud = new LeaseProvider(leases, "microsoft-tts", size);
        var sink = new RecordingSink();
        var backend = new SpeechBackend(new SpeechPlayer(sink), (_, id) => id == BuiltInCatalog.NativeTts ? sapi : id == "microsoft-tts" ? cloud : null, null, cache);
        return (backend, sapi, cloud, cache, sink);
    }

    private static async Task<PlaybackStatus> Say(SpeechBackend backend, AppSettings s, string text, string? lang = "en", string instance = BuiltInCatalog.NativeTts)
        => (await backend.SpeakWithAsync(s, instance, text, lang, TimeSpan.FromSeconds(10), Ct)).Status;

    [Fact] // a replay plays the same leased file without a second synthesis; the file goes once the cache lets go too
    public async Task Replay_reuses_the_leased_clip_and_the_file_goes_with_the_last_owner()
    {
        var (backend, sapi, _, cache, sink) = Rig();
        var s = Settings();
        Assert.Equal(PlaybackStatus.Completed, await Say(backend, s, "hello"));
        Assert.Equal(PlaybackStatus.Completed, await Say(backend, s, "hello"));
        Assert.Equal(1, sapi.Calls);
        Assert.Equal(2, sink.Paths.Count);
        Assert.Equal(sink.Paths[0], sink.Paths[1]);
        Assert.Equal(1, cache.Count);
        Assert.Equal(100, cache.Bytes);
        Assert.Equal(1, leases.ActiveCount); // only the cache's reference is left after playback
        Assert.True(File.Exists(sink.Paths[0]));
        cache.Clear(); // the data-cleanup entry
        Assert.Equal(0, leases.ActiveCount);
        Assert.False(File.Exists(sink.Paths[0]));
        Assert.Equal(PlaybackStatus.Completed, await Say(backend, s, "hello"));
        Assert.Equal(2, sapi.Calls);
    }

    [Fact] // TTS02: every part of the request is in the key
    public async Task Text_voice_language_speed_and_service_each_miss()
    {
        var (backend, sapi, cloud, cache, _) = Rig();
        var s = Settings();
        await Say(backend, s, "hello");
        await Say(backend, s, "hello!");
        await Say(backend, s, "hello", "en-GB");
        await Say(backend, s, "hello", null);
        Assert.Equal(4, sapi.Calls);
        await Say(backend, Settings(voice: "TTS_ZIRA"), "hello"); // voice change (new config): misses and drops the old entries
        Assert.Equal(5, sapi.Calls);
        Assert.Equal(1, cache.Count);
        await Say(backend, Settings(voice: "TTS_ZIRA", rate: "1.5"), "hello");
        Assert.Equal(6, sapi.Calls);
        await Say(backend, Settings(voice: "TTS_ZIRA", rate: "1.5"), "hello", instance: "microsoft-tts");
        Assert.Equal(1, cloud.Calls);
        Assert.Equal(2, cache.Count);
        await Say(backend, Settings(voice: "TTS_ZIRA", rate: "1.5"), "hello");
        await Say(backend, Settings(voice: "TTS_ZIRA", rate: "1.5"), "hello", instance: "microsoft-tts");
        Assert.Equal(6, sapi.Calls);
        Assert.Equal(1, cloud.Calls);
    }

    [Fact] // a settings change (config revision, account binding) or a credential write drops the instance's cached audio
    public async Task Config_revision_account_and_credential_changes_invalidate()
    {
        var (backend, sapi, cloud, cache, _) = Rig();
        var s = Settings();
        await Say(backend, s, "hello");
        await Say(backend, s, "hello", instance: "microsoft-tts");
        Assert.Equal(2, cache.Count);

        cache.Retain(Settings(revision: 2)); // SAPI config revision moved: its entry goes, the cloud one stays
        Assert.Equal(1, cache.Count);
        await Say(backend, Settings(revision: 2), "hello");
        Assert.Equal(2, sapi.Calls);

        cache.Retain(Settings(revision: 2, account: "a2")); // the cloud instance is bound to another account
        Assert.Equal(1, cache.Count);
        var rebound = Settings(revision: 2, account: "a2");
        await Say(backend, rebound, "hello", instance: "microsoft-tts");
        Assert.Equal(2, cloud.Calls);

        cache.Invalidate("microsoft-tts"); // a key was written for its account
        Assert.Equal(1, cache.Count);
        await Say(backend, rebound, "hello", instance: "microsoft-tts");
        Assert.Equal(3, cloud.Calls);
        await Say(backend, rebound, "hello", instance: "microsoft-tts");
        Assert.Equal(3, cloud.Calls);

        var gone = rebound with { Instances = [.. rebound.Instances.Where(i => i.Id != "microsoft-tts")] };
        cache.Retain(gone); // the instance was removed
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact] // ARCHITECTURE 8.4 bounds: bytes (least recently used goes first), age, entry count; an oversize clip is not kept
    public async Task Bounds_evict_least_recently_used_expire_by_age_and_skip_oversize()
    {
        var (backend, sapi, _, cache, _) = Rig(new TtsCacheLimits(250, TimeSpan.FromDays(7), 10));
        var s = Settings();
        await Say(backend, s, "one");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Say(backend, s, "two");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Say(backend, s, "one"); // touch "one": "two" is now the least recently used
        clock.Advance(TimeSpan.FromSeconds(1));
        await Say(backend, s, "three"); // 300 > 250: "two" goes
        Assert.Equal(2, cache.Count);
        Assert.Equal(200, cache.Bytes);
        Assert.Equal(3, sapi.Calls);
        await Say(backend, s, "one");
        Assert.Equal(3, sapi.Calls);
        await Say(backend, s, "two");
        Assert.Equal(4, sapi.Calls);

        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(10)); // everything is older than 7 days
        await Say(backend, s, "one");
        Assert.Equal(5, sapi.Calls);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, leases.ActiveCount);

        var (big, bigSapi, _, bigCache, _) = Rig(new TtsCacheLimits(50, TimeSpan.FromDays(7), 10));
        await Say(big, s, "large");
        await Say(big, s, "large");
        Assert.Equal(2, bigSapi.Calls);
        Assert.Equal(0, bigCache.Count);

        var (few, fewSapi, _, fewCache, _) = Rig(new TtsCacheLimits(1 << 20, TimeSpan.FromDays(7), 2));
        foreach (var word in new[] { "a", "b", "c" }) { await Say(few, s, word); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(2, fewCache.Count);
        cache.Clear(); fewCache.Clear();
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact] // failures and non-shareable clips are never cached; the key is a hash (no text); limits follow ARCHITECTURE 8.4
    public async Task Failures_are_not_cached_and_the_key_carries_no_text()
    {
        var (backend, sapi, _, cache, _) = Rig();
        var s = Settings();
        sapi.FailNext = true;
        var failed = await backend.SpeakWithAsync(s, BuiltInCatalog.NativeTts, "secret words", "en", TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(PlaybackStatus.Failed, failed.Status);
        Assert.Equal(0, cache.Count);
        await Say(backend, s, "secret words");
        Assert.Equal(2, sapi.Calls);
        Assert.Equal(1, cache.Count);

        string fingerprint = cache.FingerprintOf(s, BuiltInCatalog.NativeTts)!;
        string key = TtsCache.KeyFor(BuiltInCatalog.NativeTts, fingerprint, new SpeakRequest("secret words", "en"));
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.NotEqual(key, TtsCache.KeyFor(BuiltInCatalog.NativeTts, fingerprint, new SpeakRequest("secret words", "en", Rate: 1.25)));
        Assert.Equal(key, TtsCache.KeyFor(BuiltInCatalog.NativeTts, fingerprint, new SpeakRequest("secret words", "en", Rate: 1.0)));
        Assert.Null(cache.FingerprintOf(s, "no-such-instance"));

        cache.Add(s, BuiltInCatalog.NativeTts, new SpeakRequest("plain"), new PlainClip());
        Assert.Equal(1, cache.Count);
        Assert.Equal(32L << 20, TtsCacheLimits.Default.MaxBytes);
        Assert.Equal(TimeSpan.FromDays(7), TtsCacheLimits.Default.MaxAge);
        cache.Dispose();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, leases.ActiveCount);
        Assert.Null(cache.TryGet(s, BuiltInCatalog.NativeTts, new SpeakRequest("secret words", "en")));
    }

    [Fact] // clearing while a cached clip plays: the playing file stays until the player lets go, then it goes
    public async Task Clear_during_playback_keeps_the_playing_file_until_the_player_releases_it()
    {
        var cache = new TtsCache(clock);
        var sapi = new LeaseProvider(leases, BuiltInCatalog.NativeTts);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new BlockingSink(gate, started);
        var backend = new SpeechBackend(new SpeechPlayer(sink), (_, _) => sapi, null, cache);
        var s = Settings();
        var task = backend.SpeakWithAsync(s, BuiltInCatalog.NativeTts, "hello", "en", TimeSpan.FromSeconds(10), Ct);
        string path = await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cache.Clear();
        Assert.True(File.Exists(path));
        gate.SetResult();
        Assert.Equal(PlaybackStatus.Completed, (await task).Status);
        Assert.False(File.Exists(path));
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact] // the lease store side: a shared reference keeps the file; releasing both deletes it; a released clip cannot be shared
    public async Task Leased_clip_share_keeps_the_file_until_every_owner_released()
    {
        var clip = (LeasedAudioClip)new LeasedAudioFiles(leases).Create("audio/wav", "wav");
        await File.WriteAllBytesAsync(clip.FilePath, [1, 2, 3], Ct);
        string path = clip.FilePath;
        var shared = clip.Share();
        Assert.NotNull(shared);
        clip.Dispose();
        Assert.True(File.Exists(path));
        Assert.Equal(3, shared.Bytes);
        Assert.Null(clip.Share());
        shared.Dispose();
        shared.Dispose();
        Assert.False(File.Exists(path));
        Assert.Equal(0, leases.ActiveCount);
    }

    private sealed class PlainClip : IAudioClip
    {
        public string Mime => "audio/wav";
        public string FilePath => "fake://plain";
        public long Bytes => 10;
        public void Dispose() { }
    }

    private sealed class BlockingSink(TaskCompletionSource gate, TaskCompletionSource<string> started) : IAudioSink
    {
        public AudioDeviceStatus Probe() => new(true);
        public async Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken)
        {
            started.TrySetResult(clip.FilePath);
            await gate.Task.WaitAsync(cancellationToken);
        }
    }
}
