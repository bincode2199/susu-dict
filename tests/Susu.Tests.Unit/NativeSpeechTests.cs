using Susu.Abstractions;
using Susu.Contracts;
using Susu.Jobs;
using Susu.Storage;
using Susu.Windows.Audio;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F10.1 native speech on the real OS (TTS01 SAPI part, B07 cancel cleanup): SAPI voices and synthesis into a leased
/// WAV file with no network and no plugin process, Media Foundation decoding, and the WASAPI output with classified
/// device errors. Tests needing an installed SAPI voice skip with the reason when there is none; playback tests accept
/// either a real device (plays, stops) or the classified NoDevice failure, and print which one this machine has.
/// </summary>
public class NativeSpeechTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<Voice>> VoicesOrSkip()
    {
        var voices = await SapiTtsProvider.VoicesAsync();
        if (voices.Count == 0) Assert.Skip("no SAPI voice is installed on this machine");
        return voices;
    }

    private static SpeakCall Call(string text, string? lang = "en", string? voice = null, double rate = 1.0)
        => new(new SpeakRequest(text, lang, voice, rate), "native-test", TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Lists_installed_voices_with_languages()
    {
        var voices = await VoicesOrSkip();
        Assert.All(voices, v => { Assert.False(string.IsNullOrEmpty(v.Id)); Assert.DoesNotContain('\\', v.Id); Assert.False(string.IsNullOrEmpty(v.Name)); });
        Assert.Contains(voices, v => v.Lang.Length > 0);
        TestContext.Current.TestOutputHelper?.WriteLine($"SAPI voices: {string.Join(", ", voices.Select(v => $"{v.Id} ({v.Lang})"))}");
    }

    [Fact] // TTS01: offline synthesis into a leased WAV; rate changes the length; the lease owns the file
    public async Task Synthesizes_a_wav_into_a_lease_and_rate_changes_length()
    {
        var voices = await VoicesOrSkip();
        string lang = voices.First(v => v.Lang.Length > 0).Lang;
        using var leases = new FileLeases(TestTemp.NewDir("susu-sapi"));
        var sapi = new SapiTtsProvider(new LeasedAudioFiles(leases));
        Assert.True(sapi.Native);
        Assert.Equal("native-sapi", sapi.InstanceId);

        var slow = Assert.IsType<AudioOutcome.Ready>(await sapi.SynthesizeAsync(Call("Pronunciation test sentence number one.", lang, rate: 0.5), Ct));
        var fast = Assert.IsType<AudioOutcome.Ready>(await sapi.SynthesizeAsync(Call("Pronunciation test sentence number one.", lang, rate: 2.0), Ct));
        Assert.Equal("audio/wav", slow.Clip.Mime);
        byte[] head = (await File.ReadAllBytesAsync(slow.Clip.FilePath, Ct))[..12];
        Assert.Equal("RIFF"u8.ToArray(), head[..4]);
        Assert.Equal("WAVE"u8.ToArray(), head[8..12]);
        Assert.True(fast.Clip.Bytes > 1000);
        Assert.True(slow.Clip.Bytes > fast.Clip.Bytes * 2, $"slow {slow.Clip.Bytes} vs fast {fast.Clip.Bytes}");
        Assert.Equal(2, leases.ActiveCount);

        // Media Foundation decodes the SAPI output to PCM (22.05 kHz mono 16-bit).
        var (pcm, format) = WasapiAudioSink.Decode(slow.Clip.FilePath, Ct);
        Assert.True(pcm.Length > 1000);
        Assert.Equal(22050, BitConverter.ToInt32(format, 4));
        Assert.Equal(1, BitConverter.ToUInt16(format, 2));

        string path = slow.Clip.FilePath;
        slow.Clip.Dispose();
        fast.Clip.Dispose();
        Assert.False(File.Exists(path));
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact] // F10.3: warm-up speaks once into a discarded lease (no file left), reports phase timings, and runs only once
    public async Task Warm_up_runs_once_leaves_no_file_and_reports_phase_timings()
    {
        await VoicesOrSkip();
        using var leases = new FileLeases(TestTemp.NewDir("susu-sapi-warm"));
        var sapi = new SapiTtsProvider(new LeasedAudioFiles(leases));
        var timings = new List<SapiTiming>();
        sapi.Timed += t => { lock (timings) timings.Add(t); };
        await sapi.WarmUpAsync();
        await sapi.WarmUpAsync();
        var timing = Assert.Single(timings);
        Assert.True(timing.VoiceMs >= 0 && timing.SetupMs >= 0 && timing.SpeakMs > 0, timing.ToString());
        Assert.Equal(0, leases.ActiveCount);
        var ready = Assert.IsType<AudioOutcome.Ready>(await sapi.SynthesizeAsync(Call("warm"), Ct));
        ready.Clip.Dispose();
        Assert.Equal(2, timings.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"SAPI warm-up {timings[0]}, next {timings[1]}");
    }

    [Fact] // an explicit voice id is used; an unknown voice or a language no voice speaks is classified, no file left
    public async Task Voice_selection_and_classified_refusals()
    {
        var voices = await VoicesOrSkip();
        using var leases = new FileLeases(TestTemp.NewDir("susu-sapi"));
        var sapi = new SapiTtsProvider(new LeasedAudioFiles(leases));
        var chosen = Assert.IsType<AudioOutcome.Ready>(await sapi.SynthesizeAsync(Call("hello", null, voices[^1].Id), Ct));
        chosen.Clip.Dispose();

        var unknown = Assert.IsType<AudioOutcome.Failure>(await sapi.SynthesizeAsync(Call("hello", "en", "TTS_NOT_INSTALLED_VOICE"), Ct));
        Assert.Equal(ErrorKind.BadResponse, unknown.Error.Kind);

        string? missing = new[] { "sw", "is", "cy", "mt" }.FirstOrDefault(l => voices.All(v => !v.Lang.StartsWith(l, StringComparison.OrdinalIgnoreCase)));
        if (missing is not null)
        {
            var refused = Assert.IsType<AudioOutcome.Failure>(await sapi.SynthesizeAsync(Call("habari", missing), Ct));
            Assert.Equal(ErrorKind.UnsupportedLanguage, refused.Error.Kind);
        }
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AudioOutcome.Failure>(await sapi.SynthesizeAsync(Call("   "), Ct)).Error.Kind);
        Assert.Equal(ErrorKind.BadResponse, Assert.IsType<AudioOutcome.Failure>(await sapi.SynthesizeAsync(Call(new string('a', SapiTtsProvider.MaxChars + 1)), Ct)).Error.Kind);
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact] // B07: cancelling a long synthesis stops it promptly and deletes the partial file
    public async Task Cancel_during_synthesis_deletes_the_partial_file()
    {
        var voices = await VoicesOrSkip();
        string dir = TestTemp.NewDir("susu-sapi");
        using var leases = new FileLeases(dir);
        var sapi = new SapiTtsProvider(new LeasedAudioFiles(leases));
        string text = string.Join(" ", Enumerable.Repeat("This is a long sentence that keeps the synthesizer busy for a while.", 60));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(150);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sapi.SynthesizeAsync(Call(text, voices[0].Lang), cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"cancel took {watch.Elapsed}");
        Assert.Equal(0, leases.ActiveCount);
        Assert.Empty(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
    }

    [Fact] // timeout is its own class, not a cancel
    public async Task Synthesis_timeout_is_classified()
    {
        var voices = await VoicesOrSkip();
        using var leases = new FileLeases(TestTemp.NewDir("susu-sapi"));
        var sapi = new SapiTtsProvider(new LeasedAudioFiles(leases));
        string text = string.Join(" ", Enumerable.Repeat("A long sentence for the timeout check.", 80));
        var outcome = await sapi.SynthesizeAsync(new SpeakCall(new SpeakRequest(text, voices[0].Lang), "t", TimeSpan.FromMilliseconds(100)), Ct);
        Assert.Equal(ErrorKind.Timeout, Assert.IsType<AudioOutcome.Failure>(outcome).Error.Kind);
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public void Rate_and_language_mapping()
    {
        Assert.Equal(0, SapiTtsProvider.SapiRate(1.0));
        Assert.Equal(6, SapiTtsProvider.SapiRate(2.0));
        Assert.Equal(-6, SapiTtsProvider.SapiRate(0.5));
        Assert.Equal(6, SapiTtsProvider.SapiRate(9)); // clamped to 2x
        Assert.Equal("en-US", SapiTtsProvider.LanguageFromLcid("409;9"));
        Assert.Equal("zh-CN", SapiTtsProvider.LanguageFromLcid("804"));
        Assert.Equal("", SapiTtsProvider.LanguageFromLcid("zz"));
        Assert.Equal(2, SapiTtsProvider.LanguageScore("zh-Hans", "zh-CN"));
        Assert.Equal(1, SapiTtsProvider.LanguageScore("zh-Hans", "zh-TW"));
        Assert.Equal(0, SapiTtsProvider.LanguageScore("ja", "en-US"));
        Assert.Equal(3, SapiTtsProvider.LanguageScore("en-GB", "en-GB"));
    }

    [Theory] // TTS02 device errors: WASAPI HRESULTs map to the classes the bar shows (a real device can't be unplugged here)
    [InlineData(unchecked((int)0x88890004), AudioFailure.DeviceLost)]   // AUDCLNT_E_DEVICE_INVALIDATED: unplugged, disabled, default changed
    [InlineData(unchecked((int)0x88890026), AudioFailure.DeviceLost)]   // AUDCLNT_E_RESOURCES_INVALIDATED
    [InlineData(unchecked((int)0x80070490), AudioFailure.NoDevice)]     // E_NOTFOUND: no default render endpoint
    [InlineData(unchecked((int)0x88890010), AudioFailure.NoDevice)]     // AUDCLNT_E_SERVICE_NOT_RUNNING
    [InlineData(unchecked((int)0x8889000F), AudioFailure.NoDevice)]     // AUDCLNT_E_ENDPOINT_CREATE_FAILED
    [InlineData(unchecked((int)0x88890008), AudioFailure.Unsupported)]  // AUDCLNT_E_UNSUPPORTED_FORMAT
    [InlineData(unchecked((int)0x8889000A), AudioFailure.Failed)]       // AUDCLNT_E_DEVICE_IN_USE
    public void Wasapi_errors_are_classified(int hr, AudioFailure expected) => Assert.Equal(expected, WasapiAudioSink.Classify(hr));

    [Fact] // a JSON error body saved as audio is never "played": the decoder refuses it
    public void Decoder_refuses_non_audio()
    {
        string path = Path.Combine(TestTemp.NewDir("susu-decode"), "error.mp3");
        File.WriteAllText(path, "{\"error\":{\"code\":401,\"message\":\"bad key\"}}");
        var error = Assert.Throws<AudioPlaybackException>(() => WasapiAudioSink.Decode(path, Ct));
        Assert.Equal(AudioFailure.Unsupported, error.Failure);
    }

    [Fact] // TTS01 end to end on this machine: SAPI -> single player -> WASAPI; plays and stops, or fails with NoDevice
    public async Task Real_output_plays_and_stops_or_reports_no_device()
    {
        var voices = await VoicesOrSkip();
        using var leases = new FileLeases(TestTemp.NewDir("susu-sapi"));
        var sapi = new SapiTtsProvider(new LeasedAudioFiles(leases));
        var sink = new WasapiAudioSink();
        var device = sink.Probe();
        TestContext.Current.TestOutputHelper?.WriteLine($"audio output: available={device.Available} failure={device.Failure} detail={device.Detail}");
        var player = new SpeechPlayer(sink);

        var shortResult = await player.SpeakAsync(sapi, new SpeakRequest("Hi.", voices[0].Lang, Rate: 2.0), TimeSpan.FromSeconds(30), Ct);
        if (device.Available) Assert.Equal(PlaybackStatus.Completed, shortResult.Status);
        else
        {
            Assert.Equal(PlaybackStatus.Failed, shortResult.Status);
            Assert.NotNull(shortResult.Device);
        }

        string text = string.Join(" ", Enumerable.Repeat("This sentence is long enough to be stopped.", 10));
        var longPlay = player.SpeakAsync(sapi, new SpeakRequest(text, voices[0].Lang), TimeSpan.FromSeconds(30), Ct);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (player.State.Phase != PlayerPhase.Playing && !longPlay.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        await Task.Delay(300, Ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        player.Stop();
        var longResult = await longPlay;
        if (device.Available)
        {
            Assert.Equal(PlaybackStatus.Stopped, longResult.Status);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"stop took {watch.Elapsed}");
        }
        else Assert.Equal(PlaybackStatus.Failed, longResult.Status);
        await player.IdleAsync();
        Assert.Equal(0, leases.ActiveCount);
    }
}
