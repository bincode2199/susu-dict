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
/// F12.1 microphone recording (TEST-PLAN REC01, REC02, REC03; DESIGN recording-interrupt row; PLAN 6.3). A fake capture device
/// drives the real recorder: permission and device states, level meter and silence notice, the 10-minute limit by captured
/// duration, pause/resume, stop/cancel, device removal / default change / sleep keeping the captured part, and device release.
/// One test opens the real default capture endpoint and skips with a reason when the machine has none.
/// </summary>
public class AudioRecordingTests : IDisposable
{
    private const int Rate = 16000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly FileLeases leases;
    private readonly ManualClock clock = new();

    public AudioRecordingTests() { leases = new FileLeases(root.Paths.Cache); }

    public void Dispose() { leases.Dispose(); root.Dispose(); }

    private sealed class FakeStream : IMicrophoneStream
    {
        private readonly Channel<byte[]> blocks = Channel.CreateUnbounded<byte[]>();
        public int SampleRate => Rate;
        public bool Disposed { get; private set; }
        public void Push(byte[] block) => blocks.Writer.TryWrite(block);
        public void Remove() => blocks.Writer.TryComplete();
        public void Fail(MicFailure failure) => blocks.Writer.TryComplete(new MicrophoneException(failure, failure.ToString()));
        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            try { return await blocks.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException e) { if (e.InnerException is MicrophoneException m) throw m; return null; }
        }
        public void Dispose() { Disposed = true; blocks.Writer.TryComplete(); }
    }

    private sealed class FakeDevices : IMicrophoneDevices
    {
        public bool Present = true;
        public MicFailure? OpenFails;
        public int Opens;
        public FakeStream? Last;
        public bool HasDevice() => Present;
        public IMicrophoneStream Open()
        {
            Opens++;
            if (OpenFails is { } f) throw new MicrophoneException(f, f.ToString());
            if (!Present) throw new MicrophoneException(MicFailure.NoDevice, "none");
            return Last = new FakeStream();
        }
    }

    /// <summary>A block of <paramref name="ms"/> milliseconds: a constant-amplitude square wave, or zeros when amplitude is 0.</summary>
    private static byte[] Block(int ms, short amplitude)
    {
        var b = new byte[ms * Rate / 1000 * 2];
        for (int i = 0; i < b.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i), (i / 2) % 2 == 0 ? amplitude : (short)-amplitude);
        return b;
    }

    private AudioCaptureCoordinator Coordinator(FakeDevices devices, TimeSpan? limit = null) => new(devices, new LeasedFiles(leases), clock, limit);

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition(), "condition not reached");
    }

    private async Task<(IRecordingSession Session, FakeStream Stream)> Begin(FakeDevices devices, TimeSpan? limit = null)
    {
        var started = await Coordinator(devices, limit).StartAsync(Ct);
        Assert.NotNull(started.Session);
        return (started.Session!, devices.Last!);
    }

    // ---------- REC01: permission and device states ----------

    [Fact]
    public async Task Denied_no_device_and_other_open_failures_are_distinct_error_codes_and_leave_no_file()
    {
        var devices = new FakeDevices();
        var capture = Coordinator(devices);
        foreach (var (failure, code) in new[] { (MicFailure.Denied, "mic.denied"), (MicFailure.NoDevice, "mic.noDevice"), (MicFailure.Failed, "mic.failed") })
        {
            devices.OpenFails = failure;
            var r = await capture.StartAsync(Ct);
            Assert.Null(r.Session);
            Assert.Equal(code, r.ErrorCode);
            Assert.Equal(failure, r.Failure);
        }
        Assert.Equal(0, leases.ActiveCount);
        devices.OpenFails = null; devices.Present = false;
        Assert.False(capture.HasDevice());
        Assert.Equal("mic.noDevice", (await capture.StartAsync(Ct)).ErrorCode);
        devices.Present = true;
        Assert.True(capture.HasDevice());
        Assert.NotNull((await capture.StartAsync(Ct)).Session); // the failed attempts did not leave the recorder busy
    }

    [Fact]
    public void Wasapi_error_codes_map_to_denied_no_device_and_removed()
    {
        Assert.Equal(MicFailure.Denied, WasapiMicrophone.Classify(unchecked((int)0x80070005)));
        Assert.Equal(MicFailure.NoDevice, WasapiMicrophone.Classify(unchecked((int)0x80070490)));
        Assert.Equal(MicFailure.NoDevice, WasapiMicrophone.Classify(unchecked((int)0x88890010)));
        Assert.Equal(MicFailure.DeviceRemoved, WasapiMicrophone.Classify(unchecked((int)0x88890004)));
        Assert.Equal(MicFailure.Failed, WasapiMicrophone.Classify(unchecked((int)0x80004005)));
    }

    [Fact]
    public async Task Normal_speech_records_a_valid_wav_with_a_level_meter_and_releases_the_device_on_stop()
    {
        var devices = new FakeDevices();
        var (session, stream) = await Begin(devices);
        var levels = new List<RecordingLevel>();
        session.LevelChanged += l => { lock (levels) levels.Add(l); };
        for (int i = 0; i < 10; i++) stream.Push(Block(100, 16384));
        await Until(() => session.Captured >= TimeSpan.FromSeconds(1));
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.True(stream.Disposed);
        using var audio = result.Audio!;
        Assert.False(audio.Silent);
        Assert.Equal(TimeSpan.FromSeconds(1), audio.Duration);
        lock (levels) { Assert.NotEmpty(levels); Assert.All(levels, l => Assert.InRange(l.Peak, 0.49, 0.51)); Assert.All(levels, l => Assert.False(l.Silent)); }
        var bytes = File.ReadAllBytes(audio.File.FilePath);
        Assert.Equal(44 + Rate * 2, bytes.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(Rate, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(Rate * 2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(1, leases.ActiveCount);
        audio.Dispose();
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public async Task Silence_raises_the_no_voice_notice_and_marks_the_recording_silent_so_it_is_not_sent_to_asr()
    {
        var devices = new FakeDevices();
        var (session, stream) = await Begin(devices);
        var levels = new List<RecordingLevel>();
        session.LevelChanged += l => { lock (levels) levels.Add(l); };
        for (int i = 0; i < 50; i++) stream.Push(Block(100, 0)); // 5 s of nothing
        await Until(() => session.Captured >= TimeSpan.FromSeconds(5));
        var result = await session.StopAsync();
        lock (levels) { Assert.Contains(levels, l => l.Silent && l.Captured >= TimeSpan.FromSeconds(3)); Assert.DoesNotContain(levels, l => l.Silent && l.Captured < TimeSpan.FromSeconds(3)); }
        Assert.True(result.Audio!.Silent);
        result.Audio.Dispose();
    }

    [Fact]
    public async Task Nothing_captured_yields_no_file()
    {
        var (session, stream) = await Begin(new FakeDevices());
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.Null(result.Audio);
        Assert.Equal(0, leases.ActiveCount);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task The_limit_counts_captured_samples_and_cuts_audio_exactly_at_it()
    {
        var (session, stream) = await Begin(new FakeDevices(), TimeSpan.FromSeconds(1));
        for (int i = 0; i < 3; i++) stream.Push(Block(600, 8000)); // 1.8 s offered, 1 s allowed
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(RecordingStatus.LimitReached, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Audio!.Duration);
        Assert.Equal(44 + Rate * 2, new FileInfo(result.Audio.File.FilePath).Length);
        Assert.True(stream.Disposed);
        result.Audio.Dispose();
    }

    [Fact]
    public void The_product_limit_is_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AudioCaptureCoordinator.MaxDuration);
    }

    // ---------- REC02: pause, resume, close, exit ----------

    [Fact]
    public async Task Pause_keeps_nothing_and_the_limit_counts_captured_time_not_wall_time()
    {
        var (session, stream) = await Begin(new FakeDevices(), TimeSpan.FromSeconds(1));
        var phases = new List<RecordingPhase>();
        session.PhaseChanged += p => { lock (phases) phases.Add(p); };
        stream.Push(Block(500, 8000));
        await Until(() => session.Captured == TimeSpan.FromMilliseconds(500));
        session.Pause();
        Assert.Equal(RecordingPhase.Paused, session.Phase);
        clock.Advance(TimeSpan.FromMinutes(30)); // a long pause is not a sleep and does not use up the limit
        stream.Push(Block(400, 8000)); // arrives while paused: dropped
        await Task.Delay(100, Ct);
        Assert.Equal(TimeSpan.FromMilliseconds(500), session.Captured);
        session.Resume();
        Assert.Equal(RecordingPhase.Recording, session.Phase);
        stream.Push(Block(300, 8000));
        await Until(() => session.Captured == TimeSpan.FromMilliseconds(800));
        Assert.False(session.Completion.IsCompleted);
        stream.Push(Block(300, 8000)); // 1.1 s captured in total: cut at 1.0 s
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(RecordingStatus.LimitReached, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Audio!.Duration);
        lock (phases) Assert.Equal([RecordingPhase.Paused, RecordingPhase.Recording, RecordingPhase.Finished], phases);
        result.Audio.Dispose();
    }

    [Fact]
    public async Task Stop_while_paused_keeps_what_was_captured_before_the_pause()
    {
        var (session, stream) = await Begin(new FakeDevices());
        stream.Push(Block(200, 8000));
        await Until(() => session.Captured == TimeSpan.FromMilliseconds(200));
        session.Pause();
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(200), result.Audio!.Duration);
        result.Audio.Dispose();
    }

    [Fact]
    public async Task Cancel_discards_the_file_and_releases_the_device()
    {
        var (session, stream) = await Begin(new FakeDevices());
        stream.Push(Block(300, 8000));
        await Until(() => session.Captured > TimeSpan.Zero);
        session.Cancel();
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(RecordingStatus.Cancelled, result.Status);
        Assert.Null(result.Audio);
        Assert.True(stream.Disposed);
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public async Task Closing_or_exiting_disposes_the_session_which_releases_the_device_and_the_file()
    {
        var (session, stream) = await Begin(new FakeDevices());
        stream.Push(Block(300, 8000));
        await Until(() => session.Captured > TimeSpan.Zero);
        session.Dispose();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(stream.Disposed);
        Assert.Equal(0, leases.ActiveCount);
    }

    [Fact]
    public async Task One_recording_at_a_time_and_the_next_can_start_after_it_ends()
    {
        var devices = new FakeDevices();
        var capture = Coordinator(devices);
        var first = (await capture.StartAsync(Ct)).Session!;
        var second = await capture.StartAsync(Ct);
        Assert.Equal("mic.busy", second.ErrorCode);
        Assert.Equal(1, devices.Opens);
        first.Cancel();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        RecordingStartResult next = RecordingStartResult.Busy();
        for (int i = 0; i < 200 && next.Session is null; i++) { next = await capture.StartAsync(Ct); if (next.Session is null) await Task.Delay(10, Ct); }
        Assert.NotNull(next.Session);
        next.Session!.Cancel();
    }

    // ---------- REC03: interruptions keep the captured part and never switch device ----------

    [Theory]
    [InlineData(MicFailure.DeviceRemoved)]
    [InlineData(MicFailure.DefaultChanged)]
    public async Task A_lost_device_stops_keeps_the_captured_segment_with_the_reason_and_opens_no_other_device(MicFailure failure)
    {
        var devices = new FakeDevices();
        var (session, stream) = await Begin(devices);
        stream.Push(Block(700, 8000));
        await Until(() => session.Captured == TimeSpan.FromMilliseconds(700));
        stream.Fail(failure);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(RecordingStatus.Interrupted, result.Status);
        Assert.Equal(failure, result.Reason);
        Assert.Equal(TimeSpan.FromMilliseconds(700), result.Audio!.Duration);
        Assert.True(File.Exists(result.Audio.File.FilePath));
        Assert.True(stream.Disposed);
        Assert.Equal(1, devices.Opens);
        result.Audio.Dispose();
    }

    [Fact]
    public async Task An_unplugged_device_that_just_ends_the_stream_is_reported_as_removed()
    {
        var devices = new FakeDevices();
        var (session, stream) = await Begin(devices);
        stream.Push(Block(400, 8000));
        await Until(() => session.Captured == TimeSpan.FromMilliseconds(400));
        stream.Remove();
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((RecordingStatus.Interrupted, MicFailure.DeviceRemoved), (result.Status, result.Reason));
        Assert.Equal(TimeSpan.FromMilliseconds(400), result.Audio!.Duration);
        result.Audio.Dispose();
    }

    [Fact]
    public async Task Sleep_is_a_multi_second_gap_that_stops_and_keeps_the_part_captured_before_it()
    {
        var devices = new FakeDevices();
        var (session, stream) = await Begin(devices);
        stream.Push(Block(500, 8000));
        await Until(() => session.Captured == TimeSpan.FromMilliseconds(500));
        clock.Advance(TimeSpan.FromMinutes(20)); // the machine slept
        stream.Push(Block(100, 8000)); // the first block after waking is not part of the recording
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((RecordingStatus.Interrupted, MicFailure.Sleep), (result.Status, result.Reason));
        Assert.Equal(TimeSpan.FromMilliseconds(500), result.Audio!.Duration);
        Assert.Equal(1, devices.Opens);
        result.Audio.Dispose();
    }

    [Fact]
    public async Task An_interruption_before_any_audio_leaves_no_file_but_still_reports_the_reason()
    {
        var (session, stream) = await Begin(new FakeDevices());
        stream.Fail(MicFailure.DeviceRemoved);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((RecordingStatus.Interrupted, MicFailure.DeviceRemoved), (result.Status, result.Reason));
        Assert.Null(result.Audio);
        Assert.Equal(0, leases.ActiveCount);
    }

    // ---------- real capture endpoint ----------

    [Fact]
    public async Task Real_default_capture_endpoint_records_and_releases_the_device()
    {
        var mic = new WasapiMicrophone();
        if (!mic.HasDevice()) { Assert.Skip("no capture endpoint on this machine (the VM has no microphone); run on a physical machine for REC01 normal"); return; }
        var capture = new AudioCaptureCoordinator(mic, new LeasedFiles(leases), clock);
        var started = await capture.StartAsync(Ct);
        if (started.Failure == MicFailure.Denied) { Assert.Skip("Windows privacy settings deny microphone access to this process"); return; }
        Assert.NotNull(started.Session);
        var session = started.Session!;
        await Until(() => session.Captured >= TimeSpan.FromMilliseconds(300));
        var result = await session.StopAsync();
        Assert.Equal(RecordingStatus.Stopped, result.Status);
        using var audio = result.Audio!;
        Assert.True(audio.Duration >= TimeSpan.FromMilliseconds(300));
        Assert.Equal(44 + (long)(audio.Duration.TotalSeconds * WasapiMicrophone.CaptureRate) * 2, new FileInfo(audio.File.FilePath).Length);
        // the device was released: a second recording opens at once
        var again = RecordingStartResult.Busy();
        for (int i = 0; i < 100 && again.Session is null && again.ErrorCode == "mic.busy"; i++) { again = await capture.StartAsync(Ct); if (again.Session is null) await Task.Delay(10, Ct); }
        Assert.NotNull(again.Session);
        again.Session!.Cancel();
    }
}
