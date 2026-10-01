using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Jobs;

public enum AsrPhase { Idle, Transcribing, Transcribed, NoSpeech, Failed, Unavailable, Cancelled }

/// <summary>
/// What the voice window shows (F12.3 projects it). <see cref="Text"/> is the merged transcript; <see cref="Segments"/> only for
/// a timecode request. <see cref="Progress"/> is the chunk being sent while Transcribing. <see cref="Translated"/>: the text was
/// handed to the translation pipeline.
/// </summary>
public sealed record AsrState(long Generation, AsrPhase Phase, string? ServiceId = null, string? Text = null, IReadOnlyList<AsrSegment>? Segments = null,
    ProviderError? Error = null, bool Translated = false, AsrProgress? Progress = null)
{
    public static readonly AsrState Idle = new(0, AsrPhase.Idle);
}

/// <summary>
/// F12.2 ASR job (PLAN 6.2, 4.7.2): a <see cref="RecordedAudio"/> WAV lease → the selected ASR service by handle, chunk by chunk
/// (<see cref="AsrTranscriber"/>) → text (plus optional timed segments) → the same translation pipeline as typed and OCR text
/// when <c>autoTranslate</c> is on.
/// <list type="bullet">
/// <item>One job at a time: a new job or <see cref="Cancel"/> cancels the running one (its plugin call and upload stop, no chunk
/// file is left), and a superseded result is never published or translated.</item>
/// <item>The job owns the audio from <see cref="TranscribeAsync"/> on and releases its lease when transcription ends, however it
/// ends: no recording outlives the job (ARCHITECTURE 8.4).</item>
/// <item>No usable service: <see cref="AsrPhase.Unavailable"/>, nothing is called. A recording with no audible sound, or a
/// transcript with no text, is <see cref="AsrPhase.NoSpeech"/>, never an empty success.</item>
/// </list>
/// </summary>
public sealed class AsrJob(Func<IAsrProvider?> provider, ILeasedFileFactory files, Func<string, Task> translate, Func<bool> autoTranslate, TimeSpan? timeout = null, Func<string>? idFactory = null)
{
    private readonly object gate = new();
    private readonly AsrTranscriber transcriber = new(files, timeout ?? ConfigSnapshot.EffectiveTimeout(Capability.Asr, null), idFactory);
    private CancellationTokenSource? running;
    private long generation;
    private AsrState state = AsrState.Idle;

    public event Action<AsrState>? StateChanged;
    public AsrState State { get { lock (gate) return state; } }

    public async Task<AsrState> TranscribeAsync(RecordedAudio audio, string? lang = null, string output = "text")
    {
        ArgumentNullException.ThrowIfNull(audio);
        CancellationTokenSource cancel;
        long current;
        lock (gate)
        {
            running?.Cancel();
            running = cancel = new CancellationTokenSource();
            current = ++generation;
        }
        try
        {
            var service = provider();
            if (service is null) return Publish(current, new AsrState(current, AsrPhase.Unavailable));
            if (audio.Silent) return Publish(current, new AsrState(current, AsrPhase.NoSpeech, service.InstanceId));
            Publish(current, new AsrState(current, AsrPhase.Transcribing, service.InstanceId));
            AsrRunOutcome outcome;
            try
            {
                outcome = await transcriber.RunAsync(service, audio.File, output, lang, cancel.Token,
                    p => Publish(current, new AsrState(current, AsrPhase.Transcribing, service.InstanceId, Progress: p)));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return Publish(current, new AsrState(current, AsrPhase.Cancelled, service.InstanceId)); }
            catch (Exception error) { outcome = new AsrRunOutcome.Failure(new ProviderError(ErrorKind.Unavailable, error.GetType().Name)); }
            if (cancel.IsCancellationRequested) return Publish(current, new AsrState(current, AsrPhase.Cancelled, service.InstanceId));
            audio.Dispose();

            var final = outcome switch
            {
                AsrRunOutcome.Done d => new AsrState(current, AsrPhase.Transcribed, service.InstanceId, d.Transcript.Text, d.Transcript.Segments),
                AsrRunOutcome.NoSpeech => new AsrState(current, AsrPhase.NoSpeech, service.InstanceId),
                AsrRunOutcome.Failure f => new AsrState(current, AsrPhase.Failed, service.InstanceId, Error: f.Error),
                _ => new AsrState(current, AsrPhase.Failed, service.InstanceId, Error: new ProviderError(ErrorKind.BadResponse)),
            };
            if (final.Phase == AsrPhase.Transcribed && final.Text!.Length > 0 && autoTranslate() && IsCurrent(current))
            {
                await translate(final.Text);
                final = final with { Translated = true };
            }
            return Publish(current, final);
        }
        finally
        {
            audio.Dispose();
            lock (gate) if (ReferenceEquals(running, cancel)) running = null;
            cancel.Dispose();
        }
    }

    /// <summary>Cancels the running job, if any. Its in-flight upload stops and its audio lease is released as it unwinds.</summary>
    public void Cancel() { lock (gate) running?.Cancel(); }

    /// <summary>Hands already transcribed (and possibly edited) text to the translation pipeline, e.g. when auto-translate is off.</summary>
    public Task TranslateAsync(string text) => string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : translate(text);

    private bool IsCurrent(long job) { lock (gate) return job == generation; }

    private AsrState Publish(long job, AsrState next)
    {
        lock (gate)
        {
            if (job != generation) return next with { Phase = AsrPhase.Cancelled };
            state = next;
        }
        StateChanged?.Invoke(next);
        return next;
    }
}
