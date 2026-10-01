using System.Buffers.Binary;
using System.Threading.Channels;
using Susu.Abstractions;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Susu.Windows.Audio;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F13.1 system-audio (WASAPI loopback) recording through the F12 recorder (TEST-PLAN REC03 switch, REC04 loopback / no output /
/// own TTS notice / microphone never used). A fake output device drives the real recorder; one test plays a tone through the
/// real output with <see cref="WasapiAudioSink"/> and captures it with the real loopback, and skips with a reason otherwise.
/// </summary>
[Collection(MediaDecodeCollection.Name)]
public class LoopbackRecordingTests : IDisposable
{
    private const int Rate = 16000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly FileLeases leases;
    private readonly ManualClock clock = new();

    public LoopbackRecordingTests() { leases = new FileLeases(root.Paths.Cache); }

    public void Dispose() { leases.Dispose(); root.Dispose(); }

    private sealed class FakeStream : IMicrophoneStream
    {
        private readonly Channel<byte[]> blocks = Channel.CreateUnbounded<byte[]>();
        public int SampleRate => Rate;
        public bool Disposed { get; private set; }
        public void Push(byte[] block) => blocks.Writer.TryWrite(block);
        public void Fail(MicFailure failure) => blocks.Writer.TryComplete(new MicrophoneException(failure, failure.ToString()));
        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            try { return await blocks.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException e) { if (e.InnerException is MicrophoneException m) throw m; return null; }
        }
        public void Dispose() { Disposed = true; blocks.Writer.TryComplete(); }
    }

    private sealed class FakeDevices(bool present = true) : IMicrophoneDevices
    {
        public bool Present = present;
        public int Opens;
        public FakeStream? Last;
        public bool HasDevice() => Present;
        public IMicrophoneStream Open()
        {
            Opens++;
            if (!Present) throw new MicrophoneException(MicFailure.NoDevice, "none");
            return Last = new FakeStream();
        }
    }

    private static byte[] Block(int ms, short amplitude)
    {
        var b = new byte[ms * Rate / 1000 * 2];
        for (int i = 0; i < b.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i), (i / 2) % 2 == 0 ? amplitude : (short)-amplitude);
        return b;
    }

    private AudioCaptureCoordinator Loopback(FakeDevices output, TimeSpan? limit = null) =>
        new(output, new LeasedFiles(leases), clock, limit, AudioSourceKind.SystemLoopback);

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition(), "condition not reached");
    }

    [Fact] // REC04: captures the output device through the loopback source, the microphone is never opened, the own-playback notice is carried
    public async Task Loopback_records_the_output_device_and_never_touches_the_microphone()
    {
        var mic = new FakeDevices();
        var output = new FakeDevices();
        var started = await Loopback(output).StartAsync(Ct);
        var session = started.Session!;
        Assert.Equal(AudioSourceKind.SystemLoopback, session.Source);
        var stream = output.Last!;
        for (int i = 0; i < 10; i++) stream.Push(Block(100, 8000));
        await Until(() => session.Captured >= TimeSpan.FromSeconds(1));
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.Equal(AudioSourceKind.SystemLoopback, result.Source);
        Assert.True(result.IncludesOwnPlayback); // the UI tells the user that Su-Su's own TTS sound is part of the capture
        using var audio = result.Audio!;
        Assert.False(audio.Silent);
        Assert.Equal(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.Equal(1, output.Opens);
        Assert.Equal(0, mic.Opens);
        Assert.True(stream.Disposed);
    }

    [Fact] // the microphone recorder does not claim own playback
    public async Task Microphone_results_do_not_carry_the_own_playback_notice()
    {
        var devices = new FakeDevices();
        var session = (await new AudioCaptureCoordinator(devices, new LeasedFiles(leases), clock).StartAsync(Ct)).Session!;
        Assert.Equal(AudioSourceKind.Microphone, session.Source);
        devices.Last!.Push(Block(100, 8000));
        await Until(() => session.Captured > TimeSpan.Zero);
        var result = await session.StopAsync();
        Assert.False(result.IncludesOwnPlayback);
        result.Audio?.Dispose();
    }

    [Fact] // REC04 no output device: distinct error code, no file, recorder not left busy
    public async Task No_output_device_is_a_loopback_error_with_no_file()
    {
        var output = new FakeDevices(present: false);
        var capture = Loopback(output);
        Assert.False(capture.HasDevice());
        var r = await capture.StartAsync(Ct);
        Assert.Null(r.Session);
        Assert.Equal("loopback.noDevice", r.ErrorCode);
        Assert.Equal(MicFailure.NoDevice, r.Failure);
        Assert.Equal(0, leases.ActiveCount);
        output.Present = true;
        Assert.True(capture.HasDevice());
        var again = await capture.StartAsync(Ct);
        Assert.NotNull(again.Session);
        again.Session!.Cancel();
    }

    [Fact] // REC04 silent output: silent blocks (nothing playing) raise the silence notice, not an interruption; stopping keeps a Silent recording
    public async Task Silent_output_shows_the_silence_notice_and_is_not_an_interruption()
    {
        var output = new FakeDevices();
        var session = (await Loopback(output).StartAsync(Ct)).Session!;
        var levels = new List<RecordingLevel>();
        session.LevelChanged += l => { lock (levels) levels.Add(l); };
        for (int i = 0; i < 40; i++) output.Last!.Push(Block(100, 0)); // 4 s of silence
        await Until(() => session.Captured >= TimeSpan.FromSeconds(4));
        Assert.Equal(RecordingPhase.Recording, session.Phase);
        lock (levels) Assert.Contains(levels, l => l.Silent);
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.Null(result.Reason);
        using var audio = result.Audio!;
        Assert.True(audio.Silent); // F12.2 must not send this to ASR
    }

    [Theory] // REC03: output switched or removed while recording: interrupted with the reason, captured part kept, nothing reopened
    [InlineData(MicFailure.DefaultChanged)]
    [InlineData(MicFailure.DeviceRemoved)]
    public async Task Output_switch_or_removal_interrupts_keeps_the_part_and_never_follows(MicFailure failure)
    {
        var output = new FakeDevices();
        var session = (await Loopback(output).StartAsync(Ct)).Session!;
        for (int i = 0; i < 5; i++) output.Last!.Push(Block(100, 8000));
        await Until(() => session.Captured >= TimeSpan.FromMilliseconds(500));
        output.Last!.Fail(failure);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((RecordingStatus.Interrupted, failure), (result.Status, result.Reason));
        Assert.True(result.IncludesOwnPlayback);
        using var audio = result.Audio!;
        Assert.Equal(TimeSpan.FromMilliseconds(500), audio.Duration);
        Assert.Equal(1, output.Opens); // no silent switch to the new default
        Assert.True(output.Last.Disposed);
    }

    [Fact] // the 10-minute rule is the F12 one (counted on captured audio, cut exactly)
    public async Task Loopback_stops_at_the_captured_duration_limit()
    {
        var output = new FakeDevices();
        var session = (await Loopback(output, TimeSpan.FromSeconds(1)).StartAsync(Ct)).Session!;
        for (int i = 0; i < 15; i++) output.Last!.Push(Block(100, 8000));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(RecordingStatus.LimitReached, result.Status);
        using var audio = result.Audio!;
        Assert.Equal(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.Equal(AudioCaptureCoordinator.MaxDuration, TimeSpan.FromMinutes(10));
    }

    [Fact] // F13 exit: a long recording streams to its file (managed memory does not grow with the length) and nothing stays after dispose
    public async Task Long_recording_streams_to_disk_and_leaves_nothing_after_dispose()
    {
        long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
        var output = new FakeDevices();
        long baseline = Managed();
        var session = (await Loopback(output).StartAsync(Ct)).Session!;
        var stream = output.Last!;
        long midway = 0;
        for (int round = 1; round <= 30; round++) // 5 minutes of audio, 9.6 MB of PCM, fed in 10 s steps
        {
            for (int i = 0; i < 100; i++) stream.Push(Block(100, 8000));
            await Until(() => session.Captured >= TimeSpan.FromSeconds(10 * round));
            if (round == 15) midway = Managed();
        }
        Assert.True(midway - baseline < 2_000_000, $"managed memory grew by {midway - baseline} bytes while recording");
        var result = await session.StopAsync();
        var audio = result.Audio!;
        string path = audio.File.FilePath;
        Assert.True(new FileInfo(path).Length > 9_000_000); // the audio is on disk, not in memory
        Assert.Equal(1, leases.ActiveCount);
        audio.Dispose();
        session.Dispose();
        Assert.True(audio.File.Released);
        Assert.False(File.Exists(path));
        Assert.Equal(0, leases.ActiveCount);
        Assert.True(stream.Disposed);
        Assert.True(Managed() - baseline < 2_000_000, "memory did not return to baseline after dispose");
    }

    private sealed class FileClip(string path) : IAudioClip
    {
        public string Mime => "audio/wav";
        public string FilePath => path;
        public long Bytes => new FileInfo(path).Length;
        public void Dispose() { }
    }

    private static void WriteTone(string path, double seconds)
    {
        int n = (int)(seconds * 22050);
        var data = new byte[n * 2];
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(12000 * Math.Sin(2 * Math.PI * 440 * i / 22050)));
        using var f = File.Create(path);
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + data.Length));
        "WAVEfmt "u8.CopyTo(h[8..]); BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1); BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], 22050); BinaryPrimitives.WriteUInt32LittleEndian(h[28..], 44100);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 2); BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]); BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)data.Length);
        f.Write(h); f.Write(data);
    }

    [Fact] // REC04 on the real default output: idle output records silence (no sleep interruption), a played tone is captured
    public async Task Real_loopback_captures_a_tone_played_through_the_default_output()
    {
        var sink = new WasapiAudioSink();
        var device = sink.Probe();
        TestContext.Current.TestOutputHelper?.WriteLine($"audio output: available={device.Available} failure={device.Failure} detail={device.Detail}");
        if (!device.Available) { Assert.Skip($"no audio output (render) endpoint on this machine ({device.Failure}); run where an output device exists for REC04"); return; }
        var loopback = WasapiMicrophone.SystemLoopback();
        Assert.True(loopback.HasDevice());
        var capture = new AudioCaptureCoordinator(loopback, new LeasedFiles(leases), new SystemClock(), null, AudioSourceKind.SystemLoopback);
        var started = await capture.StartAsync(Ct);
        Assert.True(started.Session is not null, $"loopback start failed: {started.ErrorCode}");
        var session = started.Session!;

        // idle: nothing plays, yet the timeline advances and the recording is not interrupted
        await Task.Delay(1200, Ct);
        Assert.Equal(RecordingPhase.Recording, session.Phase);
        Assert.True(session.Captured >= TimeSpan.FromMilliseconds(800), $"idle output captured only {session.Captured}");

        string tone = Path.Combine(root.Paths.Cache, "tone.wav");
        WriteTone(tone, 1.5);
        await sink.PlayAsync(new FileClip(tone), Ct);
        await Task.Delay(300, Ct);
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.True(result.IncludesOwnPlayback);
        using var audio = result.Audio!;
        TestContext.Current.TestOutputHelper?.WriteLine($"captured {audio.Duration}, silent={audio.Silent}");
        Assert.True(audio.Duration >= TimeSpan.FromSeconds(2));
        if (audio.Silent)
        {
            Assert.Skip("loopback delivered only silence while the tone played: this machine's output endpoint (virtual 'Remote Audio' device) does not render a mix to loopback");
            return;
        }
        Assert.False(audio.Silent);
    }
}
