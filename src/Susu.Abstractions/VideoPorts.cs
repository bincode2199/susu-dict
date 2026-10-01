using Susu.Contracts;
using Susu.Domain;

namespace Susu.Abstractions;

/// <summary>
/// One subtitle translation request: parts with host-assigned ids (PLAN 4.7.1). The reply is matched by id only, never by position
/// or by line breaks inside a cue.
/// </summary>
public sealed record SubtitleBatchCall(IReadOnlyList<SubtitlePart> Parts, string From, string To, string AttemptId, TimeSpan Timeout);

/// <summary>The raw reply of a batch (ids and text exactly as the service returned them: validated by the job) or a classified failure.</summary>
public abstract record SubtitleBatchOutcome
{
    public sealed record Success(IReadOnlyList<(string? Id, string? Text)> Items) : SubtitleBatchOutcome;
    public sealed record Failure(ProviderError Error) : SubtitleBatchOutcome;
}

/// <summary>
/// The translation service as the video job sees it (F14.2, T03–T05). <see cref="Limits"/> decide how cues are split into parts and
/// batched: a <see cref="BatchMode.Single"/> service gets one part per call, an items service several.
/// <see cref="QuotaNoteKey"/> names the i18n note for a shared or limited quota that must be shown before upload (T06); null when
/// the service has nothing to say (the job never invents a character total or a price).
/// </summary>
public interface ISubtitleTranslationProvider
{
    string ServiceId { get; }
    string DisplayName { get; }
    TranslationLimits Limits { get; }
    string? QuotaNoteKey { get; }
    Task<SubtitleBatchOutcome> TranslateBatchAsync(SubtitleBatchCall call, CancellationToken cancellationToken);
}

/// <summary>
/// What the UI shows before anything is uploaded (F14.2, T06): the file, which ASR and translation service, the limits, and the
/// size to be sent. <see cref="UploadBytesEstimate"/> is an upper bound (16 kHz mono WAV is 32,000 bytes per second; silent
/// stretches are not sent). Character total and price are null: they are not known before ASR and are never made up.
/// </summary>
public sealed record VideoUploadNotice(string JobId, string DisplayName, TimeSpan Duration, bool HasVideo, string AsrService, string AsrModel,
    long UploadBytesEstimate, int ChunkSecondsLimit, long ChunkBytesLimit, string TranslationService, string TranslationServiceName,
    TranslationLimits TranslationLimits, string? TranslationQuotaNoteKey, long? EstimatedCharacters, string? EstimatedPrice);

/// <summary>The UI's answer port. The job waits for it; cancelling the job cancels the wait. Nothing is uploaded before it returns true.</summary>
public interface IVideoUploadConfirmation
{
    Task<bool> ConfirmAsync(VideoUploadNotice notice, CancellationToken cancellationToken);
}

public enum VideoJobPhase { Created, Probing, AwaitingConfirm, Running, Paused, QuotaExhausted, Done, Failed, Cancelled }

public enum VideoStage { None, Probe, Asr, Translate }

public enum VideoQuotaSide { None, Asr, Translation }

/// <summary>Codes of the video job that are not provider error kinds (the provider's own kind is in <see cref="VideoJobState.Error"/>).</summary>
public static class VideoErrors
{
    public const string Busy = "video.busy";
    public const string NoAsr = "video.noAsr";
    public const string NoTranslation = "video.noTranslation";
    public const string NotConfirmed = "video.notConfirmed";
    public const string NoSpeech = "video.noSpeech";
    public const string TextTooLong = "video.textTooLong";
    public const string AsrFailed = "video.asrFailed";
    public const string TranslationFailed = "video.translationFailed";
    /// <summary>The ASR selected (or switched to) has no timecodes: refused, no time is ever made up (A03).</summary>
    public const string NoTimecodes = "asr.noTimecodes";
}

/// <summary>
/// A subtitle cue of the incremental list. <see cref="Translation"/> is null until translated; <see cref="TranslationError"/> is set
/// when this cue alone could not be translated (the job went on).
/// </summary>
public sealed record VideoCue(string Id, double Start, double End, string Original, string? Translation = null, string? TranslationError = null);

/// <summary>
/// The job's state for the UI (cues travel separately). <see cref="SlicesTotal"/> is an estimate from the duration until decode ended.
/// <see cref="Quota"/> says which service ran out while the phase is <see cref="VideoJobPhase.QuotaExhausted"/>.
/// </summary>
public sealed record VideoJobState(string JobId, VideoJobPhase Phase, VideoStage Stage = VideoStage.None, string? ErrorCode = null, ProviderError? Error = null,
    VideoQuotaSide Quota = VideoQuotaSide.None, int SlicesDone = 0, int SlicesTotal = 0, int Cues = 0, int Translated = 0, int Failed = 0,
    string? AsrService = null, string? TranslationService = null)
{
    public bool IsTerminal => Phase is VideoJobPhase.Done or VideoJobPhase.Failed or VideoJobPhase.Cancelled;
}

/// <summary>A job's state and cues as kept in the process for reopen and export (F14.3).</summary>
public sealed record VideoJobResult(string JobId, string DisplayName, VideoJobState State, IReadOnlyList<VideoCue> Cues, TimeSpan? MediaDuration = null);

public enum SubtitleFormat { Srt, Vtt, Txt }

/// <summary>What an export contains. Bilingual puts one line group per cue: first the original (or the translation) and then the other.</summary>
public enum SubtitleMode { Original, Translation, BilingualOriginalFirst, BilingualTranslationFirst }

/// <summary>The save-file dialog (F14.3). Returns the chosen full path, or null when the user cancelled.</summary>
public interface ISubtitleSavePicker
{
    Task<string?> PickAsync(string suggestedFileName, SubtitleFormat format, CancellationToken cancellationToken);
}
