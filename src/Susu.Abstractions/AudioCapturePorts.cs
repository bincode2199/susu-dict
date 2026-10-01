namespace Susu.Abstractions;

/// <summary>Why the microphone could not start or stopped (F12.1, TEST-PLAN REC01/REC03).</summary>
public enum MicFailure
{
    /// <summary>Windows privacy settings or policy refuse microphone access.</summary>
    Denied,
    /// <summary>No capture endpoint (none installed, all disabled, or the audio service is not running).</summary>
    NoDevice,
    /// <summary>The device being recorded was unplugged or disabled.</summary>
    DeviceRemoved,
    /// <summary>The system default capture device changed; the recording never moves to the new device on its own.</summary>
    DefaultChanged,
    /// <summary>The machine slept (or the process stalled for seconds); samples were not captured meanwhile.</summary>
    Sleep,
    /// <summary>Any other device error.</summary>
    Failed,
}

public sealed class MicrophoneException(MicFailure failure, string detail) : Exception(detail)
{
    public MicFailure Failure { get; } = failure;
}

/// <summary>
/// One open capture stream (the thin WASAPI layer; tests use a fake). Samples are always 16-bit signed little-endian mono PCM at
/// <see cref="SampleRate"/> (the OS converts), so everything above this interface is device-free.
/// </summary>
public interface IMicrophoneStream : IDisposable
{
    int SampleRate { get; }
    /// <summary>
    /// The next block of PCM (never empty). Throws <see cref="MicrophoneException"/> when the device was removed, the default
    /// changed or capture failed; returns null only when the stream was disposed. Honours the token.
    /// </summary>
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Opens the default capture device. Never switches device by itself: a lost device ends the stream.</summary>
public interface IMicrophoneDevices
{
    /// <summary>True when a capture endpoint exists (no stream is opened). Denied is only discovered by <see cref="Open"/>.</summary>
    bool HasDevice();
    /// <summary>Opens and starts the default capture endpoint. Throws <see cref="MicrophoneException"/> (Denied, NoDevice, Failed).</summary>
    IMicrophoneStream Open();
}

public enum RecordingPhase { Recording, Paused, Finished }

/// <summary>A level sample for the meter: Peak is 0..1 of full scale; Silent is true after a few seconds of no sound ("no voice detected").</summary>
public readonly record struct RecordingLevel(double Peak, bool Silent, TimeSpan Captured);

public enum RecordingStatus
{
    /// <summary>The user stopped.</summary>
    Stopped,
    /// <summary>The 10-minute captured-duration limit was reached; audio is cut exactly at the limit.</summary>
    LimitReached,
    /// <summary>The device was removed, the default changed or the machine slept; <see cref="RecordingResult.Reason"/> says which. What was captured is kept.</summary>
    Interrupted,
    /// <summary>The user cancelled; nothing is kept.</summary>
    Cancelled,
    /// <summary>The recording could not be saved (<see cref="RecordingResult.ErrorCode"/> is <c>record.diskFull</c>, <c>record.writeFailed</c> or <c>mic.failed</c>).</summary>
    Failed,
}

/// <summary>The recorded audio as a 16-bit mono WAV file lease. Disposing releases the lease (the file goes).</summary>
public sealed class RecordedAudio(ILeasedFile file, TimeSpan duration, int sampleRate, bool silent) : IDisposable
{
    public ILeasedFile File { get; } = file;
    public TimeSpan Duration { get; } = duration;
    public int SampleRate { get; } = sampleRate;
    /// <summary>The whole recording had no audible sound: F12.2 must not send it to ASR.</summary>
    public bool Silent { get; } = silent;
    public void Dispose() => File.Dispose();
}

/// <summary>
/// The end of a recording. <see cref="Audio"/> is present for Stopped, LimitReached and Interrupted when anything was captured
/// (the caller owns and disposes it). Nothing is sent to ASR by the recorder: transcription is requested only after this result.
/// </summary>
public sealed record RecordingResult(RecordingStatus Status, RecordedAudio? Audio, MicFailure? Reason = null, string? ErrorCode = null)
{
    public static RecordingResult Cancelled() => new(RecordingStatus.Cancelled, null);
}

/// <summary>One running recording. Pause discards samples (nothing is captured or kept while paused) and the time limit counts captured time only.</summary>
public interface IRecordingSession : IDisposable
{
    RecordingPhase Phase { get; }
    /// <summary>Captured duration (samples written ÷ rate), excluding paused time.</summary>
    TimeSpan Captured { get; }
    event Action<RecordingLevel>? LevelChanged;
    event Action<RecordingPhase>? PhaseChanged;
    void Pause();
    void Resume();
    /// <summary>Stops, finishes the file and releases the device.</summary>
    Task<RecordingResult> StopAsync();
    /// <summary>Discards the recording and releases the device.</summary>
    void Cancel();
    /// <summary>Completes when the recording ended for any reason (stop, limit, interruption, cancel, failure).</summary>
    Task<RecordingResult> Completion { get; }
}

public sealed record RecordingStartResult(IRecordingSession? Session, string? ErrorCode, MicFailure? Failure)
{
    public static RecordingStartResult Started(IRecordingSession session) => new(session, null, null);
    public static RecordingStartResult Failed(MicFailure failure) => new(null, failure switch
    {
        MicFailure.Denied => "mic.denied",
        MicFailure.NoDevice => "mic.noDevice",
        _ => "mic.failed",
    }, failure);
    public static RecordingStartResult Busy() => new(null, "mic.busy", null);
}

/// <summary>
/// ARCHITECTURE 7 <c>IAudioCapture</c> (microphone): one recording at a time; a second request while one runs returns <c>mic.busy</c>.
/// </summary>
public interface IAudioCapture
{
    /// <summary>True when a capture endpoint exists, for the start button's pre-check (REC01 no device).</summary>
    bool HasDevice();
    Task<RecordingStartResult> StartAsync(CancellationToken cancellationToken = default);
}
