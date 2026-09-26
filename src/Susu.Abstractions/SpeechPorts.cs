using Susu.Contracts;

namespace Susu.Abstractions;

/// <summary>
/// A leased audio file ready for playback (F10.1, ARCHITECTURE 7/8.4). The file lives in the host cache and is
/// deleted when the last owner releases it; <see cref="IDisposable.Dispose"/> releases this owner's reference and is
/// safe to call twice. <see cref="FilePath"/> is for host code (decoder, player) only and never reaches a page or plugin.
/// </summary>
public interface IAudioClip : IDisposable
{
    string Mime { get; }
    string FilePath { get; }
    long Bytes { get; }
}

/// <summary>
/// A clip whose file lease can take another owner (F10.3 TTS cache): <see cref="Share"/> returns a new, independently
/// disposable reference on the same file, or null when the file is already gone. The file stays until every owner let go.
/// </summary>
public interface IShareableAudioClip : IAudioClip
{
    IAudioClip? Share();
}

/// <summary>Creates an empty leased file for host-side audio (SAPI synthesis writes into it).</summary>
public interface IAudioFileFactory
{
    IAudioClip Create(string mime, string extension);
}

/// <summary>
/// One pronunciation request (PLAN 4.7 <c>tts(req: { text, lang, voice? })</c>, plus the configured speed).
/// Lang: canonical BCP-47 code of the text, or null to let the service choose. Voice: a voice id from the service's
/// <c>voices</c> list, or null for the service default for <see cref="Lang"/>. Rate: speed multiplier, 1 = normal,
/// clamped to [<see cref="MinRate"/>, <see cref="MaxRate"/>] by every provider.
/// </summary>
public sealed record SpeakRequest(string Text, string? Lang = null, string? Voice = null, double Rate = 1.0)
{
    public const double MinRate = 0.5, MaxRate = 2.0;

    public double ClampedRate => double.IsFinite(Rate) ? Math.Clamp(Rate, MinRate, MaxRate) : 1.0;
}

public sealed record SpeakCall(SpeakRequest Request, string AttemptId, TimeSpan Timeout);

/// <summary>Audio for the player: a leased clip the receiver now owns, or a classified failure.</summary>
public abstract record AudioOutcome
{
    public sealed record Ready(IAudioClip Clip) : AudioOutcome;
    public sealed record Failure(ProviderError Error) : AudioOutcome;
}

/// <summary>
/// A text-to-speech service (F10.1): native SAPI or a plugin package's <c>tts</c> capability. The result audio is
/// always a leased file (never bytes in memory crossing a boundary); cancelling the token stops the synthesis or
/// download and leaves no partial file behind (B07).
/// </summary>
public interface ITtsProvider
{
    /// <summary>The configured instance, e.g. <c>native-sapi</c> or <c>microsoft-tts</c>.</summary>
    string InstanceId { get; }
    bool Native { get; }
    Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken);
}

/// <summary>Why the audio output could not play a clip (F10.1; F10.3 shows these).</summary>
public enum AudioFailure
{
    /// <summary>No audio output device (none installed, all disabled, or the audio service is not running).</summary>
    NoDevice,
    /// <summary>The device went away or was reconfigured during playback.</summary>
    DeviceLost,
    /// <summary>The clip could not be decoded (unsupported or corrupt format).</summary>
    Unsupported,
    /// <summary>Any other playback error.</summary>
    Failed,
}

public sealed class AudioPlaybackException(AudioFailure failure, string detail) : Exception(detail)
{
    public AudioFailure Failure { get; } = failure;
}

/// <summary>Whether playback can currently work, for diagnostics and the pronunciation bar.</summary>
public sealed record AudioDeviceStatus(bool Available, AudioFailure? Failure = null, string? Detail = null);

/// <summary>
/// The audio output (F10.1): plays one clip to the end, or until the token is cancelled, then returns. Throws
/// <see cref="AudioPlaybackException"/> for device and decode errors. Implementations: WASAPI + Media Foundation in
/// <c>Susu.Windows</c>; a fake in tests.
/// </summary>
public interface IAudioSink
{
    AudioDeviceStatus Probe();
    Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken);
}
