using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Jobs;

public enum OcrPhase { Idle, Recognizing, Recognized, NoText, Failed, Unavailable, Cancelled }

/// <summary>
/// What the OCR window shows (F11.3 projects it). <see cref="Text"/> is the source-card text (<see cref="OcrText.Join"/>: one
/// block per line, formulas as LaTeX source); <see cref="Blocks"/> keep boxes, kinds and confidences. <see cref="Error"/> is set
/// for Failed; <see cref="ServiceId"/> names the OCR instance that ran. <see cref="Translated"/>: the text was handed to the
/// translation pipeline.
/// </summary>
public sealed record OcrState(long Generation, OcrPhase Phase, string? ServiceId = null, string? Text = null, IReadOnlyList<OcrBlock>? Blocks = null,
    ProviderError? Error = null, bool Translated = false)
{
    public static readonly OcrState Idle = new(0, OcrPhase.Idle);
}

/// <summary>
/// F11.2 OCR job (PLAN 6.2 ⑥–⑧, ARCHITECTURE 7): a captured <see cref="ScreenshotImage"/> → the selected OCR service by handle →
/// recognized blocks → the same translation pipeline as typed input (T02: same session, chunking and cards, via
/// <paramref name="translate"/>) when <paramref name="autoTranslate"/> is on.
/// <list type="bullet">
/// <item>One job at a time: a new capture or <see cref="Cancel"/> cancels the running one (its plugin call and broker I/O stop),
/// and a superseded result is never published or translated.</item>
/// <item>The job owns the image from <see cref="RecognizeAsync"/> on and releases its lease when recognition ends, however it
/// ends (recognized, failed, cancelled, no service): no temporary image outlives the job (OCR01, ARCHITECTURE 8.4).</item>
/// <item>No usable service (none enabled with saved and granted credentials): <see cref="OcrPhase.Unavailable"/>, nothing is
/// called. "No text found" is <see cref="OcrPhase.NoText"/>, never an empty success.</item>
/// </list>
/// </summary>
public sealed class OcrJob(Func<IOcrProvider?> provider, Func<string, Task> translate, Func<bool> autoTranslate, TimeSpan? timeout = null, Func<string>? idFactory = null)
{
    private readonly object gate = new();
    private readonly TimeSpan callTimeout = timeout ?? ConfigSnapshot.EffectiveTimeout(Capability.Ocr, null);
    private readonly Func<string> ids = idFactory ?? (() => $"ocr-{Guid.NewGuid():N}");
    private CancellationTokenSource? running;
    private long generation;
    private OcrState state = OcrState.Idle;

    /// <summary>Raised for every published state (Recognizing, then the final one) of the current job only.</summary>
    public event Action<OcrState>? StateChanged;

    public OcrState State { get { lock (gate) return state; } }

    /// <summary>
    /// Recognizes <paramref name="image"/> (taking ownership of it) and, when the text is recognized and auto-translate is on,
    /// submits it to the translation pipeline. Returns the final state of this job (Cancelled when superseded or cancelled).
    /// </summary>
    public async Task<OcrState> RecognizeAsync(ScreenshotImage image, string? lang = null)
    {
        ArgumentNullException.ThrowIfNull(image);
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
            if (service is null) return Publish(current, new OcrState(current, OcrPhase.Unavailable));
            Publish(current, new OcrState(current, OcrPhase.Recognizing, service.InstanceId));
            OcrOutcome outcome;
            try
            {
                var file = image.File;
                outcome = await service.RecognizeAsync(new OcrCall(file.LeaseId, file.Mime, file.Bytes, image.Width, image.Height, lang, ids(), callTimeout), cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return Cancelled(current, service.InstanceId); }
            catch (Exception error) { outcome = new OcrOutcome.Failure(new ProviderError(ErrorKind.Unavailable, error.GetType().Name)); }
            if (cancel.IsCancellationRequested) return Cancelled(current, service.InstanceId);
            // The image is no longer needed once the service answered: release it before translating (the lease rules, 8.4).
            image.Dispose();

            var final = outcome switch
            {
                OcrOutcome.Recognized r when OcrText.Join(r.Blocks) is { } text => new OcrState(current, OcrPhase.Recognized, service.InstanceId, text, r.Blocks),
                OcrOutcome.Recognized or OcrOutcome.NoText => new OcrState(current, OcrPhase.NoText, service.InstanceId),
                OcrOutcome.Failure f => new OcrState(current, OcrPhase.Failed, service.InstanceId, Error: f.Error),
                _ => new OcrState(current, OcrPhase.Failed, service.InstanceId, Error: new ProviderError(ErrorKind.BadResponse)),
            };
            if (final.Phase == OcrPhase.Recognized && autoTranslate() && IsCurrent(current))
            {
                await translate(final.Text!);
                final = final with { Translated = true };
            }
            return Publish(current, final);
        }
        finally
        {
            image.Dispose();
            lock (gate) if (ReferenceEquals(running, cancel)) running = null;
            cancel.Dispose();
        }
    }

    /// <summary>Cancels the running job, if any (Esc/close of the OCR window). Its image lease is released as it unwinds.</summary>
    public void Cancel()
    {
        lock (gate) running?.Cancel();
    }

    /// <summary>Hands already recognized (and possibly edited) text to the translation pipeline, e.g. when auto-translate is off.</summary>
    public Task TranslateAsync(string text) => string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : translate(text);

    private bool IsCurrent(long job) { lock (gate) return job == generation; }

    private OcrState Cancelled(long job, string serviceId) => Publish(job, new OcrState(job, OcrPhase.Cancelled, serviceId));

    private OcrState Publish(long job, OcrState next)
    {
        lock (gate)
        {
            if (job != generation) return next with { Phase = OcrPhase.Cancelled };
            state = next;
        }
        StateChanged?.Invoke(next);
        return next;
    }
}
