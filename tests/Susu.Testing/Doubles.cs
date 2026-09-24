using System.Collections.Concurrent;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Testing;

/// <summary>Deterministic monotonic clock: time moves only through <see cref="Advance"/>.</summary>
public sealed class ManualClock : IClock
{
    private readonly object gate = new();
    private readonly List<(long Due, TaskCompletionSource Done)> timers = [];
    private long now;

    public long NowMilliseconds { get { lock (gate) return now; } }
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddMilliseconds(NowMilliseconds).AddYears(56);
    public int PendingTimers { get { lock (gate) return timers.Count(t => !t.Done.Task.IsCompleted); } }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;
            timers.Add((now + (long)delay.TotalMilliseconds, done));
        }
        cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
        return done.Task;
    }

    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource> due;
        lock (gate)
        {
            now += (long)by.TotalMilliseconds;
            due = [.. timers.Where(t => t.Due <= now).Select(t => t.Done)];
            timers.RemoveAll(t => t.Due <= now);
        }
        foreach (var d in due) d.TrySetResult();
    }
}

public sealed class FixedJitter(double value = 0) : IJitter
{
    public double Next() => value;
}

public sealed class RecordingUsage : IUsageSink
{
    public ConcurrentQueue<(string Service, string Attempt, string Metric, long Units, string Outcome)> Records { get; } = new();
    public void Record(string serviceId, string attemptId, string metric, long units, string outcome) => Records.Enqueue((serviceId, attemptId, metric, units, outcome));
}

/// <summary>One scripted reaction of a <see cref="ScriptedProvider"/> to a call.</summary>
public abstract record Step
{
    public sealed record Succeed(string Text) : Step;
    public sealed record Stream(string[] Pieces, string FinalText) : Step;
    public sealed record StreamThenFail(string[] Pieces, ProviderError Error) : Step;
    public sealed record Fail(ProviderError Error) : Step;
    /// <summary>Never completes until cancelled (hung provider / slow network).</summary>
    public sealed record Hang : Step;
    /// <summary>Waits for an external release, then succeeds.</summary>
    public sealed record Gate(TaskCompletionSource Release, string Text) : Step;
    /// <summary>Echoes the request text with a prefix.</summary>
    public sealed record Echo(string Prefix) : Step;
}

/// <summary>
/// Failing-network / scripted translation provider for tests. Steps are consumed per call; the last
/// step repeats. Every call is recorded with its text and attempt id.
/// </summary>
public sealed class ScriptedProvider(string serviceId, TranslationLimits limits, params Step[] steps) : ITranslationProvider
{
    private readonly ConcurrentQueue<Step> script = new(steps);
    private Step last = steps.Length > 0 ? steps[^1] : new Step.Echo("");
    public string ServiceId { get; } = serviceId;
    public string DisplayName => ServiceId;
    public string LimiterKey { get; init; } = $"key:{serviceId}";
    public TranslationLimits Limits { get; } = limits;
    public Func<string, string, bool> Pairs { get; init; } = (_, _) => true;
    public ConcurrentQueue<(string Text, string AttemptId)> Calls { get; } = new();
    public int Cancellations;

    public bool SupportsLanguagePair(string from, string to) => Pairs(from, to);

    public async Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken)
    {
        Calls.Enqueue((call.Text, call.AttemptId));
        var step = script.TryDequeue(out var next) ? (last = next) : last;
        try
        {
            switch (step)
            {
                case Step.Succeed s: return new ProviderOutcome.Success(s.Text);
                case Step.Echo e: return new ProviderOutcome.Success(e.Prefix + call.Text);
                case Step.Fail f: return new ProviderOutcome.Failure(f.Error);
                case Step.Stream s:
                    foreach (var piece in s.Pieces) await onChunk(piece);
                    return new ProviderOutcome.Success(s.FinalText);
                case Step.StreamThenFail s:
                    foreach (var piece in s.Pieces) await onChunk(piece);
                    return new ProviderOutcome.Failure(s.Error);
                case Step.Hang:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Cancelled));
                case Step.Gate g:
                    await g.Release.Task.WaitAsync(cancellationToken);
                    return new ProviderOutcome.Success(g.Text);
                default: throw new InvalidOperationException("unknown step");
            }
        }
        catch (OperationCanceledException) { Interlocked.Increment(ref Cancellations); throw; }
    }

    public static readonly TranslationLimits Generous = new(InputUnit.Utf8Bytes, 16000, BatchMode.Single, 1, 16000);
    public static readonly TranslationLimits MyMemory = new(InputUnit.Utf8Bytes, 500, BatchMode.Single, 1, 500);
}
