using System.Globalization;
using Susu.Contracts;

namespace Susu.Domain;

/// <summary>Job lifecycle (ARCHITECTURE 5).</summary>
public enum JobState { Created, Queued, Running, Completed, CompletedWithErrors, Failed, Pausing, Paused, Cancelling, Cancelled }

public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> transitions = new()
    {
        [JobState.Created] = [JobState.Queued, JobState.Cancelled],
        [JobState.Queued] = [JobState.Running, JobState.Cancelled],
        [JobState.Running] = [JobState.Completed, JobState.CompletedWithErrors, JobState.Failed, JobState.Pausing, JobState.Cancelling],
        [JobState.Pausing] = [JobState.Paused, JobState.Cancelling],
        [JobState.Paused] = [JobState.Queued, JobState.Cancelling],
        [JobState.Cancelling] = [JobState.Cancelled],
    };

    public static bool IsTerminal(JobState state) => state is JobState.Completed or JobState.CompletedWithErrors or JobState.Failed or JobState.Cancelled;

    public static bool CanTransition(JobState from, JobState to, bool supportsPause)
        => transitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0 && (supportsPause || (to != JobState.Pausing && to != JobState.Paused));

    /// <summary>Applies a transition or throws; a terminal state is committed exactly once.</summary>
    public static JobState Transition(JobState from, JobState to, bool supportsPause)
        => CanTransition(from, to, supportsPause) ? to : throw new InvalidOperationException($"illegal job transition {from} → {to}");
}

/// <summary>Identity of one streamed/terminal event; stale ones are dropped (ARCHITECTURE 5, J01).</summary>
public readonly record struct EventStamp(long Generation, string AttemptId, long Sequence);

public abstract record RetryDecision
{
    public sealed record Retry(TimeSpan Delay, bool ResetStream) : RetryDecision;
    public sealed record Stop(string Reason) : RetryDecision;
}

/// <summary>
/// The single automatic-retry authority (ARCHITECTURE 5.1): HTTP and plugins never retry on their own.
/// network/timeout: once after 500 ms + bounded jitter; 429: honour Retry-After up to 60 s; others: never.
/// Both attempts share one total deadline. A partially streamed read-only result may be regenerated once
/// after a ResultReset; writes (vocab) never retry here.
/// </summary>
public static class RetryPolicy
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MaxJitter = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(1);

    public static RetryDecision Decide(ProviderError error, int attemptsMade, TimeSpan remaining, bool streamedPartial, bool readOnly, double jitter01)
    {
        if (attemptsMade >= 2) return new RetryDecision.Stop("retry budget used");
        if (!readOnly) return new RetryDecision.Stop("writes are not retried automatically");
        TimeSpan delay;
        switch (error.Kind)
        {
            case ErrorKind.Network or ErrorKind.Timeout:
                delay = BaseDelay + TimeSpan.FromMilliseconds(Math.Clamp(jitter01, 0, 1) * MaxJitter.TotalMilliseconds);
                break;
            case ErrorKind.RateLimited:
                delay = error.RetryAfter ?? DefaultRateLimitDelay;
                if (delay > MaxRetryAfter) return new RetryDecision.Stop("Retry-After exceeds 60 s: ask the user to retry later");
                break;
            default:
                return new RetryDecision.Stop($"{error.Kind} is not retried");
        }
        if (delay >= remaining) return new RetryDecision.Stop("no time left before the shared deadline");
        return new RetryDecision.Retry(delay, streamedPartial);
    }

    /// <summary>Parses Retry-After (delta-seconds or HTTP-date). Invalid or negative values return null.</summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.All(char.IsAsciiDigit))
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && seconds <= int.MaxValue ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var delta = date - now;
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        return null;
    }
}

/// <summary>
/// Model of the plugin-host runtime registry (ARCHITECTURE 3/4, J06): one runtime per package, loaded
/// with the first enabled capability, destroyed with the last; disabling one capability cancels only
/// its calls; a forced rebuild fails every in-flight call of that package and nothing else.
/// </summary>
public sealed class RuntimeRegistry
{
    public abstract record Effect
    {
        public sealed record Load(string PackageId) : Effect;
        public sealed record Unload(string PackageId) : Effect;
        public sealed record FailCall(string CallId, ErrorKind Kind) : Effect;
    }

    private sealed class Package
    {
        public HashSet<Capability> Enabled { get; } = [];
        public Dictionary<string, Capability> Calls { get; } = new(StringComparer.Ordinal);
        public bool Loaded { get; set; }
    }

    private readonly Dictionary<string, Package> packages = new(StringComparer.Ordinal);

    public bool IsLoaded(string packageId) => packages.TryGetValue(packageId, out var p) && p.Loaded;
    public int InFlight(string packageId) => packages.TryGetValue(packageId, out var p) ? p.Calls.Count : 0;

    public IReadOnlyList<Effect> Enable(string packageId, Capability capability)
    {
        var package = packages.TryGetValue(packageId, out var p) ? p : packages[packageId] = new Package();
        package.Enabled.Add(capability);
        if (package.Loaded) return [];
        package.Loaded = true;
        return [new Effect.Load(packageId)];
    }

    public IReadOnlyList<Effect> Disable(string packageId, Capability capability)
    {
        if (!packages.TryGetValue(packageId, out var package) || !package.Enabled.Remove(capability)) return [];
        var effects = new List<Effect>();
        foreach (var (call, cap) in package.Calls.Where(c => c.Value == capability).ToArray())
        {
            package.Calls.Remove(call);
            effects.Add(new Effect.FailCall(call, ErrorKind.Cancelled));
        }
        if (package.Enabled.Count == 0 && package.Loaded)
        {
            foreach (var call in package.Calls.Keys.ToArray()) effects.Add(new Effect.FailCall(call, ErrorKind.Unavailable));
            package.Calls.Clear();
            package.Loaded = false;
            effects.Add(new Effect.Unload(packageId));
        }
        return effects;
    }

    /// <summary>Registers a call; rejected (Unavailable/Busy) when the capability is off or the runtime is at its in-flight limit.</summary>
    public ErrorKind? Begin(string packageId, Capability capability, string callId)
    {
        if (!packages.TryGetValue(packageId, out var package) || !package.Loaded || !package.Enabled.Contains(capability)) return ErrorKind.Unavailable;
        if (package.Calls.Count >= ProtocolLimits.MaxInFlightCallsPerRuntime) return ErrorKind.Busy;
        package.Calls[callId] = capability;
        return null;
    }

    public void End(string packageId, string callId) { if (packages.TryGetValue(packageId, out var package)) package.Calls.Remove(callId); }

    /// <summary>Budget exceeded / engine fault: fail all in-flight calls of this package; the runtime stays enabled (rebuilt).</summary>
    public IReadOnlyList<Effect> Rebuild(string packageId, ErrorKind kind = ErrorKind.Timeout)
    {
        if (!packages.TryGetValue(packageId, out var package)) return [];
        var effects = package.Calls.Keys.Select(call => (Effect)new Effect.FailCall(call, kind)).ToList();
        package.Calls.Clear();
        if (package.Loaded) { effects.Add(new Effect.Unload(packageId)); effects.Add(new Effect.Load(packageId)); }
        return effects;
    }
}
