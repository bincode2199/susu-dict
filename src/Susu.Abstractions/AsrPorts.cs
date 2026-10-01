using Susu.Contracts;
using Susu.Domain;

namespace Susu.Abstractions;

/// <summary>
/// One transcription request for one audio chunk (PLAN 4.4 <c>asr</c>, F12.2). <see cref="Audio"/> is the host file lease id of
/// the encoded chunk; the provider grants exactly that handle to the one plugin call and never reads or forwards the bytes
/// itself (B02). <see cref="DurationSeconds"/> is the real chunk length (result times are validated against it) and
/// <see cref="MaxRequestBytes"/> the cap the broker enforces on the whole upload body.
/// </summary>
public sealed record AsrCall(string Audio, string Mime, long Bytes, double DurationSeconds, string Model, string Output, string? Lang, string AttemptId,
    TimeSpan Timeout, long MaxRequestBytes);

/// <summary>A validated chunk result (<see cref="PluginResultValidation.ValidateSegments"/>) or a classified failure.</summary>
public abstract record AsrOutcome
{
    /// <summary>Kind "text": <see cref="Text"/> (may be empty = no speech in the chunk); kind "segments": times relative to the chunk start.</summary>
    public sealed record Transcribed(string Kind, string Text, IReadOnlyList<AsrSegment>? Segments) : AsrOutcome;
    public sealed record Failure(ProviderError Error) : AsrOutcome;
}

/// <summary>
/// An ASR service and model (F12.2: P-R01 OpenAI, P-R02 Gemini as plugin packages). <see cref="Limits"/> are what the pipeline
/// sizes chunks by; <see cref="Timecodes"/> whether the model may return segments. Cancelling the token cancels the plugin
/// call and the broker's upload; the caller keeps ownership of the chunk lease.
/// </summary>
public interface IAsrProvider
{
    string InstanceId { get; }
    string Model { get; }
    bool Timecodes { get; }
    AsrLimits Limits { get; }
    Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken cancellationToken);
}
