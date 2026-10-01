using System.Buffers.Binary;
using Susu.Abstractions;

namespace Susu.Jobs;

/// <summary>
/// F12.1 microphone recording (ARCHITECTURE 7 <c>IAudioCapture</c>, PLAN 6.3, TEST-PLAN REC01–REC03). The coordinator opens the
/// default capture device through <see cref="IMicrophoneDevices"/> (WASAPI in production, a fake in tests) and runs one
/// <see cref="RecordingSession"/> at a time. Samples go straight into a WAV file lease; nothing is transcribed while recording
/// (F12.2 requests ASR only from the finished result).
///
/// <para>F13.1: the same coordinator and session record system audio when given a loopback <see cref="IMicrophoneDevices"/> and
/// <see cref="AudioSourceKind.SystemLoopback"/>; only the start error codes (<c>loopback.*</c>) and the own-playback flag differ.
/// A loopback device delivers no packets while nothing plays, so its stream supplies silent blocks (the silence notice path); a
/// gap here would otherwise be mistaken for sleep.</para>
///
/// <para>The 10-minute limit counts captured samples, not wall time, so pauses do not use it up. A removed device, a changed
/// default device or a stall of several seconds (sleep) ends the recording with the captured part kept and the reason set; the
/// recorder never reopens another device. Cancel and failure leave no file. The device is released whenever a recording ends.</para>
/// </summary>
public sealed class AudioCaptureCoordinator(IMicrophoneDevices devices, ILeasedFileFactory files, IClock clock, TimeSpan? limit = null, AudioSourceKind source = AudioSourceKind.Microphone) : IAudioCapture
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);
    private readonly TimeSpan limit = limit ?? MaxDuration;
    private int busy;

    public bool HasDevice()
    {
        try { return devices.HasDevice(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    public async Task<RecordingStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0) return RecordingStartResult.Busy();
        bool started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMicrophoneStream stream;
            try { stream = await Task.Run(devices.Open, CancellationToken.None); }
            catch (MicrophoneException e) { return RecordingStartResult.Failed(e.Failure, source); }
            catch (Exception e) when (e is not OperationCanceledException) { return RecordingStartResult.Failed(MicFailure.Failed, source); }
            if (cancellationToken.IsCancellationRequested) { stream.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
            ILeasedFile file;
            try { file = files.Create("record", "audio/wav", "wav"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stream.Dispose(); return RecordingStartResult.Failed(MicFailure.Failed, source); }
            var session = new RecordingSession(stream, file, clock, limit, source);
            if (!session.TryBegin()) return RecordingStartResult.Failed(MicFailure.Failed, source);
            started = true;
            _ = session.Completion.ContinueWith(_ => Volatile.Write(ref busy, 0), TaskScheduler.Default);
            return RecordingStartResult.Started(session);
        }
        finally { if (!started) Volatile.Write(ref busy, 0); }
    }
}

public sealed class RecordingSession : IRecordingSession
{
    /// <summary>No chunk for this long (the device normally delivers every ~20 ms) means the machine slept or stalled.</summary>
    public const int SuspendGapMs = 3000;
    /// <summary>Peak below this (about -46 dBFS) is not sound.</summary>
    public const double SoundThreshold = 0.005;
    public static readonly TimeSpan SilenceNotice = TimeSpan.FromSeconds(3);
    private const int HeaderBytes = 44, LevelEveryMs = 50;

    private readonly IMicrophoneStream stream;
    private readonly ILeasedFile file;
    private readonly IClock clock;
    private readonly long limitSamples;
    private readonly int rate;
    private readonly CancellationTokenSource cts = new();
    private readonly TaskCompletionSource<RecordingResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private FileStream? output;
    private long samples, lastSoundSamples, lastLevelSamples;
    private double overallPeak;
    private int phase; // RecordingPhase
    private RecordingStatus? requested;
    private bool finished;

    internal RecordingSession(IMicrophoneStream stream, ILeasedFile file, IClock clock, TimeSpan limit, AudioSourceKind source = AudioSourceKind.Microphone)
    {
        Source = source;
        this.stream = stream; this.file = file; this.clock = clock;
        rate = stream.SampleRate;
        limitSamples = (long)(limit.TotalSeconds * rate);
    }

    public AudioSourceKind Source { get; }
    public RecordingPhase Phase => (RecordingPhase)Volatile.Read(ref phase);
    public TimeSpan Captured => TimeSpan.FromSeconds(Interlocked.Read(ref samples) / (double)rate);
    public Task<RecordingResult> Completion => done.Task;
    public event Action<RecordingLevel>? LevelChanged;
    public event Action<RecordingPhase>? PhaseChanged;

    /// <summary>Creates the file with a WAV header and starts the pump. False (and everything released) when the file cannot be created.</summary>
    internal bool TryBegin()
    {
        try
        {
            output = new FileStream(file.FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.None);
            output.Write(WavHeader(0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output?.Dispose(); stream.Dispose(); file.Dispose(); cts.Dispose();
            return false;
        }
        _ = Task.Run(PumpAsync);
        return true;
    }

    public void Pause() => SetPhase(RecordingPhase.Recording, RecordingPhase.Paused);
    public void Resume() => SetPhase(RecordingPhase.Paused, RecordingPhase.Recording);

    private void SetPhase(RecordingPhase from, RecordingPhase to)
    {
        if (Interlocked.CompareExchange(ref phase, (int)to, (int)from) == (int)from) PhaseChanged?.Invoke(to);
    }

    public Task<RecordingResult> StopAsync() { Request(RecordingStatus.Stopped); return done.Task; }
    public void Cancel() => Request(RecordingStatus.Cancelled);
    public void Dispose() => Request(RecordingStatus.Cancelled);

    private void Request(RecordingStatus status)
    {
        lock (gate) { if (finished || requested is not null) return; requested = status; }
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task PumpAsync()
    {
        long last = clock.NowMilliseconds;
        try
        {
            while (true)
            {
                byte[]? chunk = await stream.ReadAsync(cts.Token);
                if (chunk is null) { Finish(RecordingStatus.Interrupted, MicFailure.DeviceRemoved); return; }
                long now = clock.NowMilliseconds;
                bool gap = now - last > SuspendGapMs;
                last = now;
                if (Phase == RecordingPhase.Paused) continue; // no upload, no capture while paused
                if (gap) { Finish(RecordingStatus.Interrupted, MicFailure.Sleep); return; }
                if (Append(chunk)) { Finish(RecordingStatus.LimitReached, null); return; }
            }
        }
        catch (OperationCanceledException) { Finish(requested ?? RecordingStatus.Cancelled, null); }
        catch (MicrophoneException e) { Finish(RecordingStatus.Interrupted, e.Failure); }
        catch (IOException e) { Finish(RecordingStatus.Failed, null, IsDiskFull(e) ? "record.diskFull" : "record.writeFailed"); }
        catch (UnauthorizedAccessException) { Finish(RecordingStatus.Failed, null, "record.writeFailed"); }
        catch (Exception) { Finish(RecordingStatus.Failed, null, "mic.failed"); }
    }

    /// <summary>Writes the chunk (cut at the limit), updates the meter; true when the limit was reached.</summary>
    private bool Append(byte[] chunk)
    {
        long take = Math.Min(chunk.Length / 2, limitSamples - samples);
        if (take <= 0) return true;
        var span = chunk.AsSpan(0, (int)take * 2);
        double peak = 0;
        for (int i = 0; i < span.Length; i += 2)
        {
            double v = Math.Abs((double)BinaryPrimitives.ReadInt16LittleEndian(span[i..])) / 32768.0;
            if (v > peak) peak = v;
        }
        output!.Write(span);
        Interlocked.Add(ref samples, take);
        if (peak > overallPeak) overallPeak = peak;
        if (peak >= SoundThreshold) lastSoundSamples = samples;
        if ((samples - lastLevelSamples) * 1000 >= (long)LevelEveryMs * rate || samples >= limitSamples)
        {
            lastLevelSamples = samples;
            var captured = TimeSpan.FromSeconds(samples / (double)rate);
            bool silent = TimeSpan.FromSeconds((samples - lastSoundSamples) / (double)rate) >= SilenceNotice;
            LevelChanged?.Invoke(new RecordingLevel(peak, silent, captured));
        }
        return samples >= limitSamples;
    }

    private void Finish(RecordingStatus status, MicFailure? reason, string? errorCode = null)
    {
        lock (gate) { if (finished) return; finished = true; }
        Volatile.Write(ref phase, (int)RecordingPhase.Finished);
        RecordedAudio? audio = null;
        string? error = errorCode;
        bool keep = status is RecordingStatus.Stopped or RecordingStatus.LimitReached or RecordingStatus.Interrupted;
        try
        {
            stream.Dispose(); // the device is released before anything else
            if (output is not null)
            {
                try
                {
                    if (keep && samples > 0)
                    {
                        output.Seek(0, SeekOrigin.Begin);
                        output.Write(WavHeader(samples * 2));
                        output.Flush(flushToDisk: true);
                    }
                }
                catch (IOException e) { keep = false; status = RecordingStatus.Failed; error = IsDiskFull(e) ? "record.diskFull" : "record.writeFailed"; }
                finally { output.Dispose(); }
            }
            if (keep && samples > 0)
                audio = new RecordedAudio(file, TimeSpan.FromSeconds(samples / (double)rate), rate, overallPeak < SoundThreshold);
            else file.Dispose();
        }
        catch { file.Dispose(); throw; }
        finally { cts.Dispose(); }
        PhaseChanged?.Invoke(RecordingPhase.Finished);
        done.TrySetResult(new RecordingResult(status, audio, status == RecordingStatus.Interrupted ? reason : null, error, Source));
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x27 or 0x70;

    /// <summary>The 44-byte RIFF/WAVE header for 16-bit mono PCM.</summary>
    internal byte[] WavHeader(long dataBytes)
    {
        var h = new byte[HeaderBytes];
        "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(rate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)dataBytes);
        return h;
    }
}
